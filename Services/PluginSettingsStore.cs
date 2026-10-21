using System.Text.Json;
using ClassIsland.ExamReminder.Models;

namespace ClassIsland.ExamReminder.Services;

public sealed class PluginSettingsStore
{
    private readonly object _gate = new();
    private readonly string _folder;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public PluginSettings Settings { get; }
    public string? SaveError { get; private set; }
    public event EventHandler? Changed;

    public PluginSettingsStore(Plugin plugin) : this(plugin.PluginConfigFolder) { }
    public PluginSettingsStore(string folder)
    {
        _folder = folder;
        try { Settings = Read<PluginSettings>("Settings.json") ?? new(); }
        catch (Exception ex) { Settings = new(); SaveError = $"配置读取失败：{ex.Message}"; }
        Normalize(Settings);
    }

    public void Update(Action<PluginSettings> update)
    {
        lock (_gate)
        {
            var before = JsonSerializer.Serialize(Settings, JsonOptions);
            update(Settings);
            Normalize(Settings);
            var after = JsonSerializer.Serialize(Settings, JsonOptions);
            if (before == after) return;
            try { WriteAtomic("Settings.json", after); SaveError = null; }
            catch (Exception ex) { SaveError = $"设置未保存：{ex.Message}"; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool HasCompletedAction(string key)
    {
        lock (_gate) return Settings.CompletedActions.Contains(key, StringComparer.Ordinal);
    }

    public void MarkCompletedAction(string key) => Update(settings =>
    {
        if (!settings.CompletedActions.Contains(key, StringComparer.Ordinal)) settings.CompletedActions.Add(key);
        if (settings.CompletedActions.Count > 2000) settings.CompletedActions.RemoveRange(0, settings.CompletedActions.Count - 2000);
    });

    public T? Read<T>(string name)
    {
        lock (_gate)
        {
            var path = Path.Combine(_folder, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default;
        }
    }

    public void Write<T>(string name, T value)
    {
        lock (_gate) WriteAtomic(name, JsonSerializer.Serialize(value, JsonOptions));
    }

    private void WriteAtomic(string name, string json)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
    }

    public static void Normalize(PluginSettings value)
    {
        if (string.IsNullOrWhiteSpace(value.PluginInstanceId)) value.PluginInstanceId = $"classisland-{Guid.NewGuid():N}";
        if (string.IsNullOrWhiteSpace(value.ClientSecret) || value.ClientSecret.Length < 32) value.ClientSecret = PluginSettings.CreateSecret();
        value.BrowserLeadMinutes = Math.Clamp(value.BrowserLeadMinutes, 1, 240);
        value.SyncSeconds = Math.Clamp(value.SyncSeconds, 30, 3600);
        value.FullSyncMinutes = Math.Clamp(value.FullSyncMinutes, 1, 1440);
        value.RequestTimeoutSeconds = Math.Clamp(value.RequestTimeoutSeconds, 3, 60);
        value.MaxRetryMinutes = Math.Clamp(value.MaxRetryMinutes, 1, 120);
        value.CacheMaxHours = Math.Clamp(value.CacheMaxHours, 1, 168);
        value.ReminderMinutes ??= new[] { value.EnableFifteenMinuteReminder ? 15 : 0, value.EnableFiveMinuteReminder ? 5 : 0 }.Where(x => x > 0).ToList();
        value.ReminderMinutes = value.ReminderMinutes.Where(x => x is > 0 and <= 1440).Distinct().OrderDescending().Take(20).ToList();
        value.EndReminderMinutes ??= [15, 5];
        value.EndReminderMinutes = value.EndReminderMinutes.Where(x => x is > 0 and <= 1440).Distinct().OrderDescending().Take(20).ToList();
        value.ReminderDurationSeconds = Math.Clamp(value.ReminderDurationSeconds, 3, 120);
        value.ReminderGraceSeconds = Math.Clamp(value.ReminderGraceSeconds, 0, 600);
        value.ExamModeLeadMinutes = Math.Clamp(value.ExamModeLeadMinutes, 0, 240);
        value.ExamModeTailMinutes = Math.Clamp(value.ExamModeTailMinutes, 0, 240);
        value.SessionBoundaryHour = Math.Clamp(value.SessionBoundaryHour, 1, 23);
        value.SchoolTimeZone = string.IsNullOrWhiteSpace(value.SchoolTimeZone) ? "Asia/Shanghai" : value.SchoolTimeZone;
        value.SuppressedProviders ??= [];
        value.CompletedActions ??= [];
        value.ReadAnnouncements ??= [];
        value.OfflineChecksBeforeReopen = Math.Clamp(value.OfflineChecksBeforeReopen, 1, 10);
        value.BrowserCooldownSeconds = Math.Clamp(value.BrowserCooldownSeconds, 30, 3600);
        value.MaxBrowserCooldownMinutes = Math.Clamp(value.MaxBrowserCooldownMinutes, 1, 120);
        value.AnnouncementRefreshMinutes = Math.Clamp(value.AnnouncementRefreshMinutes, 5, 1440);
        value.ReminderTemplate ??= "距离{考试}开始还有{分钟}分钟。";
        value.StartTemplate ??= "{考试}现在开始。";
        value.EndTemplate ??= "距离{考试}结束还有{分钟}分钟，请检查答题卡。";
        value.ExamEndTemplate ??= "{考试}已结束。";
        value.ExamChangeTemplate ??= "考试安排已变更：{变更}";
        if (!TimeSpan.TryParse(value.DayBeforeReminderTime, out var dayBefore) || dayBefore < TimeSpan.Zero || dayBefore >= TimeSpan.FromHours(24))
            value.DayBeforeReminderTime = "17:30";
        else value.DayBeforeReminderTime = dayBefore.ToString(@"hh\:mm");
        value.DayBeforeTemplate ??= "明天共 {数量} 场考试：首场 {首场} {首场时间}，末场 {末场} {末场时间}。";
    }
}
