using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;

namespace YourInterview.Services.Analytics.Application;

// =====================================================================================
//  FUN-08(票36):能力趋势接口 —— GET /api/analytics/ability/trend
//
//  此前前端恒调用此端点而后端无路由(analytics-spec CONF-AN-1),趋势区块恒 404 后
//  降级为开发者文案。本文件补齐后端本体:数据源 = 本服务 AbilitySnapshots 历史
//  (CQRS 读模型,不回查业务真源,与 GetDashboardQueryHandler 读口径一致)。
// =====================================================================================

/// <summary>趋势接口响应 —— 形状与前端 AbilityTrend 模型(days + series)对齐。</summary>
public sealed record AbilityTrendDto(int Days, IReadOnlyList<DimensionTrendDto> Series);

/// <summary>
/// 趋势聚合纯逻辑(单测直接覆盖,含 boundary)。
///
/// 口径(与 GetDashboardQueryHandler 既有 trends 逐字对齐,不另造一套):
///  - 按 (Dimension, Date) 分组,每日取 Max(Score)(同日多来源/多条只留当日最高)。
///  - 窗口 = 结束日往前 days 个日历日,含首尾;结束日由调用方按 UTC 今日给出(时区日界)。
///  - 缺日不补造:无快照的日不产点(REQ-08 无数据不造假信号;Dashboard trends 现状同)。
///  - series 只含窗口内有点的维度(按六维 canonical 顺序);全空 → 空 series。
///  - DimensionTrendDto 的 Latest/Earliest/Delta 与 Dashboard 同定义:
///    Delta = 点数 > 1 时末点 − 首点,否则 0。
/// </summary>
public static class AbilityTrendAggregation
{
    /// <summary>六维 canonical 顺序与中文名(与 GetDashboardQueryHandler.DimensionNames 一致)。</summary>
    public static readonly IReadOnlyList<(string Dimension, string NameZh)> Dimensions = new[]
    {
        ("pronunciation", "发音"),
        ("fluency", "流利度"),
        ("sentenceIntegrity", "语句完整性"),
        ("structure", "结构"),
        ("technicalDepth", "技术深度"),
        ("relevance", "相关性")
    };

    /// <summary>窗口首日:结束日往前 days 个日历日(含首尾),即 end − (days−1)。days &lt; 1 按 1 处理。</summary>
    public static DateOnly WindowStart(DateOnly endDate, int days)
        => endDate.AddDays(-(Math.Max(days, 1) - 1));

    public static IReadOnlyList<DimensionTrendDto> Build(
        IEnumerable<AbilitySnapshot> snapshots, DateOnly endDate, int days)
    {
        var start = WindowStart(endDate, days);
        var inWindow = snapshots
            .Where(s => s.Date >= start && s.Date <= endDate)
            .ToList();

        var series = new List<DimensionTrendDto>();
        foreach (var (dimension, nameZh) in Dimensions)
        {
            var points = inWindow
                .Where(s => s.Dimension == dimension)
                .GroupBy(s => s.Date)
                .OrderBy(g => g.Key)
                .Select(g => new TrendPointDto(g.Key.ToString("yyyy-MM-dd"), g.Max(x => x.Score)))
                .ToList();

            if (points.Count == 0)
                continue;

            series.Add(new DimensionTrendDto(dimension, nameZh, points,
                points[^1].Value, points[0].Value,
                points.Count > 1 ? points[^1].Value - points[0].Value : 0));
        }

        return series;
    }
}

public sealed record GetAbilityTrendQuery(Guid UserId, int Days = 30)
    : IRequest<Result<AbilityTrendDto>>;

public sealed class GetAbilityTrendQueryValidator : AbstractValidator<GetAbilityTrendQuery>
{
    public GetAbilityTrendQueryValidator()
    {
        // 与 GetDashboardQuery.TrendDays 同口径:7–365,默认 30
        RuleFor(x => x.Days).InclusiveBetween(7, 365);
    }
}

public sealed class GetAbilityTrendQueryHandler(AnalyticsDbContext db)
    : IRequestHandler<GetAbilityTrendQuery, Result<AbilityTrendDto>>
{
    public async Task<Result<AbilityTrendDto>> Handle(GetAbilityTrendQuery r, CancellationToken ct)
    {
        // 时区日界:与 RecordAbilityCommandHandler / GetDashboardQueryHandler 一致,按 UTC 今日划界
        var end = DateOnly.FromDateTime(DateTime.UtcNow);
        var start = AbilityTrendAggregation.WindowStart(end, r.Days);

        var snapshots = await db.AbilitySnapshots.AsNoTracking()
            .Where(x => x.UserId == r.UserId && x.Date >= start && x.Date <= end)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        return Result.Success(new AbilityTrendDto(r.Days,
            AbilityTrendAggregation.Build(snapshots, end, r.Days)));
    }
}
