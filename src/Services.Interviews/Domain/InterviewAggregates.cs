using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Interviews.Domain;

/// <summary>
/// 面试条目状态机。
/// 一次面试材料从"新建"到"分析完成"是一条明确的流水线,
/// 用状态机把这条流水线固化在领域层 —— 避免出现"还没转写就分析"这种脏数据。
/// </summary>
public enum InterviewStatus
{
    /// <summary>刚创建,还没上传任何材料。</summary>
    Draft,
    /// <summary>已上传录音/文本材料,可开始转写。</summary>
    AssetsUploaded,
    /// <summary>转写进行中(分析 Worker 正在跑 Azure STT)。</summary>
    Transcribing,
    /// <summary>转写完成,已有全文与分段。</summary>
    Transcribed,
    /// <summary>六维分析进行中。</summary>
    Analyzing,
    /// <summary>分析完成,短板/卡壳/问答已回写。</summary>
    Analyzed,
    /// <summary>转写或分析失败,可在修正后重试。</summary>
    Failed
}

/// <summary>材料类型。录音和文本走同一条聚合,便于"录音 + 手写纪要"混用。</summary>
public enum AssetKind
{
    /// <summary>录音文件(wav/mp3/m4a),需要走 STT。</summary>
    Audio,
    /// <summary>已有转写文本(比如会议软件导出的字幕)。</summary>
    Transcript,
    /// <summary>自己写的面试纪要,不需要 STT,直接进分析。</summary>
    Notes
}

/// <summary>
/// 短板归类。
/// 这六类 + 三类工程类短板,直接对应 Forrest 要求的分析维度
/// (发音、逻辑、表达、语句、技术点、技术回答逻辑)以及历史复盘沉淀的问题类型。
/// </summary>
public enum WeaknessCategory
{
    /// <summary>发音(术语读错、重音错)</summary>
    Pronunciation,
    /// <summary>回答逻辑(无骨架、跳跃、结论靠后)</summary>
    Logic,
    /// <summary>表达(词不达意、中式英语)</summary>
    Expression,
    /// <summary>语句(语法病:can+ing、时态、残句)</summary>
    Grammar,
    /// <summary>技术点(知识点本身不会)</summary>
    TechnicalDepth,
    /// <summary>技术回答逻辑(会但讲不出 trade-off)</summary>
    TechnicalStructure,
    /// <summary>结构骨架(没用 First/Second/Third)</summary>
    Structure,
    /// <summary>术语(用词不准)</summary>
    Terminology,
    /// <summary>听力/理解(听不出题型、答非所问)</summary>
    Listening,
    /// <summary>心态(自我否定、口头禅)</summary>
    Mindset
}

/// <summary>问题类型 —— 决定"标准答案"该用什么骨架。</summary>
public enum QuestionCategory
{
    /// <summary>技术细节题</summary>
    Technical,
    /// <summary>系统设计题(必须有 First/Second/Third + trade-off)</summary>
    SystemDesign,
    /// <summary>行为题(STAR)</summary>
    Behavioral,
    /// <summary>诚实边界题(没做过的技术,考的是怎么答)</summary>
    HonestyBoundary,
    /// <summary>筛选题(薪资/到岗时间/身份)</summary>
    Screening,
    /// <summary>闲聊</summary>
    SmallTalk
}

/// <summary>短板来源:手工录入的 vs AI 分析产出的。前端用不同颜色区分。</summary>
public enum WeaknessSource
{
    /// <summary>面试后自己复盘录入</summary>
    Manual,
    /// <summary>AI 六维分析自动产出</summary>
    AiAnalysis
}

/// <summary>
/// 面试条目 —— 实战机经的核心聚合。
///
/// 一个条目 = 某公司某岗位的**一场面试**(不是一次投递;同一家公司可能有 2-3 轮,各建一条)。
/// 聚合内包含:材料(录音/文本)、问题与回答、短板清单。
/// 不变的业务规则(不变量)集中在聚合方法里:
///   1. 没有材料不能转写;2. 没转写不能分析;3. 非法状态流转直接抛异常。
/// </summary>
public sealed class InterviewEntry : AuditableAggregateRoot
{
    private readonly List<InterviewAsset> _assets = [];
    private readonly List<InterviewQuestion> _questions = [];
    private readonly List<InterviewWeakness> _weaknesses = [];

