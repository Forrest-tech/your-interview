using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace YourInterview.SharedContracts.Ai;

/// <summary>
/// 大模型调用统一抽象。
///
/// 为什么要抽象层:面试前准备包需要 AI 生成预测问题与参考答案,
/// 但调用方(Interviews 服务)不该关心底层是哪家厂商 —— 换模型不该改业务代码。
///
/// 实现只有三个,不是"每家一个":
///   1. OpenAiCompatibleClient —— OpenAI 协议是事实标准,
///      DeepSeek / Qwen(DashScope compatible-mode) / Ollama / Groq / Together 全兼容。
///   2. AzureOpenAiClient      —— 端点含 deployment + api-version,路径与鉴权头不同。
///   3. AnthropicClient        —— 鉴权头 x-api-key + anthropic-version,消息体结构不同。
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// 单轮补全。要求返回**纯文本**(不做 JSON 模式强约束,由调用方解析)。
    /// systemPrompt 为空时用实现方的默认系统提示。
    /// </summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>一次补全请求。Temperature/MaxTokens 留空则用实现方默认。</summary>
public sealed record LlmRequest(
    string Prompt,
    string? SystemPrompt = null,
    double? Temperature = null,
    int? MaxTokens = null,
    /// <summary>true 时尽力要求模型输出 JSON(不支持该能力的厂商静默忽略)。</summary>
    bool PreferJson = false);

/// <summary>补全结果。Usage 字段缺失时一律 null —— 严禁编造 token 数。</summary>
public sealed record LlmResponse(
    string Text,
    string Model,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    TimeSpan? Latency = null);

/// <summary>LLM 调用失败。区分"配置问题"与"上游拒绝",便于上层选不同 HTTP 码。</summary>
public sealed class LlmCallException(string message, bool isConfigurationError = false, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>true = 本服务没配好(key/model 缺失);false = 上游拒收或网络故障。</summary>
    public bool IsConfigurationError { get; } = isConfigurationError;
}

/// <summary>厂商无关的模型清单(供设置页下拉预选,避免用户手打错模型名)。</summary>
public static class LlmProviders
{
    public const string OpenAiCompatible = "openai-compatible";
    public const string AzureOpenAi = "azure-openai";
    public const string Anthropic = "anthropic";

    /// <summary>
    /// 常用 provider 预设。BaseUrl 是**协议地址**(不含 /chat/completions),
    /// 客户端负责拼路径 —— 集中一处,避免每个调用点各拼一遍拼错。
    /// </summary>
    public static readonly IReadOnlyList<LlmProviderPreset> Presets =
    [
        new(OpenAiCompatible, "DeepSeek", "https://api.deepseek.com/v1",
            ["deepseek-chat", "deepseek-reasoner"]),
        new(OpenAiCompatible, "Qwen (DashScope)", "https://dashscope.aliyuncs.com/compatible-mode/v1",
            ["qwen-plus", "qwen-max", "qwen3-vl-plus", "qwen3-vl-32b-instruct"]),
        new(OpenAiCompatible, "OpenAI", "https://api.openai.com/v1",
            ["gpt-4o", "gpt-4o-mini", "gpt-4.1", "o4-mini"]),
        new(OpenAiCompatible, "Ollama (本地)", "http://127.0.0.1:11434/v1",
            ["qwen2.5:14b", "llama3.1:8b", "deepseek-r1:14b"]),
        new(OpenAiCompatible, "自定义 OpenAI 兼容端点", "",
            []),
        new(AzureOpenAi, "Azure OpenAI", "",
            ["gpt-4o", "gpt-4o-mini"]),
        new(Anthropic, "Anthropic Claude", "https://api.anthropic.com/v1",
            ["claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-3-5-haiku-20241022"])
    ];
}

/// <summary>
/// 一个 provider 预设。注意 Provider(协议类型)与 Name(显示名)分离 ——
/// DeepSeek 和 Qwen 同属 openai-compatible 协议,但显示名与默认地址不同。
/// </summary>
public sealed record LlmProviderPreset(
    string Protocol,
    string Name,
    string DefaultBaseUrl,
    IReadOnlyList<string> Models);

/// <summary>解析后的 LLM 连接参数(由 IAiKeyProvider 产出,交给工厂建客户端)。</summary>
public sealed record LlmConnection(string Protocol, string ApiKey, string? BaseUrl,
    string Model, string? Endpoint = null, string? ApiVersion = null)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool HasModel => !string.IsNullOrWhiteSpace(Model);
}

/// <summary>
/// JSON 助手:LLM 常把 JSON 包在 ```json 围栏里,或前后带解释文字。
/// 统一在这里剥壳,避免每个调用点各写一遍正则。
/// </summary>
public static class LlmJson
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// 从模型输出里抠出 JSON 数组或对象文本。
    /// 找不到时返回 null —— 调用方必须处理 null,绝不假装成功。
    /// </summary>
    public static string? ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        // 1) ```json ... ``` 或 ``` ... ``` 围栏
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var afterFence = s.IndexOf('\n', fence);
            if (afterFence > 0)
            {
                var close = s.IndexOf("```", afterFence, StringComparison.Ordinal);
                if (close > afterFence)
                {
                    var inner = s[(afterFence + 1)..close].Trim();
                    if (inner.Length > 0) s = inner;
                }
            }
        }

        // 2) 取第一个 { 或 [ 到最后一个 } 或 ]
        var startObj = s.IndexOf('{');
        var startArr = s.IndexOf('[');
        var start = startObj < 0 ? startArr : startArr < 0 ? startObj : Math.Min(startObj, startArr);
        if (start < 0) return null;

        var endObj = s.LastIndexOf('}');
        var endArr = s.LastIndexOf(']');
        var end = Math.Max(endObj, endArr);
        if (end <= start) return null;

        return s[start..(end + 1)];
    }

    /// <summary>反序列化成 T。失败返回 default,由调用方判定。</summary>
    public static T? TryParse<T>(string? raw) where T : class
    {
        var json = ExtractJson(raw);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<T>(json, Opts); }
        catch (JsonException) { return null; }
    }
}
