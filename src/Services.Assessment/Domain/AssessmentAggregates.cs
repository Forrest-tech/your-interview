namespace YourInterview.Services.Assessment.Domain;

/// <summary>
/// AI 实战模拟的领域模型。
///
/// 核心业务:AI 扮演面试官问你一道技术题 → 你口述回答 → 系统对回答做六维分析。
///
/// 与"实战机经"的区别(这是两个模块,别混淆):
///   实战机经 = 复盘**已经发生过**的真实面试(录音/转写/真实面试官问题)。
///   AI 模拟  = 用 AI **当场生成**问题、当场评分,用于练手和查漏。
///
/// 领域概念:
///   MockSession    一次模拟练习(选主题 → 若干题 → 出总评)
///   MockQuestion   单道题(题面 + 我的回答 + 该题的六维评分)
///   评分维度       发音 / 流利 / 语句 / 结构 / 技术深度 / 相关性(来自 SharedContracts 的六维契约)
/// </summary>
public enum SessionStatus
{
    /// <summary>已创建,还没开始答题。</summary>
    Draft = 0,
    /// <summary>进行中:还有没答完的题。</summary>
    InProgress = 1,
    /// <summary>已结束:所有题都答了,且已生成总评。</summary>
    Completed = 2,
    /// <summary>中途放弃。</summary>
    Abandoned = 3
}

/// <summary>练习模式 —— 决定出题的来源与难度策略。</summary>
public enum SessionMode
{
    /// <summary>按技术主题练(如 "EF Core 查询调优"):AI 就这个主题连问。</summary>
    TopicDrill = 0,
    /// <summary>按公司场景练:模仿目标公司的面试风格与考点(如 Geotab 风格)。</summary>
    CompanyStyle = 1,
    /// <summary>薄弱点强化:从我的历史短板里挑题(与 Knowledge / 实战机经联动)。</summary>
    WeaknessFocus = 2,
    /// <summary>完整模拟:像真面试一样混合技术 + 系统设计 + 行为题。</summary>
    FullLoop = 3
}

/// <summary>单题状态。</summary>
public enum QuestionState
{
    /// <summary>已出题,还没作答。</summary>
    Asked = 0,
    /// <summary>我答完了(有回答文本/音频),等分析。</summary>
    Answered = 1,
    /// <summary>已评分。</summary>
    Scored = 2,
    /// <summary>跳过。</summary>
    Skipped = 3
}

/// <summary>
/// 六维评分 —— 平台的核心度量。
/// 与 SharedContracts.Analysis.AnalysisScores 保持同一维度划分,
/// 这样"实战机经"和"AI 模拟"的分数可以放在同一张能力雷达图上对比。
/// </summary>
public sealed class DimensionScores
{
    private DimensionScores() { }

    public DimensionScores(int pronunciation, int fluency, int sentenceIntegrity, int structure,
        int technicalDepth, int relevance)
    {
        Pronunciation = Clamp(pronunciation);
        Fluency = Clamp(fluency);
        SentenceIntegrity = Clamp(sentenceIntegrity);
        Structure = Clamp(structure);
        TechnicalDepth = Clamp(technicalDepth);
        Relevance = Clamp(relevance);
    }

    /// <summary>发音:术语读音、重音、清晰度。参考值 0-100。</summary>
    public int Pronunciation { get; private set; }
    /// <summary>流利度:停顿、填充词、语速、自我重复。</summary>
    public int Fluency { get; private set; }
    /// <summary>语句完整性:句子是否完整、平均句长、有没有残句。</summary>
    public int SentenceIntegrity { get; private set; }
    /// <summary>结构:有没有骨架(结论先行 + First/Second/Third + trade-off)。</summary>
    public int Structure { get; private set; }
    /// <summary>技术深度:知识点是否正确、有没有讲到原理与权衡。</summary>
    public int TechnicalDepth { get; private set; }
    /// <summary>相关性:有没有答到点子上(答非所问是历史重灾区)。</summary>
    public int Relevance { get; private set; }