    private InterviewEntry() { }

    public InterviewEntry(Guid companyId, string companyName, string role,
        Guid? jobApplicationId = null)
    {
        CompanyId = companyId;
        CompanyName = companyName;
        Role = role;
        JobApplicationId = jobApplicationId;
        Status = InterviewStatus.Draft;
        RaiseDomainEvent(new InterviewEntryCreatedDomainEvent(Id, companyId, companyName, role));
    }

    // ---------- 基本信息 ----------
    public Guid CompanyId { get; private set; }
    public string CompanyName { get; private set; } = string.Empty;
    public Guid? JobApplicationId { get; private set; }
    public string Role { get; private set; } = string.Empty;

    /// <summary>公司情况:规模/业务/技术栈/面试风格/文化(长文本,自己整理)。</summary>
    public string? CompanyProfile { get; private set; }

    /// <summary>JD 全文。</summary>
    public string? JdText { get; private set; }

    /// <summary>JD 要点摘要(自己或 AI 提炼)。</summary>
    public string? JdSummary { get; private set; }

    // ---------- 面试场次信息 ----------
    public int RoundNo { get; private set; } = 1;
    public DateOnly? InterviewDate { get; private set; }
    public string? InterviewFormat { get; private set; }   // Phone | Video | Onsite
    public string? Interviewers { get; private set; }      // "David Castelino (Sr SWE), Vinitha Kotha (Lead SWE)"
    public string? Result { get; private set; }            // Passed | Rejected | Pending | Ghosted
    public string? Location { get; private set; }
    public string? Notes { get; private set; }

