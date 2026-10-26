using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Analytics.Application;

/// <summary>
/// 知识条目删除 → 把该条目移出掌握度分布(票91;发布侧 KnowledgeItemDeleted)。
///
/// 迁移口径与票34 台账严格一致(票34 既有消费者逻辑不动,本消费者是其镜像):
///   - 以 MasteryItemState 台账为权威:条目在哪个主题/哪个桶,就从哪边 Total-1 并移出该桶,
///     然后删台账行 —— 事件自带的 Topic/Mastery 只作日志核对,不覆盖台账
///     (条目可能在删除前改过主题/等级,以台账最后所见为准才不会移错桶)。
///   - 当日行缺失时先从该主题最近一行结转(carry-forward),与 KnowledgeLevelChangedConsumer
///     同语义:当日行 = 截至当日已知全集的当前分布。
///   - 台账里没有这条(state 为 null):条目从未被计入分布(事件化之前建的老条目、
///     或删除事件重复投递的第二份)—— 只记日志,不动分布。宁可留给 reconcile/rebuild
///     对平,也不能凭事件盲减把计数打错/打负;所有计数下限钳 0,与票34 口径一致。
/// 能力雷达(technicalDepth)不在这里动:增量消费者口径下它取最近事件分,
/// 删除单条无法增量重算均值,全量口径由 rebuild 负责(同票34 分工)。
/// </summary>
public sealed class KnowledgeItemDeletedConsumer(AnalyticsDbContext db,
    ILogger<KnowledgeItemDeletedConsumer> logger)
    : AnalyticsConsumerBase<KnowledgeItemDeleted>(db, logger)
{
    protected override async Task Handle(KnowledgeItemDeleted m, CancellationToken ct)
    {
        var state = await Db.MasteryItemStates.FirstOrDefaultAsync(x =>
            x.UserId == m.UserId && x.KnowledgeItemId == m.KnowledgeItemId, ct);

        if (state is null)
        {
            Logger.LogInformation(
                "条目删除但台账无此条目,分布不动 Item={Item} Topic={Topic} Mastery={Mastery}",
                m.KnowledgeItemId, m.Topic, m.Mastery);
            return;
        }

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var row = await GetOrCarryForwardRowAsync(m.UserId, state.Topic, today, ct);

        var mastered = row.Mastered;
        var learning = row.Learning;
        var fresh = row.Fresh;
        switch (ParseBucket(state.Bucket))
        {
            case MasteryBucket.Mastered: mastered--; break;
            case MasteryBucket.Learning: learning--; break;
            default: fresh--; break;
        }

        row.Update(Math.Max(0, row.Total - 1),
            Math.Max(0, mastered), Math.Max(0, learning), Math.Max(0, fresh));
        Db.MasteryItemStates.Remove(state);

        Logger.LogInformation("条目删除已移出台账 Item={Item} Topic={Topic} Bucket={Bucket}",
            m.KnowledgeItemId, state.Topic, state.Bucket);
    }

    /// <summary>取当日行;没有则以该主题最近一行的计数结转建行(同 KnowledgeLevelChangedConsumer 语义)。</summary>
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

    private static MasteryBucket ParseBucket(string name)
        => Enum.TryParse<MasteryBucket>(name, out var b) ? b : MasteryBucket.Fresh;
}
