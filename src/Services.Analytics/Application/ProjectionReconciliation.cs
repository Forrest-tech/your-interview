using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;

namespace YourInterview.Services.Analytics.Application;

// =====================================================================================
//  票34(REQ-18):投影对账与重建
//
//  Analytics 是事件驱动的复制式投影,真源在 Knowledge(条目/掌握度)与 Jobs(状态)。
//  事件覆盖不到的路径(条目新建/删除、复习内直接改级、历史事件丢失)必然产生分叉,
//  本文件提供两个可重演手段:
//   · ReconcileProjectionQuery —— 只读对账:经 Knowledge 既有 API 拉真源,与投影逐项
//     比对,输出差异清单(不写库)。
//   · RebuildProjectionCommand —— 以真源全量重建 Mastery 投影(分布 + 单条目台账 +
//     knowledge 来源 technicalDepth),重复执行结果不变(幂等)。
//
//  对账范围口径:
//   · MasteryBreakdown / AbilitySnapshot(knowledge 来源):真源 = Knowledge 当前条目
//     全集的 (Topic, Mastery) 分布,逐项可比。
//   · PipelineSnapshot:按 analytics-spec 落字口径,它是「状态流转事件逐日增量
//     (累计到达)」,与 Jobs 当前状态计数天然不同口径 —— 不在本对账的可比范围,
//     报告中单列说明,禁止把口径差误判为分叉。
// =====================================================================================

/// <summary>Knowledge 真源的一条条目(对账/重建的最小输入)。</summary>
public sealed record KnowledgeItemTruth(Guid Id, string Topic, string Mastery);

/// <summary>
/// Knowledge 真源读取口。返回 null = 真源不可达/不完整,调用方必须中止且不写库
/// (拿不到真源时「对齐」无从谈起,宁可报失败也不许用半截数据覆写投影)。
/// </summary>
public interface IKnowledgeTruthSource
{
    Task<IReadOnlyList<KnowledgeItemTruth>?> GetItemsAsync(CancellationToken ct);
}

/// <summary>按主题聚合后的分布计数(投影侧与真源侧共用同一形状,才能逐项比)。</summary>
public sealed record TopicMasteryCounts(string Topic, int Total, int Mastered, int Learning, int Fresh);

/// <summary>一项投影与真源的差异。Scope=mastery(按 Topic 的计数)/ ability(technicalDepth 分)。</summary>
public sealed record ProjectionDifferenceDto(string Scope, string Key, string Field,
    int Projected, int Truth);

public sealed record ReconcileReportDto(
    bool IsConsistent,
    int TruthItemCount,
    int ProjectedTopicCount,
    IReadOnlyList<ProjectionDifferenceDto> Differences,
    string PipelineNote);

public sealed record RebuildResultDto(
    int TopicsRebuilt,
    int ItemsCounted,
    int? TechnicalDepthScore,
    IReadOnlyList<ProjectionDifferenceDto> DifferencesBefore,
    bool ProjectionMatchesTruthAfter);

/// <summary>对账/重建共用的纯逻辑:真源聚合、投影聚合、逐项 diff(可独立单测)。</summary>
public static class MasteryReconciliation
{
    public const string PipelineNote =
        "PipelineSnapshot 为状态流转事件逐日增量(累计到达)口径,与 Jobs 当前状态计数天然不同口径,不在本对账可比范围(见 replica/analytics-spec.md)。";