    // ---------- 状态与流水线 ----------
    public InterviewStatus Status { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? TranscribedAt { get; private set; }
    public DateTimeOffset? AnalyzedAt { get; private set; }

    // ---------- 分析产出(六维总分,便于列表页直接展示) ----------
    public int? OverallScore { get; private set; }
    public int? PronunciationScore { get; private set; }
    public int? FluencyScore { get; private set; }
    public int? StructureScore { get; private set; }
    public int? TechnicalDepthScore { get; private set; }
    public int? RelevanceScore { get; private set; }
    public string? AnalysisSummary { get; private set; }

    public IReadOnlyCollection<InterviewAsset> Assets => _assets.AsReadOnly();
    public IReadOnlyCollection<InterviewQuestion> Questions => _questions.AsReadOnly();
    public IReadOnlyCollection<InterviewWeakness> Weaknesses => _weaknesses.AsReadOnly();

    /// <summary>合法流转表。集中定义,避免散落在各处 if-else。</summary>
    private static readonly Dictionary<InterviewStatus, InterviewStatus[]> AllowedTransitions = new()
    {
        [InterviewStatus.Draft] = [InterviewStatus.AssetsUploaded, InterviewStatus.Transcribing],
        [InterviewStatus.AssetsUploaded] = [InterviewStatus.Transcribing, InterviewStatus.Draft],
        [InterviewStatus.Transcribing] = [InterviewStatus.Transcribed, InterviewStatus.Failed],
        [InterviewStatus.Transcribed] = [InterviewStatus.Analyzing, InterviewStatus.Transcribing],
        [InterviewStatus.Analyzing] = [InterviewStatus.Analyzed, InterviewStatus.Failed],
        [InterviewStatus.Analyzed] = [InterviewStatus.Analyzing],
        [InterviewStatus.Failed] = [InterviewStatus.AssetsUploaded, InterviewStatus.Transcribing, InterviewStatus.Transcribed]
    };

    public bool CanTransitionTo(InterviewStatus target)
        => AllowedTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(target);

    // ============================ 领域行为 ============================

    public void UpdateBasicInfo(string companyName, string role, string? companyProfile,
        string? jdText, string? jdSummary, int roundNo, DateOnly? interviewDate,
        string? interviewFormat, string? interviewers, string? location, string? result, string? notes)
    {
        CompanyName = companyName;
        Role = role;
        CompanyProfile = companyProfile;
        JdText = jdText;
        JdSummary = jdSummary;
        RoundNo = roundNo <= 0 ? 1 : roundNo;
        InterviewDate = interviewDate;
        InterviewFormat = interviewFormat;
        Interviewers = interviewers;
        Location = location;
        Result = result;
        Notes = notes;
        Touch();
    }

    /// <summary>挂上一份材料(录音或文本)。会自动把 Draft 推进到 AssetsUploaded。</summary>
    public InterviewAsset AttachAsset(AssetKind kind, string fileName, string? contentType,
        long sizeBytes, string? storagePath, string? blobUrl, string? transcriptText = null,
        string? transcriptSegmentsJson = null, double? durationSeconds = null,
        string sourceLanguage = "en")
    {
        if (Status == InterviewStatus.Analyzing || Status == InterviewStatus.Transcribing)
            throw new InvalidOperationException($"当前状态 {Status} 不允许变更材料,请等待流程结束");

        var asset = new InterviewAsset(Id, kind, fileName, contentType, sizeBytes, storagePath,
            blobUrl, durationSeconds, sourceLanguage);

        // 文本类材料直接就是"已转写"状态,不需要走 STT
        if (kind is AssetKind.Transcript or AssetKind.Notes)
        {
            if (string.IsNullOrWhiteSpace(transcriptText))
                throw new InvalidOperationException("文本类材料必须提供内容");
            asset.SetTranscript(transcriptText, transcriptSegmentsJson);
            if (Status == InterviewStatus.Draft) TransitionTo(InterviewStatus.AssetsUploaded);
            TransitionTo(InterviewStatus.Transcribed);
            TranscribedAt = DateTimeOffset.UtcNow;
        }
        else if (!string.IsNullOrWhiteSpace(transcriptText))
        {
            asset.SetTranscript(transcriptText, transcriptSegmentsJson);
            TranscribedAt = DateTimeOffset.UtcNow;
        }

        _assets.Add(asset);
        if (Status == InterviewStatus.Draft) TransitionTo(InterviewStatus.AssetsUploaded);
        Touch();
        return asset;
    }

    /// <summary>开始转写(由分析 Worker 调用)。</summary>
    public void StartTranscription()
    {
        if (Status == InterviewStatus.Transcribed || Status == InterviewStatus.Analyzed)
            return; // 幂等:重复消息不报错
        if (!_assets.Any())
            throw new InvalidOperationException("还没有上传任何材料,无法开始转写");
        TransitionTo(InterviewStatus.Transcribing);
        Touch();
    }

    /// <summary>转写完成,回写全文 + 分段(分段 JSON 含说话人标签与时间戳)。</summary>
    public void CompleteTranscription(Guid assetId, string fullText, string? segmentsJson)
    {
        var asset = _assets.FirstOrDefault(a => a.Id == assetId)
            ?? throw new InvalidOperationException("材料不存在");

        asset.SetTranscript(fullText, segmentsJson);
        TranscribedAt = DateTimeOffset.UtcNow;
        TransitionTo(InterviewStatus.Transcribed);
        Touch();
        RaiseDomainEvent(new InterviewTranscribedDomainEvent(Id, assetId, fullText.Length));
    }

    /// <summary>开始六维分析。</summary>
    public void StartAnalysis()
    {
        if (Status == InterviewStatus.Analyzed) return; // 幂等
        if (_assets.All(a => string.IsNullOrWhiteSpace(a.TranscriptText)))
            throw new InvalidOperationException("没有可分析的转写文本,请先完成转写");
        TransitionTo(InterviewStatus.Analyzing);
        Touch();
    }

    /// <summary>
    /// 回写 AI 分析结果。这是整条流水线的终点:
    /// 六维分入聚合根,逐题诊断入 Questions,短板入 Weaknesses。
    /// </summary>
    public void ApplyAnalysis(int overall, int pronunciation, int fluency, int structure,
        int technicalDepth, int relevance, string? summary,
        IEnumerable<QuestionDraft>? questions = null,
        IEnumerable<WeaknessDraft>? weaknesses = null)
    {
        OverallScore = overall;
        PronunciationScore = pronunciation;
        FluencyScore = fluency;
        StructureScore = structure;
        TechnicalDepthScore = technicalDepth;
        RelevanceScore = relevance;
        AnalysisSummary = summary;

        if (questions is not null)
        {
            // 用 (题序) 做幂等键:重跑分析时覆盖旧结果,不产生重复问答
            foreach (var q in questions)
            {
                var existing = _questions.FirstOrDefault(x => x.Sequence == q.Sequence);
                if (existing is null)
                {
                    var entity = new InterviewQuestion(Id, q.Sequence, q.QuestionText, q.Category, q.Difficulty);
                    entity.FillAnswer(q.MyAnswerText, q.Assessment, q.GotStuck, q.StuckReason,
                        q.RecommendedAnswer, q.AtkSeconds);
                    entity.SetTags(q.WeaknessTags, q.FollowUpQuestionsJson, q.MissedPointsJson);
                    _questions.Add(entity);
                }
                else
                {
                    existing.UpdateFromAnalysis(q.QuestionText, q.MyAnswerText, q.Assessment,
                        q.Category, q.Difficulty, q.GotStuck, q.StuckReason, q.RecommendedAnswer, q.AtkSeconds);
                    existing.SetTags(q.WeaknessTags, q.FollowUpQuestionsJson, q.MissedPointsJson);
                }
            }
        }

        if (weaknesses is not null)
        {
            // 分析产出的短板:先清掉上一轮 AI 产出(保留手工录入的),再写入 —— 保证可重跑
            _weaknesses.RemoveAll(w => w.SourceType == WeaknessSource.AiAnalysis);
            foreach (var w in weaknesses)
                _weaknesses.Add(new InterviewWeakness(Id, w.Category, w.Title, w.Detail,
                    w.Evidence, w.Severity, w.Suggestion, WeaknessSource.AiAnalysis));
        }

        AnalyzedAt = DateTimeOffset.UtcNow;
        TransitionTo(InterviewStatus.Analyzed);
        Touch();
        RaiseDomainEvent(new InterviewAnalysisAppliedDomainEvent(Id, CompanyId, overall));
    }

    public void MarkFailed(string reason)
    {
        FailureReason = reason;
        TransitionTo(InterviewStatus.Failed);
        Touch();
        RaiseDomainEvent(new InterviewAnalysisFailedDomainEvent(Id, reason));
    }

    // ---------- 手工维护问答与短板(不依赖 AI,面试后自己复盘时用) ----------

    public InterviewQuestion AddQuestion(string questionText, string? myAnswerText,
        QuestionCategory category = QuestionCategory.Technical, int difficulty = 3)
    {
        var seq = _questions.Count == 0 ? 1 : _questions.Max(q => q.Sequence) + 1;
        var q = new InterviewQuestion(Id, seq, questionText, category, difficulty);
        q.FillAnswer(myAnswerText, null, false, null, null, null);
        _questions.Add(q);
        Touch();
        return q;
    }

    public void UpdateQuestion(Guid questionId, string questionText, string? myAnswerText,
        QuestionCategory category, int difficulty, string? assessment, bool gotStuck,
        string? stuckReason, string? recommendedAnswer)
    {
        var q = _questions.FirstOrDefault(x => x.Id == questionId)
            ?? throw new InvalidOperationException("问题不存在");
        q.UpdateFromAnalysis(questionText, myAnswerText, assessment, category, difficulty,
            gotStuck, stuckReason, recommendedAnswer, q.AskedAtSeconds);
        Touch();
    }

    public void RemoveQuestion(Guid questionId)
    {
        var q = _questions.FirstOrDefault(x => x.Id == questionId)
            ?? throw new InvalidOperationException("问题不存在");
        _questions.Remove(q);
        Touch();
    }

    public InterviewWeakness AddWeakness(WeaknessCategory category, string title, string? detail,
        string? evidence, int severity, string? suggestion)
    {
        var w = new InterviewWeakness(Id, category, title, detail, evidence, severity,
            suggestion, WeaknessSource.Manual);
        _weaknesses.Add(w);
        Touch();
        return w;
    }

    public void RemoveWeakness(Guid weaknessId)
    {
        var w = _weaknesses.FirstOrDefault(x => x.Id == weaknessId);
        if (w is not null) { _weaknesses.Remove(w); Touch(); }
    }

    private void TransitionTo(InterviewStatus target)
    {
        if (target == Status) return;
        if (!CanTransitionTo(target))
            throw new InvalidOperationException($"不允许从 {Status} 直接流转到 {target}");
        Status = target;
    }
}

/// <summary>面试材料(录音 / 转写文本 / 手写纪要)。</summary>
public sealed class InterviewAsset : Entity
{
    private InterviewAsset() { }

