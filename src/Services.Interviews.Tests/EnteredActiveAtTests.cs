using System.Threading;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票186:最近一次进入 Active(In Progress 组)的时刻 EnteredActiveAt。
/// Active 判定与前端分组逐字一致:派生 Outcome 为 null 或 Pending → Active。
/// 机制:变前/变后派生 Outcome 对比 + 单一 helper RefreshEnteredActiveAt,
/// 仅「非 Active → Active」刷新;其余一律不动。
/// 测试直接走 helper(与 handler 内调用点同一调用形态),不依赖 DB。
/// 刷新断言用 50ms 间隔 + 严格大于:时钟分辨率远小于间隔,刷新必严格大于 CreatedAt;
/// 未刷新时字段一字未动,与 CreatedAt 逐位相等。
/// </summary>
public sealed class EnteredActiveAtTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static InterviewEntry NewEntry()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer", userId: Owner);
        e.ClearDomainEvents();
        return e;
    }

    private static InterviewRound AddRound(InterviewEntry e) => e.AddRound("Technical");

    private static void SetRoundOutcome(InterviewEntry e, Guid roundId, InterviewRoundOutcome outcome)
        => e.UpdateRound(roundId, "Technical", null, null, null, null, outcome, null, null);

    private static void SetResult(InterviewEntry e, string? result)
        => e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null,
            e.RoundNo, null, null, null, null, result, null);

    private static void SetRoundNo(InterviewEntry e, int roundNo)
        => e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null,
            roundNo, null, null, null, null, e.Result, null);

    /// <summary>模拟 handler 调用形态:变异前取派生 Outcome,变异后调一次 helper。</summary>
    private static void RefreshAfter(InterviewEntry e, InterviewRoundOutcome? before, Action mutate)
    {
        mutate();
        InterviewMappingExtensions.RefreshEnteredActiveAt(e, before);
    }

    private static void RefreshAfterSleep(InterviewEntry e, InterviewRoundOutcome? before, Action mutate)
    {
        Thread.Sleep(50);
        RefreshAfter(e, before, mutate);
    }

    // ---------- W1:出生即 Active ----------

    [Fact]
    public void Constructor_SetsEnteredActiveAtToCreatedAt()
    {
        var e = NewEntry();
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
        e.ToDto().EnteredActiveAt.Should().Be(e.CreatedAt);
        e.ToListItemDto().EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    [Fact]
    public void ManualCreateSequence_KeepsBornActive()
    {
        // 手动创建(CreateEntryCommandHandler):构造 → UpdateBasicInfo(result null) → AddRound → UpdateRound(Pending)。
        var e = NewEntry();
        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null, 1, null, null, null, null, null, null);
        var round = AddRound(e);
        SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Pending);
        e.ClearDomainEvents();
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    [Fact]
    public void InviteDraftSequence_KeepsBornActive()
    {
        // 邀请自动建稿(EventConsumers 新建分支):构造 → UpdateBasicInfo(result null) → AddRound → UpdateRound(Pending)。
        var e = NewEntry();
        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, "jd", 1, null, null, null, null, null, null);
        var round = AddRound(e);
        SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Pending);
        e.ClearDomainEvents();
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    // ---------- W2:轮次编辑 ----------

    [Fact]
    public void CurrentRound_TerminalToPending_Refreshes()
    {
        var e = NewEntry();
        var round = AddRound(e);
        SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Passed);
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Passed);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfterSleep(e, before, () => SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Pending));
        e.EnteredActiveAt.Should().BeAfter(e.CreatedAt);
    }

    [Fact]
    public void CurrentRound_PendingToTerminal_DoesNotRefresh()
    {
        var e = NewEntry();
        var round = AddRound(e);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfter(e, before, () => SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Passed));
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    [Fact]
    public void CurrentRound_TerminalToTerminal_DoesNotRefresh()
    {
        var e = NewEntry();
        var round = AddRound(e);
        SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Passed);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfter(e, before, () => SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Rejected));
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    [Fact]
    public void NonCurrentRound_Change_DoesNotRefresh()
    {
        var e = NewEntry();
        var current = AddRound(e);
        var later = AddRound(e);
        // 当前轮 Pending(Active),改非当前轮为 Passed 不改变派生 Outcome。
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        before.Should().BeNull();
        RefreshAfter(e, before, () => SetRoundOutcome(e, later.Id, InterviewRoundOutcome.Passed));
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
        current.Should().NotBeNull();
    }

    // ---------- W3:基本信息编辑 ----------

    [Fact]
    public void Result_TerminalWordToEmpty_Refreshes()
    {
        var e = NewEntry();
        SetResult(e, "通过");
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Passed);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfterSleep(e, before, () => SetResult(e, ""));
        e.EnteredActiveAt.Should().BeAfter(e.CreatedAt);
    }

    [Fact]
    public void Result_TerminalWordToUndecidedWord_Refreshes()
    {
        var e = NewEntry();
        SetResult(e, "通过");
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfterSleep(e, before, () => SetResult(e, "等回复"));
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Pending);
        e.EnteredActiveAt.Should().BeAfter(e.CreatedAt);
    }

    [Fact]
    public void RoundNo_TerminalRoundToPendingRound_Refreshes()
    {
        var e = NewEntry();
        var first = AddRound(e);
        var second = AddRound(e);
        SetRoundOutcome(e, first.Id, InterviewRoundOutcome.Passed);
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Passed);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfterSleep(e, before, () => SetRoundNo(e, 2));
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
        e.EnteredActiveAt.Should().BeAfter(e.CreatedAt);
        second.Should().NotBeNull();
    }

    [Fact]
    public void BasicInfo_EnteringTerminal_DoesNotRefresh()
    {
        var e = NewEntry();
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfter(e, before, () => SetResult(e, "通过"));
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Passed);
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    // ---------- W4:轮次删除 ----------

    [Fact]
    public void DeleteCurrentRound_ResultEmpty_Refreshes()
    {
        var e = NewEntry();
        var round = AddRound(e);
        SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Rejected);
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Rejected);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfterSleep(e, before, () => e.RemoveRound(round.Id));
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
        e.EnteredActiveAt.Should().BeAfter(e.CreatedAt);
    }

    [Fact]
    public void DeleteCurrentRound_ResultBackstopsTerminal_DoesNotRefresh()
    {
        var e = NewEntry();
        var round = AddRound(e);
        SetRoundOutcome(e, round.Id, InterviewRoundOutcome.Rejected);
        SetResult(e, "通过");
        // 轮次枚举优先仍判 Rejected;删当前轮后 Result “通过”兜底判 Passed,仍为终态。
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Rejected);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfter(e, before, () => e.RemoveRound(round.Id));
        InterviewMappingExtensions.DeriveOutcome(e).Should().Be(InterviewRoundOutcome.Passed);
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
    }

    [Fact]
    public void DeleteNonCurrentRound_DoesNotRefresh()
    {
        var e = NewEntry();
        var current = AddRound(e);
        var later = AddRound(e);
        var before = InterviewMappingExtensions.DeriveOutcome(e);
        RefreshAfter(e, before, () => e.RemoveRound(later.Id));
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
        e.EnteredActiveAt.Should().Be(e.CreatedAt);
        current.Should().NotBeNull();
    }
}
