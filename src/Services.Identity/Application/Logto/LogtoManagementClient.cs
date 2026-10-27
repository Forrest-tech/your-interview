using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// Logto Management API 用户快照(只取审批门与扫表需要的字段)。
/// 票251 B4:Logto 1.45.0 的 Management API 用户对象根本没有邮箱验证字段
/// (沙盒实测:对真实 1.45.0 核心源码 grep + 用户 JSON 核对,无 primaryEmailVerified/
/// emailVerified/isEmailVerified 任一键)——本 DTO 因此不再带 EmailVerified:
/// 邮箱验证由 Console 的 sign-in experience 设置在 hosted 注册流程里强制
/// (见 docs/logto/SETUP.md §6),审批门不再独立查一个不存在的字段;
/// 批准与否只由管理员动作(批准端点 / 取消停用)决定。
/// UpdatedAt:悬停事件排序水位(见 LogtoApprovalGate),缺失时取 MinValue(永不过滤)。
/// </summary>
public sealed record LogtoUserDto(
    string Id,
    bool IsSuspended,
    JsonObject CustomData,
    DateTimeOffset CreatedAt,
    string? PrimaryEmail,
    DateTimeOffset UpdatedAt = default);

public interface ILogtoManagementClient
{
    Task<LogtoUserDto?> GetUserAsync(string userId, CancellationToken ct);
    Task SuspendUserAsync(string userId, bool suspended, CancellationToken ct);
    Task SetCustomDataAsync(string userId, JsonObject customData, CancellationToken ct);
    Task ResetPasswordAsync(string userId, string newPassword, CancellationToken ct);
    Task<IReadOnlyList<LogtoUserDto>> ListUsersAsync(int page, int pageSize, CancellationToken ct);
}

/// <summary>
/// 票241:Logto Management API 客户端(M2M client_credentials,token 缓存)。
/// 端点口径(官方文档 + 票251 对 1.45.0 的实测修正):token 在 {Endpoint}/oidc/token,
/// resource = 租户 Management API indicator(默认 https://default.logto.app/api,
/// 见 LogtoManagementOptions.ManagementApiResource)——{Endpoint}/api 在 1.45.0
/// 下回 400 oidc.invalid_target,绝不能再那样拼;
/// 用户操作在 {Endpoint}/api/users/*;停用 PATCH /users/{id}/is-suspended
/// {isSuspended};custom data PATCH /users/{id}/custom-data,体必须是
/// {"customData": {...}}(票251 B3:裸对象在 1.45.0 下回 400 guard.invalid_input);
/// 本客户端读-改-写全量对象,不依赖服务端的 merge 语义,避免把别的 customData 字段洗掉。
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
                ["resource"] = string.IsNullOrWhiteSpace(_options.ManagementApiResource)
                    ? "https://default.logto.app/api"
                    : _options.ManagementApiResource.Trim(),
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
        // 票251 B3:1.45.0 要求体为 {"customData": {...}};裸对象回 400。
        // 深拷贝一次:JsonNode 只能挂一个父节点,调用方传来的对象仍归调用方所有。
        var body = new JsonObject
        {
            ["customData"] = JsonNode.Parse(customData.ToJsonString())!.AsObject()
        };
        req.Content = JsonContent.Create(body);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ResetPasswordAsync(string userId, string newPassword, CancellationToken ct)
    {
        using var req = await RequestAsync(HttpMethod.Patch, $"/users/{Uri.EscapeDataString(userId)}/password", ct);
        req.Content = JsonContent.Create(new { password = newPassword });
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

    /// <summary>
    /// 票251 B2:Logto 1.45.0 的 createdAt/updatedAt 是数字(毫秒时间戳),
    /// 不是字符串 —— 旧代码 c.GetString() 直接抛,导致每次 GetUserAsync 都失败。
    /// 容数字毫秒时间戳、数字秒时间戳(防御)、ISO 字符串与纯数字字符串;都解析不出
    /// 时取 MinValue(排序水位永不过滤),绝不抛。
    /// </summary>
    public static DateTimeOffset ParseLogtoDate(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return DateTimeOffset.MinValue;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms))
            return ms >= 1_000_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : DateTimeOffset.FromUnixTimeSeconds(ms);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dnum))
        {
            var whole = (long)dnum;
            return whole >= 1_000_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(whole)
                : DateTimeOffset.FromUnixTimeSeconds(whole);
        }
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s) && long.TryParse(s, out var numeric))
                return numeric >= 1_000_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                    : DateTimeOffset.FromUnixTimeSeconds(numeric);
            if (DateTimeOffset.TryParse(s, out var parsed)) return parsed;
        }
        return DateTimeOffset.MinValue;
    }

    private static LogtoUserDto ParseUser(JsonElement el)
    {
        var customData = el.TryGetProperty("customData", out var cd) && cd.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(cd.GetRawText())!.AsObject()
            : new JsonObject();
        // 票251 B4:1.45.0 用户对象无邮箱验证字段,此处不再猜字段名、不再产出 EmailVerified。
        return new LogtoUserDto(
            el.GetProperty("id").GetString()!,
            el.TryGetProperty("isSuspended", out var s) && s.GetBoolean(),
            customData,
            ParseLogtoDate(el, "createdAt"),
            el.TryGetProperty("primaryEmail", out var e) ? e.GetString() : null,
            ParseLogtoDate(el, "updatedAt"));
    }
}
