using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Analytics.Domain;

/// <summary>
/// Analytics 服务的读模型(CQRS 的 Q 侧)。
///
/// 设计原则:这里**不存业务事实**,只存"为了快速出图而预先算好的汇总"。
/// 业务事实的权威来源永远在各业务服务的库里。Analytics 挂了不该影响任何业务写入 ——
/// 这是把 CQRS 读模型独立成服务的全部意义。
///
/// 所以这里每张表都能从集成事件重建:删掉重放一遍事件,应得到相同结果。
/// </summary>

/// <summary>能力雷达图的一个时间点 —— 某用户某一天在某一维上的分数。</summary>
public sealed class AbilitySnapshot
{
    private AbilitySnapshot() { }

    public AbilitySnapshot(Guid userId, DateOnly date, string dimension, int score, string source)
    {
        UserId = userId;
        Date = date;
        Dimension = dimension;
        Score = Math.Clamp(score, 0, 100);
        Source = source;
        RecordedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    /// <summary>维度 key:pronunciation / fluency / sentenceIntegrity / structure / technicalDepth / relevance。</summary>
    public string Dimension { get; private set; } = string.Empty;
    public int Score { get; private set; }
    /// <summary>数据来源:mock(AI 模拟)/ interview(真实面试)/ knowledge(技术栈)。雷达图要能按来源筛选。</summary>
    public string Source { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }

    public void UpdateScore(int score)
    {
        Score = Math.Clamp(score, 0, 100);
        RecordedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// 求职漏斗的每日快照 —— "投了多少 / 进面多少 / 到 offer 多少"。
/// 为什么要按天快照而不是每次查询时实时算:漏斗是趋势图,
/// 必须能回答"上个月转化率是多少",而实时算只能给出"现在是多少"。
/// </summary>
public sealed class PipelineSnapshot
{
    private PipelineSnapshot() { }

    public PipelineSnapshot(Guid userId, DateOnly date, int saved, int applied, int screening,
        int interviewing, int offered, int rejected)
    {
        UserId = userId;
        Date = date;
        Saved = saved;
        Applied = applied;
        Screening = screening;
        Interviewing = interviewing;
        Offered = offered;
        Rejected = rejected;
        RecordedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    public int Saved { get; private set; }
    public int Applied { get; private set; }
    public int Screening { get; private set; }
    public int Interviewing { get; private set; }
    public int Offered { get; private set; }
    public int Rejected { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>投递 → 面试 的转化率。分母为 0 时返回 null(不是 0 —— 0 会被误读成"转化率是 0")。</summary>
    public double? AppliedToInterviewRate =>
        Applied <= 0 ? null : Math.Round(Interviewing / (double)Applied * 100, 1);

    public void Update(int saved, int applied, int screening, int interviewing, int offered,
        int rejected)
    {
        Saved = saved;
        Applied = applied;
        Screening = screening;
        Interviewing = interviewing;
        Offered = offered;
        Rejected = rejected;
        RecordedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>技术栈掌握度的分布快照(供环形图 / 堆叠柱状图)。</summary>
public sealed class MasteryBreakdown
{
    private MasteryBreakdown() { }

    public MasteryBreakdown(Guid userId, DateOnly date, string topic, int total, int mastered,
        int learning, int fresh)
    {
        UserId = userId;
        Date = date;
        Topic = topic;
        Total = total;
        Mastered = mastered;
        Learning = learning;
        Fresh = fresh;
        RecordedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid UserId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Topic { get; private set; } = string.Empty;
    public int Total { get; private set; }
    public int Mastered { get; private set; }
    public int Learning { get; private set; }
    /// <summary>还没开始学的(New)。</summary>
    public int Fresh { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>掌握率。分母 0 时返回 null。</summary>
    public double? MasteryRate =>
        Total <= 0 ? null : Math.Round((Mastered + Learning) / (double)Total * 100, 1);

    public void Update(int total, int mastered, int learning, int fresh)
    {
        Total = total;
        Mastered = mastered;
        Learning = learning;
        Fresh = fresh;
        RecordedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// 事件处理台账 —— 幂等消费的基石。
///
/// 消息投递是 at-least-once,同一个事件可能被投递多次。
/// 处理前先看 EventId 有没有记过,是唯一可靠的做法
/// (试图靠"业务上重复执行也无害"来绕开,迟早会在计数类操作上翻车)。
/// </summary>
public sealed class ProcessedEvent
{
    private ProcessedEvent() { }

    public ProcessedEvent(Guid eventId, string eventType)
    {
        EventId = eventId;
        EventType = eventType;
        ProcessedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }
}
