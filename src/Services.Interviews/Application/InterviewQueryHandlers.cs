using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Domain.JsonFields;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Interviews.Application;

// ============================ Handler:查询 ============================

public sealed class ListEntriesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListEntriesQuery, Result<PagedResult<InterviewEntryListItemDto>>>
{
    public async Task<Result<PagedResult<InterviewEntryListItemDto>>> Handle(
        ListEntriesQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var size = request.PageSize is < 1 or > 200 ? 20 : request.PageSize;

        var q = db.Entries.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .Include(x => x.Assets)
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            // 票05:列表项 Outcome 由当前轮轮次枚举派生(DeriveOutcome),必须带 Rounds。
            .Include(x => x.Rounds)
            .AsQueryable();

        if (request.CompanyId is { } cid) q = q.Where(x => x.CompanyId == cid);

        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<InterviewStatus>(request.Status, true, out var st))
            q = q.Where(x => x.Status == st);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(x => x.CompanyName.ToLower().Contains(s)
                          || x.Role.ToLower().Contains(s)
                          || (x.Interviewers != null && x.Interviewers.ToLower().Contains(s)));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.InterviewDate ?? DateOnly.MinValue)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return Result.Success(new PagedResult<InterviewEntryListItemDto>(
            items.Select(x => x.ToListItemDto()).ToList(), total, page, size));
    }
}

public sealed class GetEntryQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetEntryQuery, Result<InterviewEntryDto>>
{
    public async Task<Result<InterviewEntryDto>> Handle(GetEntryQuery request, CancellationToken ct)
    {
        // 详情必须 Include 子集合:它们是字段访问模式,不显式加载会得到空集合(踩过的坑)
        var e = await OwnedEntry.LoadNoTrackingAsync(db, request.Id, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets)
                .Include(x => x.Questions)
                .Include(x => x.Weaknesses)
                .Include(x => x.Actions)
                .Include(x => x.Rounds), ct);

        if (e is null)
            return Result.Failure<InterviewEntryDto>(Error.NotFound("面试条目"));

        // 票101:读时 xmin(条目行 + 题目/轮次/材料子行)随 DTO 带出。
        var version = await db.Entries.AsNoTracking().Where(x => x.Id == e.Id)
            .Select(x => EF.Property<uint>(x, "xmin")).FirstAsync(ct);
        var questionVersions = await db.Questions.AsNoTracking().Where(x => x.InterviewEntryId == e.Id)
            .Select(x => new { x.Id, V = EF.Property<uint>(x, "xmin") })
            .ToDictionaryAsync(x => x.Id, x => x.V, ct);
        var roundVersions = await db.Rounds.AsNoTracking().Where(x => x.InterviewEntryId == e.Id)
            .Select(x => new { x.Id, V = EF.Property<uint>(x, "xmin") })
            .ToDictionaryAsync(x => x.Id, x => x.V, ct);
        var assetVersions = await db.Assets.AsNoTracking().Where(x => x.InterviewEntryId == e.Id)
            .Select(x => new { x.Id, V = EF.Property<uint>(x, "xmin") })
            .ToDictionaryAsync(x => x.Id, x => x.V, ct);
        return Result.Success(e.ToDto(version, questionVersions, roundVersions, assetVersions));
    }
}

public sealed class GetCompanySummariesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetCompanySummariesQuery, Result<IReadOnlyList<CompanySummaryDto>>>
{
    public async Task<Result<IReadOnlyList<CompanySummaryDto>>> Handle(
        GetCompanySummariesQuery request, CancellationToken ct)
    {
        // 前端左侧"公司树"用:按公司聚合出场次、已分析数、平均分、最近一场
        var rows = await db.Entries.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .GroupBy(x => new { x.CompanyId, x.CompanyName })
            .Select(g => new CompanySummaryDto(
                g.Key.CompanyId,
                g.Key.CompanyName,
                g.Count(),
                g.Count(x => x.Status == InterviewStatus.Analyzed),
                g.Where(x => x.OverallScore != null).Average(x => (double?)x.OverallScore),
                g.Max(x => x.InterviewDate),
                g.OrderByDescending(x => x.InterviewDate).Select(x => x.Result).FirstOrDefault()))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<CompanySummaryDto>>(
            rows.OrderByDescending(r => r.LatestInterviewDate ?? DateOnly.MinValue).ToList());
    }
}

public sealed class GetCompanyRoundsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetCompanyRoundsQuery, Result<IReadOnlyList<CompanyRoundDto>>>
{
    public async Task<Result<IReadOnlyList<CompanyRoundDto>>> Handle(
        GetCompanyRoundsQuery request, CancellationToken ct)
    {
        var rounds = await db.Entries.AsNoTracking()
            .Where(e => e.CompanyId == request.CompanyId && e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { EntryId = e.Id, Round = r })
            .OrderBy(x => x.Round.ScheduledDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.Round.Order)
            .Select(x => new CompanyRoundDto(x.Round.Id, x.EntryId, x.Round.Order, x.Round.Stage, x.Round.ScheduledDate))
            .ToListAsync(ct);

        // 老数据兜底:有条目但无 Round 实体时,按 entry 的 RoundNo/日期合成一条
        if (rounds.Count == 0)
        {
            var fallback = await db.Entries.AsNoTracking()
                .Where(e => e.CompanyId == request.CompanyId && e.UserId == request.UserId)
                .OrderByDescending(e => e.InterviewDate ?? DateOnly.MinValue)
                .Select(e => new { e.Id, e.RoundNo, e.InterviewDate })
                .FirstOrDefaultAsync(ct);
            if (fallback is not null)
            {
                rounds.Add(new CompanyRoundDto(
                    Guid.Empty, fallback.Id, fallback.RoundNo, string.Empty, fallback.InterviewDate));
            }
        }

        return Result.Success<IReadOnlyList<CompanyRoundDto>>(rounds);
    }
}

