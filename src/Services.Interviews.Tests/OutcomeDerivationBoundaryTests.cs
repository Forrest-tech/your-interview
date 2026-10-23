using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票65(ARC-08d):DeriveOutcome boundary 补强 —— 特征测试锁现状口径。
/// 覆盖类别:受控词全表/大小写/前后空白、数字串防误收(规则 2 名字校验)、
/// 优先级冲突(未落定>拒向>通过向>中性终局)、超长文本、Unicode/全角/emoji、
/// \bpass 词边界现状(含 password/passion 前缀命中这一既有行为,见文末注记)。
/// 纯映射测试,不依赖 DB。与 EntryOutcomeDerivationTests(对照矩阵)互补不重叠。
/// </summary>
public sealed class OutcomeDerivationBoundaryTests
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

    private static void AssertOutcome(string? result, string? expected)
        => InterviewMappingExtensions.DeriveOutcome(EntryWithResult(result))?.ToString()
            .Should().Be(expected);

    // ---------- 受控词表全表 + 形态 boundary(规则 2) ----------

    [Theory]
    [InlineData("NoShow", "NoShow")]          // 受控词表最后一员(既有矩阵未覆盖)
    [InlineData("noshow", "NoShow")]          // 忽略大小写精确解析
    [InlineData("GHOSTED", "Ghosted")]
    [InlineData(" cancelled ", "Cancelled")] // 前后空白:派生前 Trim
    [InlineData("  Passed\t", "Passed")]
    public void ControlledVocabulary_Forms(string result, string? expected)
        => AssertOutcome(result, expected);

    [Theory]
    [InlineData("1")]   // 数字串:Enum.TryParse 会收,但名字对不上(1→Passed) → 不许当结构化值
    [InlineData("0")]
    [InlineData("5")]
    public void NumericStrings_InRange_AreNotControlledWords(string result)
        => AssertOutcome(result, null);

    [Fact] // ⚠ 缺陷候选 D1(只登记不修,见 evidence-65/CLOSE-REPORT):
           // 越界数字串 "-1" 被 TryParse 收为未定义枚举值,其 ToString() 恰为 "-1",
           // 与原文相等 → 绕过规则 2 的名字校验,DTO Outcome 会输出 "-1"。
           // 本例锁**现状**(特征测试),修复须另立票后同步改此期望。
    public void OutOfRangeNumericString_CurrentlySlipsThroughAsUndefinedEnum()
        => AssertOutcome("-1", "-1");

    [Fact] // 逗号组合:TryParse 按位或合并(Passed|Rejected=Ghosted),名字对不上被挡;
           // 随后关键词回落命中 reject → Rejected(现状锁定)。
    public void CompositeString_FallsToKeywordFallback()
        => AssertOutcome("Passed,Rejected", "Rejected");

    // ---------- 优先级冲突(规则 3 顺序即优先级) ----------

    [Theory]
    [InlineData("等待中,感觉要被拒", "Pending")]   // 未落定 > 拒向
    [InlineData("未通过,但给了口头 offer", "Rejected")] // 拒向 > 通过向(未通过含「通过」)
    [InlineData("通过后对方失联", "Passed")]        // 通过向 > 中性终局(ghost)
    [InlineData("失联后流程取消", "Ghosted")]       // ghost > cancel 组
    [InlineData("in progress, likely rejected", "Pending")]
    public void PriorityConflicts_FollowRuleOrder(string result, string? expected)
        => AssertOutcome(result, expected);

    // ---------- 关键词 boundary ----------

    [Theory]
    [InlineData("对方失联了", "Ghosted")]        // 规则 3 ghost 组(改前仅测受控词 Ghosted)
    [InlineData("no-show 了一次", null)]         // 自由文本无 no-show 关键词 → 不落定(现状锁定)
    [InlineData("流程已结束", "Cancelled")]
    [InlineData("HR 说 done deal", "Cancelled")] // done ∈ cancel 组(现状锁定:哪怕语义偏正面)
    [InlineData("I passed the interview", "Passed")] // \bpass 命中 passed
    [InlineData("需要 reset password 才能登录", "Passed")] // ⚠ 现状:\bpass 无尾界,password 前缀亦命中(特征锁定;已登记 CLOSE-REPORT 缺陷候选)
    [InlineData("passion project 分享", "Passed")]     // ⚠ 同上 passion 前缀命中(现状锁定)
    [InlineData("compass 方向", null)]           // pass 非词首 → \b 不命中
    public void KeywordBoundaries(string result, string? expected)
        => AssertOutcome(result, expected);

    // ---------- 空/超长/Unicode boundary ----------

    [Fact]
    public void WhitespaceOnlyResult_NoOutcome()
        => AssertOutcome("   ", null);

    [Fact]
    public void VeryLongText_KeywordAtEnd_StillDerives()
        => AssertOutcome(new string('x', 10_000) + " 通过", "Passed");

    [Fact]
    public void VeryLongText_NoKeyword_NoOutcome()
        => AssertOutcome(new string('x', 10_000), null);

    [Theory]
    [InlineData("🎉 通过了 🎉", "Passed")]       // emoji 包裹不影响中文关键词
    [InlineData("Ｐａｓｓｅｄ", null)]           // 全角拉丁 ≠ 受控词/关键词(现状锁定)
    [InlineData("Оффер", null)]               // 西里尔同形字不命中 offer
    public void UnicodeForms(string result, string? expected)
        => AssertOutcome(result, expected);

    // ---------- 轮次与文本的衔接 boundary ----------

    [Fact]
    public void CurrentRoundPending_ResultTextStillDerives()
    {
        // 当前轮存在但仍 Pending → 规则 1 不触发,继续走文本派生。
        var e = EntryWithResult("拿到 offer");
        e.AddRound("Technical");
        InterviewMappingExtensions.DeriveOutcome(e)?.ToString().Should().Be("Passed");
    }

    [Fact]
    public void DefinitiveRoundForOtherRoundNo_DoesNotWin()
    {
        // 只有第 1 轮且已 Passed,但条目当前轮 RoundNo=3(无对应轮次) → 规则 1 不触发。
        var e = EntryWithResult(null);
        var r1 = e.AddRound("Technical");
        e.UpdateRound(r1.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.Passed, null, null);
        e.UpdateBasicInfo("Acme", "Senior .NET Developer", null, null, null, roundNo: 3,
            null, null, null, null, null, null);
        InterviewMappingExtensions.DeriveOutcome(e).Should().BeNull();
    }

    [Fact]
    public void RoundOutcomeNoShow_DerivesFromRound()
    {
        var e = EntryWithResult(null);
        var r = e.AddRound("Technical");
        e.UpdateRound(r.Id, "Technical", null, null, null, null,
            InterviewRoundOutcome.NoShow, null, null);
        InterviewMappingExtensions.DeriveOutcome(e)?.ToString().Should().Be("NoShow");
    }
}