    internal InterviewAsset(Guid interviewEntryId, AssetKind kind, string fileName,
        string? contentType, long sizeBytes, string? storagePath, string? blobUrl,
        double? durationSeconds, string sourceLanguage)
    {
        InterviewEntryId = interviewEntryId;
        Kind = kind;
        FileName = fileName;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        StoragePath = storagePath;
        BlobUrl = blobUrl;
        DurationSeconds = durationSeconds;
        SourceLanguage = sourceLanguage;
        UploadedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }
    public AssetKind Kind { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string? ContentType { get; private set; }
    public long SizeBytes { get; private set; }

    /// <summary>本地/容器内路径(分析 Worker 读它去跑 STT)。</summary>
    public string? StoragePath { get; private set; }

    /// <summary>生产环境用 Azure Blob 的 URL。</summary>
    public string? BlobUrl { get; private set; }

    public double? DurationSeconds { get; private set; }
    public string SourceLanguage { get; private set; } = "en";
    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>转写全文。</summary>
    public string? TranscriptText { get; private set; }

    /// <summary>转写分段 JSON: [{idx,speaker,start,end,text,pitchHz}] —— 说话人分离结果。</summary>
    public string? TranscriptSegmentsJson { get; private set; }

    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    internal void SetTranscript(string fullText, string? segmentsJson)
    {
        TranscriptText = fullText;
        TranscriptSegmentsJson = segmentsJson;
    }
}

/// <summary>
/// 面试官的问题 → 我的回答。
/// 这是实战机经最有价值的数据:每条都带诊断、短板标签、北美推荐答案,
/// 复习时直接当"答错题本"用。
/// </summary>
public sealed class InterviewQuestion : Entity
{
    private InterviewQuestion() { }

