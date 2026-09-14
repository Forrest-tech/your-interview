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
public sealed record InsightDto(string Headline, string Detail, string? SuggestedAction,
    string Severity);

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

    /// <summary>
    /// 生成一条"最该关注什么"的判断。
    ///
    /// 优先级(从高到低):① 有维度在退步 → 立刻说 ② 有维度长期偏低 → 指出最该练的
    /// ③ 漏斗转化异常 → 提醒投递质量 ④ 最近没练 → 提醒动手 ⑤ 都正常 → 给正反馈。
    /// </summary>
    private static InsightDto BuildInsight(IReadOnlyList<RadarAxisDto> radar,
        IReadOnlyList<PipelineTrendDto> pipeline, IReadOnlyList<TopicMasteryDto> mastery,
        ActivitySummaryDto activity)
    {
        var scored = radar.Where(x => x.Current > 0).ToList();

        if (scored.Count == 0)
        {
            return new InsightDto(
                "还没有练习数据",
                "完成一次 AI 实战模拟或导入一场真实面试的录音,系统就能给出六维基线分。",
                "从「AI 实战模拟」新建一次练习开始",
                "info");
        }

        // ① 退步优先 —— 退步比"一直低"更值得立刻干预
        var regressing = scored.Where(x => x.ChangeFromPrevious <= -8)
            .OrderBy(x => x.ChangeFromPrevious).FirstOrDefault();
        if (regressing is not null)
        {
            return new InsightDto(
                $"{regressing.NameZh} 比上次掉了 {Math.Abs(regressing.ChangeFromPrevious)} 分",
                $"从 {regressing.Previous} 掉到 {regressing.Current}。单次波动可能是题目难度差异," +
                "但如果连续两次都在掉,说明这一维需要专门练。",
                $"针对「{regressing.NameZh}」做一次专项模拟",
                "warning");
        }

        // ② 最弱的一维(加权后影响力最大)
        var weakest = scored.OrderBy(x => x.Current).First();
        if (weakest.Current < 65)
        {
            return new InsightDto(
                $"{weakest.NameZh} 是最该补的一维(当前 {weakest.Current} 分)",
                "按平台的加权算法,这一维对总分的影响排在前面 —— " +
                "把它提上去比在已经不错的维度上继续打磨更划算。",
                $"针对「{weakest.NameZh}」做一次专项模拟",
                "warning");
        }

        // ③ 漏斗转化异常
        var latestPipe = pipeline.LastOrDefault();
        if (latestPipe is { Applied: >= 10, InterviewRate: < 10 })
        {
            return new InsightDto(
                $"投递 {latestPipe.Applied} 个只拿到 {latestPipe.Interviewing} 个面试,转化偏低",
                "这个比例通常不是运气问题 —— 多半是简历与 JD 的关键词匹配度不够," +
                "或者投递方向与经历偏差较大。",
                "用「Tracker」按状态复盘最近 10 个投递,检查简历是否按岗微调",
                "warning");
        }

        // ④ 最近没练
        if (activity.SessionsThisWeek == 0 && activity.PracticeDays > 0)
        {
            return new InsightDto(
                "本周还没有练习记录",
                $"上次活动是 {activity.LastActivityDate}。间隔重复的价值在于频率 —— " +
                "一周不练,之前的肌肉记忆会明显退化。",
                "做一次 5 题的快速模拟保持手感",
                "info");
        }

        // ⑤ 都正常
        var strongest = scored.OrderByDescending(x => x.Current).First();
        return new InsightDto(
            $"状态稳定,最强项是{strongest.NameZh}({strongest.Current} 分)",
            $"最低的一维是 {weakest.NameZh}({weakest.Current} 分),已经高于及格线。" +
            "现在的重点应该是把练习频率保持住,而不是突击。",
            "保持每周 3 次、每次 5 题的节奏",
            "success");
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

        db.AbilitySnapshots.RemoveRange(abilities);
        db.PipelineSnapshots.RemoveRange(pipelines);
        db.MasteryBreakdowns.RemoveRange(mastery);

        await db.SaveChangesAsync(ct);
        return Result.Success(new ResetResultDto(abilities.Count, pipelines.Count, mastery.Count));
    }
}
