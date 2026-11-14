using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Core.Models.Notification;
using ClassIsland.ExamReminder.Models;
using ClassIsland.ExamReminder.Services;
using ClassIsland.ExamReminder.Views;
using ClassIsland.Shared;
using FluentAvalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            AppBuilder.Configure<CheckApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            if (args.Length == 1 && args[0] == "--online-only")
            {
                OnlineProtocolChecks().GetAwaiter().GetResult();
                Console.WriteLine("PASS: online protocol checks");
                return 0;
            }
            BasicChecks();
            OnlineProtocolChecks().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--standalone")
            {
                Console.WriteLine($"PASS: {_checks} standalone checks; host integration was not run.");
                return 0;
            }
            if (args.Length == 0) throw new ArgumentException("Pass the ClassIsland 2.1.0.1 app directory to run host integration and UI checks.");
            HostChecks(Path.GetFullPath(args[0]));
            Console.WriteLine($"PASS: {_checks} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        _checks++; Console.WriteLine("PASS: " + description);
    }

    private static ExamBoardExam Exam(string name, string start, string end) => new() { Id = name, Name = name, StartAt = DateTimeOffset.Parse(start), EndAt = DateTimeOffset.Parse(end) };

    private static void BasicChecks()
    {
        Check(ExamBoardUrls.TryNormalizeBaseUrl("http://192.168.1.20:3000/exam?x=1", out var url, out _) && url == "http://192.168.1.20:3000", "LAN HTTP, port and path normalization");
        Check(ExamBoardUrls.TryNormalizeBaseUrl("http://exam-server:3000/school/admin", out url, out _) && url.EndsWith("/school"), "LAN hostname and deployment subpath");
        Check(!ExamBoardUrls.TryNormalizeBaseUrl("file:///C:/temp", out _, out _) && !ExamBoardUrls.TryNormalizeBaseUrl("https://user:pass@school.example", out _, out _), "reject unsupported schemes and URL credentials");
        var s = new PluginSettings(); PluginSettingsStore.Normalize(s);
        var exams = new[]
        {
            Exam("Math", "2026-10-18T08:00+08:00", "2026-10-18T09:30+08:00"),
            Exam("Language", "2026-10-18T10:00+08:00", "2026-10-18T12:10+08:00"),
            Exam("Physics", "2026-10-18T14:00+08:00", "2026-10-18T16:30+08:00")
        };
        var sessions = ExamSchedule.Sessions(exams, s);
        Check(sessions.Count == 2 && sessions[0].Start == DateTimeOffset.Parse("2026-10-18T07:45+08:00") && sessions[0].End == exams[1].EndAt, "AM session includes preparation and gaps");
        Check(sessions[1].Start == DateTimeOffset.Parse("2026-10-18T13:45+08:00"), "PM session uses independent preparation");
        sessions = ExamSchedule.Sessions(new[] { Exam("Cross", "2026-10-18T12:30+08:00", "2026-10-18T14:30+08:00"), exams[2] }, s);
        Check(sessions.Count == 1 && sessions[0].End == exams[2].EndAt, "cross-noon overlapping sessions merge");
        var today = ExamSchedule.Today(new[]
        {
            Exam("Yesterday", "2026-10-17T23:00+08:00", "2026-10-18T01:00+08:00"),
            exams[0],
            Exam("Tomorrow", "2026-10-19T08:00+08:00", "2026-10-19T09:30+08:00")
        }, s, DateTimeOffset.Parse("2026-10-18T10:00+08:00"));
        Check(today.Count == 2 && today[0].Name == "Yesterday" && today[1].Name == "Math", "today schedule includes crossing-midnight exams and excludes other days");
        Check(ExamSchedule.Phase(exams[0], exams[0].StartAt.AddMinutes(-1)) == "待开始" &&
              ExamSchedule.Phase(exams[0], exams[0].StartAt.AddMinutes(1)) == "进行中" &&
              ExamSchedule.Phase(exams[0], exams[0].EndAt) == "已结束", "exam phase follows start and end times");
        var pausedExam = new ExamBoardExam { Id = "Paused", Name = "Paused", StartAt = exams[0].StartAt,
            EndAt = exams[0].EndAt, PausedAt = exams[0].StartAt.AddMinutes(30) };
        Check(ExamSchedule.Phase(pausedExam, exams[0].EndAt.AddMinutes(10)) == "已暂停" &&
              !ExamSchedule.IsDue(pausedExam, 0, exams[0].StartAt, 120) &&
              !ExamSchedule.IsEndDue(pausedExam, 0, exams[0].EndAt, 120), "paused exams freeze phase and suppress start/end alerts");
        var afterOriginalEnd = exams[0].EndAt.AddMinutes(30);
        var statusPausedExam = System.Text.Json.JsonSerializer.Deserialize<ExamBoardExam>(
            "{\"id\":\"Math\",\"name\":\"Math\",\"startAt\":\"2026-10-18T00:00:00Z\",\"endAt\":\"2026-10-18T01:30:00Z\",\"status\":\"paused\"}")!;
        Check(ExamSchedule.Phase(statusPausedExam, afterOriginalEnd) == "已暂停" &&
              ExamSchedule.IsStillRelevant(statusPausedExam, afterOriginalEnd) &&
              !ExamSchedule.IsDue(statusPausedExam, 0, exams[0].StartAt, 120) &&
              !ExamSchedule.IsEndDue(statusPausedExam, 0, exams[0].EndAt, 120),
            "explicit paused status survives JSON parsing and suppresses ordinary alerts");
        Check(ExamSchedule.Changes([exams[0]], [statusPausedExam], s, afterOriginalEnd).Single().Detail.Contains("后台已暂停考试"),
            "explicit paused status triggers the backend pause reminder after original end");
        Check(ExamSchedule.IsStillRelevant(pausedExam, afterOriginalEnd) &&
              ExamSchedule.EffectiveEndAt(pausedExam, afterOriginalEnd) > afterOriginalEnd,
            "paused exam stays visible beyond the original end and extends by the in-flight pause");
        var pausedFromYesterday = new ExamBoardExam { Id = "YesterdayPaused", Name = "Yesterday",
            StartAt = exams[0].StartAt.AddDays(-1), EndAt = exams[0].EndAt.AddDays(-1), PausedAt = exams[0].StartAt };
        Check(ExamSchedule.Today([pausedFromYesterday], s, afterOriginalEnd).Count == 1,
            "paused exam remains in today's schedule after crossing midnight");
        var resumedExam = new ExamBoardExam { Id = pausedExam.Id, Name = pausedExam.Name, StartAt = pausedExam.StartAt,
            EndAt = exams[0].EndAt.AddHours(1), PausedMs = 60 * 60 * 1000 };
        var pauseChanges = ExamSchedule.Changes([pausedExam], [resumedExam], s, afterOriginalEnd.AddMinutes(-10));
        Check(pauseChanges.Count == 1 && pauseChanges[0].Detail.Contains("后台已继续考试"),
            "resume after original end is still reported as a schedule change");
        var resumedAfterPause = new ExamBoardExam { Id = exams[0].Id, Name = exams[0].Name,
            StartAt = exams[0].StartAt, EndAt = exams[0].EndAt.AddHours(1), PausedMs = 60 * 60 * 1000 };
        var pauseAndResumeBetweenSyncs = ExamSchedule.Changes([exams[0]], [resumedAfterPause], s,
            afterOriginalEnd.AddMinutes(-10));
        Check(pauseAndResumeBetweenSyncs.Count == 1 && pauseAndResumeBetweenSyncs[0].Detail.Contains("结束"),
            "pause and resume between syncs still reports the extended end time after original end");
        var pauseNow = exams[0].EndAt.AddMinutes(20);
        var pausedSession = ExamSchedule.Sessions([pausedExam], s, pauseNow).Single();
        Check(pausedSession.End > pauseNow, "exam mode stays active while an exam is paused beyond its planned end");
        var reminderTypes = new PluginSettings { EnableDayBeforeReminder = true };
        ExamSchedule.SetReminderTypeEnabled(reminderTypes, ReminderType.Start, false);
        ExamSchedule.SetReminderTypeEnabled(reminderTypes, ReminderType.ScheduleChange, false);
        Check(!ExamSchedule.IsReminderTypeEnabled(reminderTypes, ReminderType.Start) &&
              !ExamSchedule.IsReminderTypeEnabled(reminderTypes, ReminderType.ScheduleChange) &&
              ExamSchedule.IsReminderTypeEnabled(reminderTypes, ReminderType.Upcoming) &&
              ExamSchedule.IsReminderTypeEnabled(reminderTypes, ReminderType.BeforeEnd) &&
              ExamSchedule.IsReminderTypeEnabled(reminderTypes, ReminderType.Ended) &&
              ExamSchedule.IsReminderTypeEnabled(reminderTypes, ReminderType.DayBefore),
            "reminder type selection maps every supported reminder category");
        var providerControl = new ExamReminderProviderSettingsControl(new PluginSettingsStore(
            Path.Combine(Path.GetTempPath(), "examreminder-provider-settings-" + Guid.NewGuid().ToString("N"))));
        Check(providerControl.Content is StackPanel providerPanel &&
              providerPanel.Children.OfType<CheckBox>().Count() == ExamSchedule.ReminderTypes.Count,
            "notification provider settings expose every reminder type");
        Capture(providerControl, "provider-settings-light.png", ThemeVariant.Light, 420, 650, 0);
        Capture(providerControl, "provider-settings-dark.png", ThemeVariant.Dark, 420, 650, 0);
        Check(PluginSettings.DefaultSuppressedProviderIds.SequenceEqual(new PluginSettings().SuppressedProviders), "default suppressed providers stay in sync");
        Check(ExamSchedule.IsDue(exams[0], 15, exams[0].StartAt.AddMinutes(-15).AddSeconds(30), 120), "late tick within configured grace triggers");
        Check(!ExamSchedule.IsDue(exams[0], 15, exams[0].StartAt.AddMinutes(-5), 120), "expired reminder does not replay");
        Check(ExamSchedule.IsEndDue(exams[0], 15, exams[0].EndAt.AddMinutes(-15).AddSeconds(30), 120), "end reminder triggers before the exam ends");
        Check(!ExamSchedule.IsEndDue(exams[0], 15, exams[0].EndAt.AddMinutes(-5), 120), "expired end reminder does not replay");
        Check(ExamSchedule.IsEndDue(exams[0], 0, exams[0].EndAt.AddSeconds(30), 120), "exam end reminder fires within grace");
        Check(!ExamSchedule.IsEndDue(exams[0], 0, exams[0].EndAt.AddSeconds(121), 120), "exam end reminder expires after grace");
        Check(ExamSchedule.Format("距{考试}结束还有{分钟}分钟，{结束时间}", exams[0], 15, s) == "距Math结束还有15分钟，09:30", "end template supports end time and minute placeholders");
        var tight = new[]
        {
            Exam("TightA", "2026-10-18T08:00+08:00", "2026-10-18T09:30+08:00"),
            Exam("TightB", "2026-10-18T09:40+08:00", "2026-10-18T11:10+08:00")
        };
        Check(ExamSchedule.IsOtherExamInProgress(tight, tight[1], DateTimeOffset.Parse("2026-10-18T09:25+08:00"), TimeSpan.FromSeconds(60)),
            "next exam pre-reminder is blocked while the previous exam is running");
        Check(ExamSchedule.IsOtherExamInProgress(tight, tight[1], DateTimeOffset.Parse("2026-10-18T09:30:30+08:00"), TimeSpan.FromSeconds(60)),
            "next exam pre-reminder stays quiet just after the previous exam ends");
        Check(!ExamSchedule.IsOtherExamInProgress(tight, tight[1], DateTimeOffset.Parse("2026-10-18T09:32+08:00"), TimeSpan.FromSeconds(60)),
            "next exam pre-reminder resumes after the guard window");
        var backToBack = new[]
        {
            Exam("BackA", "2026-10-18T08:00+08:00", "2026-10-18T09:30+08:00"),
            Exam("BackB", "2026-10-18T09:30:30+08:00", "2026-10-18T11:00+08:00")
        };
        Check(ExamSchedule.IsAnotherExamStartingSoon(backToBack, backToBack[0], DateTimeOffset.Parse("2026-10-18T09:30:10+08:00"), TimeSpan.FromSeconds(60)),
            "exam end reminder yields to a back-to-back exam starting soon");
        Check(!ExamSchedule.IsAnotherExamStartingSoon(tight, tight[0], tight[0].EndAt, TimeSpan.FromSeconds(60)),
            "exam end reminder is kept when the next exam starts later");
        var movedEnd = new ExamBoardExam { Id = exams[0].Id, Name = exams[0].Name, StartAt = exams[0].StartAt, EndAt = exams[0].EndAt.AddMinutes(10) };
        Check(ExamSchedule.EndActionKey(exams[0], "end-15") != ExamSchedule.EndActionKey(movedEnd, "end-15"), "end reminder deduplication follows changed end time");
        var diffNow = DateTimeOffset.Parse("2026-10-18T07:00+08:00");
        var updatedSchedule = new[]
        {
            new ExamBoardExam { Id = "Math", Name = "数学", StartAt = exams[0].StartAt.AddMinutes(15), EndAt = exams[0].EndAt.AddMinutes(15) },
            exams[1],
            exams[2],
            new ExamBoardExam { Id = "New", Name = "新增考试", StartAt = exams[1].StartAt, EndAt = exams[1].EndAt }
        };
        var scheduleChanges = ExamSchedule.Changes(exams, updatedSchedule, s, diffNow);
        Check(scheduleChanges.Count == 2 && scheduleChanges.Any(x => x.Detail.Contains("开考") && x.Detail.Contains("结束")) &&
              scheduleChanges.Any(x => x.Detail.Contains("新增")), "schedule diff reports changed and newly added future exams");
        Check(ExamSchedule.Changes(exams, exams, s, diffNow).Count == 0, "unchanged schedule produces no change reminder");
        Check(ExamSchedule.Changes([], [Exam("Physics", "2026-10-17T08:00+08:00", "2026-10-17T09:30+08:00")], s, diffNow).Count == 0,
            "past exams are excluded from change detection");
        var cancelled = ExamSchedule.Changes(exams, [exams[0]], s, diffNow);
        Check(cancelled.Count == 2 && cancelled.All(x => x.Detail.StartsWith("取消 原定") && x.Key.Contains("|removed|")) &&
              cancelled.Any(x => x.Title == "Language"), "future exams missing from the new schedule are reported as cancelled");
        var lifecycle = new ExamBoardLifecycleState { ReminderKey = "major:ended-major:Math", MajorExamId = "ended-major", Status = "ended",
            ChangedAt = DateTimeOffset.Parse("2026-10-18T07:30+08:00") };
        var lifecycleExam = new ExamBoardExam { Id = exams[0].Id, Name = exams[0].Name, StartAt = exams[0].StartAt,
            EndAt = exams[0].EndAt, ReminderKey = lifecycle.ReminderKey };
        var lifecycleChange = ExamSchedule.Changes([lifecycleExam], [], s, diffNow, [lifecycle], true);
        Check(lifecycleChange.Count == 1 && lifecycleChange[0].Detail.Contains("后台提前结束") &&
              !lifecycleChange[0].Detail.StartsWith("取消"), "lifecycle state distinguishes early end from schedule cancellation");
        Check(ExamSchedule.Changes([exams[0]], [], s, diffNow, [], true).Count == 0,
            "explicit lifecycle protocol does not mislabel a scope removal as cancellation");
        Check(ExamSchedule.Changes([Exam("Done", "2026-10-17T08:00+08:00", "2026-10-17T09:30+08:00")], [], s, diffNow).Count == 0,
            "finished exams dropped from the schedule are not reported as cancelled");
        Check(!ExamSchedule.Changes(exams, [new ExamBoardExam { Id = "Math", Name = "Math",
                StartAt = exams[0].StartAt.AddHours(-3), EndAt = exams[0].EndAt.AddHours(-3) }], s, diffNow).Any(x => x.Title == "Math"),
            "an exam that is still listed is never reported as cancelled");
        Check(ExamSchedule.FormatChange("{变更}", scheduleChanges, s).Contains("数学"), "change reminder keeps individual details below the summary threshold");
        Check(ExamSchedule.FormatChange("{变更}", [.. scheduleChanges, .. scheduleChanges, .. scheduleChanges], s).Contains("共 6 处变更"),
            "multiple schedule changes are summarized");
        var tomorrowExams = ExamSchedule.Tomorrow(new[]
        {
            Exam("Tomorrow", "2026-10-19T08:00+08:00", "2026-10-19T09:30+08:00"),
            Exam("Today", "2026-10-18T10:00+08:00", "2026-10-18T11:00+08:00")
        }, s, DateTimeOffset.Parse("2026-10-18T10:00+08:00"));
        Check(tomorrowExams.Count == 1 && tomorrowExams[0].Name == "Tomorrow", "day-before preview uses school-local tomorrow");
        Check(ExamSchedule.FormatDayBefore("{数量} {首场} {首场时间} {末场} {末场时间}", tomorrowExams, s) ==
            "1 Tomorrow 10-19 08:00 Tomorrow 10-19 08:00", "day-before template exposes first and last exams");
        var invalidTimeSettings = new PluginSettings { DayBeforeReminderTime = "25:00" };
        PluginSettingsStore.Normalize(invalidTimeSettings);
        Check(invalidTimeSettings.DayBeforeReminderTime == "17:30" && !invalidTimeSettings.EnableDayBeforeReminder,
            "day-before preview is opt-in and falls back to 17:30");
        s.ReminderMinutes = []; s.EndReminderMinutes = []; s.EnableExamEndReminder = false; s.SyncSeconds = 1; PluginSettingsStore.Normalize(s);
        Check(s.ReminderMinutes.Count == 0 && s.EndReminderMinutes.Count == 0 && !s.EnableExamEndReminder, "empty reminder lists stay disabled; sync interval is bounded");
        var defaults = new PluginSettings(); PluginSettingsStore.Normalize(defaults);
        Check(defaults.EndReminderMinutes!.SequenceEqual([15, 5]) && defaults.EnableBeforeStartReminder &&
              defaults.EnableStartReminder && defaults.EnableBeforeEndReminder && defaults.EnableExamEndReminder &&
              defaults.EnableExamChangeReminder, "exam reminder types are enabled by default");
        Check(defaults.EnableExamChangeReminder && defaults.ExamChangeTemplate.Contains("{变更}"), "exam change reminders are enabled by default");
        var folder = Path.Combine(Path.GetTempPath(), "exam-reminder-check-" + Guid.NewGuid().ToString("N"));
        var store = new PluginSettingsStore(folder);
        store.Update(x => { x.BaseUrl = "http://exam-server"; x.ReminderMinutes = [30, 10]; });
        store.MarkCompletedAction("exam|start");
        var reopened = new PluginSettingsStore(folder);
        Check(reopened.Settings.BaseUrl == "http://exam-server" && reopened.HasCompletedAction("exam|start"), "atomic config and action dedup survive restart");
        File.WriteAllText(Path.Combine(folder, "Settings.json"), "broken");
        var damaged = new PluginSettingsStore(folder);
        Check(damaged.SaveError is not null && File.ReadAllText(Path.Combine(folder, "Settings.json")) == "broken", "damaged config is reported and not overwritten on load");
    }

    private static async Task OnlineProtocolChecks()
    {
        const string pairToken = "A7D8fG1hJ3kL5mN7pQ9rS1tV3wX5yZ7aB9cD1eF3gHk";
        var folder = Path.Combine(Path.GetTempPath(), "exam-reminder-online-protocol-" + Guid.NewGuid().ToString("N"));
        var store = new PluginSettingsStore(folder);
        store.Update(s => { s.ServiceMode = "online"; s.BaseUrl = "https://online.example.test"; s.PendingPairToken = pairToken; });
        Check(store.Settings.ClientSecret.Length == 64 && store.Settings.ClientSecret.All(Uri.IsHexDigit),
            "plugin instance secrets keep the school server's existing hexadecimal format");
        using var handler = new ProtocolHandler();
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new ExamBoardClient(store, http);
        var pairExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();

        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true,
            pairUrl = $"https://online.example.test/online/plugin/connect?token={Uri.EscapeDataString(pairToken)}", expiresAt = pairExpiresAt }));
        var start = await client.StartPairingAsync(store.Settings.BaseUrl, store.Settings.PluginInstanceId,
            store.Settings.ClientSecret, pairToken, online: true);
        Check(start.IsSuccess && start.Value!.PairUrl.AbsolutePath == "/online/plugin/connect" &&
              start.Value.ExpiresAt == pairExpiresAt && handler.LastUri!.AbsolutePath == "/api/online" &&
              !start.Value.PairUrl.Query.Contains(store.Settings.ClientSecret, StringComparison.Ordinal),
            "online pair start uses the online API and accepts the returned HTTPS pairing URL");
        using (var body = JsonDocument.Parse(handler.LastBody!))
        {
            Check(body.RootElement.GetProperty("action").GetString() == "plugin-pair-start" &&
                  body.RootElement.GetProperty("pairToken").GetString() == pairToken &&
                  !body.RootElement.TryGetProperty("apiVersion", out _),
                "online pairing request uses the online-specific contract");
        }

        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true,
            pairUrl = $"https://attacker.example.test/online/plugin/connect?token={Uri.EscapeDataString(pairToken)}", expiresAt = 1_800_000_000_000L }));
        Check(!(await client.StartPairingAsync(store.Settings.BaseUrl, store.Settings.PluginInstanceId,
            store.Settings.ClientSecret, pairToken, online: true)).IsSuccess,
            "online pairing rejects a URL hosted outside the configured service");
        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true,
            pairUrl = $"https://online.example.test/online/plugin/connect?token={Uri.EscapeDataString(pairToken)}&token=other", expiresAt = 1_800_000_000_000L }));
        Check(!(await client.StartPairingAsync(store.Settings.BaseUrl, store.Settings.PluginInstanceId,
            store.Settings.ClientSecret, pairToken, online: true)).IsSuccess,
            "online pairing rejects duplicate token parameters without throwing");

        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, status = "pending", expiresAt = 1_800_000_000_000L }));
        var pairStatus = await client.GetPairStatusAsync(store.Settings);
        Check(pairStatus.IsSuccess && pairStatus.Value!.Status == "pending" &&
              handler.LastBody!.Contains("\"action\":\"plugin-pair-status\"", StringComparison.Ordinal) &&
              handler.LastBody.Contains(pairToken, StringComparison.Ordinal),
            "online pair polling sends the instance credentials and pending pair token");
        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, status = "confirmed",
            expiresAt = 1_800_000_000_000L, deviceId = "device-123" }));
        pairStatus = await client.GetPairStatusAsync(store.Settings);
        Check(pairStatus.IsSuccess && pairStatus.Value!.Status == "confirmed" && pairStatus.Value.DeviceId == "device-123",
            "online pairing exposes a confirmed device only after website authorization");
        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, status = "expired", expiresAt = 1_700_000_000_000L }));
        pairStatus = await client.GetPairStatusAsync(store.Settings);
        Check(pairStatus.IsSuccess && pairStatus.Value!.Status == "expired" && pairStatus.Value.DeviceId is null,
            "online pairing reports expiry without returning a device identity");

        var serverTime = DateTimeOffset.Parse("2026-11-14T04:05:06Z").ToUnixTimeMilliseconds();
        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, apiVersion = 1,
            capabilities = new[] { "online-exam-v1" }, serverTime, version = "exam-123:4", changed = true,
            exam = new { id = "exam-123", title = "期中考试", timezone = "Asia/Shanghai", items = new[] {
                new { id = "subject-1", name = "数学", startAt = "2026-11-14T08:00:00+08:00", endAt = "2026-11-14T09:30:00+08:00", note = "一号楼", sourceName = "期中考试" }
            } } }));
        var bootstrap = await client.GetBootstrapAsync(store.Settings);
        Check(bootstrap.IsSuccess && bootstrap.Value!.ServerTime.ToUnixTimeMilliseconds() == serverTime &&
              bootstrap.Value.Version == "exam-123:4" && bootstrap.Value.TimeZoneId == "Asia/Shanghai" &&
              bootstrap.Value.Exams.Single().Name == "数学" && bootstrap.Value.Exams.Single().Note == "一号楼" &&
              bootstrap.Value.Exams.Single().StartAt == DateTimeOffset.Parse("2026-11-14T08:00:00+08:00"),
            "online published snapshot maps Unix server time, timezone and ISO exam times");

        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, apiVersion = 1,
            capabilities = new[] { "online-exam-v1" }, serverTime, version = "exam-123:4", changed = false }));
        var unchanged = await client.GetBootstrapAsync(store.Settings, "exam-123:4");
        Check(unchanged.IsSuccess && !unchanged.Value!.Changed && unchanged.Value.Exams.Count == 0 &&
              handler.LastBody!.Contains("\"knownVersion\":\"exam-123:4\"", StringComparison.Ordinal),
            "online unchanged bootstrap preserves opaque knownVersion without requiring an exam body");

        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, apiVersion = 1,
            capabilities = Array.Empty<string>(), serverTime, version = "exam-123:4", changed = false }));
        Check(!(await client.GetBootstrapAsync(store.Settings, "exam-123:4")).IsSuccess,
            "online bootstrap rejects responses missing the required capability");

        store.Update(s => s.ServiceMode = "online");
        handler.Failures.Enqueue((HttpStatusCode.Forbidden,
            "{\"ok\":false,\"code\":\"ENTITLEMENT_SUSPENDED\",\"error\":\"Suspended\"}"));
        var suspended = await client.GetBootstrapAsync(store.Settings, "exam-123:4");
        Check(!suspended.IsSuccess && suspended.ErrorCode == "ENTITLEMENT_SUSPENDED" &&
              suspended.Error?.Contains("已暂停", StringComparison.Ordinal) == true,
            "online service errors retain stable codes and localized entitlement feedback");

        store.Update(s => s.ServiceMode = "school");
        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true, apiVersion = 2, schemaVersion = 2,
            capabilities = new[] { "conditional-sync-v2" }, serverTime = DateTimeOffset.FromUnixTimeMilliseconds(serverTime),
            version = "school-v2", changed = false, viewerOnline = true, binding = new { classTag = "高一（3）班" }, exams = Array.Empty<object>() }));
        var school = await client.GetBootstrapAsync(store.Settings, "school-v1");
        Check(school.IsSuccess && handler.LastUri!.AbsolutePath == "/api/exams" &&
              handler.LastBody!.Contains("\"apiVersion\":2", StringComparison.Ordinal) &&
              handler.LastBody.Contains("\"includeCompletedToday\":true", StringComparison.Ordinal),
            "school mode keeps its original API path and request fields");
        handler.Responses.Enqueue(JsonSerializer.Serialize(new { ok = true }));
        const string schoolPairToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var schoolPair = await client.StartPairingAsync(store.Settings.BaseUrl, store.Settings.PluginInstanceId,
            store.Settings.ClientSecret, schoolPairToken);
        Check(schoolPair.IsSuccess && schoolPair.Value!.PairUrl.AbsolutePath == "/plugin/connect" &&
              handler.LastUri!.AbsolutePath == "/api/exams" &&
              handler.LastBody!.Contains("\"apiVersion\":2", StringComparison.Ordinal) &&
              handler.LastBody.Contains(schoolPairToken, StringComparison.Ordinal),
            "school mode keeps its original pair endpoint, response URL and API version field");

        var insecure = await client.StartPairingAsync("http://online.example.test", store.Settings.PluginInstanceId,
            store.Settings.ClientSecret, pairToken, online: true);
        Check(!insecure.IsSuccess && handler.Calls == 12,
            "online mode refuses HTTP before making a network request");
    }

    private static void HostChecks(string hostFolder)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(hostFolder, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostFolder, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(hostFolder, "ClassIsland.dll"));
        var settingsType = assembly.GetType("ClassIsland.Models.Settings", true)!;
        var settingsServiceType = assembly.GetType("ClassIsland.Services.SettingsService", true)!;
        var settingsService = RuntimeHelpers.GetUninitializedObject(settingsServiceType);
        settingsServiceType.GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settingsService, Activator.CreateInstance(settingsType));
        var hostType = assembly.GetType("ClassIsland.Services.NotificationHostService", true)!;
        var logger = typeof(NullLogger<>).MakeGenericType(hostType).GetField("Instance")!.GetValue(null);
        var workerType = assembly.GetType("ClassIsland.Services.NotificationWorkerService", true)!;
        var worker = RuntimeHelpers.GetUninitializedObject(workerType);
        var testPatch = new HarmonyLib.Harmony("exam-reminder.test-worker");
        testPatch.Patch(workerType.GetMethod("CreateTicket")!, prefix: new HarmonyLib.HarmonyMethod(typeof(WorkerStub).GetMethod("CreateTicket")));
        var notificationHost = (INotificationHostService)Activator.CreateInstance(hostType, settingsService, logger, worker)!;
        var folder = Path.Combine(Path.GetTempPath(), "exam-reminder-host-" + Guid.NewGuid().ToString("N"));
        var store = new PluginSettingsStore(folder);
        using var appHost = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(store); services.AddSingleton(notificationHost); services.AddSingleton<ExamReminderProvider>();
            services.AddNotificationProvider<ExamReminderProvider>();
        }).Build();
        IAppHost.Host = appHost;
        var provider = appHost.Services.GetRequiredService<ExamReminderProvider>();
        using var suppression = new ReminderSuppressionService(store, notificationHost);
        Check(suppression.Supported, "actual ClassIsland 2.1.0.1 host adapter installs");
        var show = hostType.GetMethod("ShowNotification")!;
        var course = Guid.Parse(store.Settings.SuppressedProviders[0]);
        var emergency = Guid.NewGuid();
        suppression.SetActive(true);
        var blocked = new NotificationRequest();
        show.Invoke(notificationHost, [blocked, course, Guid.Empty, true, false]);
        Check(blocked.CancellationToken.IsCancellationRequested && blocked.CompletedToken.IsCancellationRequested, "selected source is cancelled and async completion is signalled");
        var chain = new[] { new NotificationRequest(), new NotificationRequest() };
        hostType.GetMethod("ShowChainedNotifications")!.Invoke(notificationHost, [chain, course, Guid.Empty]);
        Check(chain.All(x => x.CompletedToken.IsCancellationRequested), "selected chained reminders complete without hanging");
        var allowed = new NotificationRequest();
        show.Invoke(notificationHost, [allowed, emergency, Guid.Empty, true, false]);
        Check(!allowed.CancellationToken.IsCancellationRequested, "unselected emergency source is preserved");
        var own = new NotificationRequest();
        store.Update(s => s.SuppressedProviders = s.SuppressedProviders.Append(ExamReminderProvider.ProviderId).ToList());
        show.Invoke(notificationHost, [own, Guid.Parse(ExamReminderProvider.ProviderId), Guid.Empty, true, false]);
        Check(!own.CancellationToken.IsCancellationRequested, "exam source cannot suppress itself");
        store.Update(s => s.SuppressedProviders = s.SuppressedProviders.Where(x => x != ExamReminderProvider.ProviderId).ToList());
        suppression.SetActive(false);
        var restored = new NotificationRequest();
        show.Invoke(notificationHost, [restored, course, Guid.Empty, true, false]);
        Check(!restored.CancellationToken.IsCancellationRequested, "ordinary reminders resume on mode exit");
        suppression.SetActive(true); Dispatcher.UIThread.RunJobs();
        Check(restored.CancellationToken.IsCancellationRequested && !allowed.CancellationToken.IsCancellationRequested, "mode entry cancels already queued course reminders and preserves emergency requests");
        suppression.SetActive(false);

        using var server = new FakeServer();
        store.Update(s => { s.BaseUrl = server.Url; s.IsPaired = true; s.AutoOpenBoard = false; s.EnableAnnouncements = false; });
        using var client = new ExamBoardClient(store);
        using var sync = new ExamSyncService(store, client, provider, new ExternalUriLauncher(), suppression);
        sync.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Until(() => store.Settings.LastSyncAt is not null, "initial full sync");
        Check(sync.Exams.Count == 2, "full sync stores entire session schedule");
        sync.RequestImmediateSync();
        Until(() => server.ConditionalCalls > 0, "conditional sync request");
        Until(() => sync.ViewerStatusKnown, "conditional response applied");
        Check(sync.Exams.Count == 2 && sync.SyncMode == "轻量同步", "unchanged response preserves complete schedule");
        var total = server.Calls;
        Thread.Sleep(1200); Dispatcher.UIThread.RunJobs();
        Check(server.Calls == total, "local ticks do not issue extra network requests");
        server.Fail = true; sync.RequestImmediateSync();
        Until(() => sync.UsingCache, "offline cache");
        Check(sync.Exams.Count == 2 && !sync.ViewerStatusKnown, "network failure preserves cache and does not assert viewer offline");
        Check(sync.IsOfflineData && sync.OfflineWarning.Contains("离线数据") && sync.DataAge is not null, "offline cache exposes a data warning");
        using var announcements = new AnnouncementService(store);
        server.Fail = false;
        store.Update(s => s.EnableAnnouncements = true);
        announcements.Refresh(); announcements.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        Until(() => announcements.Items.Count == 2, "author announcements load and expired notices are hidden");
        Check(announcements.UnreadCount == 2, "new announcements are unread");
        announcements.MarkRead(announcements.Items[0]);
        Check(announcements.UnreadCount == 1, "opening an announcement records its version as read");
        server.Fail = true; announcements.Refresh();
        Until(() => announcements.Status.Contains("缓存"), "announcement request failure");
        Check(announcements.Status.Contains("HTTP 503") && !announcements.Status.Contains("Response status code"), "announcement failure is localized");
        Check(announcements.LastErrorDetail.Length > 0 && announcements.LastAttemptAt is not null, "announcement failure keeps diagnostic detail and attempt time");
        Check(announcements.Items.Count == 2 && announcements.UnreadCount == 1, "announcement failure preserves cached content and read state");
        server.Fail = false; announcements.Refresh();
        Until(() => announcements.Status.Contains("已更新"), "announcement recovers after failure");
        Check(announcements.LastErrorDetail.Length == 0, "announcement recovery clears diagnostic detail");
        Check(sync.LastAttemptAt is not null && sync.DataValidatedAt is not null, "sync exposes last attempt and validated time");
        sync.RequestImmediateSync();
        Until(() => sync.ViewerStatusKnown && !sync.UsingCache, "sync recovers after failure for overview");
        Check(!sync.IsOfflineData && sync.OfflineWarning.Length == 0, "successful sync clears the offline data marker");
        var page = new ExamReminderSettingsPage(store, client, sync, new ExternalUriLauncher(), suppression, announcements);
        Capture(page, "overview-light.png", ThemeVariant.Light, 1040, 1100, 0);
        Capture(page, "overview-dark.png", ThemeVariant.Dark, 1040, 1100, 0);
        Capture(page, "overview-narrow-dark.png", ThemeVariant.Dark, 620, 1100, 0);
        Capture(page, "settings-dark.png", ThemeVariant.Dark, 1040, 1000, 1);
        Capture(page, "settings-narrow.png", ThemeVariant.Light, 620, 1000, 1);
        Capture(page, "announcements-light.png", ThemeVariant.Light, 1040, 900, 2);
        Capture(page, "announcements-narrow-dark.png", ThemeVariant.Dark, 620, 900, 2);
        announcements.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        sync.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var restarted = new ExamSyncService(new PluginSettingsStore(folder), client, provider, new ExternalUriLauncher(), suppression);
        Check(restarted.Exams.Count == 2 && restarted.UsingCache, "offline restart restores scoped schedule cache");
        store.Update(s => s.BaseUrl = "http://another-server");
        using var other = new ExamSyncService(store, client, provider, new ExternalUriLauncher(), suppression);
        Check(other.Exams.Count == 0, "another server never receives previous schedule cache");
    }

    private static void Until(Func<bool> ready, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!ready() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Check(ready(), description);
    }

    private static void Capture(Control page, string name, ThemeVariant theme, int width, int height, int tab)
    {
        Application.Current!.RequestedThemeVariant = theme;
        var window = new Window { Width = width, Height = height, Content = page,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(theme == ThemeVariant.Light ? "#FAFAFC" : "#202024")) };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var tabs = Find<TabControl>(page); if (tabs is not null) tabs.SelectedIndex = tab;
        Dispatcher.UIThread.RunJobs();
        if (tab == 2 && Find<Expander>(page) is {} article)
        {
            article.IsExpanded = true;
            var animationEnd = DateTime.UtcNow.AddMilliseconds(450);
            while (DateTime.UtcNow < animationEnd) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); Thread.Sleep(15); }
        }
        page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height)); Dispatcher.UIThread.RunJobs();
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window);
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../outputs/ui"));
        Directory.CreateDirectory(output); bitmap.Save(Path.Combine(output, name));
        window.Content = null; window.Close();
        Check(File.Exists(Path.Combine(output, name)), "render " + name);
    }
    private static T? Find<T>(Control root) where T : Control
    {
        if (root is T result) return result;
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root).OfType<Control>())
            if (Find<T>(child) is {} match) return match;
        return null;
    }
}

