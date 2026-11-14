using System.Text.Json.Serialization;

namespace ClassIsland.ExamReminder.Models;

public sealed class ExamBoardExam
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("startAt")]
    public DateTimeOffset StartAt { get; set; }

    [JsonPropertyName("endAt")]
    public DateTimeOffset EndAt { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "major";

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reminderKey")]
    public string ReminderKey { get; set; } = string.Empty;

    [JsonPropertyName("majorExamId")]
    public string MajorExamId { get; set; } = string.Empty;

    [JsonPropertyName("weeklyPlanId")]
    public string WeeklyPlanId { get; set; } = string.Empty;

    [JsonPropertyName("weeklyItemId")]
    public string WeeklyItemId { get; set; } = string.Empty;

    [JsonPropertyName("pausedAt")]
    public DateTimeOffset? PausedAt { get; set; }

    [JsonPropertyName("pausedMs")]
    public long PausedMs { get; set; }

    public bool IsPaused => PausedAt is not null || string.Equals(Status, "paused", StringComparison.OrdinalIgnoreCase);
}

public sealed class ExamBoardLifecycleState
{
    [JsonPropertyName("reminderKey")]
    public string ReminderKey { get; set; } = string.Empty;

    [JsonPropertyName("majorExamId")]
    public string MajorExamId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("changedAt")]
    public DateTimeOffset? ChangedAt { get; set; }
}

public sealed class ExamBoardBinding
{
    public string GradeId { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    [JsonPropertyName("classTag")]
    public string ClassTag { get; set; } = string.Empty;
}

public sealed class ExamBoardBootstrapResponse
{
    public string Version { get; set; } = string.Empty;
    public bool Changed { get; set; } = true;
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; } = 1;

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("serverTime")]
    public DateTimeOffset ServerTime { get; set; }

    [JsonPropertyName("binding")]
    public ExamBoardBinding? Binding { get; set; }

    [JsonPropertyName("viewerOnline")]
    public bool ViewerOnline { get; set; }

    [JsonPropertyName("exams")]
    public List<ExamBoardExam> Exams { get; set; } = [];

    [JsonPropertyName("lifecycle")]
    public List<ExamBoardLifecycleState> Lifecycle { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public long UpdatedAt { get; set; }

    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; set; } = string.Empty;
}

public sealed class PairStatusResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("paired")]
    public bool Paired { get; set; }

    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; } = 1;

    [JsonPropertyName("classTag")]
    public string? ClassTag { get; set; }

    [JsonPropertyName("pairExpiresAt")]
    public long? PairExpiresAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public long? ExpiresAt { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
}

public sealed record PairStartResult(Uri PairUrl, long? ExpiresAt);

public sealed class OnlineExamBootstrapResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("serverTime")]
    public long ServerTimeUnixMilliseconds { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    [JsonPropertyName("exam")]
    public OnlinePublishedExam? Exam { get; set; }
}

public sealed class OnlinePublishedExam
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("timezone")]
    public string TimeZoneId { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public List<OnlinePublishedExamItem>? Items { get; set; }
}

public sealed class OnlinePublishedExamItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("startAt")]
    public DateTimeOffset StartAt { get; set; }

    [JsonPropertyName("endAt")]
    public DateTimeOffset EndAt { get; set; }

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = string.Empty;
}

public sealed record ClientResult<T>(T? Value, string? Error)
{
    public int? StatusCode { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string? ErrorCode { get; init; }
    public bool IsSuccess => Value is not null && Error is null;

    public static ClientResult<T> Success(T value) => new(value, null);

    public static ClientResult<T> Failure(string error) => new(default, error);
}
