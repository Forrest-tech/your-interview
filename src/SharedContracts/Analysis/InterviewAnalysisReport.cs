namespace YourInterview.SharedContracts.Analysis;

/// <summary>
/// 实战机经 / AI 模拟 的分析结果契约。
/// 这六个维度就是 Forrest 明确要求的:"发音、逻辑、表达、语句、技术点、技术回答逻辑"
/// 再叠加历史复盘沉淀的"短板 / 卡壳点 / 结构骨架 / 术语发音 / 答非所问"。
/// </summary>
public sealed record InterviewAnalysisReport
{
    public Guid ReportId { get; init; } = Guid.NewGuid();
    public Guid InterviewSessionId { get; init; }
    public Guid AssetId { get; init; }
    public Guid UserId { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>总览评分(0-100),前端雷达图主入口。</summary>
    public AnalysisScores Scores { get; init; } = new();

    /// <summary>客观指标:语速/句长/填充词/停顿 —— Azure + 自研声学指标算出。</summary>
    public SpeechMetrics Metrics { get; init; } = new();

    /// <summary>说话人分离结果 + 每轮发言归属。</summary>
    public List<SpeakerTurn> Turns { get; init; } = [];

    /// <summary>面试官问题 → 我的回答 → 诊断(逐条对照)。</summary>
    public List<QuestionAnswerPair> QuestionAnswers { get; init; } = [];

    /// <summary>短板清单(按严重度排序)。</summary>
    public List<ShortBoard> ShortBoards { get; init; } = [];

    /// <summary>卡壳点(停顿/重复/自我纠正的具体位置)。</summary>
    public List<StuckPoint> StuckPoints { get; init; } = [];

    /// <summary>术语发音错误表(你说的 → 正确 → 危害等级)。</summary>
    public List<TermPronunciationIssue> TermIssues { get; init; } = [];

    /// <summary>结构骨架使用情况(结论先行 / First-Second-Third / trade-off)。</summary>
    public StructureAnalysis Structure { get; init; } = new();

    /// <summary>可执行提升方案。</summary>
    public List<ActionItem> ActionItems { get; init; } = [];

    /// <summary>AI 生成的自然语言总结。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>本次分析自动产出的技术知识点(会自动进入"技术栈"模块)。</summary>
    public List<ExtractedKnowledgeItem> ExtractedKnowledge { get; init; } = [];
}

public sealed record AnalysisScores
{
    /// <summary>发音准确度(Azure AccuracyScore 均值)</summary>
    public int Pronunciation { get; init; }
    /// <summary>流利度(填充词/停顿/重复综合)</summary>
    public int Fluency { get; init; }
    /// <summary>句子完整性(平均句长/残句比例)</summary>
    public int SentenceIntegrity { get; init; }
    /// <summary>结构骨架(结论先行 + 分点 + trade-off 使用率)</summary>
    public int Structure { get; init; }
    /// <summary>技术深度(术语密度 + 纵深追问响应)</summary>
    public int TechnicalDepth { get; init; }
    /// <summary>回答切题度(答非所问惩罚)</summary>
    public int Relevance { get; init; }
    /// <summary>加权总分</summary>
    public int Overall { get; init; }
}

public sealed record SpeechMetrics
{
    public int UserWordCount { get; init; }
    public int InterviewerWordCount { get; init; }
    public double UserSpeakingRatio { get; init; }
    public int UserTurnCount { get; init; }
    public double SpeakingSeconds { get; init; }
    public double WordsPerMinute { get; init; }
    public double AverageSentenceLength { get; init; }
    public int ShortSentenceCount { get; init; }
    public int FillerWordCount { get; init; }
    public double FillerPer100Words { get; init; }
    public int SelfRepetitionCount { get; init; }
    public int LongPauseCount { get; init; }
    public double AveragePauseSeconds { get; init; }
    public Dictionary<string, int> Fillers { get; init; } = new();
    public Dictionary<string, int> TechnicalTerms { get; init; } = new();
}

public sealed record SpeakerTurn
{
    public int Index { get; init; }
    public string Speaker { get; init; } = "Unknown";   // User | Interviewer | Unknown
    public double EstimatedPitchHz { get; init; }
    public double StartSeconds { get; init; }
    public double EndSeconds { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Confidence { get; init; }
}

public sealed record QuestionAnswerPair
{
    public string Question { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string QuestionType { get; init; } = "Unknown"; // Behavioral | Technical | SystemDesign | HonestyBoundary | Screening
    public bool AnsweredOnTopic { get; init; }
    public string Diagnosis { get; init; } = string.Empty;
    public string RecommendedAnswer { get; init; } = string.Empty;
    public List<string> MissedPoints { get; init; } = [];
}

public sealed record ShortBoard
{
    public string Title { get; init; } = string.Empty;
    public string Evidence { get; init; } = string.Empty;
    public string Severity { get; init; } = "Medium"; // Critical | High | Medium | Low
    public string Fix { get; init; } = string.Empty;
}

public sealed record StuckPoint
{
    public double AtSeconds { get; init; }
    public string Kind { get; init; } = "Pause"; // Pause | Repetition | SelfCorrection | AbandonedSentence
    public string Excerpt { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}

public sealed record TermPronunciationIssue
{
    public string Said { get; init; } = string.Empty;
    public string Correct { get; init; } = string.Empty;
    public string IpaHint { get; init; } = string.Empty;
    public string Severity { get; init; } = "Medium"; // High | Medium | Low
    public string Why { get; init; } = string.Empty;
}

public sealed record StructureAnalysis
{
    public int TotalTurns { get; init; }
    public int TurnsWithSignposting { get; init; }
    public double SignpostingRate { get; init; }
    public bool UsedConclusionFirst { get; init; }
    public bool UsedEnumerations { get; init; }
    public bool UsedTradeOffLanguage { get; init; }
    public List<string> MissingTemplates { get; init; } = [];
}

public sealed record ActionItem
{
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Horizon { get; init; } = "This week"; // Today | This week | This month
    public int Priority { get; init; } = 3;             // 1 最高
}

public sealed record ExtractedKnowledgeItem
{
    public string Concept { get; init; } = string.Empty;
    public string Category { get; init; } = "General";
    public string Question { get; init; } = string.Empty;
    public string MyAnswerSummary { get; init; } = string.Empty;
    public int SelfRating { get; init; } = 2; // 1 不会 / 2 一般 / 3 熟练
}
