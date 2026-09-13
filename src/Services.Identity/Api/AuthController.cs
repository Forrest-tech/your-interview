using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediatR;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Identity.Application.Abstractions;
using YourInterview.Services.Identity.Application.Admin;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.BuildingBlocks.Security;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// 认证控制器。RESTful 设计要点:
///  - 资源化路径 /api/auth/*、/api/admin/*
///  - 正确语义的状态码(201 建资源、204 无内容、400 校验、401 未认证、403 无权限、409 冲突)
///  - 错误统一 ProblemDetails(RFC 9457)
/// </summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>注册新用户(默认 User 角色)。</summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IResult> Register([FromBody] RegisterUserCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created("/api/auth/me", result.Value)
            : result.ToProblemDetails();
    }

    /// <summary>登录,返回 access token + refresh token。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IResult> Login([FromBody] LoginCommand command, CancellationToken ct)
    {
        var cmd = command with { Device = command.Device ?? Request.Headers.UserAgent.FirstOrDefault() };
        var result = await sender.Send(cmd, ct);
        return result.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(result.Value) : result.ToProblemDetails();
    }

    /// <summary>用刷新令牌换取新的访问令牌(轮转 + 复用检测)。</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<IResult> Refresh([FromBody] RefreshTokenCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(result.Value) : result.ToProblemDetails();
    }

    /// <summary>登出(撤销当前刷新令牌)。</summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IResult> Logout([FromBody] LogoutCommand command, CancellationToken ct)
    {
        var result = await sender.Send(command, ct);
        return result.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : result.ToProblemDetails();
    }

    /// <summary>获取当前登录用户(含角色与权限点,前端据此渲染菜单)。</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserProfile), StatusCodes.Status200OK)]
    public async Task<IResult> Me(CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null) return Microsoft.AspNetCore.Http.Results.Unauthorized();

        var result = await sender.Send(new GetCurrentUserQuery(userId.Value), ct);
        return result.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(result.Value) : result.ToProblemDetails();
    }

    /// <summary>修改自己的密码(会踢掉其它设备)。</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IResult> ChangePassword([FromBody] ChangePasswordRequest body, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null) return Microsoft.AspNetCore.Http.Results.Unauthorized();

        var result = await sender.Send(
            new ChangePasswordCommand(userId.Value, body.CurrentPassword, body.NewPassword), ct);
        return result.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : result.ToProblemDetails();
    }

    /// <summary>更新自己的资料。</summary>
    [HttpPut("me")]
    [Authorize]
    public async Task<IResult> UpdateProfile([FromBody] UpdateProfileRequest body, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null) return Microsoft.AspNetCore.Http.Results.Unauthorized();

        var result = await sender.Send(new UpdateProfileCommand(
            userId.Value, body.DisplayName, body.AvatarUrl, body.PreferredLanguage, body.TimeZone), ct);
        return result.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(result.Value) : result.ToProblemDetails();
    }

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record UpdateProfileRequest(string DisplayName, string? AvatarUrl, string? PreferredLanguage, string? TimeZone);
}

/// <summary>
/// 管理后台控制器。整控制器要求已认证 + 具体权限点(MVP 权限模型)。
/// 每个动作单独挂权限策略 —— 这是"最小权限原则"的落地。
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
[Produces("application/json")]
public sealed class AdminController(ISender sender) : ControllerBase
{
    // ---------- 概览 ----------

    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new AdminStatsQuery(), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------- 用户 ----------

    [HttpGet("users")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersRead)]
    public async Task<IResult> ListUsers([FromQuery] string? search, [FromQuery] string? role,
        [FromQuery] bool? isActive, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var r = await sender.Send(new AdminUserQuery(search, role, isActive, page, pageSize), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("users")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersWrite)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IResult> CreateUser([FromBody] AdminCreateUserCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command, ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/admin/users/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("users/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersWrite)]
    public async Task<IResult> UpdateUser(Guid id, [FromBody] AdminUpdateUserBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AdminUpdateUserCommand(id, body.DisplayName, body.IsActive,
            body.RequirePasswordChange, body.Roles), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("users/{id:guid}/reset-password")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersWrite)]
    public async Task<IResult> ResetPassword(Guid id, [FromBody] ResetPasswordBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AdminResetPasswordCommand(id, body.NewPassword), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("users/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersWrite)]
    public async Task<IResult> DeleteUser(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new AdminDeleteUserCommand(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 角色 ----------

    [HttpGet("roles")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersRead)]
    public async Task<IResult> ListRoles(CancellationToken ct)
    {
        var r = await sender.Send(new AdminRolesQuery(), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("roles")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminRolesWrite)]
    public async Task<IResult> CreateRole([FromBody] AdminUpsertRoleCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command with { RoleId = null }, ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/admin/roles/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("roles/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminRolesWrite)]
    public async Task<IResult> UpdateRole(Guid id, [FromBody] AdminUpsertRoleBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AdminUpsertRoleCommand(id, body.Name, body.Description, body.Permissions), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("roles/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminRolesWrite)]
    public async Task<IResult> DeleteRole(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new AdminDeleteRoleCommand(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpGet("permissions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersRead)]
    public async Task<IResult> PermissionCatalog(CancellationToken ct)
    {
        var r = await sender.Send(new AdminPermissionCatalogQuery(), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------- 审计 ----------

    [HttpGet("audit")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminAuditRead)]
    public async Task<IResult> Audit([FromQuery] string? action, [FromQuery] Guid? userId,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var r = await sender.Send(new AdminAuditLogQuery(action, userId, from, to, page, pageSize), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    public sealed record AdminUpdateUserBody(string DisplayName, bool IsActive, bool RequirePasswordChange, string[] Roles);
    public sealed record ResetPasswordBody(string NewPassword);
    public sealed record AdminUpsertRoleBody(string Name, string? Description, string[] Permissions);
}
