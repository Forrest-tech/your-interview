using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>Logto Management API 用户快照(只取审批门与扫表需要的字段)。</summary>
public sealed record LogtoUserDto(
    string Id,
    bool IsSuspended,
    JsonObject CustomData,
    DateTimeOffset CreatedAt,
    string? PrimaryEmail);

public interface ILogtoManagementClient
{
    Task<LogtoUserDto?> GetUserAsync(string userId, CancellationToken ct);
    Task SuspendUserAsync(string userId, bool suspended, CancellationToken ct);
    Task SetCustomDataAsync(string userId, JsonObject customData, CancellationToken ct);
    Task<IReadOnlyList<LogtoUserDto>> ListUsersAsync(int page, int pageSize, CancellationToken ct);
}

/// <summary>
/// 票241:Logto Management API 客户端(M2M client_credentials,token 缓存)。
/// 端点口径(官方文档):token 在 {Endpoint}/oidc/token,resource={Endpoint}/api;
/// 用户操作在 {Endpoint}/api/users/*;停用 PATCH /users/{id}/is-suspended
/// {isSuspended};custom data PATCH /users/{id}/custom-data(本客户端读-改-写
/// 全量对象,不依赖服务端的 merge 语义,避免把别的 customData 字段洗掉)。
/// </summary>
public sealed class LogtoManagementClient : ILogtoManagementClient
{
    public const string HttpClientName = "logto-management";

    private readonly HttpClient _http;
    private readonly LogtoManagementOptions _options;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public LogtoManagementClient(IHttpClientFactory httpFactory, IOptions<LogtoManagementOptions> options)
    {
        _http = httpFactory.CreateClient(HttpClientName);
        _options = options.Value;
    }

    private string ApiBase => _options.Endpoint.TrimEnd('/') + "/api";

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt) return _token;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt) return _token;
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.M2mClientId,
                ["client_secret"] = _options.M2mClientSecret,
                ["resource"] = _options.Endpoint.TrimEnd('/') + "/api",
                ["scope"] = "all"
            });
            using var resp = await _http.PostAsync(_options.Endpoint.TrimEnd('/') + "/oidc/token", form, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            _token = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Logto token 响应缺 access_token");
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return _token;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, ApiBase + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(ct));
        return req;
    }

    public async Task<LogtoUserDto?> GetUserAsync(string userId, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Get, $"/users/{Uri.EscapeDataString(userId)}", ct);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ParseUser(await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct));
    }

    public async Task SuspendUserAsync(string userId, bool suspended, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Patch, $"/users/{Uri.EscapeDataString(userId)}/is-suspended", ct);
        req.Content = JsonContent.Create(new { isSuspended = suspended });
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task SetCustomDataAsync(string userId, JsonObject customData, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Patch, $"/users/{Uri.EscapeDataString(userId)}/custom-data", ct);
        req.Content = JsonContent.Create(customData);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<LogtoUserDto>> ListUsersAsync(int page, int pageSize, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Get, $"/users?page={page}&page_size={pageSize}", ct);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.EnumerateArray().Select(ParseUser).ToList();
    }

    private static LogtoUserDto ParseUser(JsonDocument doc) => ParseUser(doc.RootElement);

    private static LogtoUserDto ParseUser(JsonElement el)
    {
        var customData = el.TryGetProperty("customData", out var cd) && cd.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(cd.GetRawText())!.AsObject()
            : new JsonObject();
        return new LogtoUserDto(
            el.GetProperty("id").GetString()!,
            el.TryGetProperty("isSuspended", out var s) && s.GetBoolean(),
            customData,
            el.TryGetProperty("createdAt", out var c) && DateTimeOffset.TryParse(c.GetString(), out var d)
                ? d : DateTimeOffset.MinValue,
            el.TryGetProperty("primaryEmail", out var e) ? e.GetString() : null);
    }
}
