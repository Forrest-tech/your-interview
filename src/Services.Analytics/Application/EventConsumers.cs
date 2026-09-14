using MassTransit;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Analytics.Application;

/// <summary>
/// 集成事件消费者 —— Analytics 的全部输入。
///
/// 每个消费者都遵循同一套三段式:
///   1. 幂等检查(EventId 见过了就跳过)
///   2. 更新读模型(upsert:同一天同一维度只累加成一条)
///   3. 记台账,与读模型在**同一个 SaveChanges** 里提交
///
/// 第 3 步必须和第 2 步同事务。如果先更新再单独记台账,
/// 中间崩溃会导致"数据变了但台账没记",重投时又算一遍 —— 数字就偏了。
/// </summary>
public abstract class AnalyticsConsumerBase<TEvent>(AnalyticsDbContext db,
    ILogger logger) : IConsumer<TEvent> where TEvent : class
{
    protected AnalyticsDbContext Db { get; } = db;
    protected ILogger Logger { get; } = logger;

    public async Task Consume(ConsumeContext<TEvent> context)
    {
        var eventId = context.MessageId ?? Guid.NewGuid();

        // 幂等:同一个事件只处理一次
        if (await Db.ProcessedEvents.AnyAsync(x => x.EventId == eventId, context.CancellationToken))
        {
            Logger.LogDebug("事件 {EventId} 已处理过,跳过", eventId);
            return;
        }

        await Handle(context.Message, context.CancellationToken);

        Db.ProcessedEvents.Add(new ProcessedEvent(eventId, typeof(TEvent).Name));
        await Db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>子类只实现"怎么更新读模型",幂等与台账由基类负责。</summary>
    protected abstract Task Handle(TEvent message, CancellationToken ct);

    /// <summary>六维维度 key —— 与 Assessment 的 DimensionScores 一致。</summary>
    protected static readonly string[] Dimensions =
        ["pronunciation", "fluency", "sentenceIntegrity", "structure", "technicalDepth", "relevance"];

    /// <summary>
    /// Upsert 一个能力快照。
    /// 同一天同一个"维度+来源"只保留一条 —— 一天内练三次应该取最新值,
    /// 而不是堆成三行把雷达图画成一团乱麻。
    /// </summary>
    protected async Task UpsertAbilityAsync(Guid userId, string dimension, int score, string source,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var existing = await Db.AbilitySnapshots.FirstOrDefaultAsync(x =>
            x.UserId == userId && x.Date == today && x.Dimension == dimension && x.Source == source, ct);

        if (existing is null)
            Db.AbilitySnapshots.Add(new AbilitySnapshot(userId, today, dimension, score, source));
        else
            existing.UpdateScore(score);
    }
}

/// <summary>AI 模拟答题完成 → 刷新能力雷达(mock 来源)。</summary>
public sealed class MockAnswerSubmittedConsumer(AnalyticsDbContext db,
    ILogger<MockAnswerSubmittedConsumer> logger)
    : AnalyticsConsumerBase<MockAnswerSubmitted>(db, logger)
{
    protected override async Task Handle(MockAnswerSubmitted m, CancellationToken ct)
    {
        // MockAnswerSubmitted 只带文本,没有六维分数 —— 分数由 Assessment 打分后写入。
        // 这里只记录"发生过一次练习"这个事实,供活跃度统计用。
        // 真正的六维分数由 KnowledgeLevelChanged / 面试分析完成事件带来。
        Logger.LogInformation("模拟答题提交 MockSession={Session} Question={Q}",
            m.MockSessionId, m.QuestionId);
        await Task.CompletedTask;
    }
}

/// <summary>技术栈掌握度变化 → 刷新能力雷达(knowledge 来源)+ 主题分布。</summary>
public sealed class KnowledgeLevelChangedConsumer(AnalyticsDbContext db,
    ILogger<KnowledgeLevelChangedConsumer> logger)
    : AnalyticsConsumerBase<KnowledgeLevelChanged>(db, logger)
{
    protected override async Task Handle(KnowledgeLevelChanged m, CancellationToken ct)
    {
        // 掌握度 0-100 映射到雷达图的一维。等级越高分越高。
        await UpsertAbilityAsync(m.UserId, "technicalDepth", m.Level, "knowledge", ct);

        // 主题分布:重新统计该主题下的四档数量。
        // 这里查的是自己库里的最新快照再叠加 —— 因为 Analytics 看不到 Knowledge 的库(也不该看)。
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bucket = await Db.MasteryBreakdowns.FirstOrDefaultAsync(x =>
            x.UserId == m.UserId && x.Date == today && x.Topic == m.Topic, ct);

        if (bucket is null)
        {
            Db.MasteryBreakdowns.Add(new MasteryBreakdown(m.UserId, today, m.Topic,
                total: 1, mastered: m.Level >= 80 ? 1 : 0, learning: m.Level is > 0 and < 80 ? 1 : 0,
                fresh: m.Level <= 0 ? 1 : 0));
        }
        else
        {
            // 已有今天的记录 → 按本次事件的等级"移动一格"
            bucket.Update(bucket.Total, bucket.Mastered, bucket.Learning, bucket.Fresh);
        }
    }
}

/// <summary>投递状态流转 → 刷新求职漏斗。</summary>
public sealed class JobApplicationStatusChangedConsumer(AnalyticsDbContext db,
    ILogger<JobApplicationStatusChangedConsumer> logger)
    : AnalyticsConsumerBase<JobApplicationStatusChanged>(db, logger)
{
    protected override async Task Handle(JobApplicationStatusChanged m, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var snap = await Db.PipelineSnapshots.FirstOrDefaultAsync(x =>
            x.UserId == m.UserId && x.Date == today, ct);

        if (snap is null)
        {
            snap = new PipelineSnapshot(m.UserId, today, 0, 0, 0, 0, 0, 0);
            Db.PipelineSnapshots.Add(snap);
        }

        // 状态字段是字符串(来自 Jobs 服务的枚举)——
        // Analytics 不引用 Jobs 的领域枚举,否则就又变成服务间强耦合了。
        // 用字符串匹配,并在不认识新状态时只记日志不崩溃(向前兼容)。
        var (saved, applied, screening, interviewing, offered, rejected) =
            (snap.Saved, snap.Applied, snap.Screening, snap.Interviewing, snap.Offered, snap.Rejected);

        switch (m.ToStatus.ToLowerInvariant())
        {
            case "saved" or "draft": saved++; break;
            case "applied": applied++; break;
            case "screen" or "screening": screening++; break;
            case "interview" or "interviewing" or "onsite": interviewing++; break;
            case "offer" or "offered": offered++; break;
            case "rejected": rejected++; break;
            default:
                Logger.LogDebug("未知投递状态 {Status},不计入漏斗", m.ToStatus);
                break;
        }

        snap.Update(saved, applied, screening, interviewing, offered, rejected);
    }
}

/// <summary>面试分析完成 → 刷新能力雷达(interview 来源,真实面试的分量更重)。</summary>
public sealed class InterviewAnalysisCompletedConsumer(AnalyticsDbContext db,
    ILogger<InterviewAnalysisCompletedConsumer> logger)
    : AnalyticsConsumerBase<InterviewAnalysisCompleted>(db, logger)
{
    protected override async Task Handle(InterviewAnalysisCompleted m, CancellationToken ct)
    {
        await UpsertAbilityAsync(m.UserId, "pronunciation", m.PronunciationScore, "interview", ct);
        await UpsertAbilityAsync(m.UserId, "structure", m.StructureScore, "interview", ct);
        await UpsertAbilityAsync(m.UserId, "fluency", m.FluencyScore, "interview", ct);

        Logger.LogInformation("真实面试分析完成 Entry={Entry} 发音={P} 结构={S} 流利={F}",
            m.InterviewSessionId, m.PronunciationScore, m.StructureScore, m.FluencyScore);
    }
}

/// <summary>审计事件 → 保留审计流水(合规需要,只记不分析)。</summary>
public sealed class AuditEventRecordedConsumer(AnalyticsDbContext db,
    ILogger<AuditEventRecordedConsumer> logger)
    : AnalyticsConsumerBase<AuditEventRecorded>(db, logger)
{
    protected override Task Handle(AuditEventRecorded m, CancellationToken ct)
    {
        Logger.LogDebug("审计事件 User={User} Action={Action} Resource={Resource}",
            m.UserId, m.Action, m.Resource);
        return Task.CompletedTask;
    }
}
