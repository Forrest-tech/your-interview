using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using YourInterview.Services.Identity.Infrastructure.Security;
using YourInterview.Services.Identity.Infrastructure.Services;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// Google 登录(M2.1)。授权码流程:
///   浏览器 → /authorize(302 → Google 授权页)
///   用户点同意 → Google 302 → /callback?code&state
///   后端换身份 → 邮箱找不到就建号(默认 User 角色)→ 发自家 JWT
///   → 302 回前端 /login#access_token=…(fragment 不进服务器日志)
///
/// 所有失败路径都重定向回登录页带 googleError 参数 —— 中间没有可登录的会话,
/// 返回 500 白页对用户毫无意义。
/// </summary>
[ApiController]
[Route("api/auth/google")]
[Produces("application/json")]
public sealed class GoogleAuthController(
    GoogleOAuthService google,
    IdentityDbContext db,
    IPasswordHasher hasher,
    AuthResponseBuilder authBuilder,
    ICurrentUser currentUser,
    ILogger<GoogleAuthController> logger) : ControllerBase
{
    private const string StateCookie = "yi.g_state";

    /// <summary>登录页据此决定是否显示 Google 按钮(未配置凭据时隐藏)。</summary>
    [HttpGet("status")]
    [AllowAnonymous]
    public IActionResult Status() => Ok(new { enabled = google.IsConfigured });

    /// <summary>发起 Google 登录。returnUrl(本地路径)原样带回给前端。</summary>
    [HttpGet("authorize")]
    [AllowAnonymous]
    public IActionResult Authorize([FromQuery] string? returnUrl)
    {
        if (!google.IsConfigured)
            return FailToLogin("Google 登录尚未配置,请使用邮箱登录");

        // returnUrl 只允许站内路径(防开放重定向);为空/非法就回首页
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//"))
            returnUrl = "/dashboard";

        var state = GoogleOAuthService.NewState();
        Response.Cookies.Append(StateCookie, $"{state}|{returnUrl}", new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,   // 回调是跨站顶层 GET 导航,Lax 才会带上 cookie
            MaxAge = TimeSpan.FromMinutes(10),
            Path = "/"
        });

        return Redirect(google.BuildAuthorizeUrl(state));
    }

    /// <summary>Google 授权后的回程。换身份 → 建号/登录 → 带 token 跳回前端。</summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, CancellationToken ct)
    {
        // 拆 state(回调里无论对错都要清 cookie)
        var rawState = Request.Cookies.TryGetValue(StateCookie, out var v) ? v : null;
        Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/" });

        var returnTo = "/dashboard";
        if (!string.IsNullOrEmpty(rawState))
        {
            var parts = rawState.Split('|', 2);
            if (parts.Length == 2 && parts[1].StartsWith('/') && !parts[1].StartsWith("//"))
                returnTo = parts[1];
        }

        if (!string.IsNullOrEmpty(error))
        {
            logger.LogWarning("Google 登录被用户取消或出错:{Error}", error);
            return FailToLogin("Google 登录未完成", returnTo);
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            return FailToLogin("Google 登录参数缺失", returnTo);

        // state 核对(防 CSRF:发起时种的随机数必须一致)
        if (string.IsNullOrEmpty(rawState) || rawState.Split('|')[0] != state)
            return FailToLogin("登录会话已过期,请重试", returnTo);

        var info = await google.ExchangeAndGetUserAsync(code, ct);
        if (info is null)
        {
            logger.LogWarning("Google 授权码换取失败(code 无效/过期/复用)");
            return FailToLogin("Google 登录失败,请重试", returnTo);
        }

        if (!info.EmailVerified)
            return FailToLogin("该 Google 账号的邮箱未通过验证,无法登录", returnTo);

        var email = AppUser.Normalize(info.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is not null && !user.IsActive)
            return FailToLogin("账号已停用,请联系管理员", returnTo);

        if (user is null)
        {
            // Google 账号首次登录:自动注册。密码置为不可登录的随机哈希
            // —— 密码哈希列非空,但随机盐值永远校验不过,密码登录对这类账号天然关闭。
            user = new AppUser(email,
                string.IsNullOrWhiteSpace(info.Name) ? email.Split('@')[0] : info.Name!,
                hasher.Hash(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")),
                info.Picture);
            db.Users.Add(user);

            var defaultRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == Roles.User, ct);
            if (defaultRole is not null) user.AssignRole(defaultRole.Id);

            logger.LogInformation("Google 登录自动注册新用户 {Email}", email);
        }
        else if (!string.IsNullOrWhiteSpace(info.Picture) && user.AvatarUrl != info.Picture)
        {
            user.SetAvatar(info.Picture);   // 头像跟随 Google 更新
        }

        user.RecordSuccessfulLogin();
        await db.SaveChangesAsync(ct);

        var result = await authBuilder.BuildAsync(user, "google-oauth", currentUser.IpAddress, ct);

        // token 走 URL fragment(#):不会出现在服务器日志/Referer 里
        var fragment = $"access_token={Uri.EscapeDataString(result.Tokens.AccessToken)}"
            + $"&refresh_token={Uri.EscapeDataString(result.Tokens.RefreshToken)}"
            + $"&expires_at={Uri.EscapeDataString(result.Tokens.AccessTokenExpiresAt.ToString("O"))}"
            + $"&returnUrl={Uri.EscapeDataString(returnTo)}";
        return Redirect($"/login#{fragment}");
    }

    private RedirectResult FailToLogin(string message, string returnUrl = "/login")
        => Redirect($"/login?googleError={Uri.EscapeDataString(message)}"
                    + $"&returnUrl={Uri.EscapeDataString(returnUrl)}");
}
