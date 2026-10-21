using System.Text;
using System.Text.Json;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Interviews.Infrastructure.Services;

// ============================================================================
//  AiGateway 的内部客户端(2026-10-07,照 Jobs 同名客户端模式)
//
//  为什么不让 Jobs 直接建 ILlmClient:
//    凭据、协议、端点、回退顺序都是实现细节。Jobs 只该说
//    "帮我生成这段文本",不该知道用的是 DeepSeek 还是 Anthropic。
//    凭据只存一处(AiGateway),换 key 只改一个地方。
//
//  ⚠️ 关键:转发用户 JWT。
//     AiGateway 的 /api/ai/complete 是 [Authorize] 且按 **调用者身份** 取凭据。
//     如果这里不带 Authorization 头,网关拿到的是匿名 → 401 或取不到该用户的 key。
//     所以必须把当前请求的 Authorization 头原样透传。
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

public sealed class InterviewAiGatewayClient(
    HttpClient http,
    IHttpContextAccessor httpContextAccessor,
    ILogger<InterviewAiGatewayClient> logger) : IAiGatewayClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<AiCompletionResult> CompleteAsync(string purpose, string prompt, string? systemPrompt,
        double? temperature, int? maxTokens, CancellationToken ct)
    {
        var body = new
        {
            purpose,
            prompt,
            systemPrompt,
            temperature,
            maxTokens
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ai/complete")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json")
        };

        // 透传调用者 JWT —— AiGateway 靠它识别用户并取该用户的凭据
        var auth = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AiGatewayException($"无法连接 AiGateway:{ex.Message}", false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // 生成 Cover Letter 是长请求(几十秒),这里给出明确提示而不是泛泛的超时
            throw new AiGatewayException("AI 生成超时。可稍后重试,或在设置里换更快的模型。", false, ex);
        }

        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var msg = ExtractProblemDetail(raw) ?? $"AiGateway 返回 {(int)resp.StatusCode}。";
            // 404/501 = 没配置凭据(网关侧报 Ai.NotConfigured 时是 400,这里按语义归类)
            var isConfig = raw.Contains("Ai.NotConfigured", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("尚未配置", StringComparison.Ordinal);
            logger.LogWarning("AiGateway 补全失败({Status}):{Msg}", (int)resp.StatusCode, msg);
            throw new AiGatewayException(msg, isConfig);
        }

        var dto = JsonSerializer.Deserialize<AiCompletionResult>(raw, JsonOpts);
        if (dto is null || string.IsNullOrWhiteSpace(dto.Text))
            throw new AiGatewayException("AI 返回了空内容。请重试,或检查所选模型是否可用。", false);

        return dto;
    }

    public async Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct)
    {
        var raw = await SendJsonAsync(HttpMethod.Get, "/api/ai/prep-sources/settings", null, ct);
        return JsonSerializer.Deserialize<PrepSourceSettingsDto>(raw, JsonOpts)
            ?? throw new AiGatewayException("AiGateway 返回了空的备考数据源配置。", false);
    }

    public async Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string source, string company,
        string? role, string? stage, CancellationToken ct)
    {
        var body = new { source, company, role, stage };
        var raw = await SendJsonAsync(HttpMethod.Post, "/api/ai/prep-sources/search", body, ct);
        return JsonSerializer.Deserialize<PrepSourceSearchDto>(raw, JsonOpts)
            ?? throw new AiGatewayException("AiGateway 返回了空的检索结果。", false);
    }

    /// <summary>带 JWT 透传的 JSON 请求(备考数据源端点共用)。失败抛 AiGatewayException。</summary>
    private async Task<string> SendJsonAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        var auth = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(auth))
            req.Headers.TryAddWithoutValidation("Authorization", auth);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AiGatewayException($"无法连接 AiGateway:{ex.Message}", false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AiGatewayException("AiGateway 请求超时。", false, ex);
        }

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = ExtractProblemDetail(raw) ?? $"AiGateway 返回 {(int)resp.StatusCode}。";
            logger.LogWarning("AiGateway 请求失败({Path} {Status}):{Msg}", path, (int)resp.StatusCode, msg);
            throw new AiGatewayException(msg, false);
        }
        return raw;
    }

    /// <summary>从 ProblemDetails 里抠出人类可读消息。</summary>
    private static string? ExtractProblemDetail(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("detail", out var d)) return d.GetString();
            if (doc.RootElement.TryGetProperty("title", out var t)) return t.GetString();
        }
        catch { /* 不是 JSON,交给调用方用状态码兜底 */ }
        return null;
    }
}
