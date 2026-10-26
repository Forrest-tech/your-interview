using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Analytics.Domain;
using YourInterview.Services.Analytics.Infrastructure.Persistence;

namespace YourInterview.Services.Analytics.Application;

// =====================================================================================
//  DTO —— 全部是"前端画图直接能用"的形状,不要求前端二次加工
// =====================================================================================

/// <summary>雷达图的一个轴 + 当前值 + 历史最好值。</summary>
public sealed record RadarAxisDto(string Dimension, string NameZh, int Current, int? Best,
    int? Previous, int ChangeFromPrevious);

/// <summary>折线图上的一个点。</summary>
public sealed record TrendPointDto(string Date, int Value);

/// <summary>一个维度的趋势序列。</summary>
public sealed record DimensionTrendDto(string Dimension, string NameZh,
    IReadOnlyList<TrendPointDto> Points, int? Latest, int? Earliest, int Delta);

/// <summary>仪表盘总览 —— 一次请求给全首屏需要的所有数据。</summary>
public sealed record DashboardDto(
    IReadOnlyList<RadarAxisDto> Radar,
    IReadOnlyList<DimensionTrendDto> Trends,
    IReadOnlyList<PipelineTrendDto> Pipeline,
    IReadOnlyList<TopicMasteryDto> Mastery,
    ActivitySummaryDto Activity,
    InsightDto Insight);

/// <summary>漏斗趋势的一个点。</summary>
public sealed record PipelineTrendDto(string Date, int Saved, int Applied, int Screening,
    int Interviewing, int Offered, int Rejected, double? InterviewRate);

/// <summary>某主题的掌握分布。</summary>
public sealed record TopicMasteryDto(string Topic, int Total, int Mastered, int Learning,
    int Fresh, double? MasteryRate);

/// <summary>活跃度摘要 —— "我最近有没有在练"。</summary>
public sealed record ActivitySummaryDto(int PracticeDays, int TotalSnapshots,
    string? LastActivityDate, int SessionsThisWeek);

/// <summary>
/// 最该关注的一条洞察 —— 仪表盘顶部的"一句话结论"。
/// 仪表盘最容易犯的错是把 20 个数字扔给用户让他自己找问题;
/// 这里强制给出一个判断,让用户打开就知道下一步做什么。
/// </summary>
/// <summary>
/// FUN-07(票35):洞察不再直出中文句 —— 只回结构化 Code + Params(维度键/数值/日期均为
/// 语言无关值),三语文案由前端按 Code 查 i18n 字典本地化(insight.* 键)。
/// Params 中维度以 dimension 键(如 pronunciation)传递,前端再映射为本地化维度名。
/// </summary>
public sealed record InsightDto(string Code, IReadOnlyDictionary<string, string> Params,
    string Severity);

/// <summary>InsightDto.Code 取值(与前端 insight.* i18n 键一一对应)。</summary>
public static class InsightCodes
{
    public const string NoData = "NoData";
    public const string Regressing = "Regressing";
    public const string Weakest = "Weakest";
    public const string FunnelLow = "FunnelLow";
    public const string Inactive = "Inactive";
    public const string Stable = "Stable";
}

// =====================================================================================
//  查询
// =====================================================================================

public sealed record GetDashboardQuery(Guid UserId, int TrendDays = 30)
    : IRequest<Result<DashboardDto>>;

public sealed class GetDashboardQueryValidator : AbstractValidator<GetDashboardQuery>
{
    public GetDashboardQueryValidator()
    {
        RuleFor(x => x.TrendDays).InclusiveBetween(7, 365);
    }
}

