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

    /// <summary>列出内置 provider 预设与常用模型(供设置页下拉)。无密钥,可安全下发。
    /// M2.3:从前端改调本端点(原 Assessment 的 ai/providers 已随收敛移除)。</summary>
    [HttpGet("providers")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> ListProviders(CancellationToken ct)
    {
        var list = await sender.Send(new ListAiProvidersQuery(), ct);
        return Microsoft.AspNetCore.Http.Results.Ok(list);
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

    // ---------------------------- 备考数据源(2026-10-21) ----------------------------

    /// <summary>读当前用户的备考数据源配置(搜索密钥只回掩码)。</summary>
    [HttpGet("prep-sources/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> GetPrepSourceSettings(CancellationToken ct)
    {
        var r = await sender.Send(new GetPrepSourceSettingsQuery(Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>保存备考数据源配置(开关 + 搜索服务商/密钥;密钥为空 = 不改)。</summary>
    [HttpPut("prep-sources/settings")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> SavePrepSourceSettings([FromBody] PrepSourceSettingsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SavePrepSourceSettingsCommand(Me,
            body.QuestionBankEnabled, body.CompanyBackgroundEnabled,
            body.WebExperiencesEnabled, body.GlassdoorEnabled,
            body.SearchProvider, body.SearchApiKey), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 按公司 + 岗位检索一个外部数据源。状态如实返回
    /// (fetched/disabled/notConfigured/noData/failed),不编造内容。
    /// </summary>
    [HttpPost("prep-sources/search")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SearchPrepSources([FromBody] PrepSourceSearchBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SearchPrepSourcesCommand(Me, body.Source, body.Company, body.Role, body.Stage), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 截图视觉 OCR —— 把图片经用户配置的视觉模型转成文字(定稿规则①)。
    /// 状态如实返回(ok/notConfigured/unsupported/failed),失败不编造文本。
    /// </summary>
    [HttpPost("vision/extract-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<IResult> ExtractImageText([FromBody] ImageTextBody body, CancellationToken ct)
    {
        var dto = await sender.Send(new ExtractImageTextCommand(Me, body.ImageBase64, body.MimeType), ct);
        return Microsoft.AspNetCore.Http.Results.Ok(dto);
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

/// <summary>备考数据源配置提交体。SearchApiKey 为空表示"不改密钥"。</summary>
public sealed record PrepSourceSettingsBody(bool QuestionBankEnabled, bool CompanyBackgroundEnabled,
    bool WebExperiencesEnabled, bool GlassdoorEnabled, string? SearchProvider, string? SearchApiKey);

/// <summary>备考数据源检索请求体(source: web | glassdoor)。</summary>
public sealed record PrepSourceSearchBody(string Source, string Company, string? Role, string? Stage);

public sealed record ImageTextBody(string ImageBase64, string? MimeType);
