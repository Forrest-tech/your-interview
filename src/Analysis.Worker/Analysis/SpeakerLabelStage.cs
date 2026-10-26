using System.Text.RegularExpressions;
using YourInterview.SharedContracts.Analysis;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 阶段 2.5:说话人标注 —— 跑在 Transcribe 之后、Measure/Diagnose 之前。
///
/// 为什么独立成阶段:之前的启发式(前半段 ≤12 词的分段算面试官)错得离谱 ——
/// 用户只粘贴自己回答时前半段短句被丢弃,正常交替问答时后半段面试官提问被算进
/// 候选人发言。标注逻辑必须显式、可测试,所以单拎出来。
///
/// 规则(按优先级):
///   1. segment 已有可信标签(Speaker 非 UNKNOWN 且 SpeakerConfidence ≥ 0.8) → 保留不动,
///      并成为后续继承的"当前说话人"。
///   2. 文本 marker:segment 开头形如 `面试官:` `面试官：` `Interviewer:` `我:` `我：`
///      `Me:` `候选人:` `候选人：` `Candidate:`(英文大小写不敏感),或方括号标签
///      `[面试官]` `[我]` `[Interviewer]` `[Candidate]`。命中后剥离 marker,后续 segment
///      继承该说话人,直到下一个 marker。
///   3. 全文无任何 marker 时 → 全部标 CANDIDATE。用户粘贴的往往只有自己的回答,
///      这比启发式丢弃文本安全得多。第一个 marker 之前的分段同样走这条默认。
///
/// 输出:Segments 的 Speaker/SpeakerConfidence 就地更新;marker 文本从段文本中剥离。
/// </summary>
public sealed class SpeakerLabelStage(ILogger<SpeakerLabelStage> logger) : IPipelineStage
{
    public string Name => "SpeakerLabel";

    /// <summary>可信标签的置信度门限 —— 达到即认为已有可靠标注,不再覆盖。</summary>
    public const double TrustedConfidenceThreshold = 0.8;

    /// <summary>冒号式 marker:`面试官:` `Interviewer:` `我:` `Me:` 等(英文不分大小写)。</summary>
    private static readonly Regex ColonMarker = new(
        @"^\s*(面试官|候选人|我|interviewer|candidate|me)\s*[:：]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>方括号 marker:`[面试官]` `[我]` `[Interviewer]` 等。</summary>
    private static readonly Regex BracketMarker = new(
        @"^\s*\[(面试官|候选人|我|interviewer|candidate|me)\]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool CanRun(PipelineContext context, PipelineState state) =>
        state.Segments.Count > 0;

    public async Task<PipelineState> ExecuteAsync(PipelineContext context, PipelineState state,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Label(state);
            state.Stages.Add(new StageResult(Name, true, null, sw.Elapsed.TotalMilliseconds));

            var counts = state.Segments.GroupBy(s => s.Speaker)
                .ToDictionary(g => g.Key, g => g.Count());
            logger.LogInformation("说话人标注完成:候选人 {C} 段 / 面试官 {I} 段 / 未知 {U} 段",
                counts.GetValueOrDefault("CANDIDATE"), counts.GetValueOrDefault("INTERVIEWER"),
                counts.GetValueOrDefault("UNKNOWN"));
        }
        catch (Exception ex)
        {
            // 标注失败不该让整场分析失败 —— 降级为全候选人,至少不丢文本
            logger.LogWarning(ex, "说话人标注异常,降级为全文候选人");
            for (var i = 0; i < state.Segments.Count; i++)
                state.Segments[i] = state.Segments[i] with
                {
                    Speaker = "CANDIDATE",
                    SpeakerConfidence = 0.4
                };
            state.Stages.Add(new StageResult(Name, true, "标注异常,已降级为全文候选人",
                sw.Elapsed.TotalMilliseconds));
        }

        await Task.CompletedTask;
        return state;
    }

    /// <summary>纯逻辑入口 —— 单元测试直接调这个,不碰日志与计时。</summary>
    public static void Label(PipelineState state)
    {
        var segments = state.Segments;
        if (segments.Count == 0) return;

        // 先扫一遍:有没有任何 marker。没有 → 全文 CANDIDATE,提前返回。
        var hasAnyMarker = segments.Any(s => TryParseMarker(s.Text).Speaker is not null);

        string currentSpeaker = "CANDIDATE"; // 默认说话人(规则 3)
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            // 规则 1:已有可信标签 → 保留,并成为继承基准
            if (IsTrusted(seg))
            {
                currentSpeaker = seg.Speaker;
                continue;
            }

            var (markerSpeaker, rest) = TryParseMarker(seg.Text);
            if (markerSpeaker is not null && hasAnyMarker)
            {
                // 规则 2:marker 命中 → 剥离 marker,切换当前说话人
                currentSpeaker = markerSpeaker;
                segments[i] = seg with
                {
                    Text = rest.TrimStart(),
                    Speaker = markerSpeaker,
                    SpeakerConfidence = 1.0
                };
            }
            else
            {
                // 规则 3:无 marker → 全部候选人(或继承当前说话人)
                segments[i] = seg with
                {
                    Speaker = currentSpeaker,
                    SpeakerConfidence = currentSpeaker == "CANDIDATE" ? 0.6 : 1.0
                };
            }
        }
    }

    private static bool IsTrusted(TranscriptSegment seg) =>
        seg.Speaker is "INTERVIEWER" or "CANDIDATE" && seg.SpeakerConfidence >= TrustedConfidenceThreshold;

    /// <summary>
    /// 解析段首 marker。返回 (说话人, 剥离 marker 后的文本);无 marker 时说话人为 null。
    /// </summary>
    public static (string? Speaker, string Remaining) TryParseMarker(string text)
    {
        var m = ColonMarker.Match(text);
        if (m.Success)
            return (MapSpeaker(m.Groups[1].Value), text[(m.Index + m.Length)..]);

        m = BracketMarker.Match(text);
        if (m.Success)
            return (MapSpeaker(m.Groups[1].Value), text[(m.Index + m.Length)..]);

        return (null, text);
    }

    private static string MapSpeaker(string marker) => marker.ToLowerInvariant() switch
    {
        "面试官" or "interviewer" => "INTERVIEWER",
        _ => "CANDIDATE" // 我 / 候选人 / me / candidate
    };
}
