using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ Handler:统计与看板 ============================

/// <summary>行动中心 —— 仪表盘"今天该做什么"的 Jobs 侧聚合。</summary>
public sealed record ActionCenterDto(
    IReadOnlyList<ApplicationDto> FollowUps,
    IReadOnlyList<ApplicationDto> Deadlines,
    IReadOnlyList<StatusCountDto> ByStatus,
    int ActiveCount, int InterviewCount, int OfferCount, int Total);

/// <summary>单个状态的数量。</summary>
public sealed record StatusCountDto(string Status, int Count);

public sealed class GetTrackerStatsQueryHandler(JobsDbContext db)
    : IRequestHandler<GetTrackerStatsQuery, Result<TrackerStats>>
{
    public async Task<Result<TrackerStats>> Handle(GetTrackerStatsQuery request, CancellationToken ct)
    {
        var apps = await db.Applications.AsNoTracking().Include(a => a.Rounds)
            .Where(a => a.UserId == request.UserId && !a.IsArchived).ToListAsync(ct);
        var total = apps.Count;

        // 状态计数 (string key 供前端直接查)
        var byStatus = apps.GroupBy(a => a.Status.ToString()).ToDictionary(g => g.Key, g => g.Count());
        int C(ApplicationStatus s) => byStatus.TryGetValue(s.ToString(), out var v) ? v : 0;

        var active = apps.Count(a => a.Status is ApplicationStatus.Applied or ApplicationStatus.Screen
            or ApplicationStatus.Interview or ApplicationStatus.Offer);

        var appliedOrBeyond = apps.Count(a => a.Status != ApplicationStatus.Saved);
        var responded = apps.Count(a => a.Status is ApplicationStatus.Screen or ApplicationStatus.Interview
            or ApplicationStatus.Offer or ApplicationStatus.Accepted or ApplicationStatus.Rejected);
        // ★ F-09 统计口径:有面试活动 = 状态已到 Interview/Offer/Accepted,
        //   或已登记过至少一轮面试(Rounds)—— 状态可能因 Paused 等原因暂时回落,
        //   但"面过"这个事实不能从统计里消失,否则与 Playbook 进度自相矛盾。
        var interviewed = apps.Count(a => a.Status is ApplicationStatus.Interview or ApplicationStatus.Offer
            or ApplicationStatus.Accepted || a.Rounds.Count > 0);
        var offers = apps.Count(a => a.Status is ApplicationStatus.Offer or ApplicationStatus.Accepted);

        double Rate(int num, int den) => den <= 0 ? 0 : Math.Round(num * 100.0 / den, 1);

        // 首次响应天数:创建 → 首次离开 Applied 的时间
        var responseDurations = new List<double>();
        foreach (var a in apps)
        {
            var firstResponse = a.History
                .Where(h => h.Status is ApplicationStatus.Screen or ApplicationStatus.Interview
                    or ApplicationStatus.Offer or ApplicationStatus.Rejected)
                .OrderBy(h => h.ChangedAt).FirstOrDefault();
            if (firstResponse is not null)
                responseDurations.Add((firstResponse.ChangedAt - a.CreatedAt).TotalDays);
        }

        var pipeline = Enum.GetValues<ApplicationStatus>()
            .Select(s => new PipelineColumn(s.ToString(), C(s)))
            .ToList();

        var bySource = apps.Where(a => !string.IsNullOrWhiteSpace(a.Source))
            .GroupBy(a => a.Source!)
            .Select(g => new SourceBreakdown(g.Key, g.Count(),
                g.Count(a => a.Status is ApplicationStatus.Screen or ApplicationStatus.Interview
                    or ApplicationStatus.Offer or ApplicationStatus.Accepted or ApplicationStatus.Rejected)))
            .OrderByDescending(x => x.Count).ToList();

        var byMonth = apps.Where(a => a.AppliedDate.HasValue)
            .GroupBy(a => a.AppliedDate!.Value.ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyCount(g.Key, g.Count(),
                g.Count(a => a.Status is ApplicationStatus.Interview or ApplicationStatus.Offer or ApplicationStatus.Accepted),
                g.Count(a => a.Status is ApplicationStatus.Offer or ApplicationStatus.Accepted)))
            .ToList();

        return Result.Success(new TrackerStats(
            total, active, appliedOrBeyond, interviewed, offers, C(ApplicationStatus.Rejected),
            C(ApplicationStatus.Ghosted),
            Rate(responded, appliedOrBeyond), Rate(interviewed, appliedOrBeyond), Rate(offers, appliedOrBeyond),
            responseDurations.Count > 0 ? Math.Round(responseDurations.Average(), 1) : 0,
            pipeline, bySource, byMonth, byStatus));
    }
}

