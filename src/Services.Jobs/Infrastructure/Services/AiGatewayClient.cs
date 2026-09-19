using System.Text;
using System.Text.Json;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  AiGateway 的内部客户端(2026-09-18)
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

public interface IAiGatewayClient
{
    /// <summary>
    /// 通用补全。purpose 是用途标签(写入网关的 call_logs,便于按功能统计用量)。
    /// 未配置凭据时抛 AiGatewayException(isConfigurationError: true)。
    /// </summary>
    Task<AiCompletionResult> CompleteAsync(string purpose, string prompt, string? systemPrompt,
        double? temperature, int? maxTokens, CancellationToken ct);
}

public sealed class AiGatewayClient(
    HttpClient http,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AiGatewayClient> logger) : IAiGatewayClient
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
