// 票40(ARC-15):LlmJson 原在 SharedContracts/Ai/ILlmClient.cs 尾部,是解析实现逻辑而非契约,
// 随 LLM 实现一并迁 BuildingBlocks/Ai,函数体逐字不变(此前零消费方)。
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YourInterview.BuildingBlocks.Ai;

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
