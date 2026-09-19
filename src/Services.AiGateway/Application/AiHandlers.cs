using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.AiGateway.Domain;
using YourInterview.Services.AiGateway.Infrastructure.Persistence;
using YourInterview.Services.AiGateway.Infrastructure.Storage;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.AiGateway.Application;

// ============================================================================
//  AiGateway 应用层(2026-09-18)
//
//  两类职责:
//    A. 凭据管理 —— 保存/读取/测试用户的 LLM 配置
//    B. 补全转发 —— "把这段 prompt 发给 LLM,把文本还给我"
//
//  为什么 B 要做成服务而不是让调用方直接建客户端:
//    凭据、协议派发、回退顺序都是实现细节。Jobs/Interviews 只该说
//    "帮我生成这封 Cover Letter 草稿",不该知道用的是 DeepSeek 还是 Anthropic。
// ============================================================================

// ---------------------------- DTO ----------------------------

/// <summary>
/// 凭据状态回显。⚠️ 绝不返回 ApiKey 明文,只回掩码与"是否已配置"。
/// </summary>
public sealed record AiSettingStatusDto(
    bool Configured, string? Protocol, string? Model, string? BaseUrl,
    string? Endpoint, string? ApiVersion, string? DisplayName, string? MaskedKey,
    DateTimeOffset? UpdatedAt);

public sealed record AiCompletionResultDto(
    string Text, string Model, int? PromptTokens, int? CompletionTokens, int? LatencyMs);

// ---------------------------- 凭据:读 ----------------------------

public sealed record GetAiSettingQuery(Guid UserId) : IRequest<Result<AiSettingStatusDto>>;

public sealed class GetAiSettingQueryHandler(AiGatewayDbContext db)
    : IRequestHandler<GetAiSettingQuery, Result<AiSettingStatusDto>>
{
    public async Task<Result<AiSettingStatusDto>> Handle(GetAiSettingQuery request, CancellationToken ct)
    {
        var row = await db.AiSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);

        if (row is null)
            return Result.Success(new AiSettingStatusDto(false, null, null, null, null, null, null, null, null));

        return Result.Success(new AiSettingStatusDto(
            true, row.Protocol, row.Model, row.BaseUrl, row.Endpoint, row.ApiVersion,
            row.DisplayName, Mask(row.ApiKey), row.UpdatedAt));
    }

    /// <summary>只露前 3 后 4(与 Assessment 侧同一口径),中间用点。</summary>
    private static string Mask(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (key.Length <= 8) return new string('•', key.Length);
        return key[..3] + new string('•', 6) + key[^4..];
    }
}

// ---------------------------- 凭据:写 ----------------------------

/// <summary>
/// 保存凭据。**保存前必须真测一次** —— 不通不入库。
/// 理由:用户填错 key 却保存成功,要到真正生成时才报错,那时排查成本高得多。
/// </summary>
public sealed record SaveAiSettingCommand(Guid UserId, string Protocol, string ApiKey, string? BaseUrl,
    string Model, string? Endpoint, string? ApiVersion, string? DisplayName)
    : IRequest<Result<AiSettingStatusDto>>;

public sealed class SaveAiSettingCommandValidator : AbstractValidator<SaveAiSettingCommand>
{
    public SaveAiSettingCommandValidator()
    {
        RuleFor(x => x.Model).NotEmpty().WithMessage("模型名不能为空。");
        RuleFor(x => x.Protocol).NotEmpty().WithMessage("协议类型不能为空。");
        RuleFor(x => x.ApiKey).NotEmpty().WithMessage("API Key 不能为空。");
    }
}

