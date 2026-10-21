using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClassIsland.ExamReminder.Models;

namespace ClassIsland.ExamReminder.Services;

public sealed class ExamBoardClient : IDisposable
{
    public const string ClientVersion = "0.3.2.1";
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly PluginSettingsStore _store;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    public ExamBoardClient(PluginSettingsStore store) => _store = store;

    public async Task<ClientResult<Uri>> StartPairingAsync(string baseUrl, string pluginInstanceId, string clientSecret, string pairToken, CancellationToken cancellationToken = default)
    {
        var result = await PostAsync<SimpleResponse>(baseUrl, new { action = "plugin-pair-start", pluginInstanceId, clientSecret, pairToken, apiVersion = 2, clientVersion = ClientVersion }, cancellationToken);
        return result.IsSuccess && result.Value?.Ok == true
            ? ClientResult<Uri>.Success(ExamBoardUrls.Pairing(baseUrl, pairToken))
            : ClientResult<Uri>.Failure(result.Error ?? "考试看板拒绝了配对请求");
    }

    public Task<ClientResult<PairStatusResponse>> GetPairStatusAsync(PluginSettings settings, CancellationToken cancellationToken = default) =>
        PostAsync<PairStatusResponse>(settings.BaseUrl, new { action = "plugin-pair-status", pluginInstanceId = settings.PluginInstanceId, clientSecret = settings.ClientSecret, apiVersion = 2, clientVersion = ClientVersion }, cancellationToken);

    public Task<ClientResult<ExamBoardBootstrapResponse>> GetBootstrapAsync(PluginSettings settings, string? knownVersion = null, CancellationToken cancellationToken = default) =>
        PostAsync<ExamBoardBootstrapResponse>(settings.BaseUrl, new { action = "plugin-bootstrap", pluginInstanceId = settings.PluginInstanceId, clientSecret = settings.ClientSecret, apiVersion = 2, clientVersion = ClientVersion, knownVersion, includeCompletedToday = true }, cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<ClientResult<T>> PostAsync<T>(string baseUrl, object body, CancellationToken cancellationToken)
    {
        if (!ExamBoardUrls.TryNormalizeBaseUrl(baseUrl, out var normalized, out var validationError)) return ClientResult<T>.Failure(validationError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_store.Settings.RequestTimeoutSeconds));
        try
        {
            using var response = await _http.PostAsJsonAsync(ExamBoardUrls.Api(normalized), body, JsonOptions, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var error = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "插件凭据已失效，请重新连接",
                    HttpStatusCode.Conflict => "班级绑定已失效，请重新连接",
                    HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest => "当前考试看板不支持插件联动，请检查网址或更新看板",
                    HttpStatusCode.TooManyRequests => "服务器限流，稍后重试",
                    _ => $"考试看板连接失败（HTTP {(int)response.StatusCode}）"
                };
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is {} date ? date - DateTimeOffset.UtcNow : (TimeSpan?)null);
                return ClientResult<T>.Failure(error) with { StatusCode = (int)response.StatusCode, RetryAfter = delay };
            }
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, timeout.Token);
            return value is null ? ClientResult<T>.Failure("考试看板返回了空数据") : ClientResult<T>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return ClientResult<T>.Failure("连接考试看板超时"); }
        catch (HttpRequestException) { return ClientResult<T>.Failure("无法连接考试看板，请检查网址和网络"); }
        catch (JsonException) { return ClientResult<T>.Failure("考试看板返回的数据格式不兼容"); }
    }

    private sealed class SimpleResponse { public bool Ok { get; set; } }
}
