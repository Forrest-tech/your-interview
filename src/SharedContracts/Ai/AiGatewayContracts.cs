namespace YourInterview.SharedContracts.Ai;

// ============================================================================
//  AiGateway 客户端契约(票38 ARC-10 单一化,2026-10-22)
//
//  历史:Jobs 与 Interviews 各写一份同名 IAiGatewayClient / AiCompletionResult /
//  AiGatewayException(逐字复制),契约一变要改两处。本文件是唯一定义:
//    契约(本文件,SharedContracts)—— 接口 + DTO + 异常,零 HTTP 依赖;
//    实现(BuildingBlocks/Ai/AiGatewayClient.cs)—— 单一 HTTP adapter。
//  字段与语义逐字承继两副本的并集(Interviews 副本为超集),行为不变。
// ============================================================================

/// <summary>补全结果(与 AiGateway 的 AiCompletionResultDto 同字段)。</summary>
public sealed record AiCompletionResult(
    string Text, string Model, int? PromptTokens, int? CompletionTokens, int? LatencyMs);

/// <summary>AiGateway 调用失败。区分"未配置"与"上游拒绝",便于上层选不同 HTTP 码。</summary>
public sealed class AiGatewayException(string message, bool isConfigurationError = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool IsConfigurationError { get; } = isConfigurationError;
}

/// <summary>备考数据源配置(与 AiGateway PrepSourceSettingsDto 同字段)。</summary>
public sealed record PrepSourceSettingsDto(
    bool QuestionBankEnabled, bool CompanyBackgroundEnabled,
    bool WebExperiencesEnabled, bool GlassdoorEnabled,
    string? SearchProvider, bool HasSearchKey, string? MaskedSearchKey,
    DateTimeOffset? UpdatedAt);

/// <summary>一条外部检索结果。</summary>
public sealed record PrepSourceResultDto(string Title, string Url, string Snippet);

/// <summary>一次外部源检索的状态与结果(状态口径见 AiGateway)。</summary>
public sealed record PrepSourceSearchDto(
    string Source, string Status, string? Provider,
    IReadOnlyList<PrepSourceResultDto> Results, string? Message);

public interface IAiGatewayClient
{
    /// <summary>
    /// 通用补全。purpose 是用途标签(写入网关的 call_logs,便于按功能统计用量)。
    /// 未配置凭据时抛 AiGatewayException(isConfigurationError: true)。
    /// </summary>
    Task<AiCompletionResult> CompleteAsync(string purpose, string prompt, string? systemPrompt,
        double? temperature, int? maxTokens, CancellationToken ct);

    /// <summary>读当前用户的备考数据源配置(开关 + 搜索服务是否已配)。</summary>
    Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct);

    /// <summary>按公司 + 岗位检索一个外部数据源(web / glassdoor),状态如实返回。</summary>
    Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string source, string company,
        string? role, string? stage, CancellationToken ct);
}
