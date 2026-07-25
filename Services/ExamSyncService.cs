using ClassIsland.ExamReminder.Models;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ExamReminder.Services;

public sealed class ExamSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PairingPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(5);
    private readonly PluginSettingsStore _store;
    private readonly ExamBoardClient _client;
    private readonly ExamReminderProvider _provider;
    private readonly ExternalUriLauncher _uriLauncher;
    private ExamBoardBootstrapResponse? _snapshot;
    private long _nextSyncAtUnixMilliseconds = long.MinValue;
    private long _syncRequestVersion;
    private DateTimeOffset _nextBrowserOpenAttemptAt = DateTimeOffset.MinValue;

    public ExamSyncService(
        PluginSettingsStore store,
        ExamBoardClient client,
        ExamReminderProvider provider,
        ExternalUriLauncher uriLauncher)
    {
        _store = store;
        _client = client;
        _provider = provider;
        _uriLauncher = uriLauncher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >=
                Interlocked.Read(ref _nextSyncAtUnixMilliseconds))
            {
                var requestVersion = Interlocked.Read(ref _syncRequestVersion);
                var nextInterval = await SynchronizeAsync(stoppingToken);
                var nextSyncAt = requestVersion == Interlocked.Read(ref _syncRequestVersion)
                    ? DateTimeOffset.UtcNow.Add(nextInterval).ToUnixTimeMilliseconds()
                    : long.MinValue;
                Interlocked.Exchange(ref _nextSyncAtUnixMilliseconds, nextSyncAt);
            }

            EvaluateSchedule();
            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }

    public void RequestImmediateSync()
    {
        Interlocked.Increment(ref _syncRequestVersion);
        Interlocked.Exchange(ref _nextSyncAtUnixMilliseconds, long.MinValue);
    }

    public bool TryShowCurrentOrNextExamReminder(out string error)
    {
        var settings = _store.Settings;
        var now = DateTimeOffset.UtcNow.AddMilliseconds(settings.ServerOffsetMilliseconds);
        var exam = _snapshot?.Exams
            .Where(value => value.EndAt > now)
            .OrderBy(value => value.StartAt)
            .FirstOrDefault();
        if (exam is null)
        {
            error = "没有可用于测试的考试，请先同步考试数据";
            return false;
        }

        _provider.ShowExamReminder(exam, exam.StartAt - now);
        error = string.Empty;
        return true;
    }

    private async Task<TimeSpan> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var settings = _store.Settings;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return SyncInterval;
        }

        if (!settings.IsPaired)
        {
            if (string.IsNullOrWhiteSpace(settings.PendingPairToken))
            {
                return SyncInterval;
            }
            if (settings.PairExpiresAt is not null && settings.PairExpiresAt <= DateTimeOffset.Now)
            {
                _store.Update(value =>
                {
                    value.PendingPairToken = string.Empty;
                    value.PairExpiresAt = null;
                    value.LastStatus = "浏览器绑定已过期，请重新连接";
                });
                return SyncInterval;
            }

            var pairResult = await _client.GetPairStatusAsync(settings, cancellationToken);
            if (!pairResult.IsSuccess || pairResult.Value is null)
            {
                SetStatus(pairResult.Error ?? "等待浏览器完成班级配对");
                return FailureRetryInterval;
            }
            if (!pairResult.Value.Paired)
            {
                SetStatus("等待浏览器完成班级配对");
                return PairingPollInterval;
            }
            _store.Update(value =>
            {
                value.IsPaired = true;
                value.BoundClassTag = pairResult.Value.ClassTag ?? string.Empty;
                value.PendingPairToken = string.Empty;
                value.PairExpiresAt = null;
                value.LastStatus = "班级配对成功，正在同步考试";
            });
        }

        var result = await _client.GetBootstrapAsync(settings, cancellationToken);
        if (!result.IsSuccess || result.Value is null)
        {
            _store.Update(value => value.LastStatus = result.Error ?? "考试同步失败");
            return FailureRetryInterval;
        }
        if (result.Value.ApiVersion > 2)
        {
            _store.Update(value => value.LastStatus = "考试看板 API 版本过新，请更新 ClassIsland 插件");
            return SyncInterval;
        }

        _snapshot = result.Value;
        var offset = result.Value.ServerTime - DateTimeOffset.UtcNow;
        var nextExam = result.Value.Exams
            .Where(exam => exam.EndAt > result.Value.ServerTime)
            .OrderBy(exam => exam.StartAt)
            .FirstOrDefault();
        _store.Update(value =>
        {
            value.IsPaired = true;
            value.BoundClassTag = result.Value.Binding?.ClassTag ?? value.BoundClassTag;
            value.LastSyncAt = DateTimeOffset.Now;
            value.ServerOffsetMilliseconds = (long)offset.TotalMilliseconds;
            value.NextExamName = nextExam?.Name ?? string.Empty;
            value.NextExamStartAt = nextExam?.StartAt;
            value.LastStatus = "已同步考试看板";
        });
        return SyncInterval;
    }

    private void SetStatus(string status)
    {
        if (!string.Equals(_store.Settings.LastStatus, status, StringComparison.Ordinal))
        {
            _store.Update(value => value.LastStatus = status);
        }
    }

    private void EvaluateSchedule()
    {
        if (_snapshot is null)
        {
            return;
        }

        var settings = _store.Settings;
        var now = DateTimeOffset.UtcNow.AddMilliseconds(settings.ServerOffsetMilliseconds);
        foreach (var exam in _snapshot.Exams.OrderBy(value => value.StartAt))
        {
            var remaining = exam.StartAt - now;
            if (remaining <= TimeSpan.Zero && remaining > TimeSpan.FromMinutes(-2))
            {
                TriggerReminder(exam, TimeSpan.Zero, "start", settings.EnableStartReminder);
                continue;
            }
            if (remaining <= TimeSpan.Zero)
            {
                continue;
            }

            if (remaining <= TimeSpan.FromMinutes(settings.BrowserLeadMinutes) && settings.AutoOpenBoard)
            {
                var key = ActionKey(exam, "open");
                if (!_store.HasCompletedAction(key) && _snapshot.ViewerOnline)
                {
                    _store.MarkCompletedAction(key);
                }
                else if (!_store.HasCompletedAction(key) && now >= _nextBrowserOpenAttemptAt)
                {
                    if (OpenBoard(settings, out var error))
                    {
                        _store.MarkCompletedAction(key);
                    }
                    else
                    {
                        _nextBrowserOpenAttemptAt = now.AddMinutes(1);
                        _store.Update(value => value.LastStatus = $"无法自动打开考试看板：{error}");
                    }
                }
            }
            if (remaining <= TimeSpan.FromMinutes(15) && remaining > TimeSpan.FromMinutes(5))
            {
                TriggerReminder(exam, remaining,
                    "reminder-15", settings.EnableFifteenMinuteReminder);
            }
            if (remaining <= TimeSpan.FromMinutes(5))
            {
                TriggerReminder(exam, remaining,
                    "reminder-5", settings.EnableFiveMinuteReminder);
            }
        }
    }

    private void TriggerReminder(ExamBoardExam exam, TimeSpan remaining, string action, bool enabled)
    {
        if (!enabled)
        {
            return;
        }
        var key = ActionKey(exam, action);
        if (_store.HasCompletedAction(key))
        {
            return;
        }
        _provider.ShowExamReminder(exam, remaining);
        _store.MarkCompletedAction(key);
    }

    private bool OpenBoard(PluginSettings settings, out string error) =>
        _uriLauncher.TryOpen(ExamBoardUrls.Board(settings.BaseUrl, settings.PluginInstanceId), out error);

    private static string ActionKey(ExamBoardExam exam, string action) =>
        $"{exam.Id}|{exam.StartAt.UtcTicks}|{action}";
}
