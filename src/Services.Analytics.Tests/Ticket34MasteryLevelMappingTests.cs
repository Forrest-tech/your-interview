using FluentAssertions;
using YourInterview.Services.Analytics.Application;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票34 §7:MasteryLevelMapping 纯逻辑边界测试。
/// 用例名后缀标注 boundary 类别:EmptyNull(空/null)、UnknownFallback(无法识别回落)、
/// NegativeOutOfRange(0/负数/超界)、CaseWhitespace(大小写/空白)、EmptyCollection(空集)。
/// </summary>
public sealed class Ticket34MasteryLevelMappingTests
{
    [Theory]
    [InlineData("New", MasteryBucket.Fresh, 0)]
    [InlineData("Learning", MasteryBucket.Learning, 40)]
    [InlineData("Familiar", MasteryBucket.Learning, 65)]
    [InlineData("Mastered", MasteryBucket.Mastered, 80)]
    [InlineData("NeedsReview", MasteryBucket.Learning, 20)]
    public void Classify_AllMasteryNames_MapToBucketAndScore(string mastery,
        MasteryBucket bucket, int score)
    {
        MasteryLevelMapping.Classify(mastery, level: -999).Should().Be((bucket, score),
            "Mastery 字符串自描述,优先于 Level int");
    }

    [Theory]
    [InlineData(0, MasteryBucket.Fresh, 0)]
    [InlineData(1, MasteryBucket.Learning, 40)]
    [InlineData(2, MasteryBucket.Learning, 65)]
    [InlineData(3, MasteryBucket.Mastered, 80)]
    [InlineData(4, MasteryBucket.Learning, 20)]
    public void Classify_LevelEnumFallback_WhenNameMissing_BoundaryEmptyNull(
        int level, MasteryBucket bucket, int score)
    {
        MasteryLevelMapping.Classify(null, level).Should().Be((bucket, score));
        MasteryLevelMapping.Classify("", level).Should().Be((bucket, score));
        MasteryLevelMapping.Classify("   ", level).Should().Be((bucket, score));
    }

    [Theory]
    [InlineData(" mastered ", MasteryBucket.Mastered, 80)]
    [InlineData("MASTERED", MasteryBucket.Mastered, 80)]
    [InlineData("needsreview", MasteryBucket.Learning, 20)]
    [InlineData("NeedsReview", MasteryBucket.Learning, 20)]
    public void Classify_CaseAndWhitespaceInsensitive_BoundaryCaseWhitespace(
        string mastery, MasteryBucket bucket, int score)
    {
        MasteryLevelMapping.Classify(mastery, level: -999).Should().Be((bucket, score));
    }

    [Fact]
    public void Classify_UnknownName_FallsBackToLevel_BoundaryUnknownFallback()
    {
        // 特殊字符/未知等级名不得抛错:回落 Level int 口径
        MasteryLevelMapping.Classify("未知等级🎓", 3).Should().Be((MasteryBucket.Mastered, 80));
        MasteryLevelMapping.Classify("SuperMaster", 1).Should().Be((MasteryBucket.Learning, 40));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void Classify_OutOfRangeLevel_FreshZero_BoundaryNegativeOutOfRange(int level)
    {
        MasteryLevelMapping.Classify(null, level).Should().Be((MasteryBucket.Fresh, 0));
        MasteryLevelMapping.Classify("???", level).Should().Be((MasteryBucket.Fresh, 0));
    }

    [Fact]
    public void ClassifyName_NullOrUnknown_FreshZero_BoundaryEmptyNull()
    {
        MasteryLevelMapping.ClassifyName(null).Should().Be((MasteryBucket.Fresh, 0));
        MasteryLevelMapping.ClassifyName("").Should().Be((MasteryBucket.Fresh, 0));
        MasteryLevelMapping.ClassifyName("n/a").Should().Be((MasteryBucket.Fresh, 0));
    }

    [Fact]
    public void AverageScore_EmptyCollection_ReturnsNull_BoundaryEmptyCollection()
    {
        MasteryLevelMapping.AverageScore([]).Should().BeNull();
        MasteryLevelMapping.AverageScore([null, "???"]).Should().Be(0,
            "不可识别等级按 0 分计入均值,不抛错");
    }

    [Fact]
    public void AverageScore_MixedLevels_AveragesMappedScores()
    {
        // Mastered 80 + Learning 40 + New 0 = 40
        MasteryLevelMapping.AverageScore(["Mastered", "Learning", "New"]).Should().Be(40);
        MasteryLevelMapping.AverageScore(["Mastered"]).Should().Be(80);
    }
}
