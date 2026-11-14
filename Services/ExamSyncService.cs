using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ClassIsland.ExamReminder.Models;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ExamReminder.Services;

public sealed record ExamCache(string Scope, DateTimeOffset CheckedAt, ExamBoardBootstrapResponse Snapshot);

public sealed class ExamSyncService : BackgroundService
{
    private readonly PluginSettingsStore _store;
    private readonly ExamBoardClient _client;
    private readonly ExamReminderProvider _provider;
    private readonly ExternalUriLauncher _launcher;
    private readonly ReminderSuppressionService _suppression;
    private volatile ExamBoardBootstrapResponse? _snapshot;
    private DateTimeOffset _validatedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFullSync = DateTimeOffset.MinValue;
    private sealed record ClockAnchor(DateTimeOffset ServerTime, long Timestamp);
    private volatile ClockAnchor? _clock;
    private long _generation;
    private long _syncRequestVersion;
    private long _nextSyncMs;
    private Task? _networkTask;
    private int _failures;
    private int _offlineChecks;
    private int _reopens;
    private DateTimeOffset _nextOpen = DateTimeOffset.MinValue;
    private string _precheckKey = "";
    private string _modeDescription = "";
    private static readonly TimeSpan ConsecutiveReminderGuard = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PreStartCheckWindow = TimeSpan.FromMinutes(1);
    public bool UsingCache { get; private set; }
    public bool ViewerStatusKnown { get; private set; }
    public string SyncMode { get; private set; } = "尚未同步";
    public string RuntimeError { get; private set; } = "";
    public long SuccessfulChecks { get; private set; }
    public long FailedChecks { get; private set; }
    public long BrowserOpenAttempts { get; private set; }
    public double LastRequestMilliseconds { get; private set; }
    /// <summary>最近一次网络尝试时间；失败时也会推进，用于和最近成功同步区分。</summary>
    public DateTimeOffset? LastAttemptAt { get; private set; }
    /// <summary>当前考试快照在校正后的时间轴上完成校验的时刻。</summary>
    public DateTimeOffset? DataValidatedAt => _validatedAt == DateTimeOffset.MinValue ? null : _validatedAt;
    public TimeSpan? DataAge => DataValidatedAt is {} at ? Now - at : null;
    /// <summary>同步失败或数据超过两个检查周期时，仍可使用快照，但提醒会标注离线数据。</summary>
    public bool IsOfflineData
    {
        get
        {
            if (_snapshot is null || DataValidatedAt is not {} at) return false;
            var threshold = TimeSpan.FromMinutes(Math.Max(5, _store.Settings.SyncSeconds * 2 / 60.0));
            return UsingCache || Now - at > threshold;
        }
    }
    public string OfflineWarning => IsOfflineData && DataValidatedAt is {} at
        ? $"离线数据 · 最后同步 {SchoolTime(at)}"
        : "";
    public ExamSession? CurrentSession { get; private set; }
    public ExamSession? NextSession { get; private set; }
    public IReadOnlyList<ExamBoardExam> Exams => _snapshot?.Exams ?? [];
    public string ModeDescription => _modeDescription;
    public DateTimeOffset? NextSyncAt => (_store.Settings.EnableAutoSync || !_store.Settings.IsPaired) && Interlocked.Read(ref _nextSyncMs) is > 0 and < long.MaxValue ? DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _nextSyncMs)) : null;
    public DateTimeOffset Now => _clock is {} clock
        ? clock.ServerTime.Add(Stopwatch.GetElapsedTime(clock.Timestamp))
        : DateTimeOffset.UtcNow.AddMilliseconds(_store.Settings.ServerOffsetMilliseconds);

    private string SchoolTime(DateTimeOffset value)
    {
        try { return TimeZoneInfo.ConvertTime(value, ExamSchedule.TimeZone(_store.Settings)).ToString("MM-dd HH:mm"); }
        catch { return value.ToLocalTime().ToString("MM-dd HH:mm"); }
    }

    public ExamSyncService(PluginSettingsStore store, ExamBoardClient client, ExamReminderProvider provider, ExternalUriLauncher launcher, ReminderSuppressionService suppression)
    {
        _store = store; _client = client; _provider = provider; _launcher = launcher; _suppression = suppression;
        try
        {
            var cache = store.Read<ExamCache>("Exams.cache.json");
            if (store.Settings.IsPaired && store.Settings.UseOfflineCache && cache is not null && cache.Scope == Scope(store.Settings) &&
                cache.CheckedAt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - cache.CheckedAt <= TimeSpan.FromHours(store.Settings.CacheMaxHours))
            {
                _snapshot = cache.Snapshot;
                _validatedAt = cache.CheckedAt;
                UsingCache = true;
            }
        }
        catch { RuntimeError = "考试缓存不可用，等待联网同步"; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                if ((_store.Settings.EnableAutoSync || !_store.Settings.IsPaired || Interlocked.Read(ref _nextSyncMs) == 0) &&
                    (_networkTask is null || _networkTask.IsCompleted) && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= Interlocked.Read(ref _nextSyncMs))
                    _networkTask = SynchronizeSafelyAsync(stoppingToken);
                try { EvaluateSchedule(); }
                catch (Exception ex) { RuntimeError = $"本地调度异常：{ex.Message}"; _suppression.SetActive(false); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _suppression.SetActive(false);
            if (_networkTask is not null) await _networkTask;
        }
    }

    public void RequestImmediateSync(bool full = false)
    {
        Interlocked.Increment(ref _syncRequestVersion);
        if (full) _lastFullSync = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _nextSyncMs, 0);
    }

    public void Reschedule() => Schedule(_store.Settings.SyncSeconds);

    public void ResetConnection()
    {
        Interlocked.Increment(ref _generation);
        _snapshot = null;
        CurrentSession = NextSession = null;
        _suppression.SetActive(false);
        _failures = _offlineChecks = _reopens = 0;
        _nextOpen = DateTimeOffset.MinValue;
        _lastFullSync = DateTimeOffset.MinValue;
        _clock = null;
        ViewerStatusKnown = false;
        UsingCache = false;
        _store.Update(s => { s.SkippedSessionKey = ""; s.CompletedActions = s.CompletedActions.Where(x => x.Count(c => c == '|') != 2).ToList(); });
        RequestImmediateSync(true);
    }

    public void SkipCurrentSession()
    {
        if (CurrentSession is {} session) _store.Update(s => s.SkippedSessionKey = session.Key);
        EvaluateSchedule();
    }

    public bool TryShowCurrentOrNextExamReminder(out string error)
    {
        var exam = Exams.Where(x => ExamSchedule.IsStillRelevant(x, Now))
            .OrderBy(x => x.StartAt).FirstOrDefault();
        if (exam is null) { error = "没有可用于测试的考试，请先同步考试数据"; return false; }
        _provider.ShowExamReminder(exam, exam.StartAt - Now, ExamReminderKind.BeforeStart, OfflineWarning);
        error = "";
        return true;
    }

    private async Task SynchronizeSafelyAsync(CancellationToken token)
    {
        var generation = Interlocked.Read(ref _generation);
        var requestVersion = Interlocked.Read(ref _syncRequestVersion);
        // An independent copy prevents pairing edits from changing an in-flight request.
        var current = _store.Settings;
        var settings = new PluginSettings { ServiceMode = current.ServiceMode, BaseUrl = current.BaseUrl, PluginInstanceId = current.PluginInstanceId,
            ClientSecret = current.ClientSecret, IsPaired = current.IsPaired, PendingPairToken = current.PendingPairToken,
            PairExpiresAt = current.PairExpiresAt, BoundDeviceId = current.BoundDeviceId, OnlineTimeZoneId = current.OnlineTimeZoneId };
        Interlocked.Exchange(ref _nextSyncMs, long.MaxValue);
        LastAttemptAt = DateTimeOffset.Now;
        try
        {
            if (string.IsNullOrWhiteSpace(settings.BaseUrl)) { Schedule(60); return; }
            if (!settings.IsPaired || (settings.ServiceMode == "online" && !string.IsNullOrWhiteSpace(settings.PendingPairToken)))
            {
                if (string.IsNullOrWhiteSpace(settings.PendingPairToken)) { Schedule(60); return; }
                if (settings.ServiceMode != "online" && settings.PairExpiresAt <= DateTimeOffset.Now)
                {
                    _store.Update(s => { s.PendingPairToken = ""; s.PairExpiresAt = null; s.LastStatus = "浏览器绑定已过期，请重新连接"; });
                    Schedule(60); return;
                }
                var pair = await _client.GetPairStatusAsync(settings, token);
                if (generation != Interlocked.Read(ref _generation)) return;
                if (!pair.IsSuccess || pair.Value?.Ok != true)
                {
                    if (settings.ServiceMode == "online" && pair.ErrorCode is ("PAIR_EXPIRED" or "PAIR_NOT_FOUND"))
                    {
                        _store.Update(s => { s.PendingPairToken = ""; s.PairExpiresAt = null; s.LastStatus = pair.Error ?? "网页配对已过期，请重新连接"; });
                        if (!settings.IsPaired) { Schedule(60); return; }
                    }
                    else { Failure(pair.Error ?? "无法查询配对状态", pair.RetryAfter); return; }
                }
                else if (settings.ServiceMode == "online")
                {
                    var pairStatus = pair.Value.Status;
                    if (pairStatus == "pending")
                    {
                        Status("等待网页确认考试绑定");
                        if (!settings.IsPaired) { Schedule(5, false); return; }
                    }
                    else if (pairStatus == "expired")
                    {
                        _store.Update(s => { s.PendingPairToken = ""; s.PairExpiresAt = null; s.LastStatus = "网页配对已过期，请重新连接"; });
                        if (!settings.IsPaired) { Schedule(60); return; }
                    }
                    else if (pairStatus == "confirmed" && !string.IsNullOrWhiteSpace(pair.Value.DeviceId))
                    {
                        if (!string.Equals(settings.BoundDeviceId, pair.Value.DeviceId, StringComparison.Ordinal))
                        {
                            ForgetSnapshot();
                            _lastFullSync = DateTimeOffset.MinValue;
                            _store.Update(s =>
                            {
                                s.BoundDeviceId = pair.Value.DeviceId!;
                                s.BoundClassTag = "";
                                s.BoundGradeId = s.BoundClassId = "";
                            });
                        }
                        _store.Update(s => { s.IsPaired = true; s.PendingPairToken = ""; s.PairExpiresAt = null; });
                    }
                    else { Failure("在线服务返回了无效的配对状态"); return; }
                }
                else
                {
                    if (!pair.Value.Paired) { Status("等待浏览器完成班级配对"); Schedule(5, false); return; }
                    _store.Update(s => { s.IsPaired = true; s.BoundClassTag = pair.Value.ClassTag ?? ""; s.PendingPairToken = ""; s.PairExpiresAt = null; });
                }
            }

            var full = _snapshot is null || DateTimeOffset.UtcNow - _lastFullSync >= TimeSpan.FromMinutes(_store.Settings.FullSyncMinutes);
            var started = Stopwatch.GetTimestamp();
            var result = await _client.GetBootstrapAsync(settings, full ? null : _snapshot?.Version, token);
            LastRequestMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (generation != Interlocked.Read(ref _generation)) return;
            if (!result.IsSuccess || result.Value?.Ok != true)
            {
                if (settings.ServiceMode == "online" && result.ErrorCode is ("ENTITLEMENT_PENDING" or "ENTITLEMENT_SUSPENDED" or "ENTITLEMENT_EXPIRED"))
                {
                    ForgetSnapshot();
                    Status(result.Error ?? "在线服务权益当前不可用");
                    Failure(result.Error ?? "在线服务权益当前不可用", result.RetryAfter);
                    return;
                }
                if (settings.ServiceMode == "online" && result.ErrorCode is ("AUTH_REQUIRED" or "DEVICE_REVOKED" or "DEVICE_EXPIRED" or "EXAM_ARCHIVED"))
                {
                    ForgetSnapshot();
                    _store.Update(s =>
                    {
                        s.IsPaired = false;
                        s.PendingPairToken = "";
                        s.PairExpiresAt = null;
                        s.BoundDeviceId = "";
                        s.BoundClassTag = "";
                        s.BoundGradeId = s.BoundClassId = "";
                    });
                }
                else if (settings.ServiceMode != "online" && result.StatusCode is (401 or 403 or 409))
                {
                    _snapshot = null;
                    _suppression.SetActive(false);
                    _store.Update(s => { s.IsPaired = false; s.PendingPairToken = ""; s.BoundClassTag = ""; s.BoundGradeId = s.BoundClassId = ""; s.NextExamName = ""; s.NextExamStartAt = null; });
                }
                Failure(result.Error ?? "考试看板拒绝同步", result.RetryAfter);
                return;
            }
            var data = result.Value;
            if (data.ApiVersion > 2) { Failure("考试看板 API 版本过新，请更新插件", TimeSpan.FromMinutes(10)); return; }
            if (data.ServerTime.Year < 2020 || data.ServerTime.Year > 2200 || (data.Changed && (data.Exams is null || data.Exams.Any(x =>
                string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name) || x.StartAt.Year < 2020 || x.EndAt <= x.StartAt))))
            { Failure("考试数据或服务器时间无效，保留上次有效数据"); return; }
            if (settings.ServiceMode == "online" && data.Changed)
            {
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(data.TimeZoneId); }
                catch { Failure("在线服务返回的考试时区无效，保留上次有效数据"); return; }
            }
            if (!data.Changed && (_snapshot is null || string.IsNullOrEmpty(data.Version) || data.Version != _snapshot.Version))
            { _lastFullSync = DateTimeOffset.MinValue; Failure("轻量同步版本不一致，等待完整校验"); return; }

            if (!data.Changed)
            {
                // Preserve the complete schedule when the server returns only status.
                data.Exams = _snapshot!.Exams;
                data.Binding = _snapshot.Binding;
                data.Lifecycle = _snapshot.Lifecycle;
                data.TimeZoneId = _snapshot.TimeZoneId;
            }
            else
            {
                if (settings.ServiceMode != "online" && data.Capabilities?.Contains("session-history", StringComparer.Ordinal) != true && _snapshot is {} previous &&
                    data.Binding?.ClassTag is {} classTag && classTag == previous.Binding?.ClassTag)
                {
                    var today = TimeZoneInfo.ConvertTime(data.ServerTime, ExamSchedule.TimeZone(_store.Settings)).Date;
                    var completed = previous.Exams.Where(x => x.EndAt <= data.ServerTime &&
                        TimeZoneInfo.ConvertTime(x.StartAt, ExamSchedule.TimeZone(_store.Settings)).Date == today &&
                        !data.Exams.Any(y => y.Id == x.Id));
                    data.Exams = data.Exams.Concat(completed).ToList();
                }
                _lastFullSync = DateTimeOffset.UtcNow;
            }

            var oldSnapshot = _snapshot;
            _clock = new ClockAnchor(data.ServerTime.Add(Stopwatch.GetElapsedTime(started) / 2), Stopwatch.GetTimestamp());
            _validatedAt = DateTimeOffset.UtcNow;
            _snapshot = data;
            SuccessfulChecks++;
            _failures = 0;
            UsingCache = false;
            ViewerStatusKnown = true;
            SyncMode = string.IsNullOrEmpty(data.Version) ? "兼容同步（完整数据）" : "轻量同步";
            RuntimeError = "";
            if (data.Changed && _store.Settings.EnableExamChangeReminder && oldSnapshot is {} old &&
                old.Binding?.ClassTag == data.Binding?.ClassTag &&
                (string.IsNullOrEmpty(old.Binding?.ClassId) || string.IsNullOrEmpty(data.Binding?.ClassId) ||
                    old.Binding.ClassId == data.Binding.ClassId))
            {
                var changeScope = Scope(_store.Settings) + "|change|";
                var pending = ExamSchedule.Changes(old.Exams, data.Exams, _store.Settings, Now, data.Lifecycle,
                        data.Capabilities?.Contains("exam-lifecycle", StringComparer.Ordinal) == true)
                    .Where(x => !_store.HasCompletedAction(changeScope + x.Key)).ToList();
                if (pending.Count > 0)
                {
                    _provider.ShowExamChangeReminder(pending);
                    foreach (var change in pending) _store.MarkCompletedAction(changeScope + change.Key);
                }
            }
            var nextExam = data.Exams.Where(x => ExamSchedule.IsStillRelevant(x, Now))
                .OrderBy(x => x.StartAt).FirstOrDefault();
            _store.Update(s =>
            {
                if (data.Binding is {} binding && ((binding.ClassId.Length > 0 && s.BoundClassId != binding.ClassId) || s.BoundClassTag != binding.ClassTag)) s.SkippedSessionKey = "";
                s.BoundClassTag = data.Binding?.ClassTag ?? s.BoundClassTag;
                s.BoundGradeId = data.Binding?.GradeId ?? s.BoundGradeId;
                s.BoundClassId = data.Binding?.ClassId ?? s.BoundClassId;
                if (settings.ServiceMode == "online" && data.TimeZoneId.Length > 0) s.OnlineTimeZoneId = data.TimeZoneId;
                s.LastSyncAt = DateTimeOffset.Now;
                s.ViewerOnline = data.ViewerOnline;
                if (data.ViewerOnline) s.LastViewerConfirmedAt = DateTimeOffset.Now;
                s.ServerOffsetMilliseconds = (long)(Now - DateTimeOffset.UtcNow).TotalMilliseconds;
                s.NextExamName = nextExam?.Name ?? ""; s.NextExamStartAt = nextExam?.StartAt;
                s.LastStatus = "已同步考试看板";
            });
            if (_store.Settings.CompletedActions.Any(x => x.Count(c => c == '|') == 2))
            {
                var actionScope = Scope(_store.Settings) + "|";
                _store.Update(s => s.CompletedActions = s.CompletedActions.Select(x => x.Count(c => c == '|') == 2 ? actionScope + x : x).Distinct().TakeLast(2000).ToList());
            }
            try { _store.Write("Exams.cache.json", new ExamCache(Scope(_store.Settings), _validatedAt, data)); }
            catch (Exception ex) { RuntimeError = $"考试缓存未保存：{ex.Message}"; }
            EvaluateProtection(data);
            Schedule(!string.IsNullOrWhiteSpace(_store.Settings.PendingPairToken) ? 5 : _store.Settings.SyncSeconds, !settings.ServiceMode.Equals("online", StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (generation == Interlocked.Read(ref _generation)) Failure($"同步失败：{ex.Message}"); }
        finally
        {
            if (generation != Interlocked.Read(ref _generation)) RequestImmediateSync(true);
            else if (requestVersion != Interlocked.Read(ref _syncRequestVersion)) Interlocked.Exchange(ref _nextSyncMs, 0);
        }
    }

    private void Failure(string error, TimeSpan? retryAfter = null)
    {
        FailedChecks++;
        ViewerStatusKnown = false;
        UsingCache = _snapshot is not null;
        _offlineChecks = 0;
        Status(error);
        _failures = Math.Min(_failures + 1, 10);
        var delay = Math.Max(_store.Settings.SyncSeconds, Math.Min(_store.Settings.MaxRetryMinutes * 60, _store.Settings.SyncSeconds * Math.Pow(2, _failures - 1)));
        if (retryAfter is {} retry && retry.TotalSeconds > delay) delay = Math.Min(retry.TotalSeconds, 86400);
        Schedule(delay);
    }

    private void Schedule(double seconds, bool stagger = true)
    {
        // Only delay, never shorten the configured minimum interval.
        var jitter = stagger && _store.Settings.StaggerRequests ? Random.Shared.NextDouble() * Math.Min(seconds * 0.1, 10) : 0;
        Interlocked.Exchange(ref _nextSyncMs, DateTimeOffset.UtcNow.AddSeconds(seconds + jitter).ToUnixTimeMilliseconds());
    }

    private void Status(string text) => _store.Update(s => s.LastStatus = text);

    private void ForgetSnapshot()
    {
        _snapshot = null;
        CurrentSession = NextSession = null;
        ViewerStatusKnown = false;
        UsingCache = false;
        _suppression.SetActive(false);
        _store.Delete("Exams.cache.json");
        _store.Update(s => { s.NextExamName = ""; s.NextExamStartAt = null; });
    }

    private void EvaluateSchedule()
    {
        var s = _store.Settings;
        var snapshot = _snapshot;
        var valid = s.IsPaired && snapshot is not null && DateTimeOffset.UtcNow - _validatedAt <= TimeSpan.FromHours(s.CacheMaxHours) &&
            (!UsingCache || s.UseOfflineCache);
        if (!valid)
        {
            CurrentSession = NextSession = null;
            _modeDescription = snapshot is null ? "等待考试数据" : "缓存已停用或过期，等待联网";
            _suppression.SetActive(false);
            return;
        }
        var now = Now;
        var dataWarning = OfflineWarning;
        var sessions = ExamSchedule.Sessions(snapshot!.Exams, s, now);
        CurrentSession = sessions.FirstOrDefault(x => x.Start <= now && x.End > now);
        NextSession = sessions.FirstOrDefault(x => x.End > now);
        var active = s.EnableExamMode && CurrentSession is {} session && session.Key != s.SkippedSessionKey;
        _suppression.SetActive(active);
        _modeDescription = !s.EnableExamMode ? "自动考试模式已关闭" : CurrentSession is null ? "尚未进入考试时段" :
            active ? "考试模式进行中" : "本时段已暂不启用";

        var scope = Scope(s);
        if (ExamSchedule.IsReminderTypeEnabled(s, ReminderType.DayBefore) &&
            TimeSpan.TryParse(s.DayBeforeReminderTime, out var dayBeforeAt))
        {
            var localNow = TimeZoneInfo.ConvertTime(now, ExamSchedule.TimeZone(s));
            if (localNow.TimeOfDay >= dayBeforeAt)
            {
                var tomorrow = ExamSchedule.Tomorrow(snapshot.Exams, s, now);
                var previewKey = scope + "|day-before|" + localNow.Date.ToString("yyyy-MM-dd");
                if (tomorrow.Count > 0 && !_store.HasCompletedAction(previewKey))
                {
                    _provider.ShowDayBeforeReminder(tomorrow, dataWarning);
                    _store.MarkCompletedAction(previewKey);
                }
            }
        }
        foreach (var exam in snapshot.Exams)
        {
            var otherExamActive = ExamSchedule.IsOtherExamInProgress(snapshot.Exams, exam, now, ConsecutiveReminderGuard);
            if (ExamSchedule.IsReminderTypeEnabled(s, ReminderType.Upcoming))
            {
                foreach (var minutes in s.ReminderMinutes!)
                {
                    // 连考时不要用下一场的考前提醒打断当前考试。
                    if (otherExamActive) continue;
                    var key = scope + "|" + ExamSchedule.ActionKey(exam, $"reminder-{minutes}");
                    if (ExamSchedule.IsDue(exam, minutes, now, s.ReminderGraceSeconds) && !_store.HasCompletedAction(key))
                    {
                        _provider.ShowExamReminder(exam, TimeSpan.FromMinutes(minutes), ExamReminderKind.BeforeStart, dataWarning);
                        _store.MarkCompletedAction(key);
                    }
                }
            }
            if (ExamSchedule.IsReminderTypeEnabled(s, ReminderType.Start))
            {
                var key = scope + "|" + ExamSchedule.ActionKey(exam, "start");
                if (ExamSchedule.IsDue(exam, 0, now, s.ReminderGraceSeconds) && !_store.HasCompletedAction(key))
                {
                    _provider.ShowExamReminder(exam, TimeSpan.Zero, ExamReminderKind.BeforeStart, dataWarning);
                    _store.MarkCompletedAction(key);
                }
            }
            if (ExamSchedule.IsReminderTypeEnabled(s, ReminderType.BeforeEnd))
            {
                foreach (var minutes in s.EndReminderMinutes ?? [])
                {
                    var key = scope + "|" + ExamSchedule.EndActionKey(exam, $"end-{minutes}");
                    if (ExamSchedule.IsEndDue(exam, minutes, now, s.ReminderGraceSeconds) && !_store.HasCompletedAction(key))
                    {
                        _provider.ShowExamReminder(exam, exam.PausedAt is {} pausedAt ? exam.EndAt - pausedAt : exam.EndAt - now,
                            ExamReminderKind.BeforeEnd, dataWarning);
                        _store.MarkCompletedAction(key);
                    }
                }
            }
            if (ExamSchedule.IsReminderTypeEnabled(s, ReminderType.Ended))
            {
                var key = scope + "|" + ExamSchedule.EndActionKey(exam, "ended");
                var startsSoon = ExamSchedule.IsAnotherExamStartingSoon(snapshot.Exams, exam, now, ConsecutiveReminderGuard);
                if (!otherExamActive && !startsSoon && ExamSchedule.IsEndDue(exam, 0, now, s.ReminderGraceSeconds) &&
                    !_store.HasCompletedAction(key))
                {
                    _provider.ShowExamReminder(exam, TimeSpan.Zero, ExamReminderKind.Ended, dataWarning);
                    _store.MarkCompletedAction(key);
                }
            }
            if (s.ServiceMode == "online" || !s.AutoOpenBoard || now >= exam.StartAt ||
                exam.StartAt - now > TimeSpan.FromMinutes(s.BrowserLeadMinutes)) continue;
            var openKey = scope + "|" + ExamSchedule.ActionKey(exam, "open");
            if (!_store.HasCompletedAction(openKey))
            {
                if (ViewerStatusKnown && snapshot.ViewerOnline) _store.MarkCompletedAction(openKey);
                else if (now >= _nextOpen && TryOpenBoard("考前自动打开看板")) _store.MarkCompletedAction(openKey);
            }
            if (s.EnableAutoSync && exam.StartAt - now <= PreStartCheckWindow)
            {
                var checkKey = scope + "|" + ExamSchedule.ActionKey(exam, "precheck");
                if (_precheckKey != checkKey)
                {
                    _precheckKey = checkKey;
                    if (DateTimeOffset.UtcNow - _validatedAt > TimeSpan.FromSeconds(10)) RequestImmediateSync();
                }
            }
        }
    }

    private void EvaluateProtection(ExamBoardBootstrapResponse data)
    {
        var s = _store.Settings;
        if (s.ServiceMode == "online") { _offlineChecks = _reopens = 0; return; }
        if (data.ViewerOnline) { _offlineChecks = _reopens = 0; return; }
        var near = data.Exams.Any(x => x.StartAt > Now && x.StartAt - Now <= PreStartCheckWindow);
        var active = data.Exams.Any(x => x.StartAt <= Now && (x.EndAt > Now || x.IsPaused));
        if (!s.AutoOpenBoard || (!near && !(active && s.EnableExamGuard))) { _offlineChecks = 0; return; }
        _offlineChecks++;
        if ((near || _offlineChecks >= s.OfflineChecksBeforeReopen) && Now >= _nextOpen) TryOpenBoard("看板离线，尝试恢复");
    }

    private bool TryOpenBoard(string reason)
    {
        BrowserOpenAttempts++;
        var s = _store.Settings;
        var delay = Math.Max(s.BrowserCooldownSeconds, Math.Min(s.MaxBrowserCooldownMinutes * 60, s.BrowserCooldownSeconds * Math.Pow(2, Math.Min(_reopens, 8))));
        _nextOpen = Now.AddSeconds(delay);
        _reopens++;
        _offlineChecks = 0;
        var target = s.ServiceMode == "online" ? ExamBoardUrls.OnlineHome(s.BaseUrl) : ExamBoardUrls.Board(s.BaseUrl, s.PluginInstanceId);
        if (_launcher.TryOpen(target, out var error)) { Status(reason + "，已发起打开"); return true; }
        Status("无法打开考试看板：" + error);
        return false;
    }

    private static string Scope(PluginSettings s)
    {
        var value = s.ServiceMode == "online"
            ? $"online|{s.BaseUrl}|{s.PluginInstanceId}|{s.ClientSecret}|{s.BoundDeviceId}"
            : $"{s.BaseUrl}|{s.PluginInstanceId}|{s.ClientSecret}|{s.BoundGradeId}|{(s.BoundClassId.Length > 0 ? s.BoundClassId : s.BoundClassTag)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
