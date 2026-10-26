using YourInterview.SharedContracts.Analysis;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 分析管线的 Worker 内部编排类型(非跨服务契约)。
///
/// 票40(ARC-15)合一:原同文件的 SpeechMetrics / TranscriptSegment /
/// WordPronunciation 三个线缆类型已升入 SharedContracts.Analysis 成为唯一
/// 定义(字段逐字不变,见 SpeechAnalysisContracts.cs),本文件只留管线
/// 编排内部类型(PipelineContext/StageResult/AnalysisArtifact/
/// IPipelineStage/PipelineState)——它们无 SharedContracts 对应物、
/// 从不以类型形态跨服务序列化,不存在副本问题。
/// Worker 只负责"把音频变成数字",业务怎么用这些数字由各服务决定。
///
/// 管线五阶段(每阶段都能单独重跑,这是排障的关键):
///   1. Prepare   —— 音频转成 Azure 要的 WAV PCM 16k 单声道
///   2. Transcribe—— 分段转写(说话人标签初始为 UNKNOWN)
///   2.5 SpeakerLabel —— 说话人标注:文本 marker 解析 / 可信标签保留 / 无 marker 全文候选人
///   3. Measure   —— 客观声学指标(语速/填充词/发音准确度)
///   4. Diagnose  —— 汇总成六维评分 + 问题清单
/// </summary>
public sealed record PipelineContext(
    Guid InterviewEntryId,
    Guid AssetId,
    Guid UserId,
    string SourceFilePath,
    string WorkingDirectory,
    string Language = "en-US",
    /// <summary>
    /// 粘贴文本预播种的分段(文本模式)。有值时管线跳过音频阶段
    /// (Prepare/Transcribe 的 CanRun 本来就要求音频文件),直接从文本算指标。
    /// </summary>
    IReadOnlyList<TranscriptSegment>? SeedSegments = null);

/// <summary>一阶段的结果 —— 带成功/失败,让管线能"部分成功"地报错。</summary>
public sealed record StageResult(string Stage, bool Success, string? Error, double ElapsedMs);

/// <summary>管线跑完的完整产物。</summary>
public sealed record AnalysisArtifact(
    Guid InterviewEntryId,
    Guid AssetId,
    IReadOnlyList<TranscriptSegment> Segments,
    SpeechMetrics Metrics,
    string FullTranscript,
    string? InterviewerTranscript,
    string? CandidateTranscript,
    IReadOnlyList<StageResult> Stages,
    string? ReportMarkdown);

/// <summary>
/// 管线的一个阶段。
/// 抽象成接口是为了能替换实现:本地跑 ffmpeg 与将来跑 Azure Batch 是同一套编排。
/// </summary>
public interface IPipelineStage
{
    string Name { get; }

    /// <summary>这一阶段能不能跳过(输入不具备时)。</summary>
    bool CanRun(PipelineContext context, PipelineState state);

    Task<PipelineState> ExecuteAsync(PipelineContext context, PipelineState state,
        CancellationToken ct);
}

/// <summary>阶段之间传递的中间状态(可变,便于逐阶段累加)。</summary>
public sealed class PipelineState
{
    public string? NormalizedWavPath { get; set; }
    public double DurationSeconds { get; set; }
    public List<TranscriptSegment> Segments { get; } = [];
    public SpeechMetrics? Metrics { get; set; }
    public string? FullTranscript { get; set; }
    public string? InterviewerTranscript { get; set; }
    public string? CandidateTranscript { get; set; }
    public string? ReportMarkdown { get; set; }
    public List<StageResult> Stages { get; } = [];
}
