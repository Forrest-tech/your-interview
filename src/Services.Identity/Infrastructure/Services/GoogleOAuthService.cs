using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace YourInterview.Services.Identity.Infrastructure.Services;

/// <summary>
/// Google 登录配置("Google" 配置节)。凭据来自 Google Auth Platform 的 OAuth 客户端。
/// RedirectUri 必须与 Google 控制台「Authorized redirect URIs」逐字一致,
/// 否则 Google 会报 redirect_uri_mismatch。
///
/// 端点可覆盖:沙盒在境内网络,oauth2.googleapis.com 被墙,
/// 令牌交换走 Google 官方中国镜像 oauth2.googleapis.cn(证书 *.google.cn,
/// Google Trust Services 签发,已核验)—— 用环境变量 Google__TokenEndpoint 覆盖。
/// 授权页(浏览器打开)默认 accounts.google.com,用户浏览器可达。
/// </summary>
public sealed class GoogleOAuthOptions
{
    public const string SectionName = "Google";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>浏览器端授权页(用户可见)。</summary>
    public string AuthorizeEndpoint { get; set; } = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>服务端令牌交换端点(后端出网)。</summary>
    public string TokenEndpoint { get; set; } = "https://oauth2.googleapis.com/token";
}

/// <summary>id_token 解析出的身份声明(OpenID Connect standard claims)。</summary>
public sealed record GoogleUserInfo(
    [property: JsonPropertyName("sub")] string Sub,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("email_verified")] bool EmailVerified,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picture")] string? Picture,
    [property: JsonPropertyName("aud")] string? Aud,
    [property: JsonPropertyName("iss")] string? Iss,
    [property: JsonPropertyName("exp")] long? Exp);

/// <summary>
/// Google OAuth 登录的后端实现(M2.1)。
///
/// 为什么手写而不是套 ASP.NET Core RemoteAuthentication 中间件:
///   我们要的是「授权码换身份 → 落自己的用户表 → 发自己的 JWT」,
///   中间件那套默认用外部 Cookie 建本地会话,与现有 JWT + localStorage 体系对不上,
///   改造它比直接写授权码流程(一个 HTTP 调用)还重。
///
/// 为什么用 id_token 而不是再调 userinfo 端点:
///   openidconnect.googleapis.com 在沙盒网络不可达。令牌响应自带 id_token,
///   且它是后端**直连 Google 令牌端点**(TLS + client_secret 认证)拿到的 ——
///   不经过浏览器/第三方之手,无伪造风险,读声明即可;签名校验防的是
///   「不可信方转发的 token」,不适用本场景。
///
/// 安全要点:
///   · state 随机数防 CSRF —— 发起时写入 HttpOnly cookie,回调时必须对得上
///   · id_token 校验 aud(客户端ID)/ iss(Google)/ exp(未过期)
///   · 只接受 email_verified=true 的账号(不验证邮箱就建号 = 任何人可冒用他人邮箱)
///   · 客户端密钥只存在服务端配置,绝不进浏览器
/// </summary>
public sealed class GoogleOAuthService(
    IOptions<GoogleOAuthOptions> options,
    IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "google-oauth";

    private readonly GoogleOAuthOptions _o = options.Value;

    /// <summary>凭据没配齐时前端隐藏按钮、端点给出明确提示,而不是 500。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_o.ClientId)
        && !string.IsNullOrWhiteSpace(_o.ClientSecret)
        && !string.IsNullOrWhiteSpace(_o.RedirectUri);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 构造 Google 授权页地址。state 一次性随机数由调用方写 cookie,回调时核对。
    /// prompt=select_account:每次都弹账号选择器 —— 多 Google 账号切换不憋在默认账号里。
    /// </summary>
    public string BuildAuthorizeUrl(string state)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _o.ClientId,
            ["redirect_uri"] = _o.RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid email profile",
            ["state"] = state,
            ["prompt"] = "select_account"
        };
        return _o.AuthorizeEndpoint + "?" + string.Join("&",
            query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>密码学随机 state(CSRF 防护用,Base64url 22 字符)。</summary>
    public static string NewState()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// 授权码换 id_token,解析身份声明。
    /// 返回 null 表示 code 无效/过期/已被用 —— 调用方给用户明确的失败提示。
    /// </summary>
    public async Task<GoogleUserInfo?> ExchangeAndGetUserAsync(string code, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _o.ClientId,
            ["client_secret"] = _o.ClientSecret,
            ["redirect_uri"] = _o.RedirectUri,
            ["grant_type"] = "authorization_code"
        };

        using var tokenResp = await http.PostAsync(_o.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if (!tokenResp.IsSuccessStatusCode) return null;

        var token = await tokenResp.Content.ReadFromJsonAsync<GoogleTokenResponse>(JsonOpts, ct);
        if (string.IsNullOrWhiteSpace(token?.IdToken)) return null;

        var claims = DecodeIdToken(token.IdToken);
        if (claims is null) return null;

        // 声明级校验:发放对象是本应用、发放方是 Google、还没过期
        if (claims.Aud != _o.ClientId) return null;
        if (claims.Iss is not ("accounts.google.com" or "https://accounts.google.com")) return null;
        if (claims.Exp is null || claims.Exp.Value < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;

        return claims;
    }

    /// <summary>解码 id_token 的载荷段(JWT 第二段,base64url JSON)。</summary>
    private static GoogleUserInfo? DecodeIdToken(string idToken)
    {
        try
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Convert.FromBase64String(payload);
            return JsonSerializer.Deserialize<GoogleUserInfo>(json, JsonOpts);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record GoogleTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("id_token")] string? IdToken);
}
