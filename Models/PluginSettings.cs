using System.Security.Cryptography;

namespace ClassIsland.ExamReminder.Models;

public sealed class PluginSettings
{
    public const string CourseProviderId = "08F0D9C3-C770-4093-A3D0-02F3D90C24BC";
    public const string AfterSchoolProviderId = "8FBC3A26-6D20-44DD-B895-B9411E3DDC51";
    public static readonly string[] DefaultSuppressedProviderIds = [CourseProviderId, AfterSchoolProviderId];

    public string BaseUrl { get; set; } = string.Empty;
    public string PluginInstanceId { get; set; } = $"classisland-{Guid.NewGuid():N}";
    public string ClientSecret { get; set; } = CreateSecret();
    public string PendingPairToken { get; set; } = string.Empty;
    public DateTimeOffset? PairExpiresAt { get; set; }
    public string BoundClassTag { get; set; } = string.Empty;
    public string BoundGradeId { get; set; } = string.Empty;
    public string BoundClassId { get; set; } = string.Empty;
    public bool IsPaired { get; set; }
    public int BrowserLeadMinutes { get; set; } = 20;
    public bool AutoOpenBoard { get; set; } = true;
    public bool EnableExamGuard { get; set; } = true;
    public bool EnableFifteenMinuteReminder { get; set; } = true;
    public bool EnableFiveMinuteReminder { get; set; } = true;
    public bool EnableStartReminder { get; set; } = true;
    public DateTimeOffset? LastSyncAt { get; set; }
    public bool ViewerOnline { get; set; }
    public DateTimeOffset? LastViewerConfirmedAt { get; set; }
    public string LastStatus { get; set; } = "请填写考试看板网址并连接";
    public string NextExamName { get; set; } = string.Empty;
    public DateTimeOffset? NextExamStartAt { get; set; }
    public long ServerOffsetMilliseconds { get; set; }
    public List<string> CompletedActions { get; set; } = [];

    public int SyncSeconds { get; set; } = 60;
    public bool EnableAutoSync { get; set; } = true;
    public int FullSyncMinutes { get; set; } = 10;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int MaxRetryMinutes { get; set; } = 10;
    public bool StaggerRequests { get; set; } = true;
    public bool UseOfflineCache { get; set; } = true;
    public int CacheMaxHours { get; set; } = 24;
    public bool EnableSpeech { get; set; } = true;
    public List<int>? ReminderMinutes { get; set; }
    public string ReminderTemplate { get; set; } = "距离{考试}开始还有{分钟}分钟，请做好准备。";
    public string StartTemplate { get; set; } = "{考试}现在开始。";
    public List<int>? EndReminderMinutes { get; set; } = [15, 5];
    public bool EnableExamEndReminder { get; set; } = true;
    public string EndTemplate { get; set; } = "距离{考试}结束还有{分钟}分钟，请检查答题卡。";
    public string ExamEndTemplate { get; set; } = "{考试}已结束。";
    public bool EnableExamChangeReminder { get; set; } = true;
    public string ExamChangeTemplate { get; set; } = "考试安排已变更：{变更}";
    public bool EnableDayBeforeReminder { get; set; }
    public string DayBeforeReminderTime { get; set; } = "17:30";
    public string DayBeforeTemplate { get; set; } = "明天共 {数量} 场考试：首场 {首场} {首场时间}，末场 {末场} {末场时间}。";
    public int ReminderDurationSeconds { get; set; } = 10;
    public int ReminderGraceSeconds { get; set; } = 120;
    public bool EnableExamMode { get; set; } = true;
    public int ExamModeLeadMinutes { get; set; } = 15;
    public int ExamModeTailMinutes { get; set; }
    public int SessionBoundaryHour { get; set; } = 13;
    public string SchoolTimeZone { get; set; } = "Asia/Shanghai";
    public List<string> SuppressedProviders { get; set; } = [.. DefaultSuppressedProviderIds];
    public string SkippedSessionKey { get; set; } = string.Empty;
    public int OfflineChecksBeforeReopen { get; set; } = 2;
    public int BrowserCooldownSeconds { get; set; } = 120;
    public int MaxBrowserCooldownMinutes { get; set; } = 15;
    public bool EnableAnnouncements { get; set; } = true;
    public int AnnouncementRefreshMinutes { get; set; } = 30;
    public List<string> ReadAnnouncements { get; set; } = [];

    public static string CreateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
