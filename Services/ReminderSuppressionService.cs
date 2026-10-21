using System.Collections;
using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Models.Notification;
using ClassIsland.ExamReminder.Models;
using HarmonyLib;
using Avalonia.Threading;
using NotificationPriority = ClassIsland.Shared.Models.Notification.NotificationPriority;

namespace ClassIsland.ExamReminder.Services;

public sealed record ReminderSource(string Id, string Name);

// ClassIsland 2.1 has no public filtering event. Keep the host adapter in this file;
// filtering is transient and never changes host settings or the timetable.
public sealed class ReminderSuppressionService : IDisposable
{
    private readonly PluginSettingsStore _store;
    private readonly INotificationHostService _host;
    private readonly Harmony _harmony = new("classisland.exam-reminder.exam-mode");
    private static ReminderSuppressionService? _current;
    private static readonly PropertyInfo? CompletionSource = typeof(NotificationRequest).GetProperty("CompletedTokenSource", BindingFlags.Instance | BindingFlags.NonPublic);
    private volatile bool _active;
    private string _policy = "";
    public bool Supported { get; private set; }
    public string Status { get; private set; } = "正在检查提醒控制能力";

    public ReminderSuppressionService(PluginSettingsStore store, INotificationHostService host)
    {
        _store = store;
        _host = host;
        try
        {
            if (CompletionSource is null) throw new NotSupportedException("提醒完成接口不兼容");
            var single = host.GetType().GetMethod("ShowNotification", BindingFlags.Public | BindingFlags.Instance);
            var chain = host.GetType().GetMethod("ShowChainedNotifications", BindingFlags.Public | BindingFlags.Instance);
            if (single is null || chain is null || single.GetParameters()[0].ParameterType != typeof(NotificationRequest) ||
                chain.GetParameters()[0].ParameterType != typeof(NotificationRequest[])) throw new NotSupportedException("提醒主机接口不兼容");
            _current = this;
            _harmony.Patch(single, prefix: new HarmonyMethod(typeof(ReminderSuppressionService).GetMethod(nameof(FilterSingle))!));
            _harmony.Patch(chain, prefix: new HarmonyMethod(typeof(ReminderSuppressionService).GetMethod(nameof(FilterChain))!));
            Supported = true;
            Status = "支持按提醒提供方屏蔽；不修改宿主配置";
        }
        catch (Exception ex)
        {
            _harmony.UnpatchAll(_harmony.Id);
            _current = null;
            Status = $"此宿主暂不支持提醒屏蔽：{ex.GetBaseException().Message}";
        }
    }

    public IReadOnlyList<ReminderSource> Sources()
    {
        var sources = new List<ReminderSource>
        {
            new(PluginSettings.CourseProviderId, "上下课提醒（含准备上课、课间休息）"),
            new(PluginSettings.AfterSchoolProviderId, "放学提醒")
        };
        if (_host.GetType().GetProperty("NotificationProviders")?.GetValue(_host) is IEnumerable providers)
        {
            foreach (var provider in providers)
            {
                var id = provider.GetType().GetProperty("ProviderGuid")?.GetValue(provider)?.ToString();
                var name = provider.GetType().GetProperty("Name")?.GetValue(provider)?.ToString();
                if (id is null || id.Equals(ExamReminderProvider.ProviderId, StringComparison.OrdinalIgnoreCase) || sources.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                sources.Add(new(id, name ?? id));
            }
        }
        return sources;
    }

    public void SetActive(bool active)
    {
        _active = active && Supported;
        var policy = _active ? string.Join("|", _store.Settings.SuppressedProviders) : "";
        if (policy == _policy) return;
        _policy = policy;
        if (!_active) return;
        // A prepare-class chain may already be queued before the exam window starts.
        // Cancel selected pending/playing requests on entry as well as filtering new ones.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_active) return;
            var requests = new List<NotificationRequest>();
            if (_host.GetType().GetProperty("RequestQueue")?.GetValue(_host) is PriorityQueue<NotificationRequest, NotificationPriority> queue)
                requests.AddRange(queue.UnorderedItems.Select(x => x.Element));
            if (_host.GetType().GetField("<PlayingNotifications>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_host) is IEnumerable playing)
                requests.AddRange(playing.Cast<NotificationRequest>().ToList());
            var sourceProperty = typeof(NotificationRequest).GetProperty("NotificationSourceGuid", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var request in requests.Distinct())
                if (sourceProperty?.GetValue(request) is Guid source && ShouldBlock(source) && !request.CompletedToken.IsCancellationRequested) TryComplete(request);
        });
    }

    public static bool FilterSingle(NotificationRequest __0, Guid __1)
    {
        if (_current?.ShouldBlock(__1) != true) return true;
        return !_current.TryComplete(__0);
    }

    public static bool FilterChain(NotificationRequest[] __0, Guid __1)
    {
        if (_current?.ShouldBlock(__1) != true) return true;
        foreach (var request in __0) if (!_current.TryComplete(request)) return true;
        return false;
    }

    private bool ShouldBlock(Guid source) => _active && source != Guid.Parse(ExamReminderProvider.ProviderId) &&
        _store.Settings.SuppressedProviders.Any(x => Guid.TryParse(x, out var id) && id == source);

    private bool TryComplete(NotificationRequest request)
    {
        try
        {
            request.Cancel();
            (CompletionSource!.GetValue(request) as CancellationTokenSource)?.Cancel();
            return true;
        }
        catch (Exception ex)
        {
            _active = false; Supported = false;
            Status = $"提醒屏蔽已停用：{ex.GetBaseException().Message}";
            return false;
        }
    }

    public void Dispose()
    {
        _active = false;
        _harmony.UnpatchAll(_harmony.Id);
        if (_current == this) _current = null;
    }
}
