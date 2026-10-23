using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Analytics.Application;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票36(FUN-08)§7:趋势聚合纯逻辑 + Handler 窗口/归属过滤。
///
/// 口径(与 GetDashboardQueryHandler 既有 trends 对齐,AbilityTrendAggregation):
/// 按 (维度, 日) 分组每日取 Max;窗口 = UTC 结束日往前 days 个日历日含首尾;
/// 缺日不补造(无快照的日不产点);series 只含窗口内有点的维度,六维 canonical 顺序。
/// boundary 类别:空集 / 单日单点 / 跨月(含闰年) / 缺日口径 / 时区日界(窗口首尾日) /
/// days 下限 1 与校验 7–365 / 同日多条 Max / 未知维度 / 他人数据。
/// </summary>
public sealed class AbilityTrendAggregationTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly DateOnly End = new(2026, 3, 2); // 窗口 30 天 → 首日 2026-02-01(跨月)

    private static AbilitySnapshot Snap(DateOnly date, string dim, int score, string source = "mock")
        => new(User, date, dim, score, source);

    // ---------- boundary: 空集 ----------
    [Fact]
    public void EmptySet_ReturnsEmptySeries_BoundaryEmpty()
    {
        AbilityTrendAggregation.Build(Array.Empty<AbilitySnapshot>(), End, 30)
            .Should().BeEmpty();
    }

    [Fact]
    public void NullFree_OnlyOtherDimensions_ReturnsEmptyForMissingDims_BoundaryEmpty()
    {
        var series = AbilityTrendAggregation.Build(
            new[] { Snap(End, "fluency", 70) }, End, 30);
        series.Should().ContainSingle().Which.Dimension.Should().Be("fluency");
    }

    // ---------- boundary: 单日单点 ----------
    [Fact]
    public void SingleDaySinglePoint_LatestEqualsEarliestDeltaZero_BoundarySingle()
    {
        var series = AbilityTrendAggregation.Build(new[] { Snap(End, "structure", 55) }, End, 30);
        var s = series.Should().ContainSingle().Subject;
        s.Points.Should().ContainSingle().Which.Should().Be(new TrendPointDto("2026-03-02", 55));
        s.Latest.Should().Be(55);
        s.Earliest.Should().Be(55);
        s.Delta.Should().Be(0);
        s.NameZh.Should().Be("结构");
    }

    // ---------- 同日多条:每日 Max(与 Dashboard 口径一致) ----------
    [Fact]
    public void SameDayMultipleSources_TakesDailyMax()
    {
        var series = AbilityTrendAggregation.Build(new[]
        {
            Snap(End, "fluency", 40, "mock"),
            Snap(End, "fluency", 75, "interview"),
            Snap(End, "fluency", 60, "knowledge"),
        }, End, 30);
        series.Should().ContainSingle().Which.Points.Should().ContainSingle()
            .Which.Value.Should().Be(75);
    }

    // ---------- boundary: 缺日口径 —— 不补造,delta 取首末实际点 ----------
    [Fact]
    public void MissingDays_NotFabricated_DeltaFromActualPoints_BoundaryGap()
    {
        var series = AbilityTrendAggregation.Build(new[]
        {
            Snap(new DateOnly(2026, 2, 10), "relevance", 50),
            // 2/11 – 2/27 无快照:不得产点
            Snap(new DateOnly(2026, 2, 28), "relevance", 80),
        }, End, 30);
        var s = series.Should().ContainSingle().Subject;
        s.Points.Select(p => p.Date).Should().Equal("2026-02-10", "2026-02-28");
        s.Delta.Should().Be(30);
    }

    // ---------- boundary: 跨月窗口 + 时区日界(首尾日含、界外不含) ----------
    [Fact]
    public void WindowStart_CrossMonth_BoundaryDates()
    {
        AbilityTrendAggregation.WindowStart(new DateOnly(2026, 3, 2), 30)
            .Should().Be(new DateOnly(2026, 2, 1));
        // 闰年 2 月 29 天:2024-03-01 往前 30 天首日仍是 02-01
        AbilityTrendAggregation.WindowStart(new DateOnly(2024, 3, 1), 30)
            .Should().Be(new DateOnly(2024, 2, 1));
        // days=1:窗口只有结束日当天
        AbilityTrendAggregation.WindowStart(End, 1).Should().Be(End);
        AbilityTrendAggregation.WindowStart(End, 7).Should().Be(new DateOnly(2026, 2, 24));
    }

    [Fact]
    public void BoundaryDays_FirstAndLastIncluded_OutsideExcluded_BoundaryTimezoneEdge()
    {
        var series = AbilityTrendAggregation.Build(new[]
        {
            Snap(new DateOnly(2026, 1, 31), "fluency", 11), // 首日前一天 → 排除
            Snap(new DateOnly(2026, 2, 1), "fluency", 22),  // 首日 → 含
            Snap(End, "fluency", 33),                        // 结束日(UTC 今日) → 含
            Snap(new DateOnly(2026, 3, 3), "fluency", 44),  // 结束日后(未来) → 排除
        }, End, 30);
        var s = series.Should().ContainSingle().Subject;
        s.Points.Select(p => p.Value).Should().Equal(22, 33);
    }

    // ---------- 顺序:输入乱序 → 点升序;维度 canonical 顺序 ----------
    [Fact]
    public void UnsortedInput_PointsAscending_DimensionsCanonicalOrder()
    {
        var series = AbilityTrendAggregation.Build(new[]
        {
            Snap(new DateOnly(2026, 2, 20), "relevance", 90),
            Snap(new DateOnly(2026, 2, 5), "pronunciation", 40),
            Snap(new DateOnly(2026, 2, 3), "relevance", 60),
            Snap(new DateOnly(2026, 2, 12), "pronunciation", 45),
        }, End, 30);
        series.Select(s => s.Dimension).Should().Equal("pronunciation", "relevance");
        series[0].Points.Select(p => p.Date).Should().Equal("2026-02-05", "2026-02-12");
        series[1].Delta.Should().Be(30);
    }

    // ---------- boundary: 未知维度不进 series ----------
    [Fact]
    public void UnknownDimension_Ignored_BoundaryUnknownKey()
    {
        AbilityTrendAggregation.Build(new[] { Snap(End, "not-a-dimension", 88) }, End, 30)
            .Should().BeEmpty();
    }

    // ---------- 校验:days 7–365(与 Dashboard TrendDays 同口径) ----------
    [Theory]
    [InlineData(7, true)]
    [InlineData(30, true)]
    [InlineData(365, true)]
    [InlineData(6, false)]
    [InlineData(366, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Validator_DaysRange_7To365_BoundaryLimits(int days, bool valid)
    {
        var v = new GetAbilityTrendQueryValidator();
        v.Validate(new GetAbilityTrendQuery(User, days)).IsValid.Should().Be(valid);
    }

    // ---------- Handler:窗口 + 用户隔离(InMemory) ----------
    private static AnalyticsDbContext NewDb() => new(
        new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Handler_FiltersByUserAndWindow_ReturnsTrendDto()
    {
        using var db = NewDb();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var other = Guid.NewGuid();
        db.AbilitySnapshots.AddRange(
            new AbilitySnapshot(User, today.AddDays(-40), "fluency", 10, "mock"), // 窗外
            new AbilitySnapshot(User, today.AddDays(-5), "fluency", 50, "mock"),
            new AbilitySnapshot(User, today, "fluency", 70, "mock"),
            new AbilitySnapshot(other, today, "fluency", 99, "mock"));            // 他人
        await db.SaveChangesAsync();

        var handler = new GetAbilityTrendQueryHandler(db);
        var result = await handler.Handle(new GetAbilityTrendQuery(User, 30), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Days.Should().Be(30);
        var s = result.Value.Series.Should().ContainSingle().Subject;
        s.Dimension.Should().Be("fluency");
        s.Points.Select(p => p.Value).Should().Equal(50, 70);
        s.Delta.Should().Be(20);
    }

    [Fact]
    public async Task Handler_NoSnapshots_ReturnsEmptySeriesNotError_BoundaryEmpty()
    {
        using var db = NewDb();
        var handler = new GetAbilityTrendQueryHandler(db);
        var result = await handler.Handle(new GetAbilityTrendQuery(User, 30), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value.Series.Should().BeEmpty();
    }
}