public sealed class SaveAiSettingCommandHandler(
    AiGatewayDbContext db,
    ILlmClientFactory clientFactory,
    ILogger<SaveAiSettingCommandHandler> logger)
    : IRequestHandler<SaveAiSettingCommand, Result<AiSettingStatusDto>>
{
    public async Task<Result<AiSettingStatusDto>> Handle(SaveAiSettingCommand request, CancellationToken ct)
    {
        if (request.Protocol == LlmProviders.AzureOpenAi && string.IsNullOrWhiteSpace(request.Endpoint))
            return Result.Failure<AiSettingStatusDto>(
                Error.Validation("Ai.EndpointEmpty", "Azure OpenAI 需要填写资源端点(Endpoint)。"));

        var conn = new LlmConnection(request.Protocol, request.ApiKey, request.BaseUrl, request.Model,
            request.Endpoint, request.ApiVersion);

        // 保存前真测 —— 不通就不保存。
        try
        {
            var client = clientFactory.Create(conn);
            await client.CompleteAsync(
                new LlmRequest("Reply with exactly: OK", "You are a connectivity probe.", 0, 16), ct);
        }
        catch (LlmCallException ex)
        {
            logger.LogWarning(ex, "保存 LLM 凭据前的连通性验证失败");
            return Result.Failure<AiSettingStatusDto>(
                new Error("Ai.CredentialRejected", ex.Message, ErrorType.Failure));
        }
        catch (Exception ex)
        {
            return Result.Failure<AiSettingStatusDto>(
                new Error("Ai.ProbeUnexpected", $"保存前验证失败:{ex.Message}", ErrorType.Failure));
        }

        var existing = await db.AiSettings.FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (existing is null)
        {
            var created = new AiSetting(request.UserId, request.Protocol, request.ApiKey, request.BaseUrl,
                request.Model, request.Endpoint, request.ApiVersion, request.DisplayName);
            db.AiSettings.Add(created);
        }
        else
        {
            existing.Update(request.Protocol, request.ApiKey, request.BaseUrl, request.Model,
                request.Endpoint, request.ApiVersion, request.DisplayName);
        }

        await db.SaveChangesAsync(ct);
        return await new GetAiSettingQueryHandler(db).Handle(new GetAiSettingQuery(request.UserId), ct);
    }
}

/// <summary>删除凭据(回到未配置状态)。幂等。</summary>
public sealed record DeleteAiSettingCommand(Guid UserId) : IRequest<Result>;

