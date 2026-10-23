using FluentAssertions;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票65(ARC-08d):Assessment 计分纯逻辑 boundary 补强(特征测试锁现状)。
/// DimensionScores 钳位极值/加权权重单维验证/最弱并列取序、DimensionIssue
/// 守卫与 Severity 钳位、ParseScore 残余两行(坏类型 JsonException 回落、
/// FillerWords 字符串/非字符串/显式 null 三态)。与 MockAiParsingTests 互补。
/// </summary>
public sealed class ScoringPureLogicBoundaryTests
{
    // ---------- DimensionScores ----------

    [Fact] // 钳位 boundary:负数→0、>100→100、恰 0/100 原样
    public void DimensionScores_ClampsExtremes()
    {
        var s = new DimensionScores(-5, 250, 0, 100, 101, -1);
        s.Pronunciation.Should().Be(0);
        s.Fluency.Should().Be(100);
        s.SentenceIntegrity.Should().Be(0);
        s.Structure.Should().Be(100);
        s.TechnicalDepth.Should().Be(100);
        s.Relevance.Should().Be(0);
    }

    [Theory] // Overall 权重逐维:单维 100 其余 0 → 总分恰为权重×100(取整)
    [InlineData(100, 0, 0, 0, 0, 0, 8)]    // 发音 0.08
    [InlineData(0, 100, 0, 0, 0, 0, 14)]   // 流利 0.14
    [InlineData(0, 0, 100, 0, 0, 0, 10)]   // 语句 0.10
    [InlineData(0, 0, 0, 100, 0, 0, 22)]   // 结构 0.22
    [InlineData(0, 0, 0, 0, 100, 0, 28)]   // 技术深度 0.28
    [InlineData(0, 0, 0, 0, 0, 100, 18)]   // 相关性 0.18
    public void DimensionScores_OverallWeights(int p, int f, int si, int st, int td, int r,
        int expected)
        => new DimensionScores(p, f, si, st, td, r).Overall.Should().Be(expected);

    [Fact] // 最弱并列:全等取数组首位「发音」;流利/结构并列取靠前「流利度」(OrderBy 稳定序,现状锁定)
    public void WeakestDimension_TiesTakeArrayOrder()
    {
        new DimensionScores(50, 50, 50, 50, 50, 50).WeakestDimension().Should().Be("发音");
        new DimensionScores(90, 10, 90, 10, 90, 90).WeakestDimension().Should().Be("流利度");
    }

    // ---------- DimensionIssue ----------

    [Theory] // Severity 钳位 1..5 boundary
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    [InlineData(99, 5)]
    public void DimensionIssue_SeverityClamped(int severity, int expected)
        => new DimensionIssue("结构", "标题", null, null, null, severity).Severity
            .Should().Be(expected);

    [Theory] // 守卫:dimension/title 空白抛 ArgumentException
    [InlineData("", "标题")]
    [InlineData("  ", "标题")]
    [InlineData("结构", "")]
    [InlineData("结构", "   ")]
    public void DimensionIssue_BlankDimensionOrTitle_Throws(string dimension, string title)
    {
        var act = () => new DimensionIssue(dimension, title, null, null, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact] // dimension/title 入库前 Trim;其余字段不 Trim(现状锁定)
    public void DimensionIssue_TrimsDimensionAndTitleOnly()
    {
        var i = new DimensionIssue(" 结构 ", " 标题 ", "  detail  ", null, null);
        i.Dimension.Should().Be("结构");
        i.Title.Should().Be("标题");
        i.Detail.Should().Be("  detail  ");
    }

    // ---------- ParseScore 残余口径 ----------

    [Fact] // 坏类型(分数给字符串)→ JsonException 回落 null(不抛给调用方)
    public void ParseScore_WrongValueType_ReturnsNull()
        => MockAiParsing.ParseScore("{\"pronunciation\":\"abc\"}").Should().BeNull();

    [Fact] // FillerWords 字符串 → 原文;非字符串(数组)→ RawText;显式 null → null
    public void ParseScore_FillerWords_ThreeForms()
    {
        MockAiParsing.ParseScore("{\"fluency\":70,\"fillerWords\":\"um, uh\"}")
            !.FillerWordsJson.Should().Be("um, uh");
        MockAiParsing.ParseScore("{\"fluency\":70,\"fillerWords\":[\"um\",\"uh\"]}")
            !.FillerWordsJson.Should().Be("[\"um\",\"uh\"]");
        MockAiParsing.ParseScore("{\"fluency\":70,\"fillerWords\":42}")
            !.FillerWordsJson.Should().Be("42");
        MockAiParsing.ParseScore("{\"fluency\":70,\"fillerWords\":null}")
            !.FillerWordsJson.Should().BeNull();
    }

    [Fact] // 缺维补齐的均值取整:given 80/61 → 均值 70.5 → Round 70(银行家舍入,现状锁定)
    public void ParseScore_FillMean_RoundsBankers()
    {
        var s = MockAiParsing.ParseScore("{\"pronunciation\":80,\"fluency\":61}");
        s!.Structure.Should().Be(70);
    }
}
