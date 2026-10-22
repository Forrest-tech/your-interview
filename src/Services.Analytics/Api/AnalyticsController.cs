using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Analytics.Application;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Analytics.Api;

/// <summary>
/// Analytics API —— 全部是读接口(读模型的本分)。
///
/// 唯一两个写接口是 RecordAbility(让 Assessment 打完分立刻反映到雷达图)
/// 和 ResetReadModel(重建读模型用)。除此之外 Analytics 只从事件流接收输入。
/// </summary>
[ApiController]
[Route("api/analytics")]
[Authorize]
public sealed class AnalyticsController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    private Guid Me => currentUser.UserId ?? Guid.Empty;

    /// <summary>仪表盘首屏 —— 雷达图 + 趋势 + 漏斗 + 掌握分布 + 一句话洞察,一次拿全。</summary>
    [HttpGet("dashboard")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AnalyticsRead)]
    public async Task<IResult> Dashboard([FromQuery] int trendDays = 30, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetDashboardQuery(Me, trendDays), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>只要雷达图(小部件用)。可按来源过滤:mock / interview / knowledge。</summary>
    [HttpGet("radar")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AnalyticsRead)]
    public async Task<IResult> Radar([FromQuery] string? source = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetRadarQuery(Me, source), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>求职漏斗趋势。</summary>
    [HttpGet("pipeline")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AnalyticsRead)]
    public async Task<IResult> Pipeline([FromQuery] int days = 90, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetPipelineTrendQuery(Me, days), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>写入一条能力快照(Assessment 打分后立刻反映到雷达图)。</summary>
    /// <remarks>
    /// COD-02:归属默认以当前登录用户(Me)为准,请求体不接受 UserId,
    /// 杜绝带他人 UserId 替他人写入能力快照(可写型 IDOR)。
    /// 票56(解法A)可信通道:Worker 服务账号回写走 ?ownerUserId=(与票03 query 形态对齐),
    /// 仅当 JWT 带 perm claim analytics.writeback 时 honoured;判定见 AbilityOwnership。
    /// </remarks>
    [HttpPost("ability")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AnalyticsWrite)]
    public async Task<IResult> RecordAbility(
        [FromBody] RecordAbilityBody body,
        [FromQuery] Guid? ownerUserId = null,
        CancellationToken ct = default)
    {
        var callerPermissions = User.FindAll(CustomClaims.Permission).Select(c => c.Value);
        var owner = AbilityOwnership.Resolve(Me, callerPermissions, ownerUserId);
        var r = await sender.Send(new RecordAbilityCommand(
            owner, body.Dimension, body.Score, body.Source ?? "mock"), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>清空当前用户的读模型(重建/排障用)。</summary>
    [HttpPost("reset")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AnalyticsWrite)]
    public async Task<IResult> Reset(CancellationToken ct)
    {
        var r = await sender.Send(new ResetReadModelCommand(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }
}

public sealed record RecordAbilityBody(string Dimension, int Score, string? Source);
