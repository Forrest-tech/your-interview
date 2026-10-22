using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票05(ARC-14):条目列表/详情 DTO 的结构化 Outcome 派生(DeriveOutcome)单一事实源。
/// 口径与票04 对齐:当前轮轮次枚举为准;Result 受控词表精确解析;自由文本关键词
/// 回落与 Jobs 侧 LegacyOutcomeTextAdapter 同向(未通过/不通过/拒/挂 → Rejected
/// 先于 通过 → Passed;等回复 → 不落定)。
/// 对照矩阵五例(Rejected/Passed/Pending/等回复/无结果)的改前前端分组结论
/// 在此逐例锁定:Rejected→Completed(rejected)、Passed→Completed(positive)、
/// Pending/等回复/无结果→In Progress —— 改后经后端 Outcome 映射分组须一致。
/// 纯映射测试,不依赖 DB。
/// </summary>
public sealed class EntryOutcomeDerivationTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static InterviewEntry EntryWithResult(string? result)
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Acme", "Senior .NET Developer", userId: Owner);
        if (result is not null)
            e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null, roundNo: 1,
                null, null, null, null, result, null);
        e.ClearDomainEvents();
        return e;
    }

    private static void AssertOutcome(InterviewEntry e, string? expected)
    {
        // 列表与详情 DTO 必须同源同结论。
        InterviewMappingExtensions.DeriveOutcome(e)?.ToString().Should().Be(expected);
        e.ToListItemDto().Outcome.Should().Be(expected);
        e.ToDto().Outcome.Should().Be(expected);
    }

    // ---------- 对照矩阵五例(结果原文 → 后端 Outcome;括号内为改前前端分组) ----------

    [Theory]
    [InlineData("Rejected", "Rejected")]   // 改前:Completed / rejected
    [InlineData("Passed", "Passed")]       // 改前:Completed / positive
    [InlineData("Pending", "Pending")]     // 改前:In Progress
    [InlineData("等回复", "Pending")]       // 改前:In Progress;Tracker Legacy 同样不回写
    [InlineData("通过", "Passed")]          // Legacy:通过 → Passed,同向
    [InlineData("被拒", "Rejected")]        // Legacy:拒 → Failed,同向
    [InlineData("未通过", "Rejected")]      // Legacy:未通过先于通过 → Failed,同向
    [InlineData("Ghosted", "Ghosted")]     // 改前:Completed / closed
    [InlineData("已取消", "Cancelled")]     // 改前:Completed / closed
    [InlineData("拿到 offer", "Passed")]    // 改前:Completed / positive
    [InlineData("waiting for reply", "Pending")]
    [InlineData("随便写点什么", null)]       // 不命中任何口径 → 无结论,留 In Progress
    public void ResultText_DerivesOutcome(string result, string? expected)
        => AssertOutcome(EntryWithResult(result), expected);

    [Fact]
    public void NoResult_NoOutcome() // 对照矩阵「无结果」:改前 In Progress
        => AssertOutcome(EntryWithResult(null), null);

    [Fact]
    public void EmptyResult_NoOutcome()
        => AssertOutcome(EntryWithResult(""), null);

    // ---------- 向票04口径靠拢的已知差异(旧前端判读不同,后端为准) ----------

    [Theory]
    [InlineData("不通过", "Rejected")] // 旧前端含「通过」误判 positive;Legacy 判 Failed
    [InlineData("挂了", "Rejected")]   // 旧前端不命中留 In Progress;Legacy 判 Failed
    public void LegacyAlignedDivergences(string result, string? expected)
        => AssertOutcome(EntryWithResult(result), expected);

    // ---------- 结构化轮次优先(票04:枚举为准,文本只作展示) ----------

    [Fact]
    public void DefinitiveRoundOutcome_WinsOverResultText()
    {
        var e = EntryWithResult("Rejected");
        var round = e.AddRound("Technical");
        e.UpdateRound(round.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Passed, null, null);

        AssertOutcome(e, "Passed");
    }

    [Fact]
    public void DefinitiveRoundOutcome_WinsOverWaitingText()
    {
        // 与票04 UpdateBasicInfo 发事件行为一致:轮次已定论 Passed,
        // Result 原文写「等后续轮次」也以枚举为准(Tracker 同样按 Passed 回写)。
        var e = EntryWithResult("等后续轮次");
        var round = e.AddRound("Technical");
        e.UpdateRound(round.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Passed, null, null);

        AssertOutcome(e, "Passed");
    }

    [Fact]
    public void RoundOutcome_WithoutResultText_DerivesFromRound()
    {
        var e = EntryWithResult(null);
        var round = e.AddRound("Technical");
        e.UpdateRound(round.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Rejected, null, null);

        AssertOutcome(e, "Rejected");
    }

    [Fact]
    public void EarlierRoundPassed_CurrentRoundPending_NotCompleted()
    {
        // 只看当前轮(RoundNo):第一轮通过、第二轮未定论 → 整场仍在进行中。
        var e = EntryWithResult(null);
        var r1 = e.AddRound("Technical");
        e.UpdateRound(r1.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Passed, null, null);
        e.AddRound("HR");
        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null, roundNo: 2,
            null, null, null, null, null, null);

        AssertOutcome(e, null);
    }
}
