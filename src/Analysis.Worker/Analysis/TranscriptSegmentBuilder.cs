using System.Text.Json;
using System.Text.RegularExpressions;
using YourInterview.SharedContracts.Analysis;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 粘贴文本 → 分段播种(文本模式)。
///
/// R01 要求"上传音频**或粘贴文本** → 跑分析":无音频时,Consumer 从
/// Interviews API 取回转写文本,用这里的方法转成管线能消费的 segments,
/// 再走 SpeakerLabel → Measure(文本) → Diagnose。
///
/// 时间戳是**估算**的(按 150wpm),只用于语速分母与停顿检测 ——
/// 精度不如音频模式,这是文本模式的诚实边界,报告里会注明。
/// </summary>
public static class TranscriptSegmentBuilder
{
    /// <summary>估算语速(词/分钟) —— 仅用于合成时间戳,不是测量值。</summary>
    public const double EstimatedWordsPerMinute = 150;

    /// <summary>
    /// 解析 Interviews 存的 segmentsJson。
    /// 形状:[{"index":0,"start":1.2,"end":3.4,"text":"..."}] ——
    /// 与 Consumer 回写转写稿时的序列化形状一致(大小写不敏感)。
    /// 解析失败或空数组返回 null,调用方退回纯文本切分。
    /// </summary>
    public static List<TranscriptSegment>? TryParseSegmentsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var list = new List<TranscriptSegment>();
            var index = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var text = GetString(el, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;
                var start = GetDouble(el, "start", index * 5.0);
                var end = GetDouble(el, "end", start + 5.0);
                if (end <= start) end = start + 5.0;
                // Speaker 统一 UNKNOWN,让下游 SpeakerLabelStage 做 marker 解析
                list.Add(new TranscriptSegment(index, start, end, text.Trim(), "UNKNOWN"));
                index++;
            }
            return list.Count > 0 ? list : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 纯文本按段落切分 → 合成时间戳。
    /// 切分规则:空行分段;超长段落(>600 字符)按句界(。！？!?.)再切。
    /// 每段时长 = 估计词数/150*60,最短 1 秒;起止时间累加。
    /// marker(如"面试官:")不剥离 —— SpeakerLabelStage 会解析并剥离。
    /// </summary>
    public static List<TranscriptSegment> SplitText(string text)
    {
        var chunks = new List<string>();
        foreach (var para in Regex.Split(text, @"\r?\n\s*\r?\n"))
        {
            var p = para.Trim();
            if (p.Length == 0) continue;
            if (p.Length > 600)
                chunks.AddRange(Regex.Split(p, @"(?<=[。！？.!?…])\s*")
                    .Select(s => s.Trim()).Where(s => s.Length > 0));
            else
                chunks.Add(p);
        }
        // 无空行时按单换行也切(用户常直接贴多行)
        if (chunks.Count <= 1)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count > 1) chunks = lines;
        }

        var segments = new List<TranscriptSegment>();
        var cursor = 0.0;
        for (var i = 0; i < chunks.Count; i++)
        {
            var duration = Math.Max(1.0, EstimateWords(chunks[i]) / EstimatedWordsPerMinute * 60.0);
            segments.Add(new TranscriptSegment(i, cursor, cursor + duration, chunks[i], "UNKNOWN"));
            cursor += duration;
        }
        return segments;
    }

    /// <summary>
    /// 估计词数:有空格按空白切分;纯 CJK(无空格)按字符数计
    /// (中文口语约 150 字/分钟,与 150wpm 同量级,估算自洽)。
    /// </summary>
    internal static int EstimateWords(string text)
    {
        var tokens = text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1) return tokens.Length;
        return text.Length;
    }

    private static string? GetString(JsonElement el, string name)
    {
        foreach (var prop in el.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        return null;
    }

    private static double GetDouble(JsonElement el, string name, double fallback)
    {
        foreach (var prop in el.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var d))
                return d;
        }
        return fallback;
    }
}
