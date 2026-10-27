using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// 票241:Logto 会话的 /me(与旧 /api/auth/me 并行,双轨期专用)。
/// 旧 /me 按 Guid 查本地 users 表 —— Logto 新用户在本地没有行,会 404;
/// 本端点直接用 token 里的声明组装同一 UserProfile 结构(角色与权限点
/// 由共享门 LogtoClaimsGate 在鉴权时按镜像 + scope 展开),前端会话建立不依赖本地行。
/// 待审批用户:门把 NameIdentifier 翻译成业务 Guid 但不给 perm,本端点返回
/// 200 + 空权限 —— 前端据此显示待审批态,绝不进 dashboard(见前端回调处理)。
/// 迁移用户同时有本地行:新旧两条 /me 返回的权限一致(同一映射表)。
/// </summary>
[ApiController]
[Route("api/auth/logto")]
[Authorize]
public sealed class LogtoProfileController : ControllerBase
{
    [HttpGet("me")]
    public IActionResult Me()
    {
        var idRaw = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (idRaw is null || !Guid.TryParse(idRaw, out var id)) return Unauthorized();
        var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email") ?? string.Empty;
        var displayName = User.FindFirstValue(CustomClaims.FullName)
            ?? User.FindFirstValue("name") ?? email;
        return Ok(new UserProfile(
            id, email, displayName,
            User.FindFirstValue(CustomClaims.Avatar),
            false,
            User.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToArray(),
            User.FindAll(CustomClaims.Permission).Select(c => c.Value).Distinct().ToArray(),
            null, null, null, DateTimeOffset.UtcNow));
    }
}
