using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Knowledge.Domain;

/// <summary>
/// 技术栈 —— 技术知识点聚合根。
///
/// 业务定位(为什么这样建模):
/// 这是 Forrest 的「个人长期技术知识库」的服务化形态。它的价值不在于"我把答案存在哪",
/// 而在于三件事同时被记录:
///   1. 知识点本身(Question + ConceptExplanation + BetterAnswer + 常见坑 + 追问);
///   2. 我对它的掌握程度(Mastery 状态机 + ReviewCount);
///   3. 什么时候该再复习一次(NextReviewAt,由 SM-2 简化算法算出)。
/// 前两点是"资产",第三点是"让它不被遗忘"的机制 —— 缺了第三点,知识库就退化成一份死的文档。
///
/// 来源(Source)是另一个关键维度:
///   - Personal       :我自己主动录入的技术问题
///   - FromInterview  :实战机经里出现过、而我当时没答好的问题(会由 Interviews 服务分析结果自动汇入)
///   - AiGenerated    :AI 实战模拟中生成/暴露出来的问题
///   - Imported       :批量粘贴/文件导入
/// 为什么区分来源:因为"我没答好的题"复习优先级天然应该高于"随手收藏的概念",
/// 后端可以在复习计划里据此加权(见 GetDueKnowledgeQuery)。
/// </summary>
public sealed class KnowledgeItem : AuditableAggregateRoot
{
    private readonly List<KnowledgeReviewLog> _reviewLogs = new();
    private readonly List<KnowledgeRelation> _relations = new();

    private KnowledgeItem() { }

    public KnowledgeItem(
        string title,
        string topic,
        string question,
        KnowledgeSource source = KnowledgeSource.Personal,
        string? subTopic = null,
        int difficulty = 3,
        int importance = 3)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("知识点标题不能为空", nameof(title));
        if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException("知识点分类不能为空", nameof(topic));

        Title = title.Trim();
        Topic = topic.Trim();
        SubTopic = string.IsNullOrWhiteSpace(subTopic) ? null : subTopic.Trim();
        Question = question;
        Source = source;
        Difficulty = Clamp(difficulty);
        Importance = Clamp(importance);