    internal InterviewQuestion(Guid interviewEntryId, int sequence, string questionText,
        QuestionCategory category, int difficulty)
    {
        InterviewEntryId = interviewEntryId;
        Sequence = sequence;
        QuestionText = questionText;
        Category = category;
        Difficulty = difficulty;
    }

    public Guid InterviewEntryId { get; private set; }

    /// <summary>在本场面里的提问顺序。</summary>
    public int Sequence { get; private set; }

    /// <summary>面试官原话(英文原文,逐字不改)。</summary>
    public string QuestionText { get; private set; } = string.Empty;

    /// <summary>我的回答原话(英文原文,逐字不改)。</summary>
    public string? MyAnswerText { get; private set; }

    public QuestionCategory Category { get; private set; }
    public int Difficulty { get; private set; }

    /// <summary>诊断:这个回答好/坏在哪。</summary>
    public string? Assessment { get; private set; }

    /// <summary>是否当场卡壳。</summary>
    public bool GotStuck { get; private set; }

    /// <summary>卡壳原因(听不懂?不会?词穷?)。</summary>
    public string? StuckReason { get; private set; }

    /// <summary>北美推荐答案(照着练)。</summary>
    public string? RecommendedAnswer { get; private set; }

    /// <summary>录音里提问所在秒数(用于回放对照)。</summary>
    public double? AskedAtSeconds { get; private set; }

