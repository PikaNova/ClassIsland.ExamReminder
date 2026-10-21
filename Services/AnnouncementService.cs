using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;

namespace ClassIsland.ExamReminder.Services;

public sealed class AuthorAnnouncement
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string Summary { get; set; } = "";
    public bool Pinned { get; set; }
    public string? Url { get; set; }
    public string? ButtonLabel { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
    [JsonPropertyName("publish_at")] public long? PublishAt { get; set; }
    [JsonPropertyName("unpublish_at")] public long? UnpublishAt { get; set; }
    public string ReadKey => $"{Id}|{UpdatedAt}";
}

public sealed record AnnouncementCache(string Source, DateTimeOffset CheckedAt, List<AuthorAnnouncement> Items);

public sealed class AnnouncementService(PluginSettingsStore store) : BackgroundService
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly SemaphoreSlim _wake = new(0, 1);
    private List<AuthorAnnouncement> _items = [];
    private string _source = "";
    public string Status { get; private set; } = "等待获取作者公告";
    /// <summary>最近一次成功获取或从本地缓存恢复的时间。</summary>
    public DateTimeOffset? LastCheckedAt { get; private set; }
    /// <summary>最近一次发起检查的时间，失败时也会更新。</summary>
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public bool IsChecking { get; private set; }
    /// <summary>供诊断区展示的原始错误；主界面只显示友好的中文状态。</summary>
    public string LastErrorDetail { get; private set; } = "";
    public event EventHandler? Changed;
    public IReadOnlyList<AuthorAnnouncement> Items
    {
        get
        {
            try { if (_source != Source().AbsoluteUri) return []; }
            catch (ArgumentException) { return []; }
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return _items.Where(x => (x.PublishAt is null || x.PublishAt <= now) && (x.UnpublishAt is null || x.UnpublishAt > now))
                .OrderByDescending(x => x.Pinned).ThenByDescending(x => x.UpdatedAt).ToList();
        }
    }
    public int UnreadCount => Items.Count(x => !IsRead(x));
    public bool IsRead(AuthorAnnouncement item) => store.Settings.ReadAnnouncements.Contains(ReadKey(item), StringComparer.Ordinal);
    public void MarkRead(AuthorAnnouncement item) => store.Update(s => s.ReadAnnouncements = s.ReadAnnouncements.Append(ReadKey(item)).Distinct().TakeLast(1000).ToList());
    public void MarkAllRead() => store.Update(s => s.ReadAnnouncements = s.ReadAnnouncements.Concat(Items.Select(ReadKey)).Distinct().TakeLast(1000).ToList());
    private string ReadKey(AuthorAnnouncement item) => _source + "|" + item.ReadKey;
    public void Refresh() { if (_wake.CurrentCount == 0) _wake.Release(); }

    public Uri Source()
    {
        var url = string.IsNullOrWhiteSpace(store.Settings.BaseUrl)
            ? "https://telemetry.pikachu2026.space/api/public-announcements?limit=30"
            : store.Settings.BaseUrl.TrimEnd('/') + "/api/announcements?limit=30";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("公告地址必须为不含凭据的 HTTP 或 HTTPS 完整网址");
        return uri;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _wake.WaitAsync(TimeSpan.FromSeconds(15), stoppingToken);
            var failures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (store.Settings.EnableAnnouncements)
                {
                    try
                    {
                        IsChecking = true;
                        LastAttemptAt = DateTimeOffset.Now;
                        var uri = Source();
                        if (_source != uri.AbsoluteUri)
                        {
                            _source = uri.AbsoluteUri;
                            _items = [];
                            LastCheckedAt = null;
                            try
                            {
                                var cache = store.Read<AnnouncementCache>("Announcements.cache.json");
                                if (cache?.Source == _source) { _items = cache.Items; LastCheckedAt = cache.CheckedAt; }
                            }
                            catch { /* A damaged optional cache must not stop exam reminders. */ }
                            Changed?.Invoke(this, EventArgs.Empty);
                        }
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(store.Settings.RequestTimeoutSeconds));
                        using var response = await _http.GetAsync(uri, timeout.Token);
                        response.EnsureSuccessStatusCode();
                        var data = await response.Content.ReadFromJsonAsync<AnnouncementResponse>(timeout.Token);
                        if (data?.Ok != true || data.Announcements is null) throw new InvalidDataException("公告响应无效");
                        // Source may change while the request is in flight.
                        if (uri != Source()) continue;
                        _items = data.Announcements.Where(x => x.Id > 0 && !string.IsNullOrWhiteSpace(x.Title) && x.UpdatedAt is >= 0 and <= 253402300799999).Take(50).ToList();
                        LastCheckedAt = DateTimeOffset.Now;
                        Status = "作者公告已更新";
                        LastErrorDetail = "";
                        try { store.Write("Announcements.cache.json", new AnnouncementCache(_source, LastCheckedAt.Value, _items)); }
                        catch (Exception ex)
                        {
                            LastErrorDetail = ex.GetBaseException().Message;
                            Status = "作者公告已更新，但本地缓存未保存";
                        }
                        failures = 0;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        failures = Math.Min(failures + 1, 5);
                        LastErrorDetail = ex.GetBaseException().Message;
                        Status = LastCheckedAt is null
                            ? $"公告暂不可用，暂无缓存 · {Describe(ex)}"
                            : $"公告暂不可用，保留缓存 · {Describe(ex)}";
                    }
                    finally { IsChecking = false; }
                }
                else { IsChecking = false; Status = "作者公告检查已关闭"; }
                Changed?.Invoke(this, EventArgs.Empty);
                var delay = Math.Min(1440, store.Settings.AnnouncementRefreshMinutes * Math.Pow(2, failures));
                await _wake.WaitAsync(TimeSpan.FromMinutes(delay), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "公告服务拒绝访问，请检查地址",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "公告地址不存在",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "公告服务限流，请稍后重试",
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 => $"公告服务暂时不可用（HTTP {(int)status}）",
        HttpRequestException => "无法连接公告服务，请检查网络",
        OperationCanceledException => "公告请求超时",
        JsonException or InvalidDataException => "公告响应格式异常",
        ArgumentException => "公告地址无效",
        _ => "公告获取失败"
    };

    public override void Dispose() { base.Dispose(); _http.Dispose(); _wake.Dispose(); }
    private sealed class AnnouncementResponse
    {
        public bool Ok { get; set; }
        public List<AuthorAnnouncement>? Announcements { get; set; }
    }
}
