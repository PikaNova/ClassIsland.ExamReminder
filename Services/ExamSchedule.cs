using ClassIsland.ExamReminder.Models;

namespace ClassIsland.ExamReminder.Services;

public sealed record ExamSession(string Key, DateTimeOffset Start, DateTimeOffset End);
public sealed record ExamChange(string ExamId, string Title, string Detail, string Key);

public static class ExamSchedule
{
    public static TimeZoneInfo TimeZone(PluginSettings settings) => TimeZoneInfo.FindSystemTimeZoneById(settings.SchoolTimeZone);

    public static IReadOnlyList<ExamSession> Sessions(IEnumerable<ExamBoardExam> exams, PluginSettings settings)
    {
        var zone = TimeZone(settings);
        var ranges = exams.Where(x => x.EndAt > x.StartAt).GroupBy(x =>
        {
            var local = TimeZoneInfo.ConvertTime(x.StartAt, zone);
            return $"{local:yyyy-MM-dd}|{(local.Hour < settings.SessionBoundaryHour ? "AM" : "PM")}";
        }).Select(group => new ExamSession(group.Key,
            group.Min(x => x.StartAt).AddMinutes(-settings.ExamModeLeadMinutes),
            group.Max(x => x.EndAt).AddMinutes(settings.ExamModeTailMinutes))).OrderBy(x => x.Start).ToList();
        var merged = new List<ExamSession>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = previous with { Key = previous.Key + "+" + range.Key, End = range.End > previous.End ? range.End : previous.End };
            }
            else merged.Add(range);
        }
        return merged;
    }

    public static string ActionKey(ExamBoardExam exam, string action) => $"{exam.Id}|{exam.StartAt.UtcTicks}|{action}";

    public static string EndActionKey(ExamBoardExam exam, string action) =>
        $"{exam.Id}|{exam.StartAt.UtcTicks}|{exam.EndAt.UtcTicks}|{action}";

    /// <summary>返回是否有另一场考试正在进行，或刚刚结束。</summary>
    public static bool IsOtherExamInProgress(IEnumerable<ExamBoardExam> exams, ExamBoardExam exam,
        DateTimeOffset now, TimeSpan recentlyEnded = default) =>
        exams.Any(x => x.Id != exam.Id && x.StartAt <= now && x.EndAt > now - recentlyEnded);

    public static bool IsAnotherExamStartingSoon(IEnumerable<ExamBoardExam> exams, ExamBoardExam exam,
        DateTimeOffset now, TimeSpan window) =>
        exams.Any(x => x.Id != exam.Id && x.StartAt >= now && x.StartAt <= now + window);

    /// <summary>返回与当前学校日期相交的考试，包含已结束的场次，按开始时间排序。</summary>
    public static IReadOnlyList<ExamBoardExam> Today(IEnumerable<ExamBoardExam> exams, PluginSettings settings, DateTimeOffset now)
    {
        var zone = TimeZone(settings);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return exams.Where(x =>
                TimeZoneInfo.ConvertTime(x.StartAt, zone).Date == today ||
                TimeZoneInfo.ConvertTime(x.EndAt, zone).Date == today)
            .OrderBy(x => x.StartAt).ToList();
    }

    public static string Phase(ExamBoardExam exam, DateTimeOffset now) =>
        exam.EndAt <= now ? "已结束" : exam.StartAt <= now ? "进行中" : "待开始";

    public static bool IsDue(ExamBoardExam exam, int minutes, DateTimeOffset now, int graceSeconds)
    {
        var due = exam.StartAt.AddMinutes(-minutes);
        return now >= due && now <= due.AddSeconds(graceSeconds) &&
            (minutes == 0 ? now < exam.EndAt : now < exam.StartAt);
    }

    public static bool IsEndDue(ExamBoardExam exam, int minutes, DateTimeOffset now, int graceSeconds)
    {
        if (minutes == 0)
            return now >= exam.EndAt && now <= exam.EndAt.AddSeconds(graceSeconds);
        var due = exam.EndAt.AddMinutes(-minutes);
        return now >= exam.StartAt && now >= due && now <= due.AddSeconds(graceSeconds) && now < exam.EndAt;
    }

    public static IReadOnlyList<ExamBoardExam> Tomorrow(IEnumerable<ExamBoardExam> exams, PluginSettings settings, DateTimeOffset now)
    {
        var zone = TimeZone(settings);
        var tomorrow = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        return exams.Where(x => TimeZoneInfo.ConvertTime(x.StartAt, zone).Date == tomorrow).OrderBy(x => x.StartAt).ToList();
    }

    public static string FormatDayBefore(string template, IReadOnlyList<ExamBoardExam> exams, PluginSettings settings)
    {
        var first = exams[0];
        var last = exams[^1];
        return template.Replace("{数量}", exams.Count.ToString())
            .Replace("{首场}", first.Name).Replace("{首场时间}", ShortTime(first.StartAt, settings))
            .Replace("{末场}", last.Name).Replace("{末场时间}", ShortTime(last.StartAt, settings))
            .Replace("{班级}", settings.BoundClassTag);
    }

    public static IReadOnlyList<ExamChange> Changes(IEnumerable<ExamBoardExam> previous,
        IEnumerable<ExamBoardExam> current, PluginSettings settings, DateTimeOffset now)
    {
        var old = previous.Where(x => x.EndAt > now).ToDictionary(x => x.Id, x => x);
        var currentExams = current.ToList();
        // 仍在列表里的考试即使时间已过也不算取消；只有整场消失才是取消。
        var currentIds = currentExams.Select(x => x.Id).ToHashSet();
        var result = new List<ExamChange>();
        foreach (var exam in old.Values.Where(x => !currentIds.Contains(x.Id)).OrderBy(x => x.StartAt))
            result.Add(new ExamChange(exam.Id, exam.Name, $"取消 原定 {ShortTime(exam.StartAt, settings)}",
                $"{exam.Id}|removed|{exam.StartAt.UtcTicks}"));
        foreach (var exam in currentExams.Where(x => x.EndAt > now).OrderBy(x => x.StartAt))
        {
            if (!old.TryGetValue(exam.Id, out var before))
            {
                result.Add(new ExamChange(exam.Id, exam.Name,
                    $"新增 {ShortTime(exam.StartAt, settings)}",
                    $"{exam.Id}|added|{exam.StartAt.UtcTicks}|{exam.Name}"));
                continue;
            }
            var details = new List<string>();
            var keyParts = new List<string>();
            if (before.Name != exam.Name)
            {
                details.Add($"名称 {before.Name} → {exam.Name}");
                keyParts.Add($"name|{before.Name}|{exam.Name}");
            }
            if (before.StartAt != exam.StartAt)
            {
                details.Add($"开考 {ShortTime(before.StartAt, settings)} → {ShortTime(exam.StartAt, settings)}");
                keyParts.Add($"start|{before.StartAt.UtcTicks}|{exam.StartAt.UtcTicks}");
            }
            if (before.EndAt != exam.EndAt)
            {
                details.Add($"结束 {ShortTime(before.EndAt, settings)} → {ShortTime(exam.EndAt, settings)}");
                keyParts.Add($"end|{before.EndAt.UtcTicks}|{exam.EndAt.UtcTicks}");
            }
            if (details.Count > 0) result.Add(new ExamChange(exam.Id, exam.Name, string.Join("；", details), $"{exam.Id}|{string.Join(";", keyParts)}"));
        }
        return result;
    }

    public static string FormatChange(string template, IReadOnlyList<ExamChange> changes, PluginSettings settings)
    {
        var details = changes.Count > 3
            ? $"共 {changes.Count} 处变更，请查看考试概览"
            : string.Join("；", changes.Select(x => x.Detail));
        return template.Replace("{变更}", details).Replace("{数量}", changes.Count.ToString())
            .Replace("{班级}", settings.BoundClassTag);
    }

    private static string ShortTime(DateTimeOffset value, PluginSettings settings)
    {
        try { return TimeZoneInfo.ConvertTime(value, TimeZone(settings)).ToString("MM-dd HH:mm"); }
        catch { return value.ToLocalTime().ToString("MM-dd HH:mm"); }
    }

    public static string Format(string template, ExamBoardExam exam, int minutes, PluginSettings settings)
    {
        var zone = TimeZone(settings);
        var start = TimeZoneInfo.ConvertTime(exam.StartAt, zone).ToString("HH:mm");
        var end = TimeZoneInfo.ConvertTime(exam.EndAt, zone).ToString("HH:mm");
        return template.Replace("{考试}", exam.Name).Replace("{分钟}", minutes.ToString())
            .Replace("{班级}", settings.BoundClassTag)
            .Replace("{时间}", start).Replace("{开始时间}", start).Replace("{结束时间}", end);
    }
}