        // 新建即入学:Mastery=New,首次复习安排在明天 —— 让"今天录进去的题"自动进入复习队列,
        // 而不是躺在库里等着被想起来(人一定会忘,系统不能忘)。
        Mastery = MasteryLevel.New;
        NextReviewAt = DateTimeOffset.UtcNow.AddDays(1);
    }

    // ---------- 基本信息 ----------

    public string Title { get; private set; } = string.Empty;
    /// <summary>分类,如 C# / Angular / SQL / 微服务 / 系统设计 / 行为题。前端左侧目录树按它分组。</summary>
    public string Topic { get; private set; } = string.Empty;
    public string? SubTopic { get; private set; }
    /// <summary>面试原题原文(英文就保留英文,中文就保留中文)。</summary>
    public string Question { get; private set; } = string.Empty;
    public KnowledgeSource Source { get; private set; }

    /// <summary>来源实战机经条目(Interviews 服务的 InterviewEntryId),可空。</summary>
    public Guid? SourceInterviewEntryId { get; private set; }
    public string? SourceCompanyName { get; private set; }
    public DateOnly? SourceDate { get; private set; }

    public int Difficulty { get; private set; }
    public int Importance { get; private set; }
    /// <summary>标签,JSON 数组字符串(如 ["async","deadlock"])。用 JSON 而不是关联表:标签是查询用的小集合,不值得一次 join。</summary>
    public string? TagsJson { get; private set; }

    // ---------- 掌握度 + 间隔重复 ----------

    public MasteryLevel Mastery { get; private set; }
    public int ReviewCount { get; private set; }
    public DateTimeOffset? LastReviewedAt { get; private set; }
    /// <summary>下次该复习的时间。SM-2 简化版算出来,GET /api/knowledge?dueOnly=true 按它筛。</summary>
    public DateTimeOffset? NextReviewAt { get; private set; }
    /// <summary>SM-2 的易度因子 EF(Easiness Factor)。初始 2.5,答得好往上走、答得差往下掉,决定间隔增长速度。</summary>
    public double EasinessFactor { get; private set; } = 2.5;
    /// <summary>SM-2 的连续答对次数 n。n=0 表示刚答错/初学,间隔回到 1 天重新爬。</summary>
    public int RepetitionStreak { get; private set; }

    // ---------- 内容 ----------

    /// <summary>概念讲解(长文本,中文为主,术语保留英文原文)。</summary>
    public string? ConceptExplanation { get; private set; }
    /// <summary>我当时的回答原文 —— 保留原始版本,才能对比出进步。</summary>
    public string? MyAnswer { get; private set; }
    /// <summary>更好的答案 / 北美推荐答案(英文可背版)。</summary>
    public string? BetterAnswer { get; private set; }
    /// <summary>要点(JSON 字符串数组)。</summary>
    public string? KeyPointsJson { get; private set; }
    /// <summary>常见坑与混淆(JSON 字符串数组)。</summary>
    public string? CommonMistakesJson { get; private set; }
    /// <summary>可能的追问(JSON 字符串数组)。面试真正拉开差距的是"能不能接住第二问"。</summary>
    public string? FollowUpsJson { get; private set; }
    /// <summary>出处(JSON 数组对象:[{"url":"...","title":"MS Learn ..."}])。可溯源才叫知识,不可溯源只是记忆。</summary>
    public string? ReferencesJson { get; private set; }

    public IReadOnlyCollection<KnowledgeReviewLog> ReviewLogs => _reviewLogs.AsReadOnly();
    public IReadOnlyCollection<KnowledgeRelation> Relations => _relations.AsReadOnly();

    // ---------- 领域行为 ----------

    public void UpdateDetails(string title, string topic, string? subTopic, string question,
        int difficulty, int importance, string? tagsJson, string? conceptExplanation,
        string? myAnswer, string? betterAnswer, string? keyPointsJson, string? commonMistakesJson,
        string? followUpsJson)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("知识点标题不能为空", nameof(title));
        Title = title.Trim();
        Topic = topic.Trim();
        SubTopic = string.IsNullOrWhiteSpace(subTopic) ? null : subTopic.Trim();
        Question = question;
        Difficulty = Clamp(difficulty);
        Importance = Clamp(importance);
        TagsJson = tagsJson;
        ConceptExplanation = conceptExplanation;
        MyAnswer = myAnswer;
        BetterAnswer = betterAnswer;
        KeyPointsJson = keyPointsJson;
        CommonMistakesJson = commonMistakesJson;
        FollowUpsJson = followUpsJson;
        Touch();
    }

    /// <summary>
    /// 调整掌握度。掌握度不是"随便改个状态",而是有方向的:
    /// 提升到 Mastered 会 RaiseDomainEvent → 由 Handler 转成 KnowledgeMasteryChanged 集成事件,
    /// 让 Analytics 服务刷新能力雷达图。
    /// 「晋升记录本身也是数据」:前端据此画"我什么时候征服了这道题"的时间线。
    /// </summary>
    public void PromoteMastery(MasteryLevel level)
    {
        var previous = Mastery;
        if (previous == level) return;
        Mastery = level;

        // 掌握度提升意味着"记得牢了" → 让间隔重复跟着放宽;下调则收紧,尽快重刷。
        if (level is MasteryLevel.Familiar or MasteryLevel.Mastered)
        {
            EasinessFactor = Math.Min(3.0, Math.Round(EasinessFactor + 0.1, 2));
        }
        else if (level is MasteryLevel.NeedsReview or MasteryLevel.New)
        {
            EasinessFactor = Math.Max(1.3, Math.Round(EasinessFactor - 0.2, 2));
            RepetitionStreak = 0;
        }

        RaiseDomainEvent(new KnowledgeMasteryChangedDomainEvent(Id, (int)previous, (int)level));
        Touch();
    }

    /// <summary>
    /// 记录一次复习 —— 本聚合最核心的方法:写入复习流水 + 用 SM-2 简化版重算下次复习时间。
    ///
    /// 算法(简化版 SM-2,SuperMemo 2):
    ///   q 由复习结果映射:Again=1 / Hard=3 / Good=4 / Easy=5
    ///   EF' = EF + (0.1 - (5-q)*(0.08 + (5-q)*0.02))   下限 1.3
    ///   q < 3  → n = 0,间隔 = 1 天(答错,重新开始爬)
    ///   q >= 3 → n += 1
    ///            n = 1 → 1 天
    ///            n = 2 → 6 天
    ///            n > 2 → round(上次间隔 * EF')
    /// 为什么用 SM-2 而不是"固定 3 天后复习":遗忘曲线是个指数衰减,固定间隔要么太密(浪费时间)
    /// 要么太疏(该忘的忘了)。SM-2 让"记得牢的题"间隔指数式拉长、"记不住的题"立刻回到 1 天。
    ///
    /// 注意 NextReviewAt 用「上次复习时间 + 间隔」而不是「现在 + 间隔」——
    /// 这样补录/迟到的复习不会把整条曲线往后顺延(你已经欠了 5 天,应该今天补,而不是从今天重新算 1 天)。
    /// </summary>
    public KnowledgeReviewLog RecordReview(ReviewResult result, int? confidenceBefore = null,
        int? confidenceAfter = null, string? note = null, int? durationSeconds = null,
        DateTimeOffset? reviewedAt = null)
    {
        var at = reviewedAt ?? DateTimeOffset.UtcNow;
        var log = new KnowledgeReviewLog(Id, result, confidenceBefore, confidenceAfter, note, durationSeconds, at);
        _reviewLogs.Add(log);

        ReviewCount++;
        LastReviewedAt = at;

        var q = result switch
        {
            ReviewResult.Again => 1,
            ReviewResult.Hard => 3,
            ReviewResult.Good => 4,
            ReviewResult.Easy => 5,
            _ => 3
        };

        // EF 下限 1.3:再差也不能低于它,否则间隔会退化到"天天复习同一题",反而打击坚持度。
        EasinessFactor = Math.Max(1.3, Math.Round(
            EasinessFactor + (0.1 - (5 - q) * (0.08 + (5 - q) * 0.02)), 2));

        var previousIntervalDays = LastIntervalDays ?? 0;

        if (q < 3)
        {
            RepetitionStreak = 0;
            NextReviewAt = at.AddDays(1);
            LastIntervalDays = 1;
            // 答错 → 掌握度不该还挂在 Mastered 上;降到 NeedsReview,让它在列表里被标红催复习。
            if (Mastery == MasteryLevel.Mastered) Mastery = MasteryLevel.NeedsReview;
        }
        else
        {
            RepetitionStreak++;
            var intervalDays = RepetitionStreak switch
            {
                1 => 1,
                2 => 6,
                _ => (int)Math.Round(Math.Max(previousIntervalDays, 1) * EasinessFactor)
            };
            intervalDays = Math.Min(intervalDays, 365); // 封顶一年,避免"答得太好"直接排到三年后
            NextReviewAt = at.AddDays(intervalDays);
            LastIntervalDays = intervalDays;

            // 连续答对 3 次以上且从没晋升过 → 自动升到 Familiar(减少手工维护负担)
            if (RepetitionStreak >= 3 && Mastery == MasteryLevel.Learning)
                Mastery = MasteryLevel.Familiar;
        }

        Touch();
        return log;
    }

    /// <summary>当前所处间隔(天),SM-2 的"上一次间隔"输入。不落库到公开字段之外,仅供算法自身使用。</summary>
    public int? LastIntervalDays { get; private set; }

    /// <summary>加知识关联(知识图谱)。重复关联同一类型会被忽略,避免前端重复点击造出脏边。</summary>
    public KnowledgeRelation AttachRelation(Guid relatedItemId, KnowledgeRelationType relationType, string? note = null)
    {
        if (relatedItemId == Id) throw new InvalidOperationException("知识点不能关联自己");
        var existing = _relations.FirstOrDefault(r => r.RelatedItemId == relatedItemId && r.RelationType == relationType);
        if (existing is not null) return existing;

        var relation = new KnowledgeRelation(Id, relatedItemId, relationType, note);
        _relations.Add(relation);
        Touch();
        return relation;
    }

    /// <summary>关联到实战机经条目:把"这题是我在哪一场、哪家公司被问住的"钉死,复习时能回忆现场。</summary>
    public void LinkToInterview(Guid entryId, string? companyName = null, DateOnly? date = null)
    {
        SourceInterviewEntryId = entryId;
        SourceCompanyName = companyName;
        SourceDate = date;
        Source = KnowledgeSource.FromInterview;
        Touch();
    }

    /// <summary>追加一条出处引用(MS Learn / RFC / 官方文档)。去重:同 URL 只留一条。</summary>
    public void AddReference(string url, string? title = null, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("引用地址不能为空", nameof(url));

        var list = KnowledgeJson.ParseReferenceList(ReferencesJson).ToList();
        if (list.Any(r => string.Equals(r.Url, url.Trim(), StringComparison.OrdinalIgnoreCase))) return;

        list.Add(new KnowledgeReference(url.Trim(), title, kind));
        ReferencesJson = KnowledgeJson.Serialize(list);
        Touch();
    }

    private static int Clamp(int value) => Math.Clamp(value, 1, 5);
}

