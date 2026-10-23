using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using YourInterview.Services.Analytics.Application;
using YourInterview.Services.Analytics.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票91 §7:KnowledgeItemDeleted 消费者台账迁移 —— 删除已计数条目须 Total-1 并移出
/// 其台账桶、删台账行;未计数/重复投递不动分布(防双减、防打负)。
/// 前置分布一律经票34 KnowledgeLevelChangedConsumer 真实消费路径建立,不手造台账,
/// 保证与生产事件流同口径。boundary 类别:空台账(未计数)、重复投递(同/异 MessageId)、
/// 计数下限(删至 0 不为负)、事件自带主题与台账不一致(以台账为准)。
/// </summary>
public sealed class Ticket91KnowledgeItemDeletedConsumerTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static AnalyticsDbContext NewDb() => new(
        new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task ConsumeLevel(AnalyticsDbContext db, KnowledgeLevelChanged msg)
    {
        var consumer = new KnowledgeLevelChangedConsumer(db,
            NullLogger<KnowledgeLevelChangedConsumer>.Instance);
        var ctx = Substitute.For<ConsumeContext<KnowledgeLevelChanged>>();
        ctx.Message.Returns(msg);
        ctx.MessageId.Returns(Guid.NewGuid());
        ctx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(ctx);
    }

    private static async Task ConsumeDelete(AnalyticsDbContext db, KnowledgeItemDeleted msg,
        Guid? messageId = null)
    {
        var consumer = new KnowledgeItemDeletedConsumer(db,
            NullLogger<KnowledgeItemDeletedConsumer>.Instance);
        var ctx = Substitute.For<ConsumeContext<KnowledgeItemDeleted>>();
        ctx.Message.Returns(msg);
        ctx.MessageId.Returns(messageId ?? Guid.NewGuid());
        ctx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(ctx);
    }

    private static KnowledgeLevelChanged Level(Guid item, string topic, string mastery, int level)
        => new(item, User, topic, mastery, level);

    private static KnowledgeItemDeleted Del(Guid item, string topic, string mastery, int level)
        => new(item, User, topic, mastery, level);

    [Fact] // 主路径:两条已计数,删其一 → Total-1、移出其桶、台账行删除,另一条不动
    public async Task Delete_CountedItem_RemovesFromBreakdownAndLedger()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "C#", "Mastered", 3));
        await ConsumeLevel(db, Level(b, "C#", "Learning", 1));

        await ConsumeDelete(db, Del(a, "C#", "Mastered", 3));

        var row = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "C#");
        row.Total.Should().Be(1);
        row.Mastered.Should().Be(0);
        row.Learning.Should().Be(1);
        var states = await db.MasteryItemStates.ToListAsync();
        states.Should().ContainSingle().Which.KnowledgeItemId.Should().Be(b);
    }

    [Fact] // boundary 空台账:从未计数的条目被删 → 分布零改动(老条目/事件化前数据口径)
    public async Task Delete_UncountedItem_LeavesBreakdownUntouched()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "C#", "Mastered", 3));

        await ConsumeDelete(db, Del(Guid.NewGuid(), "C#", "Mastered", 3));

        var row = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "C#");
        row.Total.Should().Be(1);
        row.Mastered.Should().Be(1);
    }

    [Fact] // 防重复发/防双减:同一删除以两个不同 MessageId 投递两次 → 只减一次
    public async Task Delete_DeliveredTwiceWithNewMessageIds_DecrementsOnce()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "C#", "Learning", 1));

        await ConsumeDelete(db, Del(a, "C#", "Learning", 1));
        await ConsumeDelete(db, Del(a, "C#", "Learning", 1));

        var row = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "C#");
        row.Total.Should().Be(0);
        row.Learning.Should().Be(0);
    }

    [Fact] // 防重复发:同一 MessageId 重投 → 基类 ProcessedEvents 幂等台账拦下
    public async Task Delete_SameMessageIdRedelivered_ProcessedOnce()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "C#", "Mastered", 3));
        await ConsumeLevel(db, Level(b, "C#", "Mastered", 3));

        var mid = Guid.NewGuid();
        await ConsumeDelete(db, Del(a, "C#", "Mastered", 3), mid);
        await ConsumeDelete(db, Del(a, "C#", "Mastered", 3), mid);

        var row = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "C#");
        row.Total.Should().Be(1);
        row.Mastered.Should().Be(1);
    }

    [Fact] // 改级后删除:从当前桶(迁移后)移出,不是从最初的桶
    public async Task Delete_AfterLevelChange_RemovesFromCurrentBucket()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "C#", "New", 0));
        await ConsumeLevel(db, Level(a, "C#", "Mastered", 3));

        await ConsumeDelete(db, Del(a, "C#", "Mastered", 3));

        var row = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "C#");
        row.Total.Should().Be(0);
        row.Mastered.Should().Be(0);
        row.Fresh.Should().Be(0);
    }

    [Fact] // 事件自带主题是删除时主题、台账却是改名后主题 → 以台账为准移出,旧主题行不动
    public async Task Delete_ItemTopicMoved_RemovesFromLedgerTopicNotEventTopic()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        var other = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "OldTopic", "Learning", 1));
        await ConsumeLevel(db, Level(other, "OldTopic", "Learning", 1));
        await ConsumeLevel(db, Level(a, "NewTopic", "Learning", 1));   // 条目改主题(票34 迁移路径)

        // 删除事件的 Topic 以 Knowledge 删除时回查为准;这里模拟事件主题与台账不一致的极端情形
        await ConsumeDelete(db, Del(a, "OldTopic", "Learning", 1));

        var oldRow = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "OldTopic");
        oldRow.Total.Should().Be(1);       // 只剩 other
        oldRow.Learning.Should().Be(1);
        var newRow = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "NewTopic");
        newRow.Total.Should().Be(0);       // a 从台账所在主题移出
        newRow.Learning.Should().Be(0);
        (await db.MasteryItemStates.AnyAsync(x => x.KnowledgeItemId == a)).Should().BeFalse();
    }

    [Fact] // boundary 下限:删至空集,所有计数钳在 0 不为负;异主题不受扰
    public async Task Delete_LastItem_CountsFloorAtZero_OtherTopicUntouched()
    {
        using var db = NewDb();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await ConsumeLevel(db, Level(a, "C#", "New", 0));
        await ConsumeLevel(db, Level(b, "SQL", "Mastered", 3));

        await ConsumeDelete(db, Del(a, "C#", "New", 0));
        await ConsumeDelete(db, Del(a, "C#", "New", 0));   // 再删一次(已不在台账)

        var cs = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "C#");
        cs.Total.Should().Be(0);
        cs.Fresh.Should().Be(0);
        var sql = await db.MasteryBreakdowns.SingleAsync(x => x.Topic == "SQL");
        sql.Total.Should().Be(1);
        sql.Mastered.Should().Be(1);
    }
}
