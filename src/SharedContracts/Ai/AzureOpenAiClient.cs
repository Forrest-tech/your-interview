using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YourInterview.SharedContracts.Ai;

/// <summary>
/// Azure OpenAI 客户端。
///
/// 与 OpenAI 兼容协议的区别(必须单独实现):
///   1. URL 形态: {endpoint}/openai/deployments/{deployment}/chat/completions?api-version=...
///      —— deployment 名 ≠ 模型名,路径结构完全不同。
///   2. 鉴权头用 api-key,不是 Authorization: Bearer。
///   3. api-version 必填查询参数。
///   4. 响应体基本同构(choices[0].message.content + usage),可复用解析逻辑。
/// </summary>
public sealed class AzureOpenAiClient(HttpClient http, LlmConnection conn) : ILlmClient
{
    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (!conn.HasKey)
            throw new LlmCallException("未配置 Azure OpenAI Key。", isConfigurationError: true);
        if (string.IsNullOrWhiteSpace(conn.Endpoint))
            throw new LlmCallException("未配置 Azure OpenAI Endpoint。", isConfigurationError: true);
        if (!conn.HasModel)
            throw new LlmCallException("未配置部署名(deployment)。", isConfigurationError: true);

        var version = string.IsNullOrWhiteSpace(conn.ApiVersion) ? "2024-10-21" : conn.ApiVersion.Trim();
        var ep = conn.Endpoint.TrimEnd('/');
        var url = $"{ep}/openai/deployments/{conn.Model}/chat/completions?api-version={version}";

        var body = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = request.SystemPrompt ?? "You are a senior software engineering interviewer and career coach." },
                new { role = "user", content = request.Prompt }
            },
            ["temperature"] = request.Temperature ?? 0.3
        };
        if (request.MaxTokens is int mt && mt > 0) body["max_tokens"] = mt;
        if (request.PreferJson) body["response_format"] = new { type = "json_object" };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("api-key", conn.ApiKey.Trim());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            throw new LlmCallException($"无法连接 Azure OpenAI({ep}):{ex.Message}", false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmCallException("Azure OpenAI 请求超时。", false, ex);
        }
        sw.Stop();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 500 ? raw[..500] : raw;
            throw new LlmCallException($"Azure OpenAI 返回 {(int)resp.StatusCode}:{snippet}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var text = string.Empty;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content))
        {
            text = content.GetString() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmCallException("Azure OpenAI 返回了空内容。");

        int? pt = null, ctok = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pv)) pt = pv;
            if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var cv)) ctok = cv;
        }

        return new LlmResponse(text, conn.Model, pt, ctok, sw.Elapsed);
    }
}