    /// <summary>
    /// 加权总分。
    /// 权重反映"对拿到 offer 的实际影响":技术深度与结构权重最高
    /// —— 这是历次面试复盘得出的排序(发音 92.8 分但没过,结构与深度是瓶颈)。
    /// </summary>
    public int Overall => (int)Math.Round(
        TechnicalDepth * 0.28 +
        Structure * 0.22 +
        Relevance * 0.18 +
        Fluency * 0.14 +
        SentenceIntegrity * 0.10 +
        Pronunciation * 0.08);

    /// <summary>最弱的一维 —— 决定了下一步该练什么。</summary>
    public string WeakestDimension()
    {
        var all = new (string Name, int Score)[]
        {
            ("发音", Pronunciation), ("流利度", Fluency), ("语句完整性", SentenceIntegrity),
            ("结构", Structure), ("技术深度", TechnicalDepth), ("相关性", Relevance)
        };
        return all.OrderBy(x => x.Score).First().Name;
    }

    private static int Clamp(int v) => Math.Clamp(v, 0, 100);
}

/// <summary>六维中的某一维的明细问题(用于"这一维为什么扣分")。</summary>
public sealed class DimensionIssue
{
    private DimensionIssue() { }

    public DimensionIssue(string dimension, string title, string? detail, string? evidence,
        string? suggestion, int severity = 3)
    {
        if (string.IsNullOrWhiteSpace(dimension)) throw new ArgumentException("维度不能为空", nameof(dimension));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("标题不能为空", nameof(title));

        Dimension = dimension.Trim();
        Title = title.Trim();
        Detail = detail;
        Evidence = evidence;
        Suggestion = suggestion;
        Severity = Math.Clamp(severity, 1, 5);
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    /// <summary>所属题目(EF 维护的关系字段)。</summary>
    public Guid QuestionId { get; private set; }
    /// <summary>所属维度(发音/流利度/语句/结构/技术深度/相关性)。</summary>
    public string Dimension { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string? Detail { get; private set; }
    /// <summary>证据:原话片段 / 度量数字(如"平均句长 7.5 词")。</summary>
    public string? Evidence { get; private set; }
    public string? Suggestion { get; private set; }
    public int Severity { get; private set; }
}

/// <summary>
/// 一次 AI 实战模拟会话。
/// 聚合根:题目、总评、状态全部通过它维护,外部不能直接改子对象。
/// </summary>
public sealed class MockSession
{
    private readonly List<MockQuestion> _questions = [];

    private MockSession() { }

    public MockSession(Guid userId, string title, SessionMode mode, string? topic,
        string? companyStyle, int targetQuestionCount = 5, string language = "en")
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("标题不能为空", nameof(title));

        UserId = userId;
        Title = title.Trim();
        Mode = mode;
        Topic = topic;
        CompanyStyle = companyStyle;
        TargetQuestionCount = Math.Clamp(targetQuestionCount, 1, 20);
        Language = language;
        Status = SessionStatus.Draft;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public SessionMode Mode { get; private set; }
    /// <summary>练习主题(如 "EF Core 查询调优")。</summary>
    public string? Topic { get; private set; }
    /// <summary>模仿的公司面试风格(CompanyStyle 模式用,如 "Geotab")。</summary>
    public string? CompanyStyle { get; private set; }
    public int TargetQuestionCount { get; private set; }
    /// <summary>语言(默认 en —— 练北美面试就用英文)。</summary>
    public string Language { get; private set; } = "en";
    public SessionStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>总评文本(AI 生成的整体诊断)。</summary>
    public string? OverallSummary { get; private set; }
    /// <summary>本次练习最该改的一件事。</summary>
    public string? PriorityAction { get; private set; }
    /// <summary>总评分(各题平均)。</summary>
    public int? OverallScore { get; private set; }

    public IReadOnlyCollection<MockQuestion> Questions => _questions.AsReadOnly();
    public bool IsDeleted { get; private set; }

    /// <summary>出题:AI 生成一道题后加入会话。</summary>
    public MockQuestion AddQuestion(string questionText, Domain.QuestionType type,
        string? expectedPointsJson = null, int difficulty = 3)
    {
        if (Status == SessionStatus.Completed)
            throw new InvalidOperationException("会话已结束,不能再出题");
        if (_questions.Count >= TargetQuestionCount)
            throw new InvalidOperationException($"本次练习已出满 {TargetQuestionCount} 题");

        var seq = _questions.Count == 0 ? 1 : _questions.Max(q => q.Sequence) + 1;
        var q = new MockQuestion(Id, seq, questionText, type, expectedPointsJson, difficulty);
        _questions.Add(q);

        if (Status == SessionStatus.Draft) Status = SessionStatus.InProgress;
        Touch();
        return q;
    }

    /// <summary>作答:记录我的回答(文本 + 可选音频引用)。</summary>
    public MockQuestion AnswerQuestion(Guid questionId, string? answerText,
        string? audioPath = null, double? durationSeconds = null, string? transcriptJson = null)
    {
        var q = Find(questionId);
        q.Answer(answerText, audioPath, durationSeconds, transcriptJson);
        Touch();
        return q;
    }

    /// <summary>给某题打分(六维 + 明细问题 + 点评 + 北美推荐答案)。</summary>
    public void ScoreQuestion(Guid questionId, DimensionScores scores, IEnumerable<DimensionIssue> issues,
        string? comment, string? recommendedAnswer, string? betterStructure, string? fillerWordsJson = null)
    {
        var q = Find(questionId);
        q.Score(scores, issues, comment, recommendedAnswer, betterStructure, fillerWordsJson);
        Touch();
    }

    public void SkipQuestion(Guid questionId)
    {
        Find(questionId).Skip();
        Touch();
    }

    /// <summary>
    /// 结束会话并生成总评。
    /// 总评分 = 已评分题目的平均 Overall(跳过未评分的题,不拉低分数掩盖问题)。
    /// </summary>
    public void Complete(string? overallSummary, string? priorityAction)
    {
        if (Status == SessionStatus.Completed) return;

        var scored = _questions.Where(q => q.Scores is not null).ToList();
        if (scored.Count > 0)
            OverallScore = (int)Math.Round(scored.Average(q => q.Scores!.Overall));

        OverallSummary = overallSummary;
        PriorityAction = priorityAction;
        Status = SessionStatus.Completed;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void Abandon()
    {
        if (Status == SessionStatus.Completed) return;
        Status = SessionStatus.Abandoned;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void MarkDeleted()
    {
        IsDeleted = true;
        Touch();
    }

    /// <summary>
    /// 汇总本次练习的短板 —— 各题明细问题按维度归并。
    /// 这是"练完一轮后我该做什么"的唯一出口,所以放在聚合上而不是查询端算,
    /// 保证任何读取路径(列表/详情/导出)拿到的是同一份结论。
    /// </summary>
    public IReadOnlyList<(string Dimension, int Count, double AvgSeverity)> WeaknessSummary()
    {
        return _questions
            .SelectMany(q => q.Issues)
            .GroupBy(i => i.Dimension)
            .Select(g => (g.Key, g.Count(), g.Average(i => i.Severity)))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    /// <summary>跨会话可复用的进步曲线数据点(供 Analytics 用)。</summary>
    public (int Pronunciation, int Fluency, int SentenceIntegrity, int Structure,
        int TechnicalDepth, int Relevance)? AverageScores()
    {
        var scored = _questions.Where(q => q.Scores is not null).Select(q => q.Scores!).ToList();
        if (scored.Count == 0) return null;

        return (
            (int)Math.Round(scored.Average(s => s.Pronunciation)),
            (int)Math.Round(scored.Average(s => s.Fluency)),
            (int)Math.Round(scored.Average(s => s.SentenceIntegrity)),
            (int)Math.Round(scored.Average(s => s.Structure)),
            (int)Math.Round(scored.Average(s => s.TechnicalDepth)),
            (int)Math.Round(scored.Average(s => s.Relevance)));
    }

    private MockQuestion Find(Guid questionId) =>
        _questions.FirstOrDefault(q => q.Id == questionId)
        ?? throw new InvalidOperationException("题目不存在");

    private void Touch() { /* 由 DbContext 的审计逻辑接管时间戳 */ }
}

/// <summary>题目类型 —— 决定"标准答案该用什么骨架"(与技术栈模块的分类保持一致)。</summary>
public enum QuestionType
{
    /// <summary>技术细节题:考知识点本身。</summary>
    Technical = 0,
    /// <summary>系统设计题:必须有 First/Second/Third + trade-off。</summary>
    SystemDesign = 1,
    /// <summary>行为题:STAR。</summary>
    Behavioral = 2,
    /// <summary>编码题:口述思路或写代码。</summary>
    Coding = 3,
    /// <summary>项目经历深挖:顺着简历追问。</summary>
    ProjectDeepDive = 4,
    /// <summary>反问环节:我该问面试官什么。</summary>
    ReverseQuestion = 5
}

/// <summary>单道模拟题。</summary>
public sealed class MockQuestion
{
    private readonly List<DimensionIssue> _issues = [];

    private MockQuestion() { }

    internal MockQuestion(Guid sessionId, int sequence, string questionText, QuestionType type,
        string? expectedPointsJson, int difficulty)
    {
        if (string.IsNullOrWhiteSpace(questionText))
            throw new ArgumentException("题目内容不能为空", nameof(questionText));

        SessionId = sessionId;
        Sequence = sequence;
        QuestionText = questionText.Trim();
        Type = type;
        ExpectedPointsJson = expectedPointsJson;
        Difficulty = Math.Clamp(difficulty, 1, 5);
        State = QuestionState.Asked;
        AskedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid SessionId { get; private set; }
    public int Sequence { get; private set; }
    public string QuestionText { get; private set; } = string.Empty;
    public QuestionType Type { get; private set; }
    /// <summary>出题时预期的得分点(JSON 数组)—— 评分时 AI 据此判断有没有漏讲。</summary>
    public string? ExpectedPointsJson { get; private set; }
    public int Difficulty { get; private set; }
    public QuestionState State { get; private set; }
    public DateTimeOffset AskedAt { get; private set; }

    /// <summary>我的回答文本(逐字转写后的)。</summary>
    public string? AnswerText { get; private set; }
    /// <summary>我的回答录音路径(如果要分析发音就需要它)。</summary>
    public string? AnswerAudioPath { get; private set; }
    public double? AnswerDurationSeconds { get; private set; }
    /// <summary>回答的分段转写(含说话人/时间戳,JSON)。</summary>
    public string? AnswerTranscriptJson { get; private set; }
    public DateTimeOffset? AnsweredAt { get; private set; }

    public string? Comment { get; private set; }
    /// <summary>北美面试语境下的推荐答案(这是最有价值的学习产物)。</summary>
    public string? RecommendedAnswer { get; private set; }
    /// <summary>更好的结构骨架示范(把"我该怎么说"显式写出来)。</summary>
    public string? BetterStructure { get; private set; }
    /// <summary>检测到的填充词(JSON:词 + 次数)。</summary>
    public string? FillerWordsJson { get; private set; }
    public DateTimeOffset? ScoredAt { get; private set; }

    public DimensionScores? Scores { get; private set; }
    public IReadOnlyCollection<DimensionIssue> Issues => _issues.AsReadOnly();

    /// <summary>本次回答最大的一个问题 —— 详情页把它顶到最显眼的位置。</summary>
    public DimensionIssue? TopIssue =>
        _issues.Count == 0 ? null : _issues.OrderByDescending(i => i.Severity).First();

    internal void Answer(string? answerText, string? audioPath, double? durationSeconds,
        string? transcriptJson)
    {
        if (State == QuestionState.Scored)
            throw new InvalidOperationException("该题已评分,不能重复作答");

        AnswerText = answerText;
        AnswerAudioPath = audioPath;
        AnswerDurationSeconds = durationSeconds;
        AnswerTranscriptJson = transcriptJson;
        AnsweredAt = DateTimeOffset.UtcNow;
        State = QuestionState.Answered;
    }

    internal void Score(DimensionScores scores, IEnumerable<DimensionIssue> issues, string? comment,
        string? recommendedAnswer, string? betterStructure, string? fillerWordsJson)
    {
        Scores = scores ?? throw new ArgumentNullException(nameof(scores));

        // 重新评分时替换旧明细,避免重复累积(可重跑是硬要求)
        _issues.Clear();
        _issues.AddRange(issues);

        Comment = comment;
        RecommendedAnswer = recommendedAnswer;
        BetterStructure = betterStructure;
        FillerWordsJson = fillerWordsJson;
        ScoredAt = DateTimeOffset.UtcNow;
        State = QuestionState.Scored;
    }

    internal void Skip()
    {
        if (State == QuestionState.Scored) return;
        State = QuestionState.Skipped;
    }
}
