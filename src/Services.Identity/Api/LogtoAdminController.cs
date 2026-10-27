using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Identity.Application.Admin;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.SharedContracts.Security;
using MediatR;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// 票241:Logto 原生管理端(已映射用户的管理面)。
/// 分工(不要混淆):旧 /api/admin 只管未映射账号 —— 对已映射用户的删除/重置/
/// 启用停用一律拒绝(见 AdminHandlers 的映射块);已映射用户走本控制器 +
/// Logto Console。旧 /admin 页面在切换期只读(前端 logtoCutover 开关),
/// 收敛后随旧 Identity 一起退役,Console 即停用路径。
/// approve 与 unlock 是干净分开的两个动作:unlock 只解封、不改审批标记,
/// 且拒绝 pending 用户(pending 必须走 approve);approve 接受一切 pending 用户
/// (票251 B4:1.45.0 的用户对象没有邮箱验证字段,旧“未验证一律 409”门在实测里
/// 恒成立、挡住了每一次批准,故删除;邮箱验证由 Console 的 sign-in experience
/// 设置在 hosted 注册流程里强制,见 SETUP.md §6)。
/// </summary>
[ApiController]
[Route("api/admin/logto")]
[Authorize]
public sealed class LogtoAdminController(
    ILogtoManagementClient client,
    ILogtoMappingStore mappings,
    ISender sender,
    IOptions<LogtoManagementOptions> options) : ControllerBase
{
    private const string WritePolicy = PermissionPolicy.Prefix + Permissions.AdminUsersWrite;
    private const string ReadPolicy = PermissionPolicy.Prefix + Permissions.AdminUsersRead;

    private bool Configured => options.Value.ManagementConfigured;

    /// <summary>批准:取消停用 + approval=pending→approved,两步都成功才算成功。</summary>
    [HttpPost("users/{logtoUserId}/approve")]
    [Authorize(Policy = WritePolicy)]
    public async Task<IResult> Approve(string logtoUserId, CancellationToken ct)
    {
        if (!Configured) return Results.Json(new { error = "Logto Management 未配置" }, statusCode: 503);
        var user = await client.GetUserAsync(logtoUserId, ct);
        if (user is null) return Results.NotFound(new { error = "Logto 用户不存在" });
        var approval = GetApproval(user);
        if (approval is "approved" or "migrated") return Results.Ok(new { status = "already-approved", approval });
        if (approval != "pending") return Results.Conflict(new { error = $"该用户审批态为 {approval ?? "(空)"},不是 pending,无法批准" });
        // 票251 B4:此处不再查邮箱验证 —— 1.45.0 用户对象无此字段,旧 409 门恒成立。
        try
        {
            await client.SuspendUserAsync(logtoUserId, false, ct);
            var customData = Clone(user.CustomData);
            customData["approval"] = "approved";
            await client.SetCustomDataAsync(logtoUserId, customData, ct);
            var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
            await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId,
                row?.LegacyUserId ?? ParseLegacyId(user) ?? Guid.NewGuid(),
                user.PrimaryEmail ?? row?.Email ?? string.Empty,
                "approved", false, false, null), ct);
        }
        catch (Exception ex)
        {
            // 批准是同步动作:任一步失败都算失败(5xx),管理员重试;
            // 已解封未打标的瞬态由扫表 fail-closed 重置停用兜住,管理员再点一次批准即恢复。
            return Results.Json(new { error = "批准未完成,请重试", detail = ex.Message }, statusCode: 500);
        }
        return Results.Ok(new { status = "approved" });
    }

    /// <summary>重置密码:走 Management API(密码策略由 Logto 校验,拒绝时回 400)。</summary>
    [HttpPost("users/{logtoUserId}/reset-password")]
    [Authorize(Policy = WritePolicy)]
    public async Task<IResult> ResetPassword(string logtoUserId, [FromBody] ResetPasswordBody body, CancellationToken ct)
    {
        if (!Configured) return Results.Json(new { error = "Logto Management 未配置" }, statusCode: 503);
        if (string.IsNullOrWhiteSpace(body.NewPassword))
            return Results.BadRequest(new { error = "新密码不能为空" });
        var user = await client.GetUserAsync(logtoUserId, ct);
        if (user is null) return Results.NotFound(new { error = "Logto 用户不存在" });
        try { await client.ResetPasswordAsync(logtoUserId, body.NewPassword, ct); }
        catch (HttpRequestException ex)
        {
            return Results.BadRequest(new { error = "Logto 拒绝了新密码(可能不符合 Logto 密码策略)或 Management 调用失败", detail = ex.Message });
        }
        return Results.NoContent();
    }

    /// <summary>解锁:只解封,不改审批标记;pending 用户拒绝(必须走 approve)。</summary>
    [HttpPost("users/{logtoUserId}/unlock")]
    [Authorize(Policy = WritePolicy)]
    public async Task<IResult> Unlock(string logtoUserId, CancellationToken ct)
    {
        if (!Configured) return Results.Json(new { error = "Logto Management 未配置" }, statusCode: 503);
        var user = await client.GetUserAsync(logtoUserId, ct);
        if (user is null) return Results.NotFound(new { error = "Logto 用户不存在" });
        if (GetApproval(user) == "pending")
            return Results.Json(new { error = "待审批用户不能走解锁,请走批准(approve)" }, statusCode: 409);
        if (!user.IsSuspended) return Results.Ok(new { status = "already-active" });
        await client.SuspendUserAsync(logtoUserId, false, ct);
        var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
        if (row is not null)
            await mappings.UpsertRowAsync(row with { IsSuspended = false, SuspendedByGate = false }, ct);
        return Results.Ok(new { status = "unlocked" });
    }

    /// <summary>排障视图:Logto 现态 + 镜像行(供收敛检查与演练断言)。</summary>
    [HttpGet("users/{logtoUserId}")]
    [Authorize(Policy = ReadPolicy)]
    public async Task<IResult> GetUser(string logtoUserId, CancellationToken ct)
    {
        if (!Configured) return Results.Json(new { error = "Logto Management 未配置" }, statusCode: 503);
        var user = await client.GetUserAsync(logtoUserId, ct);
        if (user is null) return Results.NotFound(new { error = "Logto 用户不存在" });
        var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
        return Results.Ok(new
        {
            logtoUserId = user.Id,
            primaryEmail = user.PrimaryEmail,
            // 票251 B4:emailVerified 字段已删 —— 1.45.0 用户对象无此字段,不得再输出一个恒假的值。
            isSuspended = user.IsSuspended,
            approval = GetApproval(user),
            mirror = row is null ? null : new
            {
                row.LegacyUserId, row.Email, row.Approval, row.IsSuspended, row.SuspendedByGate, row.LastEventAt
            }
        });
    }

    /// <summary>
    /// 回滚重导:把灰度/收敛后才在 Logto 注册的用户按映射表 Guid 在旧库重建。
    /// Guid 绝不重 mint —— 灰度期业务数据就是按这个 Guid 记的,重 mint 等于孤儿数据。
    /// 密码无法重导(Logto 不暴露哈希):重建账号带 MustChangePassword,管理员用
    /// 已有重置端点发临时密码、运维侧通知用户(不新建设置密码端点)。
    /// </summary>
    [HttpPost("users/import")]
    [Authorize(Policy = WritePolicy)]
    public async Task<IResult> Import([FromBody] ImportBody body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.LogtoUserId))
            return Results.BadRequest(new { error = "LogtoUserId 不能为空(取自回滚导出文件)" });
        if (!Configured) return Results.Json(new { error = "Logto Management 未配置" }, statusCode: 503);
        // 启用态取 fresh Logto 现态(不信任 body 里的状态描述):只有 approved/migrated
        // 且未停用才导为启用,pending/已停用一律导为停用(round-13)。
        var logtoUser = await client.GetUserAsync(body.LogtoUserId, ct);
        if (logtoUser is null) return Results.NotFound(new { error = "Logto 用户不存在" });
        var r = await sender.Send(new AdminImportLogtoUserCommand(
            body.LegacyUserId, body.LogtoUserId, body.Email, body.DisplayName,
            logtoUser.IsSuspended, GetApproval(logtoUser)), ct);
        return r.IsSuccess
            ? Results.Created($"/api/admin/users/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    public sealed record ResetPasswordBody(string NewPassword);
    public sealed record ImportBody(Guid LegacyUserId, string LogtoUserId, string Email, string DisplayName);

    private static string? GetApproval(LogtoUserDto user) =>
        user.CustomData.TryGetPropertyValue("approval", out var v) ? v?.GetValue<string>() : null;

    private static Guid? ParseLegacyId(LogtoUserDto user) =>
        user.CustomData.TryGetPropertyValue("legacyUserId", out var v)
        && Guid.TryParse(v?.GetValue<string>(), out var g) ? g : null;

    private static JsonObject Clone(JsonObject source) =>
        JsonNode.Parse(source.ToJsonString())!.AsObject();
}
