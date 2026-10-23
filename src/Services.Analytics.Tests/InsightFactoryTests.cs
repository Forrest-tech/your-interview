using FluentAssertions;
using YourInterview.Services.Analytics.Application;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票35(FUN-07):Insight 结构化码 —— 分支优先级与 Params 口径。
/// zh 文案逐字对齐在前端 i18n 字典(insight.* 键),此处只锁后端码与参数。
/// </summary>
public sealed class InsightFactoryTests
{
    private static RadarAxisDto Axis(string dim, int current, int? previous = null, int change = 0)
        => new(dim, "x", current, current, previous, change);
    private static readonly ActivitySummaryDto NoActivity = new(0, 0, null, 0);

    // 常规:无 scored 数据 → NoData,空参数
    [Fact]
    public void NoScoredData_ReturnsNoData_BoundaryEmptyRadar()
    {
        InsightFactory.Build(Array.Empty<RadarAxisDto>(), Array.Empty<PipelineTrendDto>(), NoActivity)
            .Code.Should().Be(InsightCodes.NoData);
        InsightFactory.Build(new[] { Axis("fluency", 0) }, Array.Empty<PipelineTrendDto>(), NoActivity)
            .Params.Should().BeEmpty();
    }

    // boundary(0 值):current=0 不算 scored;恰 -8 分算退步(<= -8 边界),-7 不算
    [Fact]
    public void Regressing_AtMinus8Boundary_ReturnsRegressing()
    {
        var radar = new[] { Axis("fluency", 50, 58, -8), Axis("structure", 90, 90, 0) };
        var dto = InsightFactory.Build(radar, Array.Empty<PipelineTrendDto>(), NoActivity);
        dto.Code.Should().Be(InsightCodes.Regressing);
        dto.Params.Should().Contain("dimension", "fluency")
            .And.Contain("drop", "8").And.Contain("previous", "58").And.Contain("current", "50");
        dto.Severity.Should().Be("warning");
    }

    [Fact]
    public void DropOf7_NotRegressing_FallsToWeakest()
    {
        var radar = new[] { Axis("fluency", 50, 57, -7) };
        InsightFactory.Build(radar, Array.Empty<PipelineTrendDto>(), NoActivity)
            .Code.Should().Be(InsightCodes.Weakest);
    }

    // boundary(上限±1):最弱 64 → Weakest;65 → 不触发 Weakest(落 Stable)
    [Fact]
    public void Weakest_At64_Weakest_At65_Not()
    {
        InsightFactory.Build(new[] { Axis("fluency", 64) }, Array.Empty<PipelineTrendDto>(), NoActivity)
            .Code.Should().Be(InsightCodes.Weakest);
        InsightFactory.Build(new[] { Axis("fluency", 65) }, Array.Empty<PipelineTrendDto>(),
                new ActivitySummaryDto(3, 3, "2026-10-20", 2))
            .Code.Should().Be(InsightCodes.Stable);
    }

    // boundary(漏斗临界):Applied 恰 10 且转化恰 <10 触发;Applied 9 不触发
    [Fact]
    public void FunnelLow_BoundaryApplied10RateBelow10()
    {
        var radar = new[] { Axis("fluency", 80) };
        var pipe = new[] { new PipelineTrendDto("2026-10-20", 0, 10, 0, 0, 0, 0, 5.0) };
        var dto = InsightFactory.Build(radar, pipe, new ActivitySummaryDto(3, 3, "2026-10-20", 2));
        dto.Code.Should().Be(InsightCodes.FunnelLow);
        dto.Params.Should().Contain("applied", "10").And.Contain("interviewing", "0");
        var pipe9 = new[] { new PipelineTrendDto("2026-10-20", 0, 9, 0, 0, 0, 0, 5.0) };
        InsightFactory.Build(radar, pipe9, new ActivitySummaryDto(3, 3, "2026-10-20", 2))
            .Code.Should().Be(InsightCodes.Stable);
    }

    // 常规:有历史但本周没练 → Inactive 带上次活动日期;Stable 带最强/最弱维度键
    [Fact]
    public void Inactive_And_Stable_Params()
    {
        var radar = new[] { Axis("fluency", 80), Axis("structure", 90) };
        InsightFactory.Build(radar, Array.Empty<PipelineTrendDto>(),
                new ActivitySummaryDto(4, 9, "2026-10-10", 0))
            .Params.Should().Contain("lastActivityDate", "2026-10-10");
        var stable = InsightFactory.Build(radar, Array.Empty<PipelineTrendDto>(),
            new ActivitySummaryDto(4, 9, "2026-10-10", 1));
        stable.Code.Should().Be(InsightCodes.Stable);
        stable.Params.Should().Contain("strongestDimension", "structure")
            .And.Contain("weakestDimension", "fluency");
    }

    // boundary(null):previous 为 null 时退步分支 previous 参数回 0,不抛
    [Fact]
    public void Regressing_NullPrevious_ParamsPreviousZero_BoundaryNull()
    {
        var radar = new[] { Axis("fluency", 40, null, -9) };
        InsightFactory.Build(radar, Array.Empty<PipelineTrendDto>(), NoActivity)
            .Params.Should().Contain("previous", "0");
    }
}
