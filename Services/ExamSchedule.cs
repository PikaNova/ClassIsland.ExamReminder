using ClassIsland.ExamReminder.Models;

namespace ClassIsland.ExamReminder.Services;

public sealed record ExamSession(string Key, DateTimeOffset Start, DateTimeOffset End);
public sealed record ExamChange(string ExamId, string Title, string Detail, string Key);
public enum ReminderType
{
    Upcoming,
    Start,
    BeforeEnd,
    Ended,
    ScheduleChange,
    DayBefore
}

public sealed record ReminderTypeDefinition(ReminderType Type, string Title, string Description);

public static class ExamSchedule
{
    public static readonly IReadOnlyList<ReminderTypeDefinition> ReminderTypes =
    [
        new(ReminderType.Upcoming, "即将开始", "按设置的提前分钟数提醒考试即将开始。"),
        new(ReminderType.Start, "考试开始", "考试正式开始时间到达时提醒。"),
        new(ReminderType.BeforeEnd, "结束前", "按设置的提前分钟数提醒考试即将结束。"),
        new(ReminderType.Ended, "考试结束", "考试结束后提醒一次。"),
        new(ReminderType.ScheduleChange, "考试时间变更", "考试新增、改期、暂停、继续、取消或归档时提醒。"),
        new(ReminderType.DayBefore, "次日预告", "每天指定时间预告明天的考试。")
    ];

    public static bool IsReminderTypeEnabled(PluginSettings settings, ReminderType type) => type switch
    {
        ReminderType.Upcoming => settings.EnableBeforeStartReminder,
        ReminderType.Start => settings.EnableStartReminder,
        ReminderType.BeforeEnd => settings.EnableBeforeEndReminder,
        ReminderType.Ended => settings.EnableExamEndReminder,
        ReminderType.ScheduleChange => settings.EnableExamChangeReminder,
        ReminderType.DayBefore => settings.EnableDayBeforeReminder,
        _ => false
    };

    public static void SetReminderTypeEnabled(PluginSettings settings, ReminderType type, bool enabled)
    {
        switch (type)
        {
            case ReminderType.Upcoming: settings.EnableBeforeStartReminder = enabled; break;
            case ReminderType.Start: settings.EnableStartReminder = enabled; break;
            case ReminderType.BeforeEnd: settings.EnableBeforeEndReminder = enabled; break;
            case ReminderType.Ended: settings.EnableExamEndReminder = enabled; break;
            case ReminderType.ScheduleChange: settings.EnableExamChangeReminder = enabled; break;
            case ReminderType.DayBefore: settings.EnableDayBeforeReminder = enabled; break;
        }
    }

    public static TimeZoneInfo TimeZone(PluginSettings settings) => TimeZoneInfo.FindSystemTimeZoneById(
        settings.ServiceMode == "online" && !string.IsNullOrWhiteSpace(settings.OnlineTimeZoneId)
            ? settings.OnlineTimeZoneId
            : settings.SchoolTimeZone);