/// <summary>
/// 复习流水。独立实体而不是塞进聚合根的字符串:
/// 因为要按时间画进步曲线、要统计"这次比上次自信了多少",这些都需要结构化行。
/// </summary>
public sealed class KnowledgeReviewLog : Entity
{
    private KnowledgeReviewLog() { }

    internal KnowledgeReviewLog(Guid itemId, ReviewResult result, int? confidenceBefore,
        int? confidenceAfter, string? note, int? durationSeconds, DateTimeOffset reviewedAt)
    {
        ItemId = itemId;
        Result = result;
        ConfidenceBefore = confidenceBefore;
        ConfidenceAfter = confidenceAfter;
        Note = note;
        DurationSeconds = durationSeconds;
        ReviewedAt = reviewedAt;
    }

    public Guid ItemId { get; private set; }
    public DateTimeOffset ReviewedAt { get; private set; }
    public ReviewResult Result { get; private set; }
    /// <summary>复习前的自信度(1-5),和 After 对比能看出"我以为我会了,其实没有"。</summary>
    public int? ConfidenceBefore { get; private set; }
    public int? ConfidenceAfter { get; private set; }
    public string? Note { get; private set; }
    public int? DurationSeconds { get; private set; }
}

/// <summary>知识点之间的关系 —— 支撑知识图谱(前端可画"学 A 之前先学 B")。</summary>
public sealed class KnowledgeRelation : Entity
{
    private KnowledgeRelation() { }

