using System.Text.Json;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>A4 归一化匹配 + A3 快照构造:纯静态方法测试。</summary>
public sealed class PlaybookTextTests
{
    // ---------- A4:NormalizeQuestionText ----------

    [Theory]
    [InlineData("What is a deadlock?", "whatisadeadlock")]
    [InlineData("  WHAT   is a Deadlock?? ", "whatisadeadlock")]
    [InlineData("什么是死锁?", "什么是死锁")]
    [InlineData("Explain C# async/await.", "explaincasyncawait")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void NormalizeQuestionText_Cases(string? input, string expected)
    {
        PlaybookText.NormalizeQuestionText(input).Should().Be(expected);
    }

    [Fact]
    public void NormalizeQuestionText_SameQuestionDifferentPhrasing_Matches()
    {
        var a = PlaybookText.NormalizeQuestionText("What is a deadlock?");
        var b = PlaybookText.NormalizeQuestionText("  what IS a  deadlock ???");

        a.Should().Be(b);
    }

    [Fact]
    public void NormalizeQuestionText_DifferentQuestions_DoNotMatch()
    {
        var a = PlaybookText.NormalizeQuestionText("What is a deadlock?");
        var b = PlaybookText.NormalizeQuestionText("What is a livelock?");

        a.Should().NotBe(b);
    }

    // ---------- A3:SummaryToBulletsJson ----------

    [Fact]
    public void SummaryToBulletsJson_NullSummary_ReturnsNull()
    {
        PlaybookText.SummaryToBulletsJson(null).Should().BeNull();
        PlaybookText.SummaryToBulletsJson("  ").Should().BeNull();
    }

    [Fact]
    public void SummaryToBulletsJson_ChineseSentences_Splits()
    {
        var summary = "文本模式(无音频),共 1234 词,发音未测量。语速 150 wpm,平均句长 12.3 词。最弱维度:Structure。";

        var json = PlaybookText.SummaryToBulletsJson(summary);
        var bullets = JsonSerializer.Deserialize<List<string>>(json!);

        bullets.Should().HaveCount(3);
        bullets![0].Should().Contain("发音未测量");
        bullets[1].Should().Contain("12.3", "小数点后无空格,不应被误切");
    }

    [Fact]
    public void SummaryToBulletsJson_EnglishSentences_Splits()
    {
        var summary = "Audio 12.5 minutes, 1500 words. Filler words 8 times. Weakest: Structure.";

        var bullets = JsonSerializer.Deserialize<List<string>>(
            PlaybookText.SummaryToBulletsJson(summary)!);

        bullets.Should().HaveCount(3);
        bullets![0].Should().Contain("12.5", "小数点不应被误切");
    }

    [Fact]
    public void SummaryToBulletsJson_LongSummary_CapsAtFive()
    {
        var summary = string.Join("。", Enumerable.Range(1, 8).Select(i => $"结论{i}")) + "。";

        var bullets = JsonSerializer.Deserialize<List<string>>(
            PlaybookText.SummaryToBulletsJson(summary)!);

        bullets.Should().HaveCount(5);
    }

    // ---------- A3:BuildDimensionsJson ----------

    [Fact]
    public void BuildDimensionsJson_TextMode_PronunciationNull()
    {
        var json = PlaybookText.BuildDimensionsJson(78, null, 80, 70, 75, 82);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("overall").GetInt32().Should().Be(78);
        doc.RootElement.GetProperty("pronunciation").ValueKind
            .Should().Be(JsonValueKind.Null, "文本模式发音未测量");
        doc.RootElement.GetProperty("fluency").GetInt32().Should().Be(80);
        doc.RootElement.GetProperty("structure").GetInt32().Should().Be(70);
        doc.RootElement.GetProperty("technicalDepth").GetInt32().Should().Be(75);
        doc.RootElement.GetProperty("relevance").GetInt32().Should().Be(82);
    }

    // ---------- A3:AddAnalysisSnapshot(聚合根) ----------

    [Fact]
    public void AddAnalysisSnapshot_AppendsToEntry()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Corpay", "Senior .NET Developer");

        var s = e.AddAnalysisSnapshot(78,
            PlaybookText.BuildDimensionsJson(78, null, 80, 70, 75, 82),
            PlaybookText.SummaryToBulletsJson("第一句。第二句。"));

        s.InterviewEntryId.Should().Be(e.Id);
        s.OverallScore.Should().Be(78);
        s.DimensionsJson.Should().Contain("pronunciation");
        s.SummaryBulletsJson.Should().Contain("第一句");
        s.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        e.Snapshots.Should().ContainSingle().Which.Id.Should().Be(s.Id);
    }
}
