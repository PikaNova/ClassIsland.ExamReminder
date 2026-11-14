using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClassIsland.ExamReminder.Models;

namespace ClassIsland.ExamReminder.Services;

public sealed class ExamBoardClient : IDisposable
{
    public const string ClientVersion = "0.3.2.1";
    private readonly HttpClient _http;
    private readonly HttpClient _onlineHttp;
    private readonly PluginSettingsStore _store;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    public ExamBoardClient(PluginSettingsStore store, HttpClient? httpClient = null)
    {
        _store = store;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _onlineHttp = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ClientResult<PairStartResult>> StartPairingAsync(string baseUrl, string pluginInstanceId, string clientSecret,
        string pairToken, CancellationToken cancellationToken = default, bool online = false)
    {
        if (online)
        {
            var result = await PostAsync<PairStartResponse>(baseUrl,
                new { action = "plugin-pair-start", pluginInstanceId, clientSecret, pairToken, clientVersion = ClientVersion },
                cancellationToken, requireHttps: true, online: true);
            if (!result.IsSuccess || result.Value?.Ok != true || result.Value.PairUrl.Length == 0)
                return ClientResult<PairStartResult>.Failure(result.Error ?? "在线服务拒绝了配对请求") with
                { StatusCode = result.StatusCode, RetryAfter = result.RetryAfter, ErrorCode = result.ErrorCode };
            if (result.Value.ExpiresAt is not {} expiresAt || expiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                return ClientResult<PairStartResult>.Failure("在线服务返回的配对请求已过期");
            if (!TryValidatePairUrl(baseUrl, result.Value.PairUrl, pairToken, out var pairUrl))
                return ClientResult<PairStartResult>.Failure("在线服务返回了无效的配对页面地址");
            return ClientResult<PairStartResult>.Success(new PairStartResult(pairUrl, expiresAt));
        }

        var schoolResult = await PostAsync<SimpleResponse>(baseUrl,
            new { action = "plugin-pair-start", pluginInstanceId, clientSecret, pairToken, apiVersion = 2, clientVersion = ClientVersion },
            cancellationToken);
        return schoolResult.IsSuccess && schoolResult.Value?.Ok == true
            ? ClientResult<PairStartResult>.Success(new PairStartResult(ExamBoardUrls.Pairing(baseUrl, pairToken), null))
            : ClientResult<PairStartResult>.Failure(schoolResult.Error ?? "考试看板拒绝了配对请求") with
            { StatusCode = schoolResult.StatusCode, RetryAfter = schoolResult.RetryAfter, ErrorCode = schoolResult.ErrorCode };
    }

    public Task<ClientResult<PairStatusResponse>> GetPairStatusAsync(PluginSettings settings, CancellationToken cancellationToken = default) =>
        settings.ServiceMode == "online"
            ? PostAsync<PairStatusResponse>(settings.BaseUrl,
                new { action = "plugin-pair-status", pluginInstanceId = settings.PluginInstanceId, clientSecret = settings.ClientSecret, pairToken = settings.PendingPairToken },
                cancellationToken, requireHttps: true, online: true)
            : PostAsync<PairStatusResponse>(settings.BaseUrl,
                new { action = "plugin-pair-status", pluginInstanceId = settings.PluginInstanceId, clientSecret = settings.ClientSecret, apiVersion = 2, clientVersion = ClientVersion },
                cancellationToken);

    public async Task<ClientResult<ExamBoardBootstrapResponse>> GetBootstrapAsync(PluginSettings settings, string? knownVersion = null, CancellationToken cancellationToken = default)
    {
        if (settings.ServiceMode != "online")
            return await PostAsync<ExamBoardBootstrapResponse>(settings.BaseUrl,
                new { action = "plugin-bootstrap", pluginInstanceId = settings.PluginInstanceId, clientSecret = settings.ClientSecret, apiVersion = 2, clientVersion = ClientVersion, knownVersion, includeCompletedToday = true },
                cancellationToken);

        var result = await PostAsync<OnlineExamBootstrapResponse>(settings.BaseUrl,
            new { action = "plugin-bootstrap", pluginInstanceId = settings.PluginInstanceId, clientSecret = settings.ClientSecret, knownVersion },
            cancellationToken, requireHttps: true, online: true);
        if (!result.IsSuccess || result.Value?.Ok != true)
            return ClientResult<ExamBoardBootstrapResponse>.Failure(result.Error ?? "在线服务拒绝同步") with
            { StatusCode = result.StatusCode, RetryAfter = result.RetryAfter, ErrorCode = result.ErrorCode };

        var online = result.Value;
        var exam = online.Exam;
        DateTimeOffset serverTime;
        try { serverTime = DateTimeOffset.FromUnixTimeMilliseconds(online.ServerTimeUnixMilliseconds); }
        catch (ArgumentOutOfRangeException) { return ClientResult<ExamBoardBootstrapResponse>.Failure("在线服务返回的服务器时间无效"); }
        if (online.ApiVersion != 1 || string.IsNullOrWhiteSpace(online.Version) ||
            online.Capabilities?.Contains("online-exam-v1", StringComparer.Ordinal) != true)
            return ClientResult<ExamBoardBootstrapResponse>.Failure("在线服务返回了不兼容的插件协议");
        if (online.Changed && (exam is null || string.IsNullOrWhiteSpace(exam.Id) || string.IsNullOrWhiteSpace(exam.Title) ||
            string.IsNullOrWhiteSpace(exam.TimeZoneId) || exam.Items is null))
            return ClientResult<ExamBoardBootstrapResponse>.Failure("在线服务返回的考试快照无效");

        return ClientResult<ExamBoardBootstrapResponse>.Success(new ExamBoardBootstrapResponse
        {
            Ok = true,
            ApiVersion = online.ApiVersion,
            Capabilities = online.Capabilities,
            ServerTime = serverTime,
            Version = online.Version,
            Changed = online.Changed,
            ViewerOnline = true,
            TimeZoneId = exam?.TimeZoneId ?? string.Empty,
            Binding = exam is null ? null : new ExamBoardBinding { ClassTag = exam.Title },
            Exams = exam?.Items?.Select(item => new ExamBoardExam
            {
                Id = item.Id,
                Name = item.Name,
                StartAt = item.StartAt,
                EndAt = item.EndAt,
                Note = item.Note ?? string.Empty,
                SourceName = item.SourceName
            }).ToList() ?? []
        });
    }

    private async Task<ClientResult<T>> PostAsync<T>(string baseUrl, object body, CancellationToken cancellationToken,
        bool requireHttps = false, bool online = false)
    {
        if (!ExamBoardUrls.TryNormalizeBaseUrl(baseUrl, out var normalized, out var validationError)) return ClientResult<T>.Failure(validationError);
        if (requireHttps)
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri))
                return ClientResult<T>.Failure("在线服务地址无效");
            if (baseUri.Scheme != Uri.UriSchemeHttps)
                return ClientResult<T>.Failure("在线版连接必须使用 HTTPS");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_store.Settings.RequestTimeoutSeconds));
        try
        {
            var http = online ? _onlineHttp : _http;
            using var response = await http.PostAsJsonAsync(online ? ExamBoardUrls.OnlineApi(normalized) : ExamBoardUrls.Api(normalized), body, JsonOptions, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                ApiErrorResponse? payload = null;
                try { payload = await response.Content.ReadFromJsonAsync<ApiErrorResponse>(JsonOptions, timeout.Token); }
                catch (JsonException) { }
                var error = ErrorMessage(payload?.Code, response.StatusCode) ?? response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "插件凭据已失效，请重新连接",
                    HttpStatusCode.Conflict => "班级绑定已失效，请重新连接",
                    HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest => "当前考试看板不支持插件联动，请检查网址或更新看板",
                    HttpStatusCode.TooManyRequests => "服务器限流，稍后重试",
                    _ => $"考试看板连接失败（HTTP {(int)response.StatusCode}）"
                };
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is {} date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
                return ClientResult<T>.Failure(error) with
                { StatusCode = (int)response.StatusCode, RetryAfter = delay, ErrorCode = payload?.Code };
            }
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, timeout.Token);
            return value is null ? ClientResult<T>.Failure("考试看板返回了空数据") : ClientResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ClientResult<T>.Failure("连接考试看板超时"); }
        catch (HttpRequestException) { return ClientResult<T>.Failure("无法连接考试看板，请检查网址和网络"); }
        catch (JsonException) { return ClientResult<T>.Failure("考试看板返回的数据格式不兼容"); }
    }

    private static string? ErrorMessage(string? code, HttpStatusCode status) => code switch
    {
        "PAIR_EXPIRED" => "网页配对已过期，请重新连接",
        "PAIR_NOT_FOUND" => "网页配对请求无效，请重新连接",
        "PAIR_ALREADY_CONFIRMED" => "配对已在网页确认，正在同步考试",
        "AUTH_REQUIRED" => "插件实例凭据无效，请重新连接",
        "DEVICE_REVOKED" => "插件授权已撤销，请重新配对",
        "DEVICE_EXPIRED" => "插件授权已过期，请重新配对",
        "ENTITLEMENT_PENDING" => "在线服务尚未开通",
        "ENTITLEMENT_SUSPENDED" => "在线服务已暂停",
        "ENTITLEMENT_EXPIRED" => "在线服务已到期",
        "EXAM_NOT_PUBLISHED" => "只能连接已发布的考试",
        "EXAM_ARCHIVED" => "已连接的考试已归档，请重新选择考试",
        "RATE_LIMITED" => "请求过于频繁，请稍后重试",
        "VALIDATION_ERROR" => "在线服务请求格式无效",
        _ => null
    };

    private static bool TryValidatePairUrl(string baseUrl, string candidate, string pairToken, out Uri pairUrl)
    {
        pairUrl = null!;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ||
            !Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) ||
            candidateUri.Scheme != Uri.UriSchemeHttps || candidateUri.Scheme != baseUri.Scheme ||
            !candidateUri.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase) || candidateUri.Port != baseUri.Port ||
            !string.IsNullOrEmpty(candidateUri.UserInfo) || !string.IsNullOrEmpty(candidateUri.Fragment))
            return false;
        if (candidateUri.AbsolutePath != "/online/plugin/connect") return false;
        var query = candidateUri.Query;
        if (query.Length < 2) return false;
        var parameters = query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parameters.Length != 1) return false;
        var parts = parameters[0].Split('=', 2);
        if (parts.Length != 2) return false;
        string name;
        string token;
        try
        {
            name = Uri.UnescapeDataString(parts[0]);
            token = Uri.UnescapeDataString(parts[1]);
        }
        catch (UriFormatException) { return false; }
        if (name != "token") return false;
        if (!string.Equals(token, pairToken, StringComparison.Ordinal)) return false;
        pairUrl = candidateUri;
        return true;
    }

    public void Dispose()
    {
        _http.Dispose();
        if (!ReferenceEquals(_http, _onlineHttp)) _onlineHttp.Dispose();
    }

    private sealed class SimpleResponse { public bool Ok { get; set; } }
    private sealed class PairStartResponse
    {
        public bool Ok { get; set; }
        public string PairUrl { get; set; } = string.Empty;
        public long? ExpiresAt { get; set; }
    }
    private sealed class ApiErrorResponse
    {
        public string? Code { get; set; }
        public string? Error { get; set; }
    }
}
