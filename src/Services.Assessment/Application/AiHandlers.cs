using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Assessment.Application;

// ============================================================================
//  LLM 设置(面试前准备包用)
//
//  设计原则与 SpeechSetting 严格一致:
//    1. key 绝不下发浏览器 —— 只回 hasKey / 掩码 / 非敏感字段。
//    2. 保存前必须真测一次,不通不入库(后端把关,不依赖前端老实调 test)。
//    3. 生效优先级:数据库 > 环境变量 > appsettings。
//    4. 凭据被上游拒收一律映射 502,绝不用 401/403(前端会当会话失效而登出)。
// ============================================================================

/// <summary>设置状态。⚠️ 不含明文 key,只有掩码。</summary>
public sealed record AiSettingStatusDto(
    bool HasKey,
    string? Protocol,
    string? DisplayName,
    string? BaseUrl,
    string? Model,
    string? Endpoint,
    string? ApiVersion,
    string? MaskedKey,
    string Source);

/// <summary>供前端下拉用的 provider 预设(静态,不含任何密钥)。</summary>
public sealed record AiProviderPresetDto(
    string Protocol, string Name, string DefaultBaseUrl, IReadOnlyList<string> Models);

public sealed record GetAiSettingQuery(Guid UserId) : IRequest<AiSettingStatusDto>;
public sealed record ListAiProvidersQuery : IRequest<IReadOnlyList<AiProviderPresetDto>>;

/// <summary>测一把**未保存**的候选凭据(先测后存)。</summary>
public sealed record TestAiCredentialCommand(string Protocol, string ApiKey, string? BaseUrl,
    string Model, string? Endpoint, string? ApiVersion)
    : IRequest<Result<TestAiCredentialResult>>;

public sealed record TestAiCredentialResult(bool Ok, string Message, string? SampleOutput = null);

public sealed record SaveAiSettingCommand(Guid UserId, string Protocol, string ApiKey, string? BaseUrl,
    string Model, string? Endpoint, string? ApiVersion, string? DisplayName)
    : IRequest<Result<AiSettingStatusDto>>;

public sealed record DeleteAiSettingCommand(Guid UserId) : IRequest<Result>;

// ============================ Handlers ============================

public sealed class GetAiSettingQueryHandler(AssessmentDbContext db, IConfiguration config)
    : IRequestHandler<GetAiSettingQuery, AiSettingStatusDto>
{
    public async Task<AiSettingStatusDto> Handle(GetAiSettingQuery r, CancellationToken ct)
    {
        var row = await db.AiSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);

        if (row is not null && !string.IsNullOrWhiteSpace(row.ApiKey))
        {
            return new AiSettingStatusDto(true, row.Protocol, row.DisplayName, row.BaseUrl,
                row.Model, row.Endpoint, row.ApiVersion, Mask(row.ApiKey), "database");
        }

        // 未在界面保存过 → hasKey=false。
        // 配置文件里的 key 只作服务端兜底(见 AiKeyProvider),不影响界面"已配置"判定 ——
        // 界面必须与用户真实配置一一对应,否则会出现"我从没填过却显示已配置"。
        return new AiSettingStatusDto(false, null, null, null, null, null, null, null, "none");
    }

    private static string Mask(string key) =>
        key.Length <= 8 ? "••••••••" : key[..4] + "••••••••" + key[^4..];
}

public sealed class ListAiProvidersQueryHandler : IRequestHandler<ListAiProvidersQuery, IReadOnlyList<AiProviderPresetDto>>
{
    public Task<IReadOnlyList<AiProviderPresetDto>> Handle(ListAiProvidersQuery r, CancellationToken ct)
    {
        IReadOnlyList<AiProviderPresetDto> list = LlmProviders.Presets
            .Select(p => new AiProviderPresetDto(p.Protocol, p.Name, p.DefaultBaseUrl, p.Models))
            .ToList();
        return Task.FromResult(list);
    }
}

