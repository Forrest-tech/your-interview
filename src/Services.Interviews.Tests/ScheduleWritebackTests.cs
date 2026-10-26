using System.Reflection;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.SharedContracts.Events;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票114(REQ-02 残余/CONF-DASH-1):Interviews 侧排期回写的发布条件。
/// 排期事实收敛在领域 UpdateRound(日期实际变化)与 RemoveRound(删有日期轮次)
/// 两点:已关联投递 + 日期实际变化才 Raise InterviewRoundScheduledDomainEvent;
/// 日期未变不发(票91 计数纪律),未关联投递不发。删轮次以 ScheduledDate=null
/// 形态发(排期撤销)。发布器直通映射成 SharedContracts 契约。
/// 纯领域/发布器测试,不依赖 DB。
/// </summary>
public sealed class ScheduleWritebackTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();
    private static readonly DateOnly D1 = new(2026, 10, 28);
    private static readonly DateOnly D2 = new(2026, 11, 2);

    private static InterviewEntry LinkedEntry()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer",
            jobApplicationId: AppId, userId: Owner);
        e.ClearDomainEvents();
        return e;
    }

    private static void SetDate(InterviewEntry e, InterviewRound round, DateOnly? date)
        => e.UpdateRound(round.Id, round.Stage, date, null, null, null,
            InterviewRoundOutcome.Pending, null, null);

    private static InterviewRoundScheduledDomainEvent SingleScheduledEvent(InterviewEntry e)
        => e.DomainEvents.OfType<InterviewRoundScheduledDomainEvent>().Single();

    [Fact]
    public void UpdateRound_DateSetFromNull_RaisesScheduledEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        SetDate(e, round, D1);

        var evt = SingleScheduledEvent(e);
        evt.ApplicationId.Should().Be(AppId);
        evt.EntryId.Should().Be(e.Id);
        evt.RoundNo.Should().Be(round.Order);
        evt.Stage.Should().Be("Technical");
        evt.ScheduledDate.Should().Be(D1);
        evt.UserId.Should().Be(Owner);
    }

    [Fact]
    public void UpdateRound_DateChangedDateToDate_RaisesScheduledEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        SetDate(e, round, D1);
        e.ClearDomainEvents();

        SetDate(e, round, D2);

        SingleScheduledEvent(e).ScheduledDate.Should().Be(D2);
    }

    [Fact]
    public void UpdateRound_DateUnchanged_RaisesNoScheduledEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        SetDate(e, round, D1);
        e.ClearDomainEvents();

        // 日期未变的保存(内部补字段调用、邀请消费者同日期回放)不得重复发(票91 纪律)
        e.UpdateRound(round.Id, "Technical", D1, "Alice", "Video", null,
            InterviewRoundOutcome.Pending, null, null);

        e.DomainEvents.OfType<InterviewRoundScheduledDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public void UpdateRound_DateCleared_RaisesNullScheduledEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        SetDate(e, round, D1);
        e.ClearDomainEvents();

        SetDate(e, round, null);

        // 日期 → null 是真实变化:以撤销形态发出,Jobs 只清投影日期不回退状态
        SingleScheduledEvent(e).ScheduledDate.Should().BeNull();
    }

    [Fact]
    public void UpdateRound_NoLinkedApplication_RaisesNoScheduledEvent()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer", userId: Owner);
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        SetDate(e, round, D1);

        e.DomainEvents.OfType<InterviewRoundScheduledDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public void RemoveRound_WithDate_RaisesNullScheduledEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        SetDate(e, round, D1);
        e.ClearDomainEvents();

        e.RemoveRound(round.Id);

        var evt = SingleScheduledEvent(e);
        evt.RoundNo.Should().Be(round.Order);
        evt.ScheduledDate.Should().BeNull();
        evt.ApplicationId.Should().Be(AppId);
    }

    [Fact]
    public void RemoveRound_WithoutDate_RaisesNoScheduledEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        e.RemoveRound(round.Id);

        e.DomainEvents.OfType<InterviewRoundScheduledDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Publisher_MapsDomainEventToContract()
    {
        var proxy = DispatchProxy.Create<IPublishEndpoint, CapturePublishProxy>();
        var capture = (CapturePublishProxy)proxy;
        var publisher = new InterviewsIntegrationEventPublisher(proxy,
            NullLogger<InterviewsIntegrationEventPublisher>.Instance);

        var domain = new InterviewRoundScheduledDomainEvent(
            Guid.NewGuid(), AppId, 2, "Technical", D1, Owner);
        await publisher.Handle(new DomainEventNotification<InterviewRoundScheduledDomainEvent>(domain),
            default);

        var sent = capture.Published.OfType<InterviewRoundScheduled>().Single();
        sent.ApplicationId.Should().Be(AppId);
        sent.RoundNo.Should().Be(2);
        sent.Stage.Should().Be("Technical");
        sent.ScheduledDate.Should().Be(D1);
        sent.UserId.Should().Be(Owner);
    }

    /// <summary>捕获 Publish 调用的 IPublishEndpoint 代理(只关心 InterviewRoundScheduled)。</summary>
    public class CapturePublishProxy : DispatchProxy
    {
        public List<object> Published { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPublishEndpoint.Publish)
                && args is { Length: > 0 } && args[0] is not null)
                Published.Add(args[0]!);
            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }
}