public sealed class GetDashboardQueryHandler(AnalyticsDbContext db)
    : IRequestHandler<GetDashboardQuery, Result<DashboardDto>>
{
    /// <summary>六维的中文名(与 Assessment 的六维说明保持一致)。</summary>
    private static readonly Dictionary<string, string> DimensionNames = new()
    {
        ["pronunciation"] = "发音",
        ["fluency"] = "流利度",
        ["sentenceIntegrity"] = "语句完整性",
        ["structure"] = "结构",
        ["technicalDepth"] = "技术深度",
        ["relevance"] = "相关性"
    };

    public async Task<Result<DashboardDto>> Handle(GetDashboardQuery r, CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-r.TrendDays));

        var snapshots = await db.AbilitySnapshots.AsNoTracking()
            .Where(x => x.UserId == r.UserId && x.Date >= since)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var pipelines = await db.PipelineSnapshots.AsNoTracking()
            .Where(x => x.UserId == r.UserId && x.Date >= since)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        var mastery = await db.MasteryBreakdowns.AsNoTracking()
            .Where(x => x.UserId == r.UserId)
            .GroupBy(x => x.Topic)
            .Select(g => new TopicMasteryDto(g.Key,
                g.Max(x => x.Total), g.Max(x => x.Mastered), g.Max(x => x.Learning),
                g.Max(x => x.Fresh), null))
            .ToListAsync(ct);

        // ---------- 雷达:每维取最新值,并与"上一次的值"对比 ----------
        var radar = new List<RadarAxisDto>();
        var trends = new List<DimensionTrendDto>();

        foreach (var dim in DimensionNames.Keys)
        {
            var series = snapshots.Where(s => s.Dimension == dim).OrderBy(s => s.Date).ToList();

            int current = series.Count > 0 ? series[^1].Score : 0;
            int? previous = series.Count > 1 ? series[^2].Score : null;
            int? best = series.Count > 0 ? series.Max(s => s.Score) : null;

            radar.Add(new RadarAxisDto(dim, DimensionNames[dim], current, best, previous,
                previous is null ? 0 : current - previous.Value));

            var points = series
                .GroupBy(s => s.Date)
                .Select(g => new TrendPointDto(g.Key.ToString("yyyy-MM-dd"), g.Max(x => x.Score)))
                .ToList();

            trends.Add(new DimensionTrendDto(dim, DimensionNames[dim], points,
                points.Count > 0 ? points[^1].Value : null,
                points.Count > 0 ? points[0].Value : null,
                points.Count > 1 ? points[^1].Value - points[0].Value : 0));
        }

        var pipelineDtos = pipelines.Select(p => new PipelineTrendDto(
            p.Date.ToString("yyyy-MM-dd"), p.Saved, p.Applied, p.Screening, p.Interviewing,
            p.Offered, p.Rejected, p.AppliedToInterviewRate)).ToList();

        var practiceDays = snapshots.Select(s => s.Date).Distinct().Count();
        var weekAgo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var activity = new ActivitySummaryDto(
            practiceDays,
            snapshots.Count,
            snapshots.Count > 0 ? snapshots[^1].Date.ToString("yyyy-MM-dd") : null,
            snapshots.Count(s => s.Date >= weekAgo));

        return Result.Success(new DashboardDto(radar, trends, pipelineDtos, mastery,
            activity, BuildInsight(radar, pipelineDtos, mastery, activity)));
    }

    /// <summary>洞察生成见 <see cref="InsightFactory"/>(纯逻辑,单测直接覆盖)。</summary>
    private static InsightDto BuildInsight(IReadOnlyList<RadarAxisDto> radar,
        IReadOnlyList<PipelineTrendDto> pipeline, IReadOnlyList<TopicMasteryDto> mastery,
        ActivitySummaryDto activity)
        => InsightFactory.Build(radar, pipeline, activity);
}

/// <summary>
/// 生成一条"最该关注什么"的判断 —— FUN-07(票35)起只产结构化 Code+Params,不产文案。
///
/// 优先级(从高到低,与改前逐字一致):① 有维度在退步 → 立刻说 ② 有维度长期偏低 → 指出最该练的
/// ③ 漏斗转化异常 → 提醒投递质量 ④ 最近没练 → 提醒动手 ⑤ 都正常 → 给正反馈。
/// 纯函数:入参为已算好的 DTO,无 DB/时钟依赖,单测直接覆盖(含 boundary)。
/// </summary>
public static class InsightFactory
{
    private static IReadOnlyDictionary<string, string> P(params (string K, string V)[] items)
        => items.ToDictionary(x => x.K, x => x.V);