    public static IReadOnlyList<ExamSession> Sessions(IEnumerable<ExamBoardExam> exams, PluginSettings settings,
        DateTimeOffset? now = null)
    {
        var zone = TimeZone(settings);
        var referenceNow = now ?? DateTimeOffset.UtcNow;
        var ranges = exams.Where(x => x.EndAt > x.StartAt).GroupBy(x =>
        {
            var local = TimeZoneInfo.ConvertTime(x.StartAt, zone);
            return $"{local:yyyy-MM-dd}|{(local.Hour < settings.SessionBoundaryHour ? "AM" : "PM")}";
        }).Select(group => new ExamSession(group.Key,
            group.Min(x => x.StartAt).AddMinutes(-settings.ExamModeLeadMinutes),
            group.Max(x => x.PausedAt is {} pausedAt && pausedAt < referenceNow
                ? x.EndAt.Add(referenceNow - pausedAt)
                : x.EndAt).AddMinutes(settings.ExamModeTailMinutes))).OrderBy(x => x.Start).ToList();
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

    public static string LifecycleKey(ExamBoardExam exam) => !string.IsNullOrWhiteSpace(exam.ReminderKey)
        ? exam.ReminderKey
        : $"{exam.Kind}:{exam.Id}";

    /// <summary>返回是否有另一场考试正在进行，或刚刚结束。</summary>
    public static bool IsOtherExamInProgress(IEnumerable<ExamBoardExam> exams, ExamBoardExam exam,
        DateTimeOffset now, TimeSpan recentlyEnded = default) =>
        exams.Any(x => x.Id != exam.Id && x.StartAt <= now &&
            (x.IsPaused || x.EndAt > now - recentlyEnded));

    public static bool IsAnotherExamStartingSoon(IEnumerable<ExamBoardExam> exams, ExamBoardExam exam,
        DateTimeOffset now, TimeSpan window) =>
        exams.Any(x => x.Id != exam.Id && x.StartAt >= now && x.StartAt <= now + window);

    /// <summary>返回与当前学校日期相交的考试，包含已结束的场次，按开始时间排序。</summary>
    public static IReadOnlyList<ExamBoardExam> Today(IEnumerable<ExamBoardExam> exams, PluginSettings settings, DateTimeOffset now)
    {
        var zone = TimeZone(settings);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        return exams.Where(x => x.IsPaused ||
                TimeZoneInfo.ConvertTime(x.StartAt, zone).Date == today ||
                TimeZoneInfo.ConvertTime(x.EndAt, zone).Date == today)
            .OrderBy(x => x.StartAt).ToList();
    }

    public static string Phase(ExamBoardExam exam, DateTimeOffset now) =>
        exam.IsPaused ? "已暂停" : exam.EndAt <= now ? "已结束" : exam.StartAt <= now ? "进行中" : "待开始";

    /// <summary>返回考虑累计及当前暂停时长后的实际结束时间。</summary>
    public static DateTimeOffset EffectiveEndAt(ExamBoardExam exam, DateTimeOffset now) =>
        exam.EndAt.AddMilliseconds(exam.PausedAt is { } pausedAt && pausedAt < now
            ? Math.Max(0, (now - pausedAt).TotalMilliseconds)
            : 0);

    public static bool IsStillRelevant(ExamBoardExam exam, DateTimeOffset now) =>
        exam.IsPaused || EffectiveEndAt(exam, now) > now;

    public static bool IsDue(ExamBoardExam exam, int minutes, DateTimeOffset now, int graceSeconds)
    {
        if (exam.IsPaused) return false;
        var due = exam.StartAt.AddMinutes(-minutes);
        return now >= due && now <= due.AddSeconds(graceSeconds) &&
            (minutes == 0 ? now < exam.EndAt : now < exam.StartAt);
    }

    public static bool IsEndDue(ExamBoardExam exam, int minutes, DateTimeOffset now, int graceSeconds)
    {
        if (exam.IsPaused) return false;
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
        IEnumerable<ExamBoardExam> current, PluginSettings settings, DateTimeOffset now,
        IEnumerable<ExamBoardLifecycleState>? lifecycle = null, bool explicitLifecycle = false)
    {
        var old = previous.ToDictionary(x => x.Id, x => x);
        var currentExams = current.ToList();
        // 仍在列表里的考试即使时间已过也不算取消；只有整场消失才是取消。
        var currentIds = currentExams.Select(x => x.Id).ToHashSet();
        var result = new List<ExamChange>();
        var lifecycleByKey = (lifecycle ?? []).Where(x => !string.IsNullOrWhiteSpace(x.ReminderKey))
            .GroupBy(x => x.ReminderKey).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var lifecycleReported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exam in old.Values.Where(x => !currentIds.Contains(x.Id)).OrderBy(x => x.StartAt))
        {
            var key = LifecycleKey(exam);
            if (lifecycleByKey.TryGetValue(key, out var state))
            {
                if (!lifecycleReported.Add(state.MajorExamId)) continue;
                var status = state.Status == "archived" ? "后台已归档" : "后台提前结束";
                var changedAt = state.ChangedAt is { } at ? $"（{ShortTime(at, settings)}）" : "";
                result.Add(new ExamChange(exam.Id, exam.Name, $"{status}{changedAt} 原定 {ShortTime(exam.StartAt, settings)}",
                    $"{state.MajorExamId}|lifecycle|{state.Status}|{state.ChangedAt?.UtcTicks ?? 0}"));
            }
            else if (!explicitLifecycle && exam.EndAt > now)
            {
                // Legacy servers do not expose lifecycle state, so retain the historical
                // cancellation fallback for a disappeared future item.
                result.Add(new ExamChange(exam.Id, exam.Name, $"取消 原定 {ShortTime(exam.StartAt, settings)}",
                    $"{exam.Id}|removed|{exam.StartAt.UtcTicks}"));
            }
        }
        foreach (var exam in currentExams.Where(x => IsStillRelevant(x, now)).OrderBy(x => x.StartAt))
        {
            if (old.TryGetValue(exam.Id, out var past) && !IsStillRelevant(past, now) &&
                !exam.IsPaused && past.PausedMs == exam.PausedMs) continue;
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
            if (before.IsPaused != exam.IsPaused || before.PausedAt != exam.PausedAt)
            {
                details.Add(exam.IsPaused ? "后台已暂停考试" : "后台已继续考试");
                keyParts.Add($"pause|{before.IsPaused}|{exam.IsPaused}|{before.PausedAt?.UtcTicks ?? 0}|{exam.PausedAt?.UtcTicks ?? 0}");
            }
            if (details.Count > 0) result.Add(new ExamChange(exam.Id, exam.Name, string.Join("；", details),
                $"{exam.Id}|{string.Join(";", keyParts)}"));
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
