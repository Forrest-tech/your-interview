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
    Notes,
    /// <summary>粘贴的纯文本(A6):不需要 STT,直接进分析。</summary>
    Text,
    /// <summary>备考方案(2026-10-19):AI 生成的备考文档,内容存 TranscriptText(Markdown)。</summary>
    PrepDoc,
    /// <summary>邮件(2026-10-19):预约邮件/反馈邮件的全文粘贴。</summary>
    Email,
    /// <summary>面试反馈(2026-10-19):电话/邮件/口头反馈的记录。</summary>
    Feedback,
    /// <summary>其它材料(2026-10-19):兜底分类。</summary>
    Other
}

/// <summary>
/// 材料所处阶段(2026-10-19,资产按 Round 管理):
/// Pre = 面试前(备考文档/预约邮件/recruiter 电话),Post = 面试后(录音/转录/反馈)。
/// </summary>
public enum AssetPhase
{
    Pre,
    Post
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
    private readonly List<InterviewRound> _rounds = [];
    private readonly List<GeneratedMaterial> _materials = [];
    private readonly List<InterviewAction> _actions = [];
    private readonly List<AnalysisSnapshot> _snapshots = [];

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

    /// <summary>复盘 (JSON):面试后总结,原存 localStorage,现迁后端。</summary>
    public string? DebriefJson { get; private set; }

    public void SetDebrief(string? json) { DebriefJson = json; Touch(); }

    /// <summary>
    /// 回填 Tracker 投递关联(2026-10-21):只在当前未关联时生效,已有值绝不覆盖。
    /// 返回是否真的写入(已有关联时 false)。
    /// </summary>
    public bool LinkJobApplication(Guid jobApplicationId)
    {
        if (JobApplicationId is not null || jobApplicationId == Guid.Empty) return false;
        JobApplicationId = jobApplicationId;
        Touch();
        return true;
    }

    /// <summary>公司情况:规模/业务/技术栈/面试风格/文化(长文本,自己整理)。</summary>
    public string? CompanyProfile { get; private set; }

    /// <summary>JD 全文。</summary>
    public string? JdText { get; private set; }

    /// <summary>JD 要点摘要(自己或 AI 提炼)。</summary>
    public string? JdSummary { get; private set; }

    /// <summary>"我要问面试官的问题"JSON 数组(F3):[{text, checked?}]。</summary>
    public string? QuestionsToAskJson { get; private set; }

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

    /// <summary>
    /// 客观声学指标 JSON(缺口4,2026-10-07)。Worker 回写时带上,
    /// 内容即 Analysis.Worker 的 SpeechMetrics 序列化。存原文,前端直接展示。
    /// </summary>
    public string? SpeechMetricsJson { get; private set; }

    /// <summary>
    /// 计分文本(2026-10-15):被 SpeakerLabelStage 标为 CANDIDATE 的分段拼接。
    /// 前端复盘面板展示,供用户肉眼验证说话人分离是否正确。
    /// </summary>
    public string? CandidateTranscript { get; private set; }

    public IReadOnlyCollection<InterviewAsset> Assets => _assets.AsReadOnly();
    public IReadOnlyCollection<InterviewQuestion> Questions => _questions.AsReadOnly();
    public IReadOnlyCollection<InterviewWeakness> Weaknesses => _weaknesses.AsReadOnly();
    public IReadOnlyCollection<InterviewRound> Rounds => _rounds.AsReadOnly();
    public IReadOnlyCollection<GeneratedMaterial> Materials => _materials.AsReadOnly();
    public IReadOnlyCollection<InterviewAction> Actions => _actions.AsReadOnly();
    public IReadOnlyCollection<AnalysisSnapshot> Snapshots => _snapshots.AsReadOnly();

    /// <summary>
    /// 合法流转表。集中定义,避免散落在各处 if-else。
    ///
    /// ★ 2026-09-24(M1):AssetsUploaded 允许直达 Transcribed ——
    ///   "先传录音、再补一段现成转写文本/纪要"是真实用法,
    ///   文本材料挂入即视为已转写(AttachAsset 里就是这么推进的)。
    /// </summary>
    private static readonly Dictionary<InterviewStatus, InterviewStatus[]> AllowedTransitions = new()
    {
        [InterviewStatus.Draft] = [InterviewStatus.AssetsUploaded, InterviewStatus.Transcribing],
        [InterviewStatus.AssetsUploaded] = [InterviewStatus.Transcribing, InterviewStatus.Transcribed, InterviewStatus.Draft],
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
        string? interviewFormat, string? interviewers, string? location, string? result, string? notes,
        string? questionsToAskJson = null)
    {
        // 面试结果首次填写或变更(M1.5):关联了 Tracker 申请才发回写事件。
        // 空值/没变化/没关联都不发 —— 别让每次编辑都触发下游同步。
        var outcomeRecorded = JobApplicationId is not null
            && !string.IsNullOrWhiteSpace(result)
            && !string.Equals(Result, result, StringComparison.Ordinal);

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
        // 可选字段:null = 本次没带,不覆盖已有值 —— 避免旧前端/旧调用把清单清空。
        if (questionsToAskJson is not null)
            QuestionsToAskJson = questionsToAskJson;
        Touch();

        if (outcomeRecorded)
            RaiseDomainEvent(new InterviewOutcomeRecordedDomainEvent(
                Id, JobApplicationId!.Value, RoundNo, result!));
    }