    public static InsightDto Build(IReadOnlyList<RadarAxisDto> radar,
        IReadOnlyList<PipelineTrendDto> pipeline, ActivitySummaryDto activity)
    {
        var scored = radar.Where(x => x.Current > 0).ToList();

        if (scored.Count == 0)
            return new InsightDto(InsightCodes.NoData, P(), "info");

        // ① 退步优先 —— 退步比"一直低"更值得立刻干预
        var regressing = scored.Where(x => x.ChangeFromPrevious <= -8)
            .OrderBy(x => x.ChangeFromPrevious).FirstOrDefault();
        if (regressing is not null)
        {
            return new InsightDto(InsightCodes.Regressing, P(
                ("dimension", regressing.Dimension),
                ("drop", Math.Abs(regressing.ChangeFromPrevious).ToString()),
                ("previous", (regressing.Previous ?? 0).ToString()),
                ("current", regressing.Current.ToString())), "warning");
        }

        // ② 最弱的一维(加权后影响力最大)
        var weakest = scored.OrderBy(x => x.Current).First();
        if (weakest.Current < 65)
        {
            return new InsightDto(InsightCodes.Weakest, P(
                ("dimension", weakest.Dimension),
                ("current", weakest.Current.ToString())), "warning");
        }

        // ③ 漏斗转化异常
        var latestPipe = pipeline.LastOrDefault();
        if (latestPipe is { Applied: >= 10, InterviewRate: < 10 })
        {
            return new InsightDto(InsightCodes.FunnelLow, P(
                ("applied", latestPipe.Applied.ToString()),
                ("interviewing", latestPipe.Interviewing.ToString())), "warning");
        }

        // ④ 最近没练
        if (activity.SessionsThisWeek == 0 && activity.PracticeDays > 0)
        {
            return new InsightDto(InsightCodes.Inactive, P(
                ("lastActivityDate", activity.LastActivityDate ?? "")), "info");
        }

        // ⑤ 都正常
        var strongest = scored.OrderByDescending(x => x.Current).First();
        return new InsightDto(InsightCodes.Stable, P(
            ("strongestDimension", strongest.Dimension),
            ("strongestCurrent", strongest.Current.ToString()),
            ("weakestDimension", weakest.Dimension),
            ("weakestCurrent", weakest.Current.ToString())), "success");
    }
}

/// <summary>只取能力雷达(前端小组件用,不用拉整个仪表盘)。</summary>
public sealed record GetRadarQuery(Guid UserId, string? Source = null)
    : IRequest<Result<IReadOnlyList<RadarAxisDto>>>;

public sealed class GetRadarQueryHandler(AnalyticsDbContext db)
    : IRequestHandler<GetRadarQuery, Result<IReadOnlyList<RadarAxisDto>>>
{
    public async Task<Result<IReadOnlyList<RadarAxisDto>>> Handle(GetRadarQuery r, CancellationToken ct)
    {
        var q = db.AbilitySnapshots.AsNoTracking().Where(x => x.UserId == r.UserId);
        if (!string.IsNullOrWhiteSpace(r.Source))
            q = q.Where(x => x.Source == r.Source);

        var snapshots = await q.OrderBy(x => x.Date).ToListAsync(ct);

        var names = GetDashboardQueryHandler_DimensionNames;
        var result = names.Keys.Select(dim =>
        {
            var series = snapshots.Where(s => s.Dimension == dim).OrderBy(s => s.Date).ToList();
            var current = series.Count > 0 ? series[^1].Score : 0;
            var previous = series.Count > 1 ? series[^2].Score : (int?)null;
            return new RadarAxisDto(dim, names[dim], current,
                series.Count > 0 ? series.Max(s => s.Score) : null,
                previous, previous is null ? 0 : current - previous.Value);
        }).ToList();

        return Result.Success<IReadOnlyList<RadarAxisDto>>(result);
    }

    private static readonly Dictionary<string, string> GetDashboardQueryHandler_DimensionNames = new()
    {
        ["pronunciation"] = "发音",
        ["fluency"] = "流利度",
        ["sentenceIntegrity"] = "语句完整性",
        ["structure"] = "结构",
        ["technicalDepth"] = "技术深度",
        ["relevance"] = "相关性"
    };
}

