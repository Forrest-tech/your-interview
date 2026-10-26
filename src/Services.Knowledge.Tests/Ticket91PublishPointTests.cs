using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using YourInterview.Services.Knowledge.Infrastructure.Services;
using YourInterview.SharedContracts.Events;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票91 §7:Knowledge 发布点行为 —— 条目新建/删除/RecordReview 内改级各发恰当领域事件,
/// 且不重复发。分三层:
///  A. 聚合纯逻辑(无 DB):领域事件的 Raise 时机与内容(等级 int 与 MasteryLevel 枚举对齐)。
///  B. 命令路径(DB 门控,同票16 口径连真 PG):经真实 Handler 走完命令后,
///     拦截器派发源(实体 DomainEvents)里有且仅有所期事件。
///  C. 发布器翻译(DB 门控):领域事件 → 集成事件的字段填充(UserId/Topic/Mastery 字符串/
///     Level 枚举 int),含软删后回查(IgnoreQueryFilters)与查不到不发。
/// DB 连不上时 B/C 自动跳过(A 层恒跑),与本项目既有 DB 门控先例一致。
/// </summary>
public sealed class Ticket91PublishPointTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private KnowledgeDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb();
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            var items = await _db.Items.IgnoreQueryFilters().Where(x => x.UserId == Owner).ToListAsync();
            _db.Items.RemoveRange(items);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static KnowledgeDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<KnowledgeDbContext>().UseNpgsql(Cs).Options;
        return new KnowledgeDbContext(opts, new TestCurrentUser(Owner));
    }

    // ---------- A. 聚合纯逻辑 ----------

    [Fact] // 发布点:新建 —— 构造即 Raise 创建事件,恰一条
    public void Create_RaisesCreatedEvent_ExactlyOnce()
    {
        var item = new KnowledgeItem("t", "C#", "q", userId: Owner);

        var created = item.DomainEvents.OfType<KnowledgeItemCreatedDomainEvent>().ToList();
        created.Should().ContainSingle().Which.KnowledgeItemId.Should().Be(item.Id);
        item.DomainEvents.OfType<KnowledgeItemDeletedDomainEvent>().Should().BeEmpty();
        item.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should().BeEmpty();
    }

    [Fact] // 发布点:RecordReview 改级(Mastered + Again → NeedsReview)—— 发掌握度变化事件
    public void RecordReview_MasteredAgain_RaisesMasteryChanged()
    {
        var item = new KnowledgeItem("t", "C#", "q", userId: Owner);
        item.PromoteMastery(MasteryLevel.Mastered);
        item.ClearDomainEvents();

        item.RecordReview(ReviewResult.Again);

        var ev = item.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should()
            .ContainSingle().Which;
        ev.PreviousLevel.Should().Be((int)MasteryLevel.Mastered);
        ev.NewLevel.Should().Be((int)MasteryLevel.NeedsReview);
    }

    [Fact] // 发布点:RecordReview 改级(Learning 连对 3 次 → Familiar)—— 第三次才发,前两次不发(防重复发)
    public void RecordReview_LearningStreak_RaisesOnlyWhenLevelChanges()
    {
        var item = new KnowledgeItem("t", "C#", "q", userId: Owner);
        item.PromoteMastery(MasteryLevel.Learning);
        item.ClearDomainEvents();

        item.RecordReview(ReviewResult.Good);
        item.RecordReview(ReviewResult.Good);
        item.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should().BeEmpty(
            "连对 1/2 次等级未变,不许发");

        item.RecordReview(ReviewResult.Good);
        var ev = item.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should()
            .ContainSingle().Which;
        ev.PreviousLevel.Should().Be((int)MasteryLevel.Learning);
        ev.NewLevel.Should().Be((int)MasteryLevel.Familiar);
    }

    [Fact] // 防重复发:RecordReview 未改级(New + Hard / Mastered + Good)—— 不发掌握度变化事件
    public void RecordReview_NoLevelChange_RaisesNoMasteryEvent()
    {
        var fresh = new KnowledgeItem("t", "C#", "q", userId: Owner);
        fresh.ClearDomainEvents();
        fresh.RecordReview(ReviewResult.Hard);
        fresh.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should().BeEmpty();

        var mastered = new KnowledgeItem("t", "C#", "q", userId: Owner);
        mastered.PromoteMastery(MasteryLevel.Mastered);
        mastered.ClearDomainEvents();
        mastered.RecordReview(ReviewResult.Good);
        mastered.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should().BeEmpty();
    }

    [Fact] // 发布点:删除 —— 发删除事件且带删除前等级;重复删除不重复发(防消费侧双减)
    public void MarkDeleted_RaisesDeletedEvent_OnlyOnce_WithPreviousLevel()
    {
        var item = new KnowledgeItem("t", "C#", "q", userId: Owner);
        item.PromoteMastery(MasteryLevel.Familiar);
        item.ClearDomainEvents();

        item.MarkDeleted();
        item.MarkDeleted();   // 重复删除

        var ev = item.DomainEvents.OfType<KnowledgeItemDeletedDomainEvent>().Should()
            .ContainSingle().Which;
        ev.KnowledgeItemId.Should().Be(item.Id);
        ev.PreviousLevel.Should().Be((int)MasteryLevel.Familiar);
    }

    [Fact] // 发布点:恢复是删除的逆操作 —— 已删恢复发创建事件;未删恢复不发
    public void Restore_RaisesCreatedEvent_OnlyAfterRealDelete()
    {
        var item = new KnowledgeItem("t", "C#", "q", userId: Owner);
        item.ClearDomainEvents();

        item.Restore();   // 未删, no-op
        item.DomainEvents.OfType<KnowledgeItemCreatedDomainEvent>().Should().BeEmpty();

        item.MarkDeleted();
        item.ClearDomainEvents();
        item.Restore();
        item.DomainEvents.OfType<KnowledgeItemCreatedDomainEvent>().Should().ContainSingle();
    }

    [Fact] // 防重复发(既有口径守卫):PromoteMastery 同级不发;导入「建后升至 Mastered」在消费侧同桶汇合的前提 = 两事件等级一致为最终级
    public void PromoteMastery_SameLevel_RaisesNoEvent()
    {
        var item = new KnowledgeItem("t", "C#", "q", userId: Owner);
        item.ClearDomainEvents();
        item.PromoteMastery(MasteryLevel.New);
        item.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should().BeEmpty();
    }

    // ---------- B. 命令路径(DB 门控) ----------

    [Fact] // 发布点:CreateKnowledge 命令走完后实体上挂着创建事件(拦截器派发源)
    public async Task CreateCommand_LeavesCreatedDomainEventOnEntity()
    {
        if (!_available) return;
        var handler = new CreateKnowledgeCommandHandler(_db);
        var r = await handler.Handle(new CreateKnowledgeCommand(Owner, "t91-create", "T91", null, "q"), default);
        r.IsSuccess.Should().BeTrue();

        var item = await _db.Items.FirstAsync(x => x.Id == r.Value);
        item.DomainEvents.OfType<KnowledgeItemCreatedDomainEvent>().Should().ContainSingle();
    }

    [Fact] // 发布点:RecordReview 命令(Mastered 条目答 Again)走完后挂着改级事件
    public async Task RecordReviewCommand_LeavesMasteryChangedDomainEventOnEntity()
    {
        if (!_available) return;
        var create = new CreateKnowledgeCommandHandler(_db);
        var r = await create.Handle(new CreateKnowledgeCommand(Owner, "t91-review", "T91", null, "q"), default);
        var item = await _db.Items.FirstAsync(x => x.Id == r.Value);
        item.PromoteMastery(MasteryLevel.Mastered);
        await _db.SaveChangesAsync();
        item.ClearDomainEvents();

        var review = new RecordReviewCommandHandler(_db, TimeProvider.System);
        var rr = await review.Handle(new RecordReviewCommand(Owner, item.Id, "Again"), default);
        rr.IsSuccess.Should().BeTrue();
        item.DomainEvents.OfType<KnowledgeMasteryChangedDomainEvent>().Should().ContainSingle()
            .Which.NewLevel.Should().Be((int)MasteryLevel.NeedsReview);
    }

    [Fact] // 发布点:DeleteKnowledge 命令走完后挂着删除事件(带删除前等级)
    public async Task DeleteCommand_LeavesDeletedDomainEventOnEntity()
    {
        if (!_available) return;
        var create = new CreateKnowledgeCommandHandler(_db);
        var r = await create.Handle(new CreateKnowledgeCommand(Owner, "t91-delete", "T91", null, "q"), default);
        var item = await _db.Items.FirstAsync(x => x.Id == r.Value);
        item.PromoteMastery(MasteryLevel.Learning);
        await _db.SaveChangesAsync();
        item.ClearDomainEvents();

        var del = new DeleteKnowledgeCommandHandler(_db);
        var dr = await del.Handle(new DeleteKnowledgeCommand(Owner, item.Id), default);
        dr.IsSuccess.Should().BeTrue();
        item.DomainEvents.OfType<KnowledgeItemDeletedDomainEvent>().Should().ContainSingle()
            .Which.PreviousLevel.Should().Be((int)MasteryLevel.Learning);
    }

    // ---------- C. 发布器翻译(DB 门控) ----------

    [Fact] // 发布器:创建事件 → KnowledgeLevelChanged,字段 = 本体真实 UserId/Topic/Mastery 字符串/枚举 int
    public async Task LifecyclePublisher_Created_PublishesLevelChangedWithRealFields()
    {
        if (!_available) return;
        var item = new KnowledgeItem("t91-pub", "T91Topic", "q", userId: Owner);
        _db.Items.Add(item);
        await _db.SaveChangesAsync();
        item.PromoteMastery(MasteryLevel.Mastered);
        await _db.SaveChangesAsync();

        KnowledgeLevelChanged? published = null;
        var bus = Substitute.For<IPublishEndpoint>();
        bus.Publish(Arg.Any<KnowledgeLevelChanged>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => published = c.Arg<KnowledgeLevelChanged>());

        var publisher = new KnowledgeItemLifecyclePublisher(bus, _db,
            NullLogger<KnowledgeItemLifecyclePublisher>.Instance);
        await publisher.Handle(
            new DomainEventNotification<KnowledgeItemCreatedDomainEvent>(
                new KnowledgeItemCreatedDomainEvent(item.Id)), default);

        published.Should().NotBeNull();
        published!.KnowledgeItemId.Should().Be(item.Id);
        published.UserId.Should().Be(Owner);
        published.Topic.Should().Be("T91Topic");
        published.Mastery.Should().Be(nameof(MasteryLevel.Mastered));   // 字符串优先口径
        published.Level.Should().Be((int)MasteryLevel.Mastered);          // 枚举 int 回落口径
    }

    [Fact] // 发布器:删除事件 → KnowledgeItemDeleted 带足 id/用户/原主题/原等级;条目已软删仍回查得到
    public async Task LifecyclePublisher_Deleted_PublishesItemDeletedWithOriginalFields()
    {
        if (!_available) return;
        var item = new KnowledgeItem("t91-pub-del", "T91Topic", "q", userId: Owner);
        _db.Items.Add(item);
        await _db.SaveChangesAsync();
        item.PromoteMastery(MasteryLevel.Familiar);
        item.MarkDeleted();
        await _db.SaveChangesAsync();

        KnowledgeItemDeleted? published = null;
        var bus = Substitute.For<IPublishEndpoint>();
        bus.Publish(Arg.Any<KnowledgeItemDeleted>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => published = c.Arg<KnowledgeItemDeleted>());

        var publisher = new KnowledgeItemLifecyclePublisher(bus, _db,
            NullLogger<KnowledgeItemLifecyclePublisher>.Instance);
        await publisher.Handle(
            new DomainEventNotification<KnowledgeItemDeletedDomainEvent>(
                new KnowledgeItemDeletedDomainEvent(item.Id, (int)MasteryLevel.Familiar)), default);

        published.Should().NotBeNull();
        published!.KnowledgeItemId.Should().Be(item.Id);
        published.UserId.Should().Be(Owner);
        published.Topic.Should().Be("T91Topic");
        published.Mastery.Should().Be(nameof(MasteryLevel.Familiar));
        published.Level.Should().Be((int)MasteryLevel.Familiar);
    }

    [Fact] // 发布器:条目不存在 → 不发任何事件(绝不发假归属),两类事件同口径
    public async Task LifecyclePublisher_MissingItem_PublishesNothing()
    {
        if (!_available) return;
        var bus = Substitute.For<IPublishEndpoint>();
        var publisher = new KnowledgeItemLifecyclePublisher(bus, _db,
            NullLogger<KnowledgeItemLifecyclePublisher>.Instance);
        var ghost = Guid.NewGuid();

        await publisher.Handle(
            new DomainEventNotification<KnowledgeItemCreatedDomainEvent>(
                new KnowledgeItemCreatedDomainEvent(ghost)), default);
        await publisher.Handle(
            new DomainEventNotification<KnowledgeItemDeletedDomainEvent>(
                new KnowledgeItemDeletedDomainEvent(ghost, 0)), default);

        await bus.DidNotReceive().Publish(Arg.Any<KnowledgeLevelChanged>(), Arg.Any<CancellationToken>());
        await bus.DidNotReceive().Publish(Arg.Any<KnowledgeItemDeleted>(), Arg.Any<CancellationToken>());
    }
}