public sealed class CheckApp : Application
{
    public override void Initialize() => Styles.Add(new FluentAvaloniaTheme());
}

public static class WorkerStub
{
    public static bool CreateTicket(NotificationRequest __0, ref NotificationPlayingTicket __result)
    {
        var ticket = (NotificationPlayingTicket)Activator.CreateInstance(typeof(NotificationPlayingTicket), nonPublic: true)!;
        typeof(NotificationPlayingTicket).GetProperty("Request")!.SetValue(ticket, __0);
        typeof(NotificationPlayingTicket).GetProperty("Settings")!.SetValue(ticket, new ClassIsland.Shared.Models.Notification.NotificationSettings());
        typeof(NotificationPlayingTicket).GetProperty("CancellationTokenSource")!.SetValue(ticket, new CancellationTokenSource());
        typeof(NotificationPlayingTicket).GetProperty("CancellationCompletedCompletionSource")!.SetValue(ticket, new TaskCompletionSource());
        typeof(NotificationPlayingTicket).GetProperty("ProcessMask")!.SetValue(ticket, (Func<Task>)(() => Task.CompletedTask));
        typeof(NotificationPlayingTicket).GetProperty("ProcessOverlay")!.SetValue(ticket, (Func<Task>)(() => Task.CompletedTask));
        __result = ticket;
        return false;
    }
}