    /// <summary>真源条目全集 → 按主题分布(桶口径走 MasteryLevelMapping,与消费者同一映射)。</summary>
    public static IReadOnlyList<TopicMasteryCounts> AggregateTruth(
        IEnumerable<KnowledgeItemTruth> items)
        => items.GroupBy(x => x.Topic)
            .Select(g =>
            {
                var buckets = g.Select(x => MasteryLevelMapping.ClassifyName(x.Mastery).Bucket).ToList();
                return new TopicMasteryCounts(g.Key, buckets.Count,
                    buckets.Count(b => b == MasteryBucket.Mastered),
                    buckets.Count(b => b == MasteryBucket.Learning),
                    buckets.Count(b => b == MasteryBucket.Fresh));
            })
            .OrderBy(x => x.Topic, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// 投影行 → 仪表盘可见口径的按主题分布:GetDashboardQueryHandler 对每个主题跨日期
    /// 取 Max —— 对账必须按读口径比,否则对完账页面上仍是旧数。
    /// </summary>
    public static IReadOnlyList<TopicMasteryCounts> AggregateProjected(
        IEnumerable<MasteryBreakdown> rows)
        => rows.GroupBy(x => x.Topic)
            .Select(g => new TopicMasteryCounts(g.Key,
                g.Max(x => x.Total), g.Max(x => x.Mastered),
                g.Max(x => x.Learning), g.Max(x => x.Fresh)))
            .OrderBy(x => x.Topic, StringComparer.Ordinal)
            .ToList();

    /// <summary>逐项 diff:任一侧独有主题按对方 0 计;四计数任一不等即一条差异。</summary>
    public static IReadOnlyList<ProjectionDifferenceDto> DiffMastery(
        IReadOnlyList<TopicMasteryCounts> projected, IReadOnlyList<TopicMasteryCounts> truth)
    {
        var diffs = new List<ProjectionDifferenceDto>();
        var p = projected.ToDictionary(x => x.Topic);
        var t = truth.ToDictionary(x => x.Topic);

        foreach (var topic in p.Keys.Union(t.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            var pv = p.GetValueOrDefault(topic);
            var tv = t.GetValueOrDefault(topic);
            Add("Total", pv?.Total ?? 0, tv?.Total ?? 0);
            Add("Mastered", pv?.Mastered ?? 0, tv?.Mastered ?? 0);
            Add("Learning", pv?.Learning ?? 0, tv?.Learning ?? 0);
            Add("Fresh", pv?.Fresh ?? 0, tv?.Fresh ?? 0);
            continue;

            void Add(string field, int projectedValue, int truthValue)
            {
                if (projectedValue != truthValue)
                    diffs.Add(new ProjectionDifferenceDto("mastery", topic, field,
                        projectedValue, truthValue));
            }
        }

        return diffs;
    }
}

// =====================================================================================
//  对账(只读)
// =====================================================================================

public sealed record ReconcileProjectionQuery(Guid UserId) : IRequest<Result<ReconcileReportDto>>;

public sealed class ReconcileProjectionQueryHandler(
    AnalyticsDbContext db, IKnowledgeTruthSource truthSource)
    : IRequestHandler<ReconcileProjectionQuery, Result<ReconcileReportDto>>
{
    public async Task<Result<ReconcileReportDto>> Handle(ReconcileProjectionQuery r,
        CancellationToken ct)
    {
        var items = await truthSource.GetItemsAsync(ct);
        if (items is null)
            return Result.Failure<ReconcileReportDto>(Error.Unexpected(
                "Knowledge 真源不可达或返回不完整,对账中止(投影未做任何改动)"));

        var (report, _) = await BuildReportAsync(r.UserId, items, ct);
        return Result.Success(report);
    }

    /// <summary>对账核心(与重建共用):投影聚合 vs 真源聚合 + technicalDepth 分比对。</summary>
    internal async Task<(ReconcileReportDto Report, IReadOnlyList<TopicMasteryCounts> Truth)>
        BuildReportAsync(Guid userId, IReadOnlyList<KnowledgeItemTruth> items, CancellationToken ct)
    {
        var truth = MasteryReconciliation.AggregateTruth(items);
        var rows = await db.MasteryBreakdowns.AsNoTracking()
            .Where(x => x.UserId == userId).ToListAsync(ct);
        var projected = MasteryReconciliation.AggregateProjected(rows);

        var diffs = MasteryReconciliation.DiffMastery(projected, truth).ToList();

        // technicalDepth(knowledge 来源):投影取最新一日的值,真源取全集映射分均值
        var truthScore = MasteryLevelMapping.AverageScore(items.Select(x => x.Mastery));
        var projectedScore = await db.AbilitySnapshots.AsNoTracking()
            .Where(x => x.UserId == userId && x.Dimension == "technicalDepth" && x.Source == "knowledge")
            .OrderByDescending(x => x.Date)
            .Select(x => (int?)x.Score)
            .FirstOrDefaultAsync(ct);
        if ((projectedScore ?? 0) != (truthScore ?? 0))
            diffs.Add(new ProjectionDifferenceDto("ability", "technicalDepth", "Score",
                projectedScore ?? 0, truthScore ?? 0));

        var report = new ReconcileReportDto(
            diffs.Count == 0, items.Count, projected.Count, diffs,
            MasteryReconciliation.PipelineNote);
        return (report, truth);
    }
}

// =====================================================================================
//  重建(以真源全量覆写 Mastery 投影,幂等)
// =====================================================================================

public sealed record RebuildProjectionCommand(Guid UserId) : IRequest<Result<RebuildResultDto>>;

public sealed class RebuildProjectionCommandHandler(
    AnalyticsDbContext db, IKnowledgeTruthSource truthSource)
    : IRequestHandler<RebuildProjectionCommand, Result<RebuildResultDto>>
{
    public async Task<Result<RebuildResultDto>> Handle(RebuildProjectionCommand r,
        CancellationToken ct)
    {
        var items = await truthSource.GetItemsAsync(ct);
        if (items is null)
            return Result.Failure<RebuildResultDto>(Error.Unexpected(
                "Knowledge 真源不可达或返回不完整,重建中止(投影未做任何改动)"));

        var reconcile = new ReconcileProjectionQueryHandler(db, truthSource);
        var (before, truth) = await reconcile.BuildReportAsync(r.UserId, items, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 分布行全量覆写:历史每日行是分叉期产物且真源无从还原逐日分布,保留只会让
        // 仪表盘的跨日 Max 聚合继续读到旧错数 —— 重建语义即「以当前真源重建投影」,
        // 与 ResetReadModel 的清零重建一脉。覆写后只有当日行,值 = 真源分布。
        var oldRows = await db.MasteryBreakdowns.Where(x => x.UserId == r.UserId).ToListAsync(ct);
        db.MasteryBreakdowns.RemoveRange(oldRows);
        foreach (var t in truth)
            db.MasteryBreakdowns.Add(new MasteryBreakdown(
                r.UserId, today, t.Topic, t.Total, t.Mastered, t.Learning, t.Fresh));

        // 单条目台账全量覆写:后续事件据此迁移,台账与分布必须同一口径同时换代
        var oldStates = await db.MasteryItemStates.Where(x => x.UserId == r.UserId).ToListAsync(ct);
        db.MasteryItemStates.RemoveRange(oldStates);
        foreach (var item in items)
            db.MasteryItemStates.Add(new MasteryItemState(r.UserId, item.Id, item.Topic,
                MasteryLevelMapping.ClassifyName(item.Mastery).Bucket.ToString()));

        // knowledge 来源 technicalDepth:整个序列以当日真源均值覆写(单行)。
        // 只覆写当日行不够:真源为空时昨日残留行会成为雷达的「最新值」继续冒充;
        // 分叉期的历史行与 MasteryBreakdown 历史行同理,属不可信产物,不保留。
        var truthScore = MasteryLevelMapping.AverageScore(items.Select(x => x.Mastery));
        var oldAbility = await db.AbilitySnapshots.Where(x =>
            x.UserId == r.UserId && x.Dimension == "technicalDepth" && x.Source == "knowledge")
            .ToListAsync(ct);
        db.AbilitySnapshots.RemoveRange(oldAbility);
        if (truthScore is not null)
            db.AbilitySnapshots.Add(new AbilitySnapshot(
                r.UserId, today, "technicalDepth", truthScore.Value, "knowledge"));

        await db.SaveChangesAsync(ct);

        var (after, _) = await reconcile.BuildReportAsync(r.UserId, items, ct);
        return Result.Success(new RebuildResultDto(
            truth.Count, items.Count, truthScore, before.Differences, after.IsConsistent));
    }
}
