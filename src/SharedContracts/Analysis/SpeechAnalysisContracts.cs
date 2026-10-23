namespace YourInterview.SharedContracts.Analysis;

/// <summary>
/// 分析管线的线缆契约(票40 ARC-15 合一,2026-10-22)。
///
/// 历史:这三个类型原在 Analysis.Worker/Analysis/PipelineContracts.cs 本地定义,
/// 与本契约模块旧 SpeechMetrics「靠注释同构」——但字段级盘点(见 evidence-40/STATUS.md)
/// 证明两者仅 6/15 字段同名,旧版全仓零消费,而 Worker 版才是活线缆形态:
/// WriteBack 的 speechMetrics 载荷、Interviews 存储的 SpeechMetricsJson 与其
/// SpeechMetricsDto 镜像、analysis.json 落盘,全部以本形态为准。
/// 裁定:以活线缆形态为契约真源,三类型逐字(字段名/类型/可空性/默认值/
/// 参数顺序全不变)升入契约模块成为唯一定义;Worker 删本地副本直接引用。
/// 序列化逐字段形态由 Analysis.Worker.Tests/ContractSerializationLockTests 锁定。
/// </summary>

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
    IReadOnlyList<WordPronunciation> ProblemWords,
    /// <summary>
    /// 结构骨架标记(缺口4补齐,2026-10-07):检出的分点/转折/收尾标记 → 次数。
    /// 有标记 = 在尝试结构化表达;全空 = 全程平铺直叙。
    /// </summary>
    Dictionary<string, int>? StructureMarkers = null,
    /// <summary>
    /// 是否有音频。文本模式(粘贴文本)下为 false:发音相关指标无意义,
    /// 诊断时发音维度记为 null(不参与总分)。
    /// </summary>
    bool HasAudio = true);