internal sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string Url { get; }
    public int Calls;
    public int ConditionalCalls;
    public volatile bool Fail;
    public FakeServer()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        Url = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(Url + "/"); _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    if (context.Request.HttpMethod == "GET")
                    {
                        context.Response.ContentType = "application/json";
                        if (Fail) context.Response.StatusCode = 503;
                        var noticeTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var notice = JsonSerializer.Serialize(new { ok = !Fail, announcements = new[] {
                            new { id = 1, title = "考试提醒 0.3.1", content = "## 更新说明\n\n支持考试语音、自动考试模式和低负载同步。", pinned = true, updated_at = noticeTime, unpublish_at = (long?)null },
                            new { id = 2, title = "内网部署说明", content = "支持 HTTP 内网地址。", pinned = false, updated_at = noticeTime, unpublish_at = (long?)null },
                            new { id = 3, title = "已到期", content = "expired", pinned = false, updated_at = noticeTime, unpublish_at = (long?)(noticeTime - 1) }
                        }});
                        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(notice)); context.Response.Close(); continue;
                    }
                    using var reader = new StreamReader(context.Request.InputStream);
                    var request = JsonDocument.Parse(await reader.ReadToEndAsync());
                    var known = request.RootElement.TryGetProperty("knownVersion", out var version) && version.ValueKind == JsonValueKind.String;
                    if (known) Interlocked.Increment(ref ConditionalCalls);
                    Interlocked.Increment(ref Calls);
                    context.Response.ContentType = "application/json";
                    if (Fail) context.Response.StatusCode = 503;
                    var now = DateTimeOffset.UtcNow;
                    var exams = new[]
                    {
                        new { id = "math", name = "数学", startAt = now.AddMinutes(25), endAt = now.AddMinutes(85) },
                        new { id = "language", name = "语文", startAt = now.AddMinutes(100), endAt = now.AddMinutes(160) }
                    };
                    var payload = JsonSerializer.Serialize(new { ok = !Fail, version = "v1", changed = !known, serverTime = now, apiVersion = 2,
                        viewerOnline = true, binding = new { classTag = "高一（3）班" }, exams = known ? null : exams });
                    var bytes = Encoding.UTF8.GetBytes(payload); await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
                }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });
    }
    public void Dispose() { _listener.Stop(); _listener.Close(); }
}

internal sealed class ProtocolHandler : HttpMessageHandler
{
    public Queue<string> Responses { get; } = new();
    public Queue<(HttpStatusCode Status, string Body)> Failures { get; } = new();
    public Uri? LastUri { get; private set; }
    public string? LastBody { get; private set; }
    public int Calls { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        LastUri = request.RequestUri;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri!.AbsolutePath is not ("/api/online" or "/api/exams"))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (Failures.Count > 0)
        {
            var failure = Failures.Dequeue();
            return new HttpResponseMessage(failure.Status)
            {
                Content = new StringContent(failure.Body, Encoding.UTF8, "application/json")
            };
        }
        if (Responses.Count == 0) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json")
        };
    }
}