public sealed class ListWeaknessesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListWeaknessesQuery, Result<IReadOnlyList<WeaknessDto>>>
{
    public async Task<Result<IReadOnlyList<WeaknessDto>>> Handle(
        ListWeaknessesQuery request, CancellationToken ct)
    {
        // 子表直查:先校验父条目归属,他人的条目按不存在处理(404)
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<IReadOnlyList<WeaknessDto>>(Error.NotFound("面试条目"));

        var q = db.Weaknesses.AsNoTracking().Where(w => w.InterviewEntryId == request.EntryId);

        if (!string.IsNullOrWhiteSpace(request.Category)
            && Enum.TryParse<WeaknessCategory>(request.Category, true, out var cat))
            q = q.Where(w => w.Category == cat);

        var list = await q.OrderByDescending(w => w.Severity).ThenBy(w => w.Category)
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<WeaknessDto>>(list.Select(w => w.ToDto()).ToList());
    }
}

public sealed class ListAnalysisJobsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListAnalysisJobsQuery, Result<IReadOnlyList<AnalysisJobDto>>>
{
    public async Task<Result<IReadOnlyList<AnalysisJobDto>>> Handle(
        ListAnalysisJobsQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<IReadOnlyList<AnalysisJobDto>>(Error.NotFound("面试条目"));

        var jobs = await db.AnalysisJobs.AsNoTracking()
            .Where(j => j.InterviewEntryId == request.EntryId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<AnalysisJobDto>>(jobs.Select(j => new AnalysisJobDto(
            j.Id, j.JobType.ToString(), j.Status.ToString(), j.Attempts, j.MaxAttempts,
            j.FailureReason, j.LastError, j.CreatedAt, j.DispatchedAt, j.CompletedAt)).ToList());
    }
}

public sealed class GetInterviewStatsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetInterviewStatsQuery, Result<InterviewStatsDto>>
{
    public async Task<Result<InterviewStatsDto>> Handle(GetInterviewStatsQuery request,
        CancellationToken ct)
    {
        var entries = await db.Entries.AsNoTracking()
            .Where(x => x.UserId == request.UserId).ToListAsync(ct);
        // 子表无 UserId:按当前用户的条目 Id 集合收口,不得把他人问答/短板计进统计
        var ownedIds = db.Entries.Where(x => x.UserId == request.UserId).Select(x => x.Id);
        var weaknesses = await db.Weaknesses.AsNoTracking()
            .Where(w => ownedIds.Contains(w.InterviewEntryId)).ToListAsync(ct);
        var questions = await db.Questions.AsNoTracking()
            .Where(q => ownedIds.Contains(q.InterviewEntryId)).ToListAsync(ct);

        var analyzed = entries.Where(e => e.Status == InterviewStatus.Analyzed).ToList();

        double? Avg(Func<Domain.InterviewEntry, int?> sel)
        {
            var vals = analyzed.Select(sel).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            return vals.Count == 0 ? null : Math.Round(vals.Average(), 1);
        }

        var weaknessByCat = weaknesses
            .GroupBy(w => w.Category.ToString())
            .Select(g => new CategoryCountDto(g.Key, g.Count(),
                Math.Round(g.Average(w => (double)w.Severity), 1)))
            .OrderByDescending(x => x.Count)
            .ToList();

        var questionByCat = questions
            .GroupBy(q => q.Category.ToString())
            .Select(g => new CategoryCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        // 进步曲线:按面试日期排序的总分序列(前端折线图)
        var trend = analyzed
            .Where(e => e.OverallScore.HasValue && e.InterviewDate.HasValue)
            .OrderBy(e => e.InterviewDate)
            .Select(e => new TrendPointDto(e.InterviewDate!.Value, e.OverallScore!.Value, e.CompanyName))
            .ToList();

        return Result.Success(new InterviewStatsDto(
            entries.Count,
            entries.Count(e => e.Status == InterviewStatus.Draft),
            entries.Count(e => e.Status == InterviewStatus.Transcribing),
            entries.Count(e => e.Status == InterviewStatus.Transcribed),
            entries.Count(e => e.Status == InterviewStatus.Analyzing),
            entries.Count(e => e.Status == InterviewStatus.Analyzed),
            entries.Count(e => e.Status == InterviewStatus.Failed),
            questions.Count,
            questions.Count(q => q.GotStuck),
            weaknesses.Count,
            Avg(e => e.OverallScore),
            Avg(e => e.PronunciationScore),
            Avg(e => e.FluencyScore),
            Avg(e => e.StructureScore),
            Avg(e => e.TechnicalDepthScore),
            Avg(e => e.RelevanceScore),
            weaknessByCat,
            questionByCat,
            trend,
            entries.Count(e => e.Status is InterviewStatus.Transcribed or InterviewStatus.AssetsUploaded
                or InterviewStatus.Transcribing)));
    }
}

