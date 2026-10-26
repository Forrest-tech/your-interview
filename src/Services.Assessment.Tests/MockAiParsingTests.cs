using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>票95 §7:MockAiParsing 纯逻辑 boundary 全谱(无 DB)。</summary>
public sealed class MockAiParsingTests
{
    // ---------- 生成:空/坏 ----------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("[{}]")]
    [InlineData("[{\"questionText\":\"  \"}]")]
    public void ParseQuestions_EmptyOrBad_ReturnsEmpty(string? raw)
        => Assert.Empty(MockAiParsing.ParseQuestions(raw, 5));

    [Fact]
    public void ParseQuestions_ValidArray_Parses()
    {
        var r = MockAiParsing.ParseQuestions(
            "[{\"questionText\":\"Q1\",\"type\":\"SystemDesign\",\"difficulty\":4,\"expectedPoints\":[\"a\",\"b\"]}," +
            "{\"questionText\":\"Q2\",\"type\":\"Technical\",\"difficulty\":2}]", 5);
        Assert.Equal(2, r.Count);
        Assert.Equal("Q1", r[0].QuestionText);
        Assert.Equal("SystemDesign", r[0].Type);
        Assert.Equal(4, r[0].Difficulty);
        Assert.Contains("\"a\"", r[0].ExpectedPointsJson);
    }

    [Fact]
    public void ParseQuestions_FencedAndWrapper_Accepted()
    {
        var r = MockAiParsing.ParseQuestions(
            "Here you go:\n```json\n{\"questions\":[{\"questionText\":\"Q\",\"type\":\"Coding\",\"difficulty\":3}]}\n```", 5);
        Assert.Single(r);
    }

    [Fact]
    public void ParseQuestions_OverLimit_TruncatedToMax()
    {
        var json = "[" + string.Join(",", Enumerable.Range(1, 8)
            .Select(i => $"{{\"questionText\":\"Q{i}\",\"type\":\"Technical\",\"difficulty\":3}}")) + "]";
        Assert.Equal(3, MockAiParsing.ParseQuestions(json, 3).Count);
    }

    [Fact]
    public void ParseQuestions_DifficultyClamped_TypeFallback()
    {
        var r = MockAiParsing.ParseQuestions(
            "[{\"questionText\":\"A\",\"type\":\"Bogus\",\"difficulty\":99}," +
            "{\"questionText\":\"B\",\"type\":\"technical\",\"difficulty\":0}]", 5);
        Assert.Equal("Technical", r[0].Type);
        Assert.Equal(5, r[0].Difficulty);
        Assert.Equal("Technical", r[1].Type);
        Assert.Equal(1, r[1].Difficulty);
    }

    [Fact]
    public void ParseQuestions_UnicodePreserved_OverlongTruncated()
    {
        var longText = "题目🎯" + new string('x', 5000);
        var r = MockAiParsing.ParseQuestions(
            $"[{{\"questionText\":\"{longText}\",\"type\":\"Technical\",\"difficulty\":3}}]", 5);
        Assert.Single(r);
        Assert.Equal(MockAiParsing.MaxTextLength, r[0].QuestionText.Length);
        Assert.StartsWith("题目🎯", r[0].QuestionText);
    }

    // ---------- 评分 ----------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("{\"comment\":\"no scores\"}")]
    public void ParseScore_BadOrAllMissing_ReturnsNull(string? raw)
        => Assert.Null(MockAiParsing.ParseScore(raw));

    [Fact]
    public void ParseScore_Full_Parses()
    {
        var s = MockAiParsing.ParseScore(
            "{\"pronunciation\":80,\"fluency\":70,\"sentenceIntegrity\":60,\"structure\":50," +
            "\"technicalDepth\":90,\"relevance\":85,\"comment\":\"ok\",\"recommendedAnswer\":\"rec\"," +
            "\"betterStructure\":\"st\",\"issues\":[{\"dimension\":\"structure\",\"title\":\"No skeleton\",\"severity\":4}]}");
        Assert.NotNull(s);
        Assert.Equal(80, s!.Pronunciation);
        Assert.Equal(90, s.TechnicalDepth);
        Assert.Single(s.Issues);
        Assert.Equal(4, s.Issues[0].Severity);
    }

    [Fact]
    public void ParseScore_OutOfRange_Clamped()
    {
        var s = MockAiParsing.ParseScore(
            "{\"pronunciation\":150,\"fluency\":-20,\"sentenceIntegrity\":100,\"structure\":0,\"technicalDepth\":50,\"relevance\":50}");
        Assert.Equal(100, s!.Pronunciation);
        Assert.Equal(0, s.Fluency);
    }

    [Fact]
    public void ParseScore_MissingDims_FilledWithMeanOfGiven()
    {
        // given: 80, 60 → mean 70 fills the other four
        var s = MockAiParsing.ParseScore("{\"pronunciation\":80,\"fluency\":60}");
        Assert.Equal(70, s!.Structure);
        Assert.Equal(70, s.TechnicalDepth);
        Assert.Equal(70, s.Relevance);
    }

    [Fact]
    public void ParseScore_Issues_DropEmpty_ClampSeverity_Truncate()
    {
        var s = MockAiParsing.ParseScore(
            "{\"technicalDepth\":50,\"issues\":[" +
            "{\"dimension\":\"\",\"title\":\"x\",\"severity\":3}," +
            "{\"dimension\":\"structure\",\"title\":\"\",\"severity\":3}," +
            "{\"dimension\":\"structure\",\"title\":\"t\",\"severity\":99}," +
            "{\"dimension\":\"fluency\",\"title\":\"u\",\"severity\":0}]}");
        Assert.Equal(2, s!.Issues.Count);
        Assert.Equal(5, s.Issues[0].Severity);
        Assert.Equal(1, s.Issues[1].Severity);
    }

    [Fact]
    public void ParseScore_UnicodeAndOverlong_TruncatedNotFailed()
    {
        var comment = "评语🎯" + new string('y', 6000);
        var s = MockAiParsing.ParseScore($"{{\"relevance\":42,\"comment\":\"{comment}\"}}");
        Assert.NotNull(s);
        Assert.Equal(MockAiParsing.MaxTextLength, s!.Comment!.Length);
        Assert.StartsWith("评语🎯", s.Comment);
    }
}
