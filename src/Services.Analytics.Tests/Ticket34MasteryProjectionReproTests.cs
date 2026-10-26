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
/// 票34(REQ-18)复现/回归:KnowledgeLevelChanged 消费者投影分叉。
///
/// 背景(盘点结论):
///  1. CONF-AN-3:当日已有 MasteryBreakdown 时消费者以原值回写,计数永不迁移/增长。
///  2. 等级语义错位:发布侧 Level 是 MasteryLevel 枚举 int(New=0/Learning=1/Familiar=2/
///     Mastered=3/NeedsReview=4),消费者却按 0-100 分口径消费(Mastered 恒计 0、
///     technicalDepth 雷达分恒 0-4)。
///
/// 期望口径(本票定版,见 MasteryLevelMapping):
///  桶:Mastered→Mastered;Learning/Familiar/NeedsReview→Learning;New→Fresh。
///  雷达分:New=0 / NeedsReview=20 / Learning=40 / Familiar=65 / Mastered=80。
///  同一用户同一主题的 Total = 消费者见过的不同条目数(按 KnowledgeItemId 去重),
///  同一条目等级变化只迁移桶计数,不重复计 Total。
/// </summary>
public sealed class Ticket34MasteryProjectionReproTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static AnalyticsDbContext NewDb() => new(
        new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task ConsumeKnowledge(AnalyticsDbContext db, KnowledgeLevelChanged msg,
        Guid? messageId = null)
    {
        var consumer = new KnowledgeLevelChangedConsumer(db,
            NullLogger<KnowledgeLevelChangedConsumer>.Instance);
        var ctx = Substitute.For<ConsumeContext<KnowledgeLevelChanged>>();
        ctx.Message.Returns(msg);
        ctx.MessageId.Returns(messageId ?? Guid.NewGuid());
        ctx.CancellationToken.Returns(CancellationToken.None);
        await consumer.Consume(ctx);
    }

    private static KnowledgeLevelChanged Ev(Guid item, string topic, string mastery, int level)
        => new(item, User, topic, mastery, level);

    [Fact]
    public async Task TwoItemsSameTopicSameDay_BothCounted()
    {
        using var db = NewDb();
        await ConsumeKnowledge(db, Ev(Guid.NewGuid(), "C#", "Mastered", 3));
        await ConsumeKnowledge(db, Ev(Guid.NewGuid(), "C#", "Learning", 1));

        var bucket = await db.MasteryBreakdowns.SingleAsync();
        bucket.Total.Should().Be(2);
        bucket.Mastered.Should().Be(1);
        bucket.Learning.Should().Be(1);
        bucket.Fresh.Should().Be(0);
    }

    [Fact]
    public async Task SameItemLevelChanges_CountMigratesBetweenBuckets()
    {
        using var db = NewDb();
        var item = Guid.NewGuid();
        await ConsumeKnowledge(db, Ev(item, "C#", "Learning", 1));
        await ConsumeKnowledge(db, Ev(item, "C#", "Mastered", 3));

        var bucket = await db.MasteryBreakdowns.SingleAsync();
        bucket.Total.Should().Be(1, "同一条目不重复计 Total");
        bucket.Mastered.Should().Be(1);
        bucket.Learning.Should().Be(0);
        bucket.Fresh.Should().Be(0);
    }

    [Fact]
    public async Task KnowledgeScore_MapsMasteryLevelToRadarScale()
    {
        using var db = NewDb();
        await ConsumeKnowledge(db, Ev(Guid.NewGuid(), "C#", "Mastered", 3));

        var snap = await db.AbilitySnapshots.SingleAsync();
        snap.Dimension.Should().Be("technicalDepth");
        snap.Source.Should().Be("knowledge");
        snap.Score.Should().Be(80, "Mastered 的雷达分应是掌握档分数,不是枚举 int 3");
    }

    [Fact]
    public async Task DuplicateDelivery_SameMessageId_NotDoubleCounted()
    {
        using var db = NewDb();
        var msg = Ev(Guid.NewGuid(), "C#", "Mastered", 3);
        var mid = Guid.NewGuid();
        await ConsumeKnowledge(db, msg, mid);
        await ConsumeKnowledge(db, msg, mid);

        var bucket = await db.MasteryBreakdowns.SingleAsync();
        bucket.Total.Should().Be(1);
        bucket.Mastered.Should().Be(1);
    }

    [Fact]
    public async Task NewItem_IsFreshBucket_AndZeroScore()
    {
        using var db = NewDb();
        await ConsumeKnowledge(db, Ev(Guid.NewGuid(), "SQL", "New", 0));

        var bucket = await db.MasteryBreakdowns.SingleAsync();
        bucket.Total.Should().Be(1);
        bucket.Fresh.Should().Be(1);
        (await db.AbilitySnapshots.SingleAsync()).Score.Should().Be(0);
    }

    /// <summary>不变项守卫:Pipeline 消费者逐日增量口径(累计到达)本票不动。</summary>
    [Fact]
    public async Task PipelineConsumer_DailyIncrementalCounts_Unchanged()
    {
        using var db = NewDb();
        var consumer = new JobApplicationStatusChangedConsumer(db,
            NullLogger<JobApplicationStatusChangedConsumer>.Instance);

        async Task Send(string to)
        {
            var ctx = Substitute.For<ConsumeContext<JobApplicationStatusChanged>>();
            ctx.Message.Returns(new JobApplicationStatusChanged(
                Guid.NewGuid(), User, Guid.NewGuid(), "Saved", to));
            ctx.MessageId.Returns(Guid.NewGuid());
            ctx.CancellationToken.Returns(CancellationToken.None);
            await consumer.Consume(ctx);
        }

        await Send("Applied");
        await Send("Interviewing");

        var snap = await db.PipelineSnapshots.SingleAsync();
        snap.Applied.Should().Be(1);
        snap.Interviewing.Should().Be(1);
    }
}
