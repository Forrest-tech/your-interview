using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YourInterview.Services.Analytics.Application;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票34(REQ-18):投影对账(Reconcile,只读)与重建(Rebuild,幂等)回归。
/// 真源替身 FakeTruth 直接喂条目全集 —— 对账/重建逻辑只依赖 IKnowledgeTruthSource
/// 契约,HTTP 实现的归属转发在活体段验证。
/// </summary>
public sealed class Ticket34RebuildReconcileTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    private static readonly Guid ItemCSharpMastered = Guid.NewGuid();
    private static readonly Guid ItemCSharpLearning = Guid.NewGuid();
    private static readonly Guid ItemCSharpNew = Guid.NewGuid();
    private static readonly Guid ItemSqlFamiliar = Guid.NewGuid();

    private static IReadOnlyList<KnowledgeItemTruth> Truth() =>
    [
        new(ItemCSharpMastered, "C#", "Mastered"),
        new(ItemCSharpLearning, "C#", "Learning"),
        new(ItemCSharpNew, "C#", "New"),
        new(ItemSqlFamiliar, "SQL", "Familiar"),
    ];

    private sealed class FakeTruth(IReadOnlyList<KnowledgeItemTruth>? items)
        : IKnowledgeTruthSource
    {
        public Task<IReadOnlyList<KnowledgeItemTruth>?> GetItemsAsync(CancellationToken ct)
            => Task.FromResult(items);
    }

    private static AnalyticsDbContext NewDb() => new(
        new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>种入分叉投影:旧错数(C# Total 99)、幽灵主题、枚举 int 直写的雷达分 3。</summary>
    private static async Task SeedDriftAsync(AnalyticsDbContext db)
    {
        db.MasteryBreakdowns.Add(new MasteryBreakdown(User, Yesterday, "C#",
            total: 99, mastered: 99, learning: 0, fresh: 0));
        db.MasteryBreakdowns.Add(new MasteryBreakdown(User, Today, "GhostTopic",
            total: 3, mastered: 0, learning: 3, fresh: 0));
        db.AbilitySnapshots.Add(new AbilitySnapshot(User, Yesterday,
            "technicalDepth", 3, "knowledge"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Reconcile_DriftedProjection_ReportsDifferences_AndWritesNothing()
    {
        using var db = NewDb();
        await SeedDriftAsync(db);
        var handler = new ReconcileProjectionQueryHandler(db, new FakeTruth(Truth()));

        var r = await handler.Handle(new ReconcileProjectionQuery(User), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.IsConsistent.Should().BeFalse();
        r.Value.TruthItemCount.Should().Be(4);
        r.Value.Differences.Should().Contain(d =>
            d.Scope == "mastery" && d.Key == "C#" && d.Field == "Total"
            && d.Projected == 99 && d.Truth == 3);
        r.Value.Differences.Should().Contain(d =>
            d.Scope == "mastery" && d.Key == "GhostTopic" && d.Truth == 0);
        r.Value.Differences.Should().Contain(d =>
            d.Scope == "ability" && d.Key == "technicalDepth" && d.Projected == 3 && d.Truth == 46);
        r.Value.PipelineNote.Should().Contain("逐日增量");
        // 只读:行数与值不变
        (await db.MasteryBreakdowns.CountAsync()).Should().Be(2);
        (await db.MasteryItemStates.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Rebuild_AlignsProjectionToTruth_AndReportsBeforeDifferences()
    {
        using var db = NewDb();
        await SeedDriftAsync(db);
        var handler = new RebuildProjectionCommandHandler(db, new FakeTruth(Truth()));

        var r = await handler.Handle(new RebuildProjectionCommand(User), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.ItemsCounted.Should().Be(4);
        r.Value.TopicsRebuilt.Should().Be(2);
        r.Value.TechnicalDepthScore.Should().Be(46); // (80+40+0+65)/4 = 46.25 → 46
        r.Value.DifferencesBefore.Should().NotBeEmpty();
        r.Value.ProjectionMatchesTruthAfter.Should().BeTrue();

        var projected = MasteryReconciliation.AggregateProjected(
            await db.MasteryBreakdowns.AsNoTracking().ToListAsync());
        projected.Should().BeEquivalentTo(new[]
        {
            new TopicMasteryCounts("C#", 3, 1, 1, 1),
            new TopicMasteryCounts("SQL", 1, 0, 1, 0),
        });
        // 台账与真源逐条同桶
        var states = await db.MasteryItemStates.AsNoTracking().ToListAsync();
        states.Should().HaveCount(4);
        states.Single(s => s.KnowledgeItemId == ItemCSharpMastered).Bucket.Should().Be("Mastered");
        states.Single(s => s.KnowledgeItemId == ItemCSharpNew).Bucket.Should().Be("Fresh");
        // 当日 technicalDepth = 真源均值
        var ability = await db.AbilitySnapshots.AsNoTracking().SingleAsync(a =>
            a.Dimension == "technicalDepth" && a.Source == "knowledge" && a.Date == Today);
        ability.Score.Should().Be(46);
    }

    [Fact]
    public async Task Rebuild_Twice_SecondRunChangesNothing_Idempotent()
    {
        using var db = NewDb();
        await SeedDriftAsync(db);
        var handler = new RebuildProjectionCommandHandler(db, new FakeTruth(Truth()));

        await handler.Handle(new RebuildProjectionCommand(User), default);
        var first = await SnapshotAsync(db);

        var r2 = await handler.Handle(new RebuildProjectionCommand(User), default);
        var second = await SnapshotAsync(db);

        r2.Value.DifferencesBefore.Should().BeEmpty("第二次重建前投影已与真源一致");
        second.Should().Be(first);
    }

    private static async Task<string> SnapshotAsync(AnalyticsDbContext db)
    {
        var rows = await db.MasteryBreakdowns.AsNoTracking()
            .OrderBy(x => x.Topic).ToListAsync();
        var states = await db.MasteryItemStates.AsNoTracking()
            .OrderBy(x => x.KnowledgeItemId).ToListAsync();
        var abilities = await db.AbilitySnapshots.AsNoTracking()
            .OrderBy(x => x.Date).ThenBy(x => x.Dimension).ToListAsync();
        return string.Join(";", rows.Select(x =>
                   $"{x.Date}:{x.Topic}:{x.Total}/{x.Mastered}/{x.Learning}/{x.Fresh}"))
               + "|" + string.Join(";", states.Select(x =>
                   $"{x.KnowledgeItemId}:{x.Topic}:{x.Bucket}"))
               + "|" + string.Join(";", abilities.Select(x =>
                   $"{x.Date}:{x.Dimension}:{x.Source}:{x.Score}"));
    }

    [Fact]
    public async Task Rebuild_TruthUnavailable_FailsAndProjectionUntouched()
    {
        using var db = NewDb();
        await SeedDriftAsync(db);
        var before = await SnapshotAsync(db);
        var handler = new RebuildProjectionCommandHandler(db, new FakeTruth(null));

        var r = await handler.Handle(new RebuildProjectionCommand(User), default);

        r.IsSuccess.Should().BeFalse();
        (await SnapshotAsync(db)).Should().Be(before);
    }

    [Fact]
    public async Task Reconcile_TruthUnavailable_Fails()
    {
        using var db = NewDb();
        var handler = new ReconcileProjectionQueryHandler(db, new FakeTruth(null));

        var r = await handler.Handle(new ReconcileProjectionQuery(User), default);

        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Rebuild_EmptyTruth_ClearsMasteryProjection_BoundaryEmptyTruth()
    {
        using var db = NewDb();
        await SeedDriftAsync(db);
        // 当日行 + 非 knowledge 来源行:前者应被清/覆写,后者不在重建范围
        db.AbilitySnapshots.Add(new AbilitySnapshot(User, Today, "technicalDepth", 55, "knowledge"));
        db.AbilitySnapshots.Add(new AbilitySnapshot(User, Today, "fluency", 70, "mock"));
        await db.SaveChangesAsync();
        var handler = new RebuildProjectionCommandHandler(db, new FakeTruth([]));

        var r = await handler.Handle(new RebuildProjectionCommand(User), default);

        r.IsSuccess.Should().BeTrue();
        r.Value.ProjectionMatchesTruthAfter.Should().BeTrue();
        (await db.MasteryBreakdowns.CountAsync()).Should().Be(0);
        (await db.MasteryItemStates.CountAsync()).Should().Be(0);
        (await db.AbilitySnapshots.AnyAsync(a =>
            a.Source == "knowledge" && a.Date == Today)).Should().BeFalse();
        (await db.AbilitySnapshots.AnyAsync(a =>
            a.Source == "mock" && a.Score == 70)).Should().BeTrue("其他来源投影不在本票重建范围");
    }

    /// <summary>重建后台账可用:既有条目再变等级时按台账迁移,不重复计 Total。</summary>
    [Fact]
    public async Task AfterRebuild_ConsumerMigratesWithinRebuiltLedger()
    {
        using var db = NewDb();
        var handler = new RebuildProjectionCommandHandler(db, new FakeTruth(Truth()));
        await handler.Handle(new RebuildProjectionCommand(User), default);

        var consumer = new KnowledgeLevelChangedConsumer(db,
            NullLogger<KnowledgeLevelChangedConsumer>.Instance);
        var ctx = Substitute.For<ConsumeContext<KnowledgeLevelChanged>>();
        ctx.Message.Returns(new KnowledgeLevelChanged(
            ItemCSharpLearning, User, "C#", "Mastered", 3));
        ctx.MessageId.Returns(Guid.NewGuid());
        ctx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(ctx);

        var projected = MasteryReconciliation.AggregateProjected(
            await db.MasteryBreakdowns.AsNoTracking().ToListAsync());
        projected.Single(x => x.Topic == "C#").Should()
            .Be(new TopicMasteryCounts("C#", 3, 2, 0, 1));
    }
}
