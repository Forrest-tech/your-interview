namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 分析管线的领域契约。
///
/// 这些类型刻意与 SharedContracts.Analysis 的六维契约保持同构 ——
/// Worker 只负责"把音频变成数字",业务怎么用这些数字由各服务决定。
///
/// 管线四阶段(每阶段都能单独重跑,这是排障的关键):
///   1. Prepare   —— 音频转成 Azure 要的 WAV PCM 16k 单声道
///   2. Transcribe—— 分段转写 + 说话人分离
///   3. Measure   —— 客观声学指标(语速/填充词/发音准确度)
///   4. Diagnose  —— 汇总成六维评分 + 问题清单
/// </summary>
public sealed record PipelineContext(
    Guid InterviewEntryId,
    Guid AssetId,
    Guid UserId,
    string SourceFilePath,
    string WorkingDirectory,
    string Language = "en-US");

/// <summary>转写出来的一段话。</summary>
public sealed record TranscriptSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Text,
    /// <summary>说话人标签:INTERVIEWER(面试官)/ CANDIDATE(我)/ UNKNOWN。</summary>
    string Speaker,
    /// <summary>说话人置信度 0-1。</summary>
    double SpeakerConfidence = 1.0);

/// <summary>逐词发音评估结果。</summary>
public sealed record WordPronunciation(
    string Word,
    double AccuracyScore,
    double? FluencyScore,
    double StartSeconds,
    double EndSeconds);

/// <summary>客观声学与语言指标 —— 这些是"可测量"的,不掺主观判断。</summary>
public sealed record SpeechMetrics(
    double DurationSeconds,
    int WordCount,
    /// <summary>词/分钟。北美技术面试对话的舒适区间约 130-170。</summary>
    double WordsPerMinute,
    /// <summary>平均句长(词)。低于 10 说明句子被切碎。</summary>
    double AverageSentenceLength,
    /// <summary>过短句(<5 词)条数。</summary>
    int ShortSentenceCount,
    /// <summary>填充词总数(um/uh/like...)。</summary>
    int FillerWordCount,
    /// <summary>填充词明细:词 → 次数。</summary>
    Dictionary<string, int> FillerWordBreakdown,
    /// <summary>自我重复片段数。</summary>
    int SelfRepetitionCount,
    /// <summary>长停顿(>2 秒)次数。</summary>
    int LongPauseCount,
    /// <summary>总停顿秒数。</summary>
    double TotalSilenceSeconds,
    /// <summary>发音准确度均分(0-100)。</summary>
    double PronunciationAccuracy,
    /// <summary>低分词(<60)占比。</summary>
    double LowScoreWordRatio,
    /// <summary>读错的专业术语。</summary>
    IReadOnlyList<WordPronunciation> ProblemWords);

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
