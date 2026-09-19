using Microsoft.AspNetCore.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Web;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.AiGateway.Application;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.AiGateway.Api;

/// <summary>
/// AI 网关 —— 凭据保管 + 补全转发(2026-09-18)。
///
/// 为什么独立成服务(Forrest 定夺,方案 A):
///   1. 凭据只存一处 —— 换 key 只改一个地方。
///   2. 调用方只发"把这段 prompt 发给 LLM",不关心协议/端点/回退。
///   3. 加新 AI 功能不再复制凭据解析逻辑(Jobs/Interviews/Knowledge 共用)。
/// </summary>
[ApiController]
[Route("api/ai")]
[Authorize]
[Produces("application/json")]
public sealed class AiController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    private Guid Me => currentUser.UserId ?? Guid.Empty;

    // ---------------------------- 凭据 ----------------------------

    /// <summary>读当前用户的 LLM 配置状态(key 只回掩码)。</summary>
    [HttpGet("settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetSetting(CancellationToken ct)
    {
        var r = await sender.Send(new GetAiSettingQuery(Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>保存配置(保存前真测凭据,不通不入库)。</summary>
    [HttpPut("settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SaveSetting([FromBody] AiSettingsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveAiSettingCommand(Me, body.Protocol, body.ApiKey ?? string.Empty,
            body.BaseUrl, body.Model, body.Endpoint, body.ApiVersion, body.DisplayName), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>删除配置。</summary>
    [HttpDelete("settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> DeleteSetting(CancellationToken ct)
    {
        var r = await sender.Send(new DeleteAiSettingCommand(Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>只验证凭据连通性,不写库。前端流程:填 key → 本端点 → 通过才保存。</summary>
    [HttpPost("test-credential")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> TestCredential([FromBody] AiSettingsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new TestAiCredentialCommand(body.Protocol, body.ApiKey ?? string.Empty,
            body.BaseUrl, body.Model, body.Endpoint, body.ApiVersion), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------------------- 补全转发(核心) ----------------------------

    /// <summary>
    /// 通用补全 —— 任何服务需要 LLM 都调这里。
    ///
    /// 调用方视角:给 prompt,拿文本。凭据在哪、用哪家模型,调用方不关心。
    /// 记调用日志(不含 prompt 正文),便于按用途统计用量。
    /// </summary>
    [HttpPost("complete")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> Complete([FromBody] CompleteBody body, CancellationToken ct)
    {
        var r = await sender.Send(new CompleteCommand(Me, body.Purpose, body.Prompt,
            body.SystemPrompt, body.Temperature, body.MaxTokens, body.PreferJson), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }
}

/// <summary>配置提交体。ApiKey 为空表示"不改 key"(前端未重填时)。</summary>
public sealed record AiSettingsBody(string Protocol, string? ApiKey, string? BaseUrl, string? Model,
    string? Endpoint, string? ApiVersion, string? DisplayName);

/// <summary>补全请求体。</summary>
public sealed record CompleteBody(string Purpose, string Prompt, string? SystemPrompt = null,
    double? Temperature = null, int? MaxTokens = null, bool PreferJson = false);
