using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YourInterview.SharedContracts.Ai;

/// <summary>
/// Anthropic Claude 客户端。
///
/// 与 OpenAI 协议的区别(必须单独实现):
///   1. 鉴权头是 x-api-key,并强制 anthropic-version 头。
///   2. system 提示是**顶层字段**,不放在 messages 数组里。
///   3. 响应体是 content: [{type:"text", text:"..."}],不是 choices[].message.content。
///   4. max_tokens 必填(OpenAI 侧可选)。
/// </summary>
public sealed class AnthropicClient(HttpClient http, LlmConnection conn) : ILlmClient
{
    private const string AnthropicVersion = "2023-06-01";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (!conn.HasKey)
            throw new LlmCallException("未配置 Anthropic API Key。", isConfigurationError: true);
        if (!conn.HasModel)
            throw new LlmCallException("未配置模型名。", isConfigurationError: true);

        var baseUrl = string.IsNullOrWhiteSpace(conn.BaseUrl) ? "https://api.anthropic.com/v1" : conn.BaseUrl.TrimEnd('/');
        var url = baseUrl + "/messages";

        var body = new Dictionary<string, object?>
        {
            ["model"] = conn.Model,
            ["max_tokens"] = request.MaxTokens is int mt && mt > 0 ? mt : 4096,
            // Anthropic 的 system 是顶层字段,不能塞进 messages
            ["system"] = request.SystemPrompt ?? "You are a senior software engineering interviewer and career coach.",
            ["messages"] = new object[] { new { role = "user", content = request.Prompt } },
            ["temperature"] = request.Temperature ?? 0.3
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", conn.ApiKey.Trim());
        req.Headers.Add("anthropic-version", AnthropicVersion);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex)
        {
            throw new LlmCallException($"无法连接 Anthropic({baseUrl}):{ex.Message}", false, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmCallException("Anthropic 请求超时。", false, ex);
        }
        sw.Stop();

        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var snippet = raw.Length > 500 ? raw[..500] : raw;
            throw new LlmCallException($"Anthropic 返回 {(int)resp.StatusCode}:{snippet}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }

        var text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmCallException("Anthropic 返回了空内容。");

        var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? conn.Model : conn.Model;

        int? pt = null, ctok = null;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv)) pt = iv;
            if (usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov)) ctok = ov;
        }

        return new LlmResponse(text, model, pt, ctok, sw.Elapsed);
    }
}