/// <summary>
/// 测试一把尚未保存的候选凭据。
///
/// 与线上调用的区别:这里**不读库、不写库**,直接用请求体里的参数发一次最小请求。
/// 目的:让用户在保存前就知道 key/模型/端点是否可用,而不是存完再发现不通。
/// </summary>
public sealed class TestAiCredentialCommandHandler(ILlmClientFactory factory)
    : IRequestHandler<TestAiCredentialCommand, Result<TestAiCredentialResult>>
{
    public async Task<Result<TestAiCredentialResult>> Handle(TestAiCredentialCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.ApiKey))
            return Result.Failure<TestAiCredentialResult>(
                Error.Validation("Ai.KeyEmpty", "请先填写 API Key。"));
        if (string.IsNullOrWhiteSpace(r.Model))
            return Result.Failure<TestAiCredentialResult>(
                Error.Validation("Ai.ModelEmpty", "请先选择或填写模型名。"));
        // openai-compatible 必须有 BaseUrl —— 没有它连不出请求
        if (r.Protocol == LlmProtocols.OpenAiCompatible && string.IsNullOrWhiteSpace(r.BaseUrl))
            return Result.Failure<TestAiCredentialResult>(
                Error.Validation("Ai.BaseUrlEmpty", "该协议需要填写端点地址(BaseUrl)。"));
        if (r.Protocol == LlmProtocols.AzureOpenAi && string.IsNullOrWhiteSpace(r.Endpoint))
            return Result.Failure<TestAiCredentialResult>(
                Error.Validation("Ai.EndpointEmpty", "Azure OpenAI 需要填写资源端点(Endpoint)。"));

        var conn = new LlmConnection(r.Protocol, r.ApiKey, r.BaseUrl, r.Model, r.Endpoint, r.ApiVersion);

        try
        {
            var client = factory.Create(conn);
            // 最小可用请求:短提示 + 极少 token,只验证"能不能通"。
            var resp = await client.CompleteAsync(
                new LlmRequest("Reply with exactly: OK", "You are a connectivity probe.", 0, 16), ct);

            var sample = resp.Text.Trim();
            if (sample.Length > 120) sample = sample[..120] + "…";
            return Result.Success(new TestAiCredentialResult(true,
                $"连接正常,模型可用({resp.Model})。", sample));
        }
        catch (LlmCallException ex)
        {
            // 凭据/端点问题统一归为"上游拒收",由控制器映射 502。
            // 绝不能是 401/403 —— 那在前端意味着会话失效。
            return Result.Failure<TestAiCredentialResult>(
                new Error("Ai.CredentialRejected", ex.Message, ErrorType.Failure));
        }
        catch (Exception ex)
        {
            return Result.Failure<TestAiCredentialResult>(
                new Error("Ai.ProbeUnexpected", $"测试失败:{ex.Message}", ErrorType.Failure));
        }
    }
}

/// <summary>
/// 保存 LLM 配置。
///
/// ⚠️ 保存前**必须真测一次** —— 与 SpeechSetting 同策略。
///    只查长度就入库会让"32 个 0"也能存进去,用户看到"已保存",
///    到生成时才炸。后端自己把关,不依赖前端是否老实调了 test-credential。
///    前端门禁是体验,后端校验是底线 —— 两道都要有。
/// </summary>
public sealed class SaveAiSettingCommandHandler(AssessmentDbContext db, ILlmClientFactory factory)
    : IRequestHandler<SaveAiSettingCommand, Result<AiSettingStatusDto>>
{
    public async Task<Result<AiSettingStatusDto>> Handle(SaveAiSettingCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.ApiKey))
            return Result.Failure<AiSettingStatusDto>(Error.Validation("Ai.KeyEmpty", "密钥不能为空。"));
        if (r.ApiKey.Trim().Length < 16)
            return Result.Failure<AiSettingStatusDto>(Error.Validation("Ai.KeyTooShort",
                "密钥长度明显不足,请核对厂商控制台里的密钥。"));
        if (string.IsNullOrWhiteSpace(r.Model))
            return Result.Failure<AiSettingStatusDto>(Error.Validation("Ai.ModelEmpty", "模型名不能为空。"));

        // 保存前真测 —— 不通就不保存
        var conn = new LlmConnection(r.Protocol, r.ApiKey, r.BaseUrl, r.Model, r.Endpoint, r.ApiVersion);
        try
        {
            var client = factory.Create(conn);
            await client.CompleteAsync(
                new LlmRequest("Reply with exactly: OK", "You are a connectivity probe.", 0, 16), ct);
        }
        catch (LlmCallException ex)
        {
            return Result.Failure<AiSettingStatusDto>(
                new Error("Ai.CredentialRejected", ex.Message, ErrorType.Failure));
        }
        catch (Exception ex)
        {
            return Result.Failure<AiSettingStatusDto>(
                new Error("Ai.ProbeUnexpected", $"保存前验证失败:{ex.Message}", ErrorType.Failure));
        }

        var row = await db.AiSettings.FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);
        if (row is null)
        {
            row = new AiSetting(r.UserId, r.Protocol, r.ApiKey.Trim(), r.BaseUrl, r.Model,
                r.Endpoint, r.ApiVersion, r.DisplayName);
            db.AiSettings.Add(row);
        }
        else
        {
            row.Update(r.Protocol, r.ApiKey.Trim(), r.BaseUrl, r.Model,
                r.Endpoint, r.ApiVersion, r.DisplayName);
        }

        await db.SaveChangesAsync(ct);

        return Result.Success(new AiSettingStatusDto(true, row.Protocol, row.DisplayName,
            row.BaseUrl, row.Model, row.Endpoint, row.ApiVersion,
            row.ApiKey.Length <= 8 ? "••••••••" : row.ApiKey[..4] + "••••••••" + row.ApiKey[^4..],
            "database"));
    }
}

/// <summary>删除用户 LLM 配置(回到未配置状态)。</summary>
public sealed class DeleteAiSettingCommandHandler(AssessmentDbContext db)
    : IRequestHandler<DeleteAiSettingCommand, Result>
{
    public async Task<Result> Handle(DeleteAiSettingCommand r, CancellationToken ct)
    {
        var row = await db.AiSettings.FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);
        if (row is null) return Result.Success();
        db.AiSettings.Remove(row);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