    /// <summary>挂上一份材料(录音或文本)。会自动把 Draft 推进到 AssetsUploaded。</summary>
    public InterviewAsset AttachAsset(AssetKind kind, string fileName, string? contentType,
        long sizeBytes, string? storagePath, string? blobUrl, string? transcriptText = null,
        string? transcriptSegmentsJson = null, double? durationSeconds = null,
        string sourceLanguage = "en", string? sha256 = null,
        Guid? roundId = null, AssetPhase? phase = null, string? metaJson = null)
    {
        if (Status == InterviewStatus.Analyzing || Status == InterviewStatus.Transcribing)
            throw new InvalidOperationException($"当前状态 {Status} 不允许变更材料,请等待流程结束");

        if (roundId is not null && !_rounds.Any(r => r.Id == roundId.Value))
            throw new InvalidOperationException("关联的轮次不存在");

        var asset = new InterviewAsset(Id, kind, fileName, contentType, sizeBytes, storagePath,
            blobUrl, durationSeconds, sourceLanguage, sha256, roundId, phase);
        if (!string.IsNullOrWhiteSpace(metaJson)) asset.SetMeta(metaJson);

        // 文本类材料直接就是"已转写"状态,不需要走 STT
        // 2026-10-19:PrepDoc/Email/Feedback/Other 是轮次上下文(备考/邮件/反馈),
        // 不是候选人语音 —— 只登记,不推进 Transcribed 状态,不进分析管线。
        if (kind is AssetKind.Transcript or AssetKind.Notes or AssetKind.Text)
        {
            if (string.IsNullOrWhiteSpace(transcriptText))
                throw new InvalidOperationException("文本类材料必须提供内容");
            asset.SetTranscript(transcriptText, transcriptSegmentsJson);
            if (Status == InterviewStatus.Draft) TransitionTo(InterviewStatus.AssetsUploaded);
            TransitionTo(InterviewStatus.Transcribed);
            TranscribedAt = DateTimeOffset.UtcNow;
        }
        else if (kind is AssetKind.PrepDoc or AssetKind.Email or AssetKind.Feedback or AssetKind.Other)
        {
            if (string.IsNullOrWhiteSpace(transcriptText))
                throw new InvalidOperationException("文本类材料必须提供内容");
            asset.SetTranscript(transcriptText, transcriptSegmentsJson);
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

    /// <summary>删除一件材料(2026-10-20 v2 材料页签补齐 —— 此前全站没有删材料入口)。</summary>
    public void RemoveAsset(Guid assetId)
    {
        var asset = _assets.FirstOrDefault(a => a.Id == assetId)
            ?? throw new InvalidOperationException("材料不存在");
        _assets.Remove(asset);
        Touch();
    }

    /// <summary>开始转写(用户点「开始转写」,或 Worker 状态推进时调用)。</summary>
    public void StartTranscription()
    {
        // 幂等:流程中/已完成的不重复触发 ——
        // 尤其 Transcribing:Worker 回写时也会调它,不挡住会重复发事件 → 重复跑管线。
        // 注意 Transcribed **不在**守卫里:从"已转写"重新点开始 = 用户要求重跑
        // (比如补传了更好的录音),这是状态机里 Transcribed → Transcribing 的合法流转。
        if (Status is InterviewStatus.Transcribing or InterviewStatus.Analyzing
            or InterviewStatus.Analyzed) return;
        if (!_assets.Any())
            throw new InvalidOperationException("还没有上传任何材料,无法开始转写");

        TransitionTo(InterviewStatus.Transcribing);
        Touch();

        // 只在"真正开始"时发事件(带着录音的真实路径)→ 触发 Analysis.Worker。
        // 之前这一步只改状态不发事件,点完按钮就石沉大海 —— M1 修复的核心断点。
        var audio = _assets.FirstOrDefault(a =>
            a.Kind == AssetKind.Audio && !string.IsNullOrWhiteSpace(a.StoragePath));
        RaiseDomainEvent(new InterviewTranscriptionStartedDomainEvent(
            Id, audio?.Id ?? Guid.Empty, audio?.StoragePath));
    }

    /// <summary>转写完成,回写全文 + 分段(分段 JSON 含说话人标签与时间戳)。</summary>
    public void CompleteTranscription(Guid assetId, string fullText, string? segmentsJson)
    {
        var asset = _assets.FirstOrDefault(a => a.Id == assetId)
            ?? throw new InvalidOperationException("材料不存在");

        asset.SetTranscript(fullText, segmentsJson);
        TranscribedAt = DateTimeOffset.UtcNow;

        // 只有状态**真正变为** Transcribed 才发事件。
        // 之前无条件发 → Worker 每回写一次转写稿就再触发一轮分析,
        // 分析又回写转写稿……事件风暴(重复烧 Azure 转写额度)。
        var changed = Status != InterviewStatus.Transcribed;
        TransitionTo(InterviewStatus.Transcribed);
        Touch();
        if (changed)
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
    public void ApplyAnalysis(int overall, int? pronunciation, int fluency, int structure,
        int technicalDepth, int relevance, string? summary,
        IEnumerable<QuestionDraft>? questions = null,
        IEnumerable<WeaknessDraft>? weaknesses = null,
        string? speechMetricsJson = null,
        string? candidateTranscript = null)
    {
        OverallScore = overall;
        PronunciationScore = pronunciation;
        FluencyScore = fluency;
        StructureScore = structure;
        TechnicalDepthScore = technicalDepth;
        RelevanceScore = relevance;
        AnalysisSummary = summary;
        // 指标 JSON 可空:老 Worker/手动回写可能不带
        if (!string.IsNullOrWhiteSpace(speechMetricsJson))
            SpeechMetricsJson = speechMetricsJson;
        // 计分文本可空:老 Worker 不带时不覆盖已有值
        if (!string.IsNullOrWhiteSpace(candidateTranscript))
            CandidateTranscript = candidateTranscript;

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
        QuestionCategory category = QuestionCategory.Technical, int difficulty = 3,
        Guid? roundId = null, string? askedBy = null)
    {
        if (roundId is not null && !_rounds.Any(r => r.Id == roundId.Value))
            throw new InvalidOperationException("关联的轮次不存在");
        var seq = _questions.Count == 0 ? 1 : _questions.Max(q => q.Sequence) + 1;
        var q = new InterviewQuestion(Id, seq, questionText, category, difficulty, roundId, askedBy);
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

    /// <summary>设置题目掌握度(A1):0=待练,1=练习中,2=已掌握,3=还卡壳。</summary>
    public void SetQuestionMastery(Guid questionId, int mastery)
    {
        var q = _questions.FirstOrDefault(x => x.Id == questionId)
            ?? throw new InvalidOperationException("问题不存在");
        q.SetMastery(mastery);
        Touch();
    }

    // ================= 详情页 v2(2026-10-20 定稿):归属 / 复盘 / 备考报告 =================

    /// <summary>改材料归属:roundId=null 表示移到条目级(沟通记录)。目标轮次必须存在。</summary>
    public void ReassignAsset(Guid assetId, Guid? roundId)
    {
        var a = _assets.FirstOrDefault(x => x.Id == assetId)
            ?? throw new InvalidOperationException("材料不存在");
        if (roundId is not null && !_rounds.Any(r => r.Id == roundId.Value))
            throw new InvalidOperationException("目标轮次不存在");
        a.Reassign(roundId);
        Touch();
    }

    /// <summary>更新材料元数据 JSON(解析字段/截图引用/AI 摘要/提取的安排等)。</summary>
    public void UpdateAssetMeta(Guid assetId, string? metaJson)
    {
        var a = _assets.FirstOrDefault(x => x.Id == assetId)
            ?? throw new InvalidOperationException("材料不存在");
        a.SetMeta(metaJson);
        Touch();
    }

    /// <summary>写入逐题复盘 JSON(四维分析 + 标准答案 AI 版/用户版)。</summary>
    public void SetQuestionReview(Guid questionId, string? reviewJson)
    {
        var q = _questions.FirstOrDefault(x => x.Id == questionId)
            ?? throw new InvalidOperationException("问题不存在");
        q.SetReview(reviewJson);
        Touch();
    }

    /// <summary>写入整轮复盘汇总 JSON(综合分/通过概率/最强/短板)。</summary>
    public void SetRoundReview(Guid roundId, string? reviewJson)
    {
        var r = _rounds.FirstOrDefault(x => x.Id == roundId)
            ?? throw new InvalidOperationException("轮次不存在");
        r.SetReview(reviewJson);
        Touch();
    }

    /// <summary>写入结构化备考报告 JSON(按面试流程排布,题目可挂 Tech Stack 条目 id)。</summary>
    public void SetRoundPrepReport(Guid roundId, string? prepReportJson)
    {
        var r = _rounds.FirstOrDefault(x => x.Id == roundId)
            ?? throw new InvalidOperationException("轮次不存在");
        r.SetPrepReport(prepReportJson);
        Touch();
    }

    /// <summary>
    /// 说话人批量改名:把本轮转写分段里的 speaker 标签 from → to 全篇替换,
    /// 同时把本轮题目里 AskedBy == from 的一并改掉(真名逐人标注)。
    /// </summary>
    public int RenameSpeakerInRound(Guid roundId, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException("改名参数不能为空");
        var changed = 0;
        foreach (var a in _assets.Where(a => a.RoundId == roundId
                     && !string.IsNullOrWhiteSpace(a.TranscriptSegmentsJson)))
        {
            var before = a.TranscriptSegmentsJson!;
            var after = System.Text.RegularExpressions.Regex.Replace(before,
                "\"speaker\"\\s*:\\s*\"" + System.Text.RegularExpressions.Regex.Escape(from) + "\"",
                "\"speaker\": \"" + to.Replace("\"", "") + "\"");
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                a.SetTranscript(a.TranscriptText ?? "", after);
                changed++;
            }
        }
        foreach (var q in _questions.Where(q => q.RoundId == roundId && q.AskedBy == from))
        {
            q.SetAskedBy(to);
            changed++;
        }
        if (changed > 0) Touch();
        return changed;
    }

    /// <summary>新增一轮。Order 自动取 max+1,调用方可显式指定 Stage。</summary>
    public InterviewRound AddRound(string stage = "Technical")
    {
        var order = _rounds.Count == 0 ? 1 : _rounds.Max(r => r.Order) + 1;
        var r = new InterviewRound(Id, order, string.IsNullOrWhiteSpace(stage) ? "Technical" : stage.Trim());
        _rounds.Add(r);
        Touch();
        return r;
    }

    public void UpdateRound(Guid roundId, string stage, DateOnly? scheduledDate, string? interviewers,
        string? format, string? location, InterviewRoundOutcome outcome, string? notes, string? feedback)
    {
        var r = _rounds.FirstOrDefault(x => x.Id == roundId)
            ?? throw new InvalidOperationException("轮次不存在");
        r.Update(stage, scheduledDate, interviewers, format, location, outcome, notes, feedback);
        Touch();
    }

    public void RemoveRound(Guid roundId)
    {
        var r = _rounds.FirstOrDefault(x => x.Id == roundId)
            ?? throw new InvalidOperationException("轮次不存在");
        _rounds.Remove(r);
        Touch();
    }

    /// <summary>写入轮次的邮件时间线(A7):整体替换 EmailsJson。</summary>
    public void SetRoundEmails(Guid roundId, string? emailsJson)
    {
        var r = _rounds.FirstOrDefault(x => x.Id == roundId)
            ?? throw new InvalidOperationException("轮次不存在");
        r.UpdateEmails(emailsJson);
        Touch();
    }

    // ============================ 待办事项(Next actions,A2) ============================

    /// <summary>新增待办。可由 AI 分析自动生成,也可手工添加。</summary>
    public InterviewAction AddAction(string title, string? detail, DateTimeOffset? dueDate)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("待办标题不能为空");
        var a = new InterviewAction(Id, title.Trim(), detail, dueDate);
        _actions.Add(a);
        Touch();
        return a;
    }

    /// <summary>
    /// 部分更新待办:null 的字段保持不动(清 dueDate 暂不支持 ——
    /// 传 null 即"不动",这是 PATCH 的保守语义)。
    /// </summary>
    public void UpdateAction(Guid actionId, string? title, bool? isDone, DateTimeOffset? dueDate)
    {
        var a = _actions.FirstOrDefault(x => x.Id == actionId)
            ?? throw new InvalidOperationException("待办不存在");
        a.Update(title, dueDate);
        if (isDone.HasValue) a.SetDone(isDone.Value);
        Touch();
    }

    public void RemoveAction(Guid actionId)
    {
        var a = _actions.FirstOrDefault(x => x.Id == actionId)
            ?? throw new InvalidOperationException("待办不存在");
        _actions.Remove(a);
        Touch();
    }

    // ============================ 分析快照(A3) ============================

    /// <summary>记一条分析快照:每次分析成功回写时调用,趋势曲线与"较上次"对比的数据源。</summary>
    public AnalysisSnapshot AddAnalysisSnapshot(int? overallScore, string? dimensionsJson,
        string? summaryBulletsJson)
    {
        var s = new AnalysisSnapshot(Id, overallScore, dimensionsJson, summaryBulletsJson);
        _snapshots.Add(s);
        Touch();
        return s;
    }

    /// <summary>新增一份指导材料,版本号自动取 max+1。</summary>
    public GeneratedMaterial AddMaterial(string contentMarkdown, string model,
        int? promptTokens = null, int? completionTokens = null)
    {
        var version = _materials.Count == 0 ? 1 : _materials.Max(m => m.Version) + 1;
        var m = new GeneratedMaterial(Id, version, contentMarkdown, model);
        m.SetUsage(promptTokens, completionTokens);
        _materials.Add(m);
        Touch();
        return m;
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

/// <summary>文件完整性巡检结论(M1.3)。null = 从未巡检过(新上传的文件视为 Ok)。</summary>
public enum AssetIntegrity
{
    /// <summary>文件在,大小与 SHA-256 都与上传时一致。</summary>
    Ok,
    /// <summary>文件不在了(被误删 / 卷被卸载 / 误清理)。</summary>
    Missing,
    /// <summary>文件在但字节数对不上 —— 大概率被截断或换成了别的文件。</summary>
    SizeMismatch,
    /// <summary>大小一致但摘要变了 —— 内容被改写(同名不同文件)。</summary>
    HashMismatch
}

/// <summary>面试材料(录音 / 转写文本 / 手写纪要)。</summary>
public sealed class InterviewAsset : Entity
{
    private InterviewAsset() { }

    internal InterviewAsset(Guid interviewEntryId, AssetKind kind, string fileName,
        string? contentType, long sizeBytes, string? storagePath, string? blobUrl,
        double? durationSeconds, string sourceLanguage, string? sha256 = null,
        Guid? roundId = null, AssetPhase? phase = null)
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
        Sha256 = sha256;
        RoundId = roundId;
        Phase = phase;
        // 刚落盘的文件必然完整(写入即校验);巡检会持续维护这个结论
        FileExists = storagePath is not null;
        IntegrityStatus = storagePath is not null ? AssetIntegrity.Ok : null;
        LastVerifiedAt = storagePath is not null ? DateTimeOffset.UtcNow : null;
        UploadedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }
    public AssetKind Kind { get; private set; }

    /// <summary>关联轮次(2026-10-19,资产按 Round 管理)。null = 历史遗留未归类材料。</summary>
    public Guid? RoundId { get; private set; }

    /// <summary>Pre = 面试前,Post = 面试后。null = 历史遗留未归类材料。</summary>
    public AssetPhase? Phase { get; private set; }

    public string FileName { get; private set; } = string.Empty;
    public string? ContentType { get; private set; }
    public long SizeBytes { get; private set; }

    /// <summary>本地/容器内路径(分析 Worker 读它去跑 STT)。</summary>
    public string? StoragePath { get; private set; }

    /// <summary>生产环境用 Azure Blob 的 URL。</summary>
    public string? BlobUrl { get; private set; }

    /// <summary>内容摘要(上传时算好)。完整性巡检重算比对,不一致 = 文件被换过/损坏。</summary>
    public string? Sha256 { get; private set; }

    /// <summary>最近一次巡检确认文件还在。false 时前端应提示"录音丢失"。</summary>
    public bool FileExists { get; private set; }

    /// <summary>最近一次完整性巡检时间(上传即首次校验)。</summary>
    public DateTimeOffset? LastVerifiedAt { get; private set; }

    /// <summary>最近一次巡检的完整性结论。null = 从未校验(仅无文件的资产)。</summary>
    public AssetIntegrity? IntegrityStatus { get; private set; }

    public double? DurationSeconds { get; private set; }
    public string SourceLanguage { get; private set; } = "en";
    public DateTimeOffset UploadedAt { get; private set; }

    /// <summary>转写全文。</summary>
    public string? TranscriptText { get; private set; }

    /// <summary>转写分段 JSON: [{idx,speaker,start,end,text,pitchHz}] —— 说话人分离结果。</summary>
    public string? TranscriptSegmentsJson { get; private set; }

    /// <summary>
    /// 材料元数据 JSON(2026-10-20 详情页 v2):统一入口解析出的字段
    /// (邮件分类/发件人/主题/时间、截图附件 assetId、AI 摘要、提取到的安排、
    /// 沟通处理状态、来源场景 如电话/邮件)。结构由前端约定,后端只透传存储。
    /// </summary>
    public string? MetaJson { get; private set; }

    internal void Reassign(Guid? roundId) => RoundId = roundId;

    internal void SetMeta(string? metaJson) => MetaJson = metaJson;

    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    internal void SetTranscript(string fullText, string? segmentsJson)
    {
        TranscriptText = fullText;
        TranscriptSegmentsJson = segmentsJson;
    }

    /// <summary>
    /// 完整性巡检回写(M1.3):结论与时间戳一并落。
    /// Missing 之外 FileExists 保持 true —— 文件在,只是内容不对,
    /// 前端对 Missing 显示「文件丢失」、对两种 Mismatch 显示「文件损坏」。
    /// </summary>
    internal void RecordIntegrity(AssetIntegrity integrity)
    {
        IntegrityStatus = integrity;
        FileExists = integrity != AssetIntegrity.Missing;
        LastVerifiedAt = DateTimeOffset.UtcNow;
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
        QuestionCategory category, int difficulty, Guid? roundId = null, string? askedBy = null)
    {
        InterviewEntryId = interviewEntryId;
        Sequence = sequence;
        QuestionText = questionText;
        Category = category;
        Difficulty = difficulty;
        RoundId = roundId;
        AskedBy = askedBy;
    }

    public Guid InterviewEntryId { get; private set; }

    /// <summary>所属轮次(2026-10-20 详情页 v2:问答按轮组织)。null = 历史数据未归轮。</summary>
    public Guid? RoundId { get; private set; }

    /// <summary>提问人真名(2026-10-20:逐人标注;分不清时先用"面试官 1/2",可批量改名)。</summary>
    public string? AskedBy { get; private set; }

    /// <summary>
    /// 逐题复盘 JSON(2026-10-20):{overall, verdict, dims:{relevance/depth/expression/
    /// pronunciation:{score,comment,fix}}, stdSpoken:{ai,user}, stdTechnical:{ai,user}}。
    /// 标准答案 AI 原版与用户编辑版分开存,用户版优先、可还原。
    /// </summary>
    public string? ReviewJson { get; private set; }

    internal void SetReview(string? reviewJson) => ReviewJson = reviewJson;

    internal void SetAskedBy(string? askedBy) => AskedBy = askedBy;

    /// <summary>在本场面里的提问顺序。</summary>
    public int Sequence { get; private set; }

    /// <summary>面试官原话(英文原文,逐字不改)。</summary>
    public string QuestionText { get; private set; } = string.Empty;

    /// <summary>我的回答原话(英文原文,逐字不改)。</summary>
    public string? MyAnswerText { get; private set; }

    public QuestionCategory Category { get; private set; }
    public int Difficulty { get; private set; }

    /// <summary>掌握度(A1):0=待练,1=练习中,2=已掌握,3=还卡壳。默认 0。</summary>
    public int Mastery { get; private set; }

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

    /// <summary>设置掌握度(A1)。越界直接抛 —— 调用方(Handler)应先做参数校验转 400。</summary>
    internal void SetMastery(int mastery)
    {
        if (mastery is < 0 or > 3)
            throw new InvalidOperationException("掌握度只能是 0-3(0=待练,1=练习中,2=已掌握,3=还卡壳)");
        Mastery = mastery;
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

/// <summary>
/// 面试轮次。一家公司可多轮,每轮独立记录时间/地点/面试官/形式/结果。
/// 缺口1(2026-10-07):替代原来 InterviewEntry 上的扁平 RoundNo/InterviewDate/Interviewers
/// 等字段 —— 旧字段保留做兼容,新数据走这里。
/// </summary>
public sealed class InterviewRound : Entity
{
    private InterviewRound() { }

    internal InterviewRound(Guid interviewEntryId, int order, string stage)
    {
        InterviewEntryId = interviewEntryId;
        Order = order;
        Stage = stage;
        Outcome = InterviewRoundOutcome.Pending;
    }

    public Guid InterviewEntryId { get; private set; }

    /// <summary>第几轮,从 1 开始。</summary>
    public int Order { get; private set; }

    /// <summary>轮次阶段:Screen / Technical / SystemDesign / Behavioral / Final。</summary>
    public string Stage { get; private set; } = string.Empty;

    public DateOnly? ScheduledDate { get; private set; }

    /// <summary>面试官,如 "David Castelino (Sr SWE), Vinitha Kotha (Lead SWE)"。</summary>
    public string? Interviewers { get; private set; }

    /// <summary>形式:Phone | Video | Onsite。</summary>
    public string? Format { get; private set; }

    public string? Location { get; private set; }

    public InterviewRoundOutcome Outcome { get; private set; }

    public string? Notes { get; private set; }

    public string? Feedback { get; private set; }

    /// <summary>会议链接(Teams/Zoom 等)。</summary>
    public string? MeetingLink { get; private set; }

    /// <summary>面试时间(HH:mm),与 ScheduledDate 配合。</summary>
    public string? ScheduledTime { get; private set; }

    /// <summary>备考问题清单 JSON:[{text, checked}]。</summary>
    public string? PrepQuestionsJson { get; private set; }

    /// <summary>相关邮件 JSON:[{subject, from, date, snippet}]。</summary>
    public string? EmailsJson { get; private set; }

    /// <summary>面试录音文稿。</summary>
    public string? Transcript { get; private set; }

    /// <summary>录音文件 URL。</summary>
    public string? RecordingUrl { get; private set; }

    /// <summary>准备清单 JSON:[{text, done}]。</summary>
    public string? ChecklistJson { get; private set; }

    /// <summary>
    /// 整轮复盘汇总 JSON(2026-10-20 详情页 v2):
    /// {overall, passProbability, passBasis, strongest, weakest, summary}。
    /// passProbability 明确是 AI 估计,依据写在 passBasis。
    /// </summary>
    public string? ReviewJson { get; private set; }

    /// <summary>
    /// 结构化备考报告 JSON(2026-10-20 详情页 v2):
    /// {generatedAt, generatedBy, sources, sections:[{id,title,blocks}], questions:[...]}。
    /// 题目与 Tech Stack 的关联以 knowledgeId 挂接(同一条记录,不复制答案)。
    /// </summary>
    public string? PrepReportJson { get; private set; }

    internal void SetReview(string? reviewJson) => ReviewJson = reviewJson;

    internal void SetPrepReport(string? prepReportJson) => PrepReportJson = prepReportJson;

    internal void Update(string stage, DateOnly? scheduledDate, string? interviewers,
        string? format, string? location, InterviewRoundOutcome outcome,
        string? notes, string? feedback)
    {
        if (!string.IsNullOrWhiteSpace(stage)) Stage = stage;
        ScheduledDate = scheduledDate;
        Interviewers = interviewers;
        Format = format;
        Location = location;
        Outcome = outcome;
        Notes = notes;
        Feedback = feedback;
    }

    /// <summary>
    /// 更新轮次扩展信息(会议链接/时间/备考/邮件/文稿/录音/清单)—— 部分更新协议(COD-04):
    /// 每个字段 null = 调用方未提供,保持原值;空字符串 = 明确清空(存 null);非空 = 赋值。
    /// 此前无条件整字段赋值,前端只传基础字段保存一次即静默抹除转写/备考题/邮件/录音。
    /// </summary>
    internal void UpdateDetails(string? meetingLink, string? scheduledTime,
        string? prepQuestionsJson, string? emailsJson, string? transcript, string? recordingUrl,
        string? checklistJson = null)
    {
        MeetingLink = ApplyPartial(meetingLink, MeetingLink);
        ScheduledTime = ApplyPartial(scheduledTime, ScheduledTime);
        PrepQuestionsJson = ApplyPartial(prepQuestionsJson, PrepQuestionsJson);
        EmailsJson = ApplyPartial(emailsJson, EmailsJson);
        Transcript = ApplyPartial(transcript, Transcript);
        RecordingUrl = ApplyPartial(recordingUrl, RecordingUrl);
        ChecklistJson = ApplyPartial(checklistJson, ChecklistJson);
    }

    /// <summary>部分更新单字段:未提供(null)保持、空串清空、非空赋值。</summary>
    private static string? ApplyPartial(string? provided, string? current)
        => provided is null ? current : provided.Length == 0 ? null : provided;

    /// <summary>只记结果,不动其它字段 —— 结果回写不该清掉已填的时间/面试官/形式。</summary>
    internal void RecordOutcome(InterviewRoundOutcome outcome) => Outcome = outcome;

    /// <summary>整体替换邮件时间线(A7)。传 null/空数组即清空。</summary>
    internal void UpdateEmails(string? emailsJson) => EmailsJson = emailsJson;
}

/// <summary>轮次结果。存库用字符串,见 DbContext 配置。</summary>
public enum InterviewRoundOutcome
{
    Pending = 0,
    Passed = 1,
    Rejected = 2,
    Ghosted = 3,
    Cancelled = 4,
    NoShow = 5
}

/// <summary>
/// 面试指导材料。手动点击生成,每次生成版本号 +1,历史版本保留可回看。
/// 缺口2(2026-10-07):内容由 AI 根据 JD/公司介绍/问答/短板/六维诊断拼 prompt 生成。
/// </summary>
public sealed class GeneratedMaterial : Entity
{
    private GeneratedMaterial() { }

    internal GeneratedMaterial(Guid interviewEntryId, int version, string contentMarkdown,
        string model)
    {
        InterviewEntryId = interviewEntryId;
        Version = version;
        ContentMarkdown = contentMarkdown;
        Model = model;
        GeneratedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }

    /// <summary>版本号,从 1 开始递增。</summary>
    public int Version { get; private set; }

    /// <summary>指导材料正文(Markdown)。</summary>
    public string ContentMarkdown { get; private set; } = string.Empty;

    /// <summary>生成时用的模型名(网关回传)。</summary>
    public string Model { get; private set; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; private set; }

    public int? PromptTokens { get; private set; }
    public int? CompletionTokens { get; private set; }

    internal void SetUsage(int? promptTokens, int? completionTokens)
    {
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
    }
}

/// <summary>
/// 待办事项 / Next actions(A2)。
/// AI 分析可自动生成 3~5 条,用户也可手工增删;F2 报告底部与 F5 总览共用同一份数据。
/// </summary>
public sealed class InterviewAction : Entity
{
    private InterviewAction() { }

    internal InterviewAction(Guid interviewEntryId, string title, string? detail,
        DateTimeOffset? dueDate)
    {
        InterviewEntryId = interviewEntryId;
        Title = title;
        Detail = detail;
        DueDate = dueDate;
        IsDone = false;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }

    /// <summary>待办标题。</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>补充说明。</summary>
    public string? Detail { get; private set; }

    /// <summary>截止时间(可空)。</summary>
    public DateTimeOffset? DueDate { get; private set; }

    public bool IsDone { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>部分更新:null 的字段保持不动(聚合根 UpdateAction 保证只传要改的)。</summary>
    internal void Update(string? title, DateTimeOffset? dueDate)
    {
        if (!string.IsNullOrWhiteSpace(title)) Title = title.Trim();
        DueDate = dueDate;
    }

    internal void SetDone(bool isDone) => IsDone = isDone;
}

/// <summary>
/// 分析快照(A3):每次分析成功回写记一条,形成"一条面试多次分析"的时间序列。
/// 趋势曲线(较上次 ↑/↓n 分)与 F2 页顶结论区的数据源。
/// </summary>
public sealed class AnalysisSnapshot : Entity
{
    private AnalysisSnapshot() { }

    internal AnalysisSnapshot(Guid interviewEntryId, int? overallScore,
        string? dimensionsJson, string? summaryBulletsJson)
    {
        InterviewEntryId = interviewEntryId;
        OverallScore = overallScore;
        DimensionsJson = dimensionsJson;
        SummaryBulletsJson = summaryBulletsJson;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid InterviewEntryId { get; private set; }

    /// <summary>
    /// 总分,可空:文本模式(粘贴文本、无音频)下发音未测量,极端情况下可能无总分。
    /// 当前 Worker 总是算出总分(非空维度的均值),这里留可空是给未来/手工回写留余地。
    /// </summary>
    public int? OverallScore { get; private set; }

    /// <summary>
    /// 六维分数 JSON:{overall,pronunciation,fluency,structure,technicalDepth,relevance}。
    /// 各维度可空(如文本模式的 pronunciation 为 null),前端按"未测量"展示。
    /// </summary>
    public string? DimensionsJson { get; private set; }

    /// <summary>3~5 条一句话结论的 JSON 数组(由回写时的 Summary 句子切分得到)。</summary>
    public string? SummaryBulletsJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

// ============================ 领域事件 ============================

public sealed record InterviewEntryCreatedDomainEvent(Guid EntryId, Guid CompanyId,
    string CompanyName, string Role) : DomainEventBase;

/// <summary>
/// 转写**真正开始**(用户点了「开始转写」,状态从 AssetsUploaded/Failed 进入 Transcribing)。
/// 带着录音的真实存储路径 → Analysis.Worker 拿到就能直接跑,不用再猜文件在哪。
/// </summary>
public sealed record InterviewTranscriptionStartedDomainEvent(
    Guid EntryId, Guid AssetId, string? StoragePath) : DomainEventBase;

public sealed record InterviewTranscribedDomainEvent(Guid EntryId, Guid AssetId, int TextLength)
    : DomainEventBase;

public sealed record InterviewAnalysisAppliedDomainEvent(Guid EntryId, Guid CompanyId, int OverallScore)
    : DomainEventBase;

public sealed record InterviewAnalysisFailedDomainEvent(Guid EntryId, string Reason) : DomainEventBase;

/// <summary>
/// 面试结果登记(M1.5):用户在条目上填了"结果"(通过/被拒/待定)。
/// 只有关联了 Tracker 申请(JobApplicationId)的条目才会发 ——
/// Jobs 消费后回写对应轮次的 Outcome,被拒时申请自动转 Rejected。
/// </summary>
public sealed record InterviewOutcomeRecordedDomainEvent(
    Guid EntryId, Guid ApplicationId, int RoundNo, string Result) : DomainEventBase;

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
