using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.BuildingBlocks.Ai;

// ============================================================================
//  AiGateway 的内部客户端 —— 全仓单一 adapter(票38 ARC-10,2026-10-22)
//
//  历史:Jobs(AiGatewayClient,2026-09-18)与 Interviews(InterviewAiGatewayClient,
//  2026-10-07)各写一份,CompleteAsync 逐字相同,Interviews 多备考数据源两端点。
//  本类是两副本的并集,方法体逐字承继,行为不变(差异裁定见 evidence-38/STATUS.md):
//    · CompleteAsync 的超时文案与 isConfig 判定保留原样;
//    · SendJsonAsync(备考数据源路径)的超时文案与 isConfig=false 保留原样,
//      不与 CompleteAsync 静默合并。
//
//  为什么不让调用方直接建 ILlmClient:
//    凭据、协议、端点、回退顺序都是实现细节。调用方只该说
//    "帮我生成这段文本",不该知道用的是 DeepSeek 还是 Anthropic。
//    凭据只存一处(AiGateway),换 key 只改一个地方。
//
//  ⚠️ 关键:转发用户 JWT。
//     AiGateway 的 /api/ai/complete 是 [Authorize] 且按 **调用者身份** 取凭据。
//     如果这里不带 Authorization 头,网关拿到的是匿名 → 401 或取不到该用户的 key。
//     所以必须把当前请求的 Authorization 头原样透传。
// ============================================================================

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

public static class AiGatewayClientServiceCollectionExtensions
{
    /// <summary>AiGateway 回落地址(宿主机直跑;容器内由配置 AiGateway:BaseUrl 覆盖)。</summary>
    public const string DefaultBaseUrl = "http://127.0.0.1:5268/";

    /// <summary>
    /// 两服务统一注册口径(原 Jobs/Interviews 两处 AddHttpClient 逐字同口径):
    /// 地址优先取配置 AiGateway:BaseUrl,未配回落 5268;补尾斜杠;超时 3 分钟
    /// (生成是长请求:LLM 输出通常 20-60 秒,短于上游真实耗时会误报超时)。
    /// 调用方仍需自行 AddHttpContextAccessor(JWT 透传依赖之,两服务原位保留)。
    /// </summary>
    public static IHttpClientBuilder AddAiGatewayClient(this IServiceCollection services, string? configuredBaseUrl)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl) ? DefaultBaseUrl : configuredBaseUrl;
        return services.AddHttpClient<IAiGatewayClient, AiGatewayClient>(http =>
        {
            http.BaseAddress = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
            http.Timeout = TimeSpan.FromMinutes(3);
        });
    }
}