    internal KnowledgeRelation(Guid itemId, Guid relatedItemId, KnowledgeRelationType relationType, string? note)
    {
        ItemId = itemId;
        RelatedItemId = relatedItemId;
        RelationType = relationType;
        Note = note;
    }

    public Guid ItemId { get; private set; }
    public Guid RelatedItemId { get; private set; }
    public KnowledgeRelationType RelationType { get; private set; }
    public string? Note { get; private set; }
}

public enum MasteryLevel
{
    /// <summary>未学 —— 知道有这题,但没真正学过。</summary>
    New = 0,
    /// <summary>在学 —— 看过了,讲不利索。</summary>
    Learning = 1,
    /// <summary>熟悉 —— 能讲清楚,但还需要偶尔复习。</summary>
    Familiar = 2,
    /// <summary>已掌握 —— 能主动讲、能接追问。</summary>
    Mastered = 3,
    /// <summary>需重刷 —— 复习时答错了,降级回来。</summary>
    NeedsReview = 4
}

public enum KnowledgeSource
{
    /// <summary>我自己提供的技术问题。</summary>
    Personal = 0,
    /// <summary>实战机经里出现过的题(尤其是我没答好的)。</summary>
    FromInterview = 1,
    /// <summary>AI 实战模拟中生成或暴露出来的题。</summary>
    AiGenerated = 2,
    /// <summary>批量导入。</summary>
    Imported = 3
}

public enum ReviewResult
{
    Again = 0,
    Hard = 1,
    Good = 2,
    Easy = 3
}

public enum KnowledgeRelationType
{
    /// <summary>前置知识:学 B 之前得先会 A。</summary>
    Prerequisite = 0,
    /// <summary>对比关系:常被放在一起问、容易混(如四个 map 操作符)。</summary>
    Compare = 1,
    /// <summary>延伸:同一主线上的深入(如 EF Core → N+1 诊断)。</summary>
    Extends = 2
}

/// <summary>
/// 领域事件:掌握度变化。
/// 领域事件 = 服务内部同步语义;集成事件(KnowledgeMasteryChanged)才是跨服务契约。
/// 这里刻意只带"变了什么",不带业务含义,翻译成本交给 Publisher。
/// </summary>
public sealed record KnowledgeMasteryChangedDomainEvent(
    Guid KnowledgeItemId,
    int PreviousLevel,
    int NewLevel) : DomainEventBase;
