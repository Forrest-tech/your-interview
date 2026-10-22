using System.Reflection;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.SharedContracts.Events;
using Xunit;
using SharedOutcome = YourInterview.SharedContracts.Events.InterviewOutcome;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票04(REQ-02+ARC-07):Interviews 侧结果回写的结构化 outcome 契约。
/// 轮次 Outcome 枚举是结构化事实源:UpdateRound 改结果 → 领域事件直接带枚举;
/// 条目级自由文本 Result 变更 → 有对应轮次的定论枚举时带枚举、无则留空
/// (发布端禁止再走文本关键词猜测,回落只在 Jobs 侧单一 adapter)。
/// 发布器把领域枚举显式映射成 SharedContracts 枚举填入集成事件,原文保留作展示。
/// 纯领域/发布器测试,不依赖 DB。
/// </summary>
public sealed class OutcomeWritebackTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid AppId = Guid.NewGuid();

    private static InterviewEntry LinkedEntry()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer",
            jobApplicationId: AppId, userId: Owner);
        e.ClearDomainEvents();
        return e;
    }

    private static InterviewOutcomeRecordedDomainEvent SingleOutcomeEvent(InterviewEntry e)
        => e.DomainEvents.OfType<InterviewOutcomeRecordedDomainEvent>().Single();

    [Fact]
    public void UpdateRound_OutcomeChangedToPassed_RaisesEventWithEnum()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        e.UpdateRound(round.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Passed, null, null);

        var evt = SingleOutcomeEvent(e);
        evt.ApplicationId.Should().Be(AppId);
        evt.RoundNo.Should().Be(round.Order);
        evt.Outcome.Should().Be(InterviewRoundOutcome.Passed);
        evt.Result.Should().Be("Passed");
        evt.UserId.Should().Be(Owner);
    }

    [Fact]
    public void UpdateRound_OutcomeUnchanged_RaisesNoEvent()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        // 内部调用方(邀约建稿/AddRound 补字段)以 Pending 反复保存,不得触发回写
        e.UpdateRound(round.Id, "Technical", new DateOnly(2026, 10, 22), "Alice", "Video", null,
            InterviewRoundOutcome.Pending, null, null);

        e.DomainEvents.OfType<InterviewOutcomeRecordedDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public void UpdateRound_NoLinkedApplication_RaisesNoEvent()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer", userId: Owner);
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        e.UpdateRound(round.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Rejected, null, null);

        e.DomainEvents.OfType<InterviewOutcomeRecordedDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public void UpdateBasicInfo_ResultChanged_RoundHasDefinitiveOutcome_FillsEnumFromRound()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        e.UpdateRound(round.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Passed, null, null);
        e.ClearDomainEvents();

        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null, round.Order,
            null, null, null, null, "等后续轮次", null);

        var evt = SingleOutcomeEvent(e);
        evt.Outcome.Should().Be(InterviewRoundOutcome.Passed);
        evt.Result.Should().Be("等后续轮次");
    }

    [Fact]
    public void UpdateBasicInfo_ResultChanged_NoDefinitiveRoundOutcome_LeavesEnumEmpty()
    {
        var e = LinkedEntry();
        var round = e.AddRound("Technical");
        e.ClearDomainEvents();

        // 轮次还是 Pending:发布端不许拿自由文本猜枚举 → Outcome 留空,
        // 由 Jobs 侧旧事件 adapter 回落(新结构化路径以轮次枚举为准)。
        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null, round.Order,
            null, null, null, null, "通过", null);

        var evt = SingleOutcomeEvent(e);
        evt.Outcome.Should().BeNull();
        evt.Result.Should().Be("通过");
    }

    [Fact]
    public async Task Publisher_MapsDomainEnumToContractEnum_KeepsFreeText()
    {
        var proxy = DispatchProxy.Create<IPublishEndpoint, CapturePublishProxy>();
        var capture = (CapturePublishProxy)proxy;
        var publisher = new InterviewsIntegrationEventPublisher(proxy,
            NullLogger<InterviewsIntegrationEventPublisher>.Instance);

        var domain = new InterviewOutcomeRecordedDomainEvent(
            Guid.NewGuid(), AppId, 2, "Rejected", Owner, InterviewRoundOutcome.Rejected);
        await publisher.Handle(new DomainEventNotification<InterviewOutcomeRecordedDomainEvent>(domain),
            default);

        var sent = capture.Published.OfType<InterviewOutcomeRecorded>().Single();
        sent.ApplicationId.Should().Be(AppId);
        sent.RoundNo.Should().Be(2);
        sent.Result.Should().Be("Rejected");
        sent.Outcome.Should().Be(SharedOutcome.Rejected);
        sent.UserId.Should().Be(Owner);
    }

    /// <summary>捕获 Publish 调用的 IPublishEndpoint 代理(只关心 InterviewOutcomeRecorded)。</summary>
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
