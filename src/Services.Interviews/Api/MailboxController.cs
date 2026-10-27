using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Interviews.Application;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Interviews.Api;

/// <summary>
/// 票 240：邮箱直连导入 API（方案 §5.4，全部归属 Interviews 服务，网关零新增）。
/// 读 → InterviewsRead，写 → InterviewsWrite；唯独 OAuth 回调 AllowAnonymous
///（浏览器重定向不带 bearer，信任锚是 DataProtection 保护的 state，见 MailboxProtection）。
/// </summary>
[ApiController]
[Route("api/interviews")]
[Authorize]
public sealed class MailboxController(
    ISender sender,
    ICurrentUser currentUser,
    IOptions<MailboxOptions> options) : ControllerBase
{
    private Guid Me => currentUser.RequireUserId();

    /// <summary>发起连接：返回授权跳转 URL（前端直接跳转）。</summary>
    [HttpPost("mailbox/connect")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Connect([FromBody] ConnectMailboxBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ConnectMailboxCommand(Me, body.Provider), ct);
        return r.IsSuccess ? Results.Ok(new { authUrl = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>
    /// OAuth 回调（AllowAnonymous）：state 校验 → 换 token → 建连接 → 跳回前端配置页。
    /// 失败也跳回前端（仅带 mailbox=error 标志，不把 provider 原文带进 URL）。
    /// </summary>
    [HttpGet("mailbox/callback")]
    [AllowAnonymous]
    public async Task<IResult> Callback([FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, CancellationToken ct)
    {
        var frontend = options.Value.FrontendBaseUrl.TrimEnd('/');
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Results.Redirect($"{frontend}/settings/mail?mailbox=error");
        var r = await sender.Send(new MailboxCallbackCommand(code, state), ct);
        if (!r.IsSuccess)
            return Results.Redirect($"{frontend}/settings/mail?mailbox=error");
        return Results.Redirect(
            $"{frontend}/settings/mail?mailbox=connected&account={Uri.EscapeDataString(r.Value)}");
    }

    [HttpGet("mailbox/connections")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Connections(CancellationToken ct)
    {
        var r = await sender.Send(new GetMailboxConnectionsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>断开：删连接行 + 尽力撤销；已导入资源保留。</summary>
    [HttpDelete("mailbox/connections/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Disconnect(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DisconnectMailboxCommand(Me, id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>手动刷新：entryId 缺省 = 全部条目规则；否则仅该条目规则。</summary>
    [HttpPost("mailbox/refresh")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Refresh([FromBody] RefreshMailboxBody body, CancellationToken ct)
    {
        var r = await sender.Send(new RefreshMailboxCommand(Me, body.EntryId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("mailbox/pending")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Pending([FromQuery] Guid? entryId, CancellationToken ct)
    {
        var r = await sender.Send(new GetMailboxPendingQuery(Me, entryId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>确认待确认项：entryId 缺省 = 待确认行已定条目；歧义项必须传。</summary>
    [HttpPost("mailbox/pending/{id:guid}/confirm")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ConfirmPending(Guid id, [FromBody] ConfirmPendingBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new ConfirmMailboxPendingCommand(Me, id, body.EntryId), ct);
        return r.IsSuccess ? Results.Ok(new { assetId = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>忽略待确认项（已定条目时发件地址同时记入该条目排除列表）。</summary>
    [HttpPost("mailbox/pending/{id:guid}/ignore")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> IgnorePending(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new IgnoreMailboxPendingCommand(Me, id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpGet("mailbox/imports")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Imports([FromQuery] Guid? entryId, CancellationToken ct)
    {
        var r = await sender.Send(new GetMailboxImportsQuery(Me, entryId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>票 245：OAuth 凭据卡读（Secret 永不回显，只给 secretSaved / secretUnreadable 状态位）。</summary>
    [HttpGet("mailbox/oauth-settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetOAuthSettings(CancellationToken ct)
    {
        var r = await sender.Send(new GetMailboxOAuthSettingsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>票 245：OAuth 凭据卡保存（Secret 写-only：留空 = 沿用已存密文，首次保存必填）。</summary>
    [HttpPut("mailbox/oauth-settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SaveOAuthSettings([FromBody] SaveOAuthSettingsBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new SaveMailboxOAuthSettingsCommand(
            body.Provider, body.ClientId, body.ClientSecret, body.RedirectUri), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>票 245：OAuth 凭据卡清除（删 DB 行，恢复环境变量兜底；不解密、不碰连接）。</summary>
    [HttpDelete("mailbox/oauth-settings/{provider}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ClearOAuthSettings(string provider, CancellationToken ct)
    {
        var r = await sender.Send(new ClearMailboxOAuthSettingsCommand(provider), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>条目级匹配规则读（条目详情页邮箱匹配区用；设置页绝不放规则）。</summary>
    [HttpGet("{entryId:guid}/mail-match-rules")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetRules(Guid entryId, CancellationToken ct)
    {
        var r = await sender.Send(new GetMailMatchRulesQuery(Me, entryId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPut("{entryId:guid}/mail-match-rules")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateRules(Guid entryId, [FromBody] UpdateRulesBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpdateMailMatchRulesCommand(Me, entryId,
            body.Domains ?? [], body.Contacts ?? [], body.Keywords ?? [], body.Exclusions ?? []), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }
}

/// <summary>POST /mailbox/connect body { provider: "Gmail" | "Graph" }。</summary>
public sealed record ConnectMailboxBody(string Provider);

/// <summary>POST /mailbox/refresh body { entryId? }（缺省 = 全部条目）。</summary>
public sealed record RefreshMailboxBody(Guid? EntryId = null);

/// <summary>POST /mailbox/pending/{id}/confirm body { entryId? }（歧义项必须传）。</summary>
public sealed record ConfirmPendingBody(Guid? EntryId = null);

/// <summary>PUT /{entryId}/mail-match-rules body：四张表（逗号分隔由前端拆成数组）。</summary>
public sealed record UpdateRulesBody(
    List<string>? Domains, List<string>? Contacts, List<string>? Keywords, List<string>? Exclusions);

/// <summary>PUT /mailbox/oauth-settings body：Secret 留空 = 沿用已存密文（首次保存必填）。</summary>
public sealed record SaveOAuthSettingsBody(
    string Provider, string ClientId, string? ClientSecret, string RedirectUri);