public sealed class DeleteAiSettingCommandHandler(AiGatewayDbContext db)
    : IRequestHandler<DeleteAiSettingCommand, Result>
{
    public async Task<Result> Handle(DeleteAiSettingCommand request, CancellationToken ct)
    {
        var row = await db.AiSettings.FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (row is null) return Result.Success();     // 幂等

        db.AiSettings.Remove(row);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ---------------------------- 凭据:测 ----------------------------

public sealed record TestAiCredentialCommand(string Protocol, string ApiKey, string? BaseUrl, string Model,
    string? Endpoint, string? ApiVersion) : IRequest<Result<AiProbeResultDto>>;

public sealed record AiProbeResultDto(bool Ok, string Message, string? Sample);

public sealed class TestAiCredentialCommandHandler(ILlmClientFactory clientFactory)
    : IRequestHandler<TestAiCredentialCommand, Result<AiProbeResultDto>>
{
    public async Task<Result<AiProbeResultDto>> Handle(TestAiCredentialCommand request, CancellationToken ct)
    {
        if (request.Protocol == LlmProviders.AzureOpenAi && string.IsNullOrWhiteSpace(request.Endpoint))
            return Result.Failure<AiProbeResultDto>(
                Error.Validation("Ai.EndpointEmpty", "Azure OpenAI 需要填写资源端点(Endpoint)。"));

        var conn = new LlmConnection(request.Protocol, request.ApiKey, request.BaseUrl, request.Model,
            request.Endpoint, request.ApiVersion);

        try
        {
            var client = clientFactory.Create(conn);
            // 最小可用请求:短提示 + 极少 token,只验证"能不能通"。
            var resp = await client.CompleteAsync(
                new LlmRequest("Reply with exactly: OK", "You are a connectivity probe.", 0, 16), ct);

            var sample = resp.Text.Trim();
            if (sample.Length > 120) sample = sample[..120] + "…";
            return Result.Success(new AiProbeResultDto(true, $"连接正常,模型可用({resp.Model})。", sample));
        }
        catch (LlmCallException ex)
        {
            return Result.Failure<AiProbeResultDto>(
                new Error("Ai.CredentialRejected", ex.Message, ErrorType.Failure));
        }
        catch (Exception ex)
        {
            return Result.Failure<AiProbeResultDto>(
                new Error("Ai.ProbeUnexpected", $"连通性验证失败:{ex.Message}", ErrorType.Failure));
        }
    }
}

// ---------------------------- 补全转发(核心) ----------------------------

/// <summary>
/// 通用补全 —— AiGateway 存在的理由。
///
/// 调用方只给 prompt 与用途标签;凭据、协议、回退顺序全在网关内部解决。
/// 调用方传 UserId,网关按该用户配置的 key 调用(不同用户可用不同模型)。
///
/// 用途标签(Purpose)会写入 call_logs,用于按功能统计用量与排查问题。
/// ⚠️ prompt 正文绝不落库 —— 里面可能含简历/JD 等用户资料。
/// </summary>
public sealed record CompleteCommand(
    Guid UserId,
    string Purpose,
    string Prompt,
    string? SystemPrompt = null,
    double? Temperature = null,
    int? MaxTokens = null,
    bool PreferJson = false) : IRequest<Result<AiCompletionResultDto>>;

public sealed class CompleteCommandValidator : AbstractValidator<CompleteCommand>
{
    public CompleteCommandValidator()
    {
        RuleFor(x => x.Prompt).NotEmpty().WithMessage("prompt 不能为空。");
        RuleFor(x => x.Purpose).NotEmpty().WithMessage("用途标签不能为空。");
    }
}

public sealed class CompleteCommandHandler(
    AiKeyProviderHolder holder,
    AiGatewayDbContext db,
    ILogger<CompleteCommandHandler> logger)
    : IRequestHandler<CompleteCommand, Result<AiCompletionResultDto>>
{
    public async Task<Result<AiCompletionResultDto>> Handle(CompleteCommand request, CancellationToken ct)
    {
        var client = await holder.Provider.CreateClientAsync(request.UserId, ct);
        if (client is null)
        {
            // 未配置 key —— 明确报错,绝不静默返回空文本让上层以为成功。
            return Result.Failure<AiCompletionResultDto>(
                new Error("Ai.NotConfigured",
                    "尚未配置 LLM 凭据。请先在设置里填写 API Key。", ErrorType.Failure));
        }

        var conn = await holder.Provider.ResolveAsync(request.UserId, ct);
        var model = conn?.Model ?? "unknown";

        try
        {
            var resp = await client.CompleteAsync(new LlmRequest(
                request.Prompt, request.SystemPrompt, request.Temperature, request.MaxTokens,
                request.PreferJson), ct);

            db.CallLogs.Add(AiCallLog.Success(request.UserId, request.Purpose, resp.Model,
                resp.PromptTokens, resp.CompletionTokens, resp.Latency));
            await db.SaveChangesAsync(ct);

            return Result.Success(new AiCompletionResultDto(
                resp.Text, resp.Model, resp.PromptTokens, resp.CompletionTokens,
                resp.Latency is null ? null : (int)resp.Latency.Value.TotalMilliseconds));
        }
        catch (LlmCallException ex)
        {
            db.CallLogs.Add(AiCallLog.Failure(request.UserId, request.Purpose, model, ex.Message));
            await db.SaveChangesAsync(ct);

            logger.LogWarning(ex, "LLM 调用失败 purpose={Purpose}", request.Purpose);
            return Result.Failure<AiCompletionResultDto>(
                new Error("Ai.CallFailed", ex.Message, ErrorType.Failure));
        }
    }
}

/// <summary>
/// 持有 IAiKeyProvider 的单例包装。
///
/// 为什么需要它:IAiKeyProvider 内部用 IServiceScopeFactory 开短命 scope
/// 去读 Scoped 的 DbContext,所以它本身是单例安全的。
/// 但 Handler 是 Scoped —— 直接注入 IAiKeyProvider 也能work。
/// 这里用包装是为了让 Handler 依赖更显式,避免将来有人误以为可以
/// 在 Handler 里直接持有 DbContext 跨请求使用。
/// </summary>
public sealed class AiKeyProviderHolder(IAiKeyProvider provider)
{
    public IAiKeyProvider Provider { get; } = provider;
}
