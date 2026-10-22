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
        // 票34/REQ-18:等级语义按 MasteryLevelMapping 解释(事件 Level 是枚举 int、
        // Mastery 是枚举名),不再按 0-100 分直消费 —— 旧口径下 Mastered 恒计 0、
        // 雷达分恒为枚举 int 原值(0-4)。
        var (bucket, score) = MasteryLevelMapping.Classify(m.Mastery, m.Level);
        await UpsertAbilityAsync(m.UserId, "technicalDepth", score, "knowledge", ct);

        // 主题分布:按单条目台账(MasteryItemState)做增量迁移。
        // 旧实现当日已有行时以原值回写(CONF-AN-3),计数永不迁移 —— 已废除。
        // 语义:MasteryBreakdown 当日行 = 截至当日消费者已知的全部条目的当前分布,
        // 故当日行缺失时先从该主题最近一行结转(carry-forward),再叠加本次迁移。
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var state = await Db.MasteryItemStates.FirstOrDefaultAsync(x =>
            x.UserId == m.UserId && x.KnowledgeItemId == m.KnowledgeItemId, ct);

        if (state is null)
        {
            // 首次见到这条目:新主题 Total +1 且落入本次桶
            var row = await GetOrCarryForwardRowAsync(m.UserId, m.Topic, today, ct);
            ApplyDelta(row, from: null, to: bucket, totalDelta: +1);
            Db.MasteryItemStates.Add(new MasteryItemState(
                m.UserId, m.KnowledgeItemId, m.Topic, bucket.ToString()));
        }
        else if (state.Topic == m.Topic)
        {
            // 同主题等级变化:只迁移桶,Total 不变
            if (state.Bucket != bucket.ToString())
            {
                var row = await GetOrCarryForwardRowAsync(m.UserId, m.Topic, today, ct);
                ApplyDelta(row, ParseBucket(state.Bucket), bucket, totalDelta: 0);
                state.Update(m.Topic, bucket.ToString());
            }
        }
        else
        {
            // 条目改了主题:旧主题 Total -1 并移出旧桶,新主题 Total +1 并落入新桶
            var oldRow = await GetOrCarryForwardRowAsync(m.UserId, state.Topic, today, ct);
            ApplyDelta(oldRow, ParseBucket(state.Bucket), to: null, totalDelta: -1);
            var newRow = await GetOrCarryForwardRowAsync(m.UserId, m.Topic, today, ct);
            ApplyDelta(newRow, from: null, bucket, totalDelta: +1);
            state.Update(m.Topic, bucket.ToString());
        }
    }

    /// <summary>取当日行;没有则以该主题最近一行的计数结转建行,再没有则零行。</summary>
    private async Task<MasteryBreakdown> GetOrCarryForwardRowAsync(
        Guid userId, string topic, DateOnly today, CancellationToken ct)
    {
        var row = await Db.MasteryBreakdowns.FirstOrDefaultAsync(x =>
            x.UserId == userId && x.Date == today && x.Topic == topic, ct);
        if (row is not null) return row;

        var latest = await Db.MasteryBreakdowns
            .Where(x => x.UserId == userId && x.Topic == topic)
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync(ct);

        row = new MasteryBreakdown(userId, today, topic,
            latest?.Total ?? 0, latest?.Mastered ?? 0, latest?.Learning ?? 0, latest?.Fresh ?? 0);
        Db.MasteryBreakdowns.Add(row);
        return row;
    }

    private static void ApplyDelta(MasteryBreakdown row, MasteryBucket? from, MasteryBucket? to,
        int totalDelta)
    {
        var mastered = row.Mastered;
        var learning = row.Learning;
        var fresh = row.Fresh;

        if (from is not null) Adjust(from.Value, -1);
        if (to is not null) Adjust(to.Value, +1);

        // 计数下限 0:台账与快照若曾被旧版本污染,迁移不得把桶打成负数
        row.Update(Math.Max(0, row.Total + totalDelta),
            Math.Max(0, mastered), Math.Max(0, learning), Math.Max(0, fresh));
        return;

        void Adjust(MasteryBucket b, int d)
        {
            switch (b)
            {
                case MasteryBucket.Mastered: mastered += d; break;
                case MasteryBucket.Learning: learning += d; break;
                default: fresh += d; break;
            }
        }
    }

    private static MasteryBucket ParseBucket(string name)
        => Enum.TryParse<MasteryBucket>(name, out var b) ? b : MasteryBucket.Fresh;
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
        // 文本模式下发音为 null:跳过,不拿"没测到"污染能力画像
        if (m.PronunciationScore.HasValue)
            await UpsertAbilityAsync(m.UserId, "pronunciation", m.PronunciationScore.Value, "interview", ct);
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