public sealed record GetPipelineTrendQuery(Guid UserId, int Days = 90)
    : IRequest<Result<IReadOnlyList<PipelineTrendDto>>>;

public sealed class GetPipelineTrendQueryHandler(AnalyticsDbContext db)
    : IRequestHandler<GetPipelineTrendQuery, Result<IReadOnlyList<PipelineTrendDto>>>
{
    public async Task<Result<IReadOnlyList<PipelineTrendDto>>> Handle(GetPipelineTrendQuery r,
        CancellationToken ct)
    {
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-Math.Clamp(r.Days, 7, 365)));

        var rows = await db.PipelineSnapshots.AsNoTracking()
            .Where(x => x.UserId == r.UserId && x.Date >= since)
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<PipelineTrendDto>>(rows.Select(p => new PipelineTrendDto(
            p.Date.ToString("yyyy-MM-dd"), p.Saved, p.Applied, p.Screening, p.Interviewing,
            p.Offered, p.Rejected, p.AppliedToInterviewRate)).ToList());
    }
}

// =====================================================================================
//  命令 —— 手工刷新(事件丢失/重建读模型时用)
// =====================================================================================

/// <summary>
/// 记录一次能力快照。
/// 由 Assessment 打分的接口在写完分数后调用 —— 让"打完分立刻能在雷达图看到"成立,
/// 而不必等异步事件绕一圈。
/// </summary>
public sealed record RecordAbilityCommand(Guid UserId, string Dimension, int Score, string Source)
    : IRequest<Result>;

public sealed class RecordAbilityCommandValidator : AbstractValidator<RecordAbilityCommand>
{
    public RecordAbilityCommandValidator()
    {
        RuleFor(x => x.Dimension).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Score).InclusiveBetween(0, 100);
        RuleFor(x => x.Source).NotEmpty().MaximumLength(30);
    }
}

public sealed class RecordAbilityCommandHandler(AnalyticsDbContext db)
    : IRequestHandler<RecordAbilityCommand, Result>
{
    public async Task<Result> Handle(RecordAbilityCommand r, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var existing = await db.AbilitySnapshots.FirstOrDefaultAsync(x =>
            x.UserId == r.UserId && x.Date == today && x.Dimension == r.Dimension
            && x.Source == r.Source, ct);

        if (existing is null)
            db.AbilitySnapshots.Add(new AbilitySnapshot(r.UserId, today, r.Dimension, r.Score, r.Source));
        else
            existing.UpdateScore(r.Score);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>清空某用户的读模型 —— 用于"删库重放"验证。</summary>
public sealed record ResetReadModelCommand(Guid UserId) : IRequest<Result<ResetResultDto>>;

public sealed record ResetResultDto(int AbilityRows, int PipelineRows, int MasteryRows);

public sealed class ResetReadModelCommandHandler(AnalyticsDbContext db)
    : IRequestHandler<ResetReadModelCommand, Result<ResetResultDto>>
{
    public async Task<Result<ResetResultDto>> Handle(ResetReadModelCommand r, CancellationToken ct)
    {
        var abilities = await db.AbilitySnapshots.Where(x => x.UserId == r.UserId).ToListAsync(ct);
        var pipelines = await db.PipelineSnapshots.Where(x => x.UserId == r.UserId).ToListAsync(ct);
        var mastery = await db.MasteryBreakdowns.Where(x => x.UserId == r.UserId).ToListAsync(ct);
        // 票34:单条目台账是 MasteryBreakdown 的计数依据,必须随快照一并清 ——
        // 只清快照留台账的话,重放/后续事件会按旧台账迁移不存在的行,再次分叉。
        var itemStates = await db.MasteryItemStates.Where(x => x.UserId == r.UserId).ToListAsync(ct);

        db.AbilitySnapshots.RemoveRange(abilities);
        db.PipelineSnapshots.RemoveRange(pipelines);
        db.MasteryBreakdowns.RemoveRange(mastery);
        db.MasteryItemStates.RemoveRange(itemStates);

        await db.SaveChangesAsync(ct);
        return Result.Success(new ResetResultDto(abilities.Count, pipelines.Count, mastery.Count));
    }
}