public sealed class GetActionCenterQueryHandler(JobsDbContext db, TimeProvider clock)
    : IRequestHandler<GetActionCenterQuery, Result<ActionCenterDto>>
{
    public async Task<Result<ActionCenterDto>> Handle(GetActionCenterQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var followUntil = now.AddDays(Math.Clamp(request.FollowUpDays, 1, 90));
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var deadlineUntil = today.AddDays(Math.Clamp(request.DeadlineDays, 1, 120));

        var apps = await db.Applications.AsNoTracking().Include(a => a.Rounds)
            .Where(a => a.UserId == request.UserId && !a.IsArchived).ToListAsync(ct);

        var followUps = apps
            .Where(a => a.NextFollowUpAt != null && a.NextFollowUpAt <= followUntil)
            .OrderBy(a => a.NextFollowUpAt)
            .Take(50).ToList();
        var deadlines = apps
            .Where(a => a.Deadline != null && a.Deadline >= today && a.Deadline <= deadlineUntil)
            .OrderBy(a => a.Deadline).Take(50).ToList();

        var ids = apps.Select(a => a.CompanyId).Distinct().ToList();
        var names = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var logos = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LogoUrl, ct);

        var byStatus = apps.GroupBy(a => a.Status)
            .Select(g => new StatusCountDto(g.Key.ToString(), g.Count()))
            .OrderByDescending(x => x.Count).ToList();

        var active = apps.Count(a => a.Status is ApplicationStatus.Applied or ApplicationStatus.Screen
            or ApplicationStatus.Interview or ApplicationStatus.Offer);
        // ★ F-09:与 TrackerStats 同口径 —— 登记过轮次也算有面试活动。
        var interviewed = apps.Count(a => a.Status is ApplicationStatus.Interview or ApplicationStatus.Offer
            or ApplicationStatus.Accepted || a.Rounds.Count > 0);
        var offers = apps.Count(a => a.Status is ApplicationStatus.Offer or ApplicationStatus.Accepted);

        return Result.Success(new ActionCenterDto(
            followUps.Select(a => a.ToDto(names.GetValueOrDefault(a.CompanyId, "—"),
                logos.GetValueOrDefault(a.CompanyId))).ToList(),
            deadlines.Select(a => a.ToDto(names.GetValueOrDefault(a.CompanyId, "—"),
                logos.GetValueOrDefault(a.CompanyId))).ToList(),
            byStatus, active, interviewed, offers, apps.Count));
    }
}

public sealed class GetUpcomingFollowUpsQueryHandler(JobsDbContext db, TimeProvider clock)
    : IRequestHandler<GetUpcomingFollowUpsQuery, Result<IReadOnlyList<ApplicationDto>>>
{
    public async Task<Result<IReadOnlyList<ApplicationDto>>> Handle(GetUpcomingFollowUpsQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var until = now.AddDays(Math.Clamp(request.Days, 1, 90));

        var apps = await db.Applications.AsNoTracking()
            .Where(a => a.UserId == request.UserId && a.NextFollowUpAt != null && a.NextFollowUpAt <= until)
            .OrderBy(a => a.NextFollowUpAt)
            .Take(100).ToListAsync(ct);

        var ids = apps.Select(a => a.CompanyId).Distinct().ToList();
        var names = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var logos = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LogoUrl, ct);

        return Result.Success<IReadOnlyList<ApplicationDto>>(
            apps.Select(a => a.ToDto(names.GetValueOrDefault(a.CompanyId, "—"), logos.GetValueOrDefault(a.CompanyId))).ToList());
    }
}

public sealed class GetDeadlinesQueryHandler(JobsDbContext db, TimeProvider clock)
    : IRequestHandler<GetDeadlinesQuery, Result<IReadOnlyList<ApplicationDto>>>
{
    public async Task<Result<IReadOnlyList<ApplicationDto>>> Handle(GetDeadlinesQuery request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var until = today.AddDays(Math.Clamp(request.Days, 1, 120));

        var apps = await db.Applications.AsNoTracking()
            .Where(a => a.UserId == request.UserId && a.Deadline != null && a.Deadline >= today && a.Deadline <= until)
            .OrderBy(a => a.Deadline).Take(100).ToListAsync(ct);

        var ids = apps.Select(a => a.CompanyId).Distinct().ToList();
        var names = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var logos = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LogoUrl, ct);

        return Result.Success<IReadOnlyList<ApplicationDto>>(
            apps.Select(a => a.ToDto(names.GetValueOrDefault(a.CompanyId, "—"), logos.GetValueOrDefault(a.CompanyId))).ToList());
    }
}

public sealed class BulkImportApplicationsCommandHandler(JobsDbContext db)
    : IRequestHandler<BulkImportApplicationsCommand, Result<BulkImportResult>>
{
    public async Task<Result<BulkImportResult>> Handle(BulkImportApplicationsCommand request, CancellationToken ct)
    {
        var created = 0;
        var skipped = 0;
        var errors = new List<string>();
        var index = 0;

        foreach (var row in request.Rows)
        {
            index++;
            if (string.IsNullOrWhiteSpace(row.Company) || string.IsNullOrWhiteSpace(row.Role))
            {
                errors.Add($"第 {index} 行:公司或岗位为空,已跳过");
                skipped++;
                continue;
            }

            var name = row.Company.Trim();
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower() && c.UserId == request.UserId, ct);
            if (company is null)
            {
                company = new Company(name, userId: request.UserId);
                db.Companies.Add(company);
                await db.SaveChangesAsync(ct);
            }

            var dup = await db.Applications.AnyAsync(a => a.CompanyId == company.Id && a.UserId == request.UserId
                && a.Role.ToLower() == row.Role.Trim().ToLower(), ct);
            if (dup)
            {
                skipped++;
                continue;
            }

            var app = new JobApplication(company.Id, row.Role.Trim(), row.Location, row.Link,
                row.Salary, null, row.Source, null, userId: request.UserId);
            if (!string.IsNullOrWhiteSpace(row.Notes)) app.UpdateDetails(row.Role.Trim(), row.Location,
                row.Link, row.Salary, null, row.Source, null, row.Notes);

            if (!string.IsNullOrWhiteSpace(row.Status)
                && Enum.TryParse<ApplicationStatus>(row.Status, true, out var st))
            {
                if (st == ApplicationStatus.Applied || row.AppliedDate.HasValue)
                    app.MarkApplied(row.AppliedDate, "批量导入");
                // ★ F-09:跨级状态沿路径逐级走,不要一步不可达就丢。
                if (st != ApplicationStatus.Saved && st != ApplicationStatus.Applied)
                    app.TryWalkToStatus(st, "批量导入");
            }

            db.Applications.Add(app);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(new BulkImportResult(created, skipped, errors));
    }
}
