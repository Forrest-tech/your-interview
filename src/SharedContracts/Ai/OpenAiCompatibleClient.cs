using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace YourInterview.SharedContracts.Ai;

/// <summary>
/// OpenAI 兼容协议的通用客户端。
///
/// 覆盖:OpenAI / DeepSeek / Qwen(DashScope compatible-mode)/ Ollama / Groq / Together ...
/// 这些厂商的 /chat/completions 请求体与响应体一致,所以一个实现全包。
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient http, LlmConnection conn, ILogger logger) : ILlmClient
{
    private const string DefaultSystem = "You are a senior software engineering interviewer and career coach.";

    public async Task<LlmResponse> HandleAsync(LlmRequest request, CancellationToken ct) =>
        await CompleteAsync(request, ct);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (!conn.HasKey)
            throw new LlmCallException("未配置 API Key。", isConfigurationError: true);
        if (!conn.HasModel)
            throw new LlmCallException("未配置模型名。", isConfigurationError: true);

        var baseUrl = string.IsNullOrWhiteSpace(conn.BaseUrl)
            ? throw new LlmCallException("未配置 BaseUrl(端点地址)。", isConfigurationError: true)
            : conn.BaseUrl.TrimEnd('/');

        var url = baseUrl + "/chat/completions";

        var body = new Dictionary<string, object?>
        {
            ["model"] = conn.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt ?? DefaultSystem },
                new { role = "user", content = request.Prompt }
            },
            ["temperature"] = request.Temperature ?? 0.3
        };
        if (request.MaxTokens is int mt && mt > 0)
            body["max_tokens"] = mt;
        // 支持的厂商才认这个字段;不认的会忽略,不会报错。
        if (request.PreferJson)
            body["response_format"] = new { type = "json_object" };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", conn.ApiKey.Trim());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            // 网络层失败:不是配置问题,是连不上 —— 上层映射 502
            throw new LlmCallException($"无法连接 LLM 端点({url}):{ex.Message}", false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmCallException($"LLM 请求超时({url})。", false, ex);
        }
        sw.Stop();

        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            // 关键:把上游状态码与片段原样带出,便于定位是 key 错还是额度耗尽。
            // 401/403 = key 无效;429 = 限流/欠费;其余按上游错误处理。
            var snippet = raw.Length > 500 ? raw[..500] : raw;
            logger.LogWarning("LLM 调用失败 {Status} {Url}:{Body}", (int)resp.StatusCode, url, snippet);
            throw new LlmCallException($"LLM 返回 {(int)resp.StatusCode}:{snippet}");
        }

        return ParseChatCompletion(raw, sw.Elapsed);
    }

    /// <summary>解析 OpenAI 格式响应。抽成 internal 便于单测(不依赖网络)。</summary>
    internal LlmResponse ParseChatCompletion(string raw, TimeSpan latency)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        string text = string.Empty;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content))
            {
                text = content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : content.ToString();
            }
        }

        if (string.IsNullOrWhiteSpace(text))
            throw new LlmCallException("LLM 返回了空内容 —— 可能是模型名错误或被安全策略拦截。");

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? conn.Model : conn.Model;

        int? pt = null, ct2 = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv)) pt = pv;
            if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv)) ct2 = cv;
        }

        return new LlmResponse(text, model, pt, ct2, latency);
    }
}