    /// <summary>短板标签 JSON 数组,如 ["Structure","Terminology"]。</summary>
    public string? WeaknessTagsJson { get; private set; }

    /// <summary>可能被追问什么(JSON 数组),提前准备。</summary>
    public string? FollowUpQuestionsJson { get; private set; }

    /// <summary>漏掉的得分点(JSON 数组)。</summary>
    public string? MissedPointsJson { get; private set; }

    internal void FillAnswer(string? myAnswerText, string? assessment, bool gotStuck,
        string? stuckReason, string? recommendedAnswer, double? askedAtSeconds)
    {
        MyAnswerText = myAnswerText;
        Assessment = assessment;
        GotStuck = gotStuck;
        StuckReason = stuckReason;
        RecommendedAnswer = recommendedAnswer;
        AskedAtSeconds = askedAtSeconds;
    }

    internal void UpdateFromAnalysis(string questionText, string? myAnswerText, string? assessment,
        QuestionCategory category, int difficulty, bool gotStuck, string? stuckReason,
        string? recommendedAnswer, double? askedAtSeconds)
    {
        QuestionText = questionText;
        Category = category;
        Difficulty = difficulty;
        FillAnswer(myAnswerText, assessment, gotStuck, stuckReason, recommendedAnswer, askedAtSeconds);
    }

    internal void SetTags(string? weaknessTagsJson, string? followUpsJson, string? missedPointsJson)
    {
        WeaknessTagsJson = weaknessTagsJson;
        FollowUpQuestionsJson = followUpsJson;
        MissedPointsJson = missedPointsJson;
    }
}

/// <summary>短板条目。手工录入的与 AI 分析的共存,用 SourceType 区分。</summary>
public sealed class InterviewWeakness : Entity
{
    private InterviewWeakness() { }

    internal InterviewWeakness(Guid interviewEntryId, WeaknessCategory category, string title,
        string? detail, string? evidence, int severity, string? suggestion, WeaknessSource sourceType)
    {
        InterviewEntryId = interviewEntryId;
        Category = category;
        Title = title;
        Detail = detail;
        Evidence = evidence;
        Severity = severity;
        Suggestion = suggestion;
        SourceType = sourceType;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }
    public WeaknessCategory Category { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string? Detail { get; private set; }

    /// <summary>证据:转录原文片段 / 具体时间点 —— 让复盘不靠印象。</summary>
    public string? Evidence { get; private set; }

    /// <summary>严重度 1-5,列表按它倒序。</summary>
    public int Severity { get; private set; }

    public string? Suggestion { get; private set; }

    /// <summary>反复出现次数(多次面试同一问题会累加)。</summary>
    public int OccurrenceCount { get; private set; } = 1;

    public WeaknessSource SourceType { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}

// ============================ 领域事件 ============================

public sealed record InterviewEntryCreatedDomainEvent(Guid EntryId, Guid CompanyId,
    string CompanyName, string Role) : DomainEventBase;

public sealed record InterviewTranscribedDomainEvent(Guid EntryId, Guid AssetId, int TextLength)
    : DomainEventBase;

public sealed record InterviewAnalysisAppliedDomainEvent(Guid EntryId, Guid CompanyId, int OverallScore)
    : DomainEventBase;

public sealed record InterviewAnalysisFailedDomainEvent(Guid EntryId, string Reason) : DomainEventBase;

// ============================ 分析回写用的 DTO(领域层不依赖 Application) ============================

/// <summary>AI 分析产出的单个问答草稿。</summary>
public sealed record QuestionDraft(
    int Sequence, string QuestionText, string? MyAnswerText, QuestionCategory Category,
    int Difficulty, string? Assessment, bool GotStuck, string? StuckReason,
    string? RecommendedAnswer, double? AtkSeconds,
    string? WeaknessTags = null, string? FollowUpQuestionsJson = null, string? MissedPointsJson = null);

/// <summary>AI 分析产出的单条短板草稿。</summary>
public sealed record WeaknessDraft(
    WeaknessCategory Category, string Title, string? Detail, string? Evidence,
    int Severity, string? Suggestion);
