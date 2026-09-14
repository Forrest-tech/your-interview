using System.Net.Http.Json;
using System.Text.Json;

namespace YourInterview.Analysis.Worker.Consumers;

/// <summary>
/// 服务间调用令牌。
///
/// 为什么 Worker 不能匿名回写:Interviews 的写端点要求 InterviewsAnalyze 权限。
/// 让 Worker 绕过鉴权(比如加个内部网络白名单)会削弱整个权限模型 ——
/// 一旦内网可达就能写任何数据。
///
/// 正确做法:Worker 用**服务账号**正常登录拿 token。
/// 这样权限模型只有一套,服务账号的权限也能被审计、被撤销。
/// </summary>
public sealed class ServiceTokenProvider(IHttpClientFactory httpFactory, IConfiguration config,
    ILogger<ServiceTokenProvider> logger)
{
    private string? _cachedToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        // 内存缓存 + 提前 60 秒续期,避免临界点刚好过期
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-60))
            return _cachedToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-60))
                return _cachedToken;

            var identityUrl = config["Services:Identity"] ?? "http://127.0.0.1:5262";
            var email = config["ServiceAccount:Email"];
            var password = config["ServiceAccount:Password"];

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                logger.LogWarning("未配置服务账号,回写将失败(需要 ServiceAccount:Email/Password)");
                return null;
            }

            var http = httpFactory.CreateClient("identity");
            using var resp = await http.PostAsJsonAsync($"{identityUrl}/api/auth/login",
                new { email, password }, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                logger.LogError("服务账号登录失败 {(int)resp.StatusCode}:{Body}", (int)resp.StatusCode,
                    body[..Math.Min(200, body.Length)]);
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var token = json.GetProperty("tokens").GetProperty("accessToken").GetString();
            var expiresIn = json.GetProperty("tokens").TryGetProperty("expiresIn", out var e)
                ? e.GetInt32()
                : 3600;

            _cachedToken = token;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            logger.LogInformation("服务账号令牌获取成功,{Seconds}s 后过期", expiresIn);

            return token;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取服务令牌失败");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>给请求带上 Bearer 头。没有令牌时返回 false,由调用方决定怎么处理。</summary>
    public async Task<bool> AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        if (token is null) return false;
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return true;
    }
}
