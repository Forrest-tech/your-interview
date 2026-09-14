using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ DTO ============================

public sealed record CompanyDto(Guid Id, string Name, string? Website, string? Industry, string? Location,
    string? LogoUrl, string? Notes, bool IsBlacklisted, string CompanyType, int ApplicationCount,
    DateTimeOffset CreatedAt);

public sealed record StatusChangeDto(string Status, string? Note, DateTimeOffset ChangedAt);

public sealed record InterviewRoundDto(Guid Id, int Order, string Stage, DateOnly? ScheduledDate,
    string? Interviewer, string? Format, string Outcome, string? Notes, string? Feedback);

public sealed record ApplicationDto(
    Guid Id, Guid CompanyId, string CompanyName, string Role, string? Location, string? Link,
    string? Salary, string? WorkMode, string? Source, string? JdSummary,
    string Status, string Priority, DateOnly? AppliedDate, DateOnly? Deadline,
    DateTimeOffset? NextFollowUpAt, int? ResumeScore, string? PassRateEstimate, string? MatchKeywords,
    string? Notes, string? PosterName, bool NeedsConnectFirst, string? OutreachMessage,
    DateTimeOffset? OutreachSentAt, string? RejectionReason,
    IReadOnlyList<StatusChangeDto> History, IReadOnlyList<InterviewRoundDto> Rounds,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record PipelineColumn(string Status, int Count);

public sealed record TrackerStats(
    int Total, int Active, int Applied, int InInterview, int Offers, int Rejected, int Ghosted,
    double ResponseRate, double InterviewRate, double OfferRate,
    double AverageDaysToFirstResponse, IReadOnlyList<PipelineColumn> Pipeline,
    IReadOnlyList<SourceBreakdown> BySource, IReadOnlyList<MonthlyCount> ByMonth);

public sealed record SourceBreakdown(string Source, int Count, int Responded);
public sealed record MonthlyCount(string Month, int Applications, int Interviews, int Offers);

// ============================ 查询 ============================

public sealed record ListCompaniesQuery(string? Search, bool IncludeBlacklisted = false)
    : IRequest<Result<IReadOnlyList<CompanyDto>>>;

public sealed record GetCompanyQuery(Guid Id) : IRequest<Result<CompanyDto>>;

public sealed record ListApplicationsQuery(
    string? Search, string? Status, string? Priority, Guid? CompanyId,
    string? SortBy = "updated", int Page = 1, int PageSize = 50)
    : IRequest<Result<PagedResult<ApplicationDto>>>;

public sealed record GetApplicationQuery(Guid Id) : IRequest<Result<ApplicationDto>>;

public sealed record GetTrackerStatsQuery : IRequest<Result<TrackerStats>>;

public sealed record GetUpcomingFollowUpsQuery(int Days = 7) : IRequest<Result<IReadOnlyList<ApplicationDto>>>;

public sealed record GetDeadlinesQuery(int Days = 14) : IRequest<Result<IReadOnlyList<ApplicationDto>>>;

// ============================ 命令 ============================

public sealed record CreateCompanyCommand(string Name, string? Website, string? Industry,
    string? Location, string? LogoUrl, string? Notes, string CompanyType = "Unknown", int? EmployeeCount = null)
    : IRequest<Result<Guid>>;

public sealed record UpdateCompanyCommand(Guid Id, string Name, string? Website, string? Industry,
    string? Location, string? LogoUrl, string? Notes, string CompanyType, int? EmployeeCount, bool IsBlacklisted)
    : IRequest<Result>;

public sealed record DeleteCompanyCommand(Guid Id) : IRequest<Result>;

public sealed record CreateApplicationCommand(Guid CompanyId, string Role, string? Location, string? Link,
    string? Salary, string? WorkMode, string? Source, string? JdSummary, string? Priority = null)
    : IRequest<Result<Guid>>;

public sealed record UpdateApplicationCommand(Guid Id, string Role, string? Location, string? Link,
    string? Salary, string? WorkMode, string? Source, string? JdSummary, string? Notes, string? Priority,
    DateOnly? Deadline)
    : IRequest<Result>;

public sealed record ChangeApplicationStatusCommand(Guid Id, string Status, string? Note, string? RejectionReason)
    : IRequest<Result>;

public sealed record DeleteApplicationCommand(Guid Id) : IRequest<Result>;

public sealed record SetApplicationAnalysisCommand(Guid Id, int? ResumeScore, string? PassRateEstimate,
    string? MatchKeywords) : IRequest<Result>;

public sealed record SetOutreachCommand(Guid Id, string? PosterName, bool NeedsConnectFirst, string? Message)
    : IRequest<Result>;

public sealed record MarkOutreachSentCommand(Guid Id) : IRequest<Result>;

public sealed record ScheduleFollowUpCommand(Guid Id, DateTimeOffset? At) : IRequest<Result>;

public sealed record AddInterviewRoundCommand(Guid ApplicationId, string Stage, DateOnly? ScheduledDate,
    string? Interviewer, string? Format, string? Notes) : IRequest<Result<Guid>>;

public sealed record UpdateInterviewRoundCommand(Guid ApplicationId, Guid RoundId, string? Stage,
    DateOnly? ScheduledDate, string? Interviewer, string? Format, string Outcome, string? Notes)
    : IRequest<Result>;

public sealed record BulkImportApplicationsCommand(IReadOnlyList<BulkImportRow> Rows) : IRequest<Result<BulkImportResult>>;

public sealed record BulkImportRow(string Company, string Role, string? Location, string? Link, string? Salary,
    string? Status, string? Source, string? Notes, DateOnly? AppliedDate);

public sealed record BulkImportResult(int Created, int Skipped, IReadOnlyList<string> Errors);

// ============================ 校验 ============================

public sealed class CreateCompanyCommandValidator : AbstractValidator<CreateCompanyCommand>
{
    public CreateCompanyCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(300).WithMessage("公司名称必填");
        RuleFor(x => x.Website).MaximumLength(1024)
            .Must(BeValidUrl).When(x => !string.IsNullOrWhiteSpace(x.Website))
            .WithMessage("官网地址格式不正确");
        RuleFor(x => x.CompanyType).Must(t => new[] { "DirectEmployer", "Agency", "Staffing", "Unknown" }.Contains(t))
            .WithMessage("公司类型必须是 DirectEmployer / Agency / Staffing / Unknown");
    }

    private static bool BeValidUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}

public sealed class CreateApplicationCommandValidator : AbstractValidator<CreateApplicationCommand>
{
    public CreateApplicationCommandValidator()
    {
        RuleFor(x => x.CompanyId).NotEmpty();
        RuleFor(x => x.Role).NotEmpty().MaximumLength(300).WithMessage("岗位名称必填");
        RuleFor(x => x.Link).MaximumLength(2048);
        RuleFor(x => x.Priority).Must(p => p is null || Enum.TryParse<Priority>(p, true, out _))
            .WithMessage("优先级必须是 Low / Medium / High / Critical");
    }
}

public sealed class ChangeApplicationStatusCommandValidator : AbstractValidator<ChangeApplicationStatusCommand>
{
    public ChangeApplicationStatusCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Status).NotEmpty()
            .Must(s => Enum.TryParse<ApplicationStatus>(s, true, out _))
            .WithMessage("无效的状态值");
    }
}

// ============================ 映射 ============================

internal static class JobsMapping
{
    public static ApplicationDto ToDto(this JobApplication a, string companyName) => new(
        a.Id, a.CompanyId, companyName, a.Role, a.Location, a.Link, a.Salary, a.WorkMode, a.Source, a.JdSummary,
        a.Status.ToString(), a.Priority.ToString(), a.AppliedDate, a.Deadline, a.NextFollowUpAt,
        a.ResumeScore, a.PassRateEstimate, a.MatchKeywords, a.Notes, a.PosterName, a.NeedsConnectFirst,
        a.OutreachMessage, a.OutreachSentAt, a.RejectionReason,
        a.History.OrderBy(h => h.ChangedAt).Select(h => new StatusChangeDto(h.Status.ToString(), h.Note, h.ChangedAt)).ToList(),
        a.Rounds.OrderBy(r => r.Order).Select(r => new InterviewRoundDto(r.Id, r.Order, r.Stage,
            r.ScheduledDate, r.Interviewer, r.Format, r.Outcome.ToString(), r.Notes, r.Feedback)).ToList(),
        a.CreatedAt, a.UpdatedAt);

    public static CompanyDto ToDto(this Company c, int applicationCount) => new(
        c.Id, c.Name, c.Website, c.Industry, c.Location, c.LogoUrl, c.Notes, c.IsBlacklisted,
        c.CompanyType, applicationCount, c.CreatedAt);
}

// ============================ Handler:公司 ============================

public sealed class ListCompaniesQueryHandler(JobsDbContext db)
    : IRequestHandler<ListCompaniesQuery, Result<IReadOnlyList<CompanyDto>>>
{
    public async Task<Result<IReadOnlyList<CompanyDto>>> Handle(ListCompaniesQuery request, CancellationToken ct)
    {
        var q = db.Companies.AsNoTracking().AsQueryable();
        if (!request.IncludeBlacklisted) q = q.Where(c => !c.IsBlacklisted);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(c => c.Name.ToLower().Contains(s)
                             || (c.Industry != null && c.Industry.ToLower().Contains(s))
                             || (c.Location != null && c.Location.ToLower().Contains(s)));
        }

        var items = await q.OrderBy(c => c.Name)
            .Select(c => new CompanyDto(c.Id, c.Name, c.Website, c.Industry, c.Location, c.LogoUrl,
                c.Notes, c.IsBlacklisted, c.CompanyType,
                db.Applications.Count(a => a.CompanyId == c.Id), c.CreatedAt))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<CompanyDto>>(items);
    }
}

public sealed class GetCompanyQueryHandler(JobsDbContext db)
    : IRequestHandler<GetCompanyQuery, Result<CompanyDto>>
{
    public async Task<Result<CompanyDto>> Handle(GetCompanyQuery request, CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (c is null) return Result.Failure<CompanyDto>(Error.NotFound("公司"));
        var count = await db.Applications.CountAsync(a => a.CompanyId == c.Id, ct);
        return Result.Success(c.ToDto(count));
    }
}

public sealed class CreateCompanyCommandHandler(JobsDbContext db)
    : IRequestHandler<CreateCompanyCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateCompanyCommand request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        var existing = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), ct);
        if (existing is not null)
            return Result.Success(existing.Id); // 幂等:已存在直接返回

        var company = new Company(name, request.Website, request.Industry, request.Location,
            request.LogoUrl, request.Notes)
        {
            // CompanyType/EmployeeCount 通过 Update 设置(保持聚合封装)
        };
        company.Update(name, request.Website, request.Industry, request.Location,
            request.LogoUrl, request.Notes, request.CompanyType, request.EmployeeCount);

        db.Companies.Add(company);
        await db.SaveChangesAsync(ct);
        return Result.Success(company.Id);
    }
}

public sealed class UpdateCompanyCommandHandler(JobsDbContext db)
    : IRequestHandler<UpdateCompanyCommand, Result>
{
    public async Task<Result> Handle(UpdateCompanyCommand request, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (c is null) return Result.Failure(Error.NotFound("公司"));

        c.Update(request.Name, request.Website, request.Industry, request.Location,
            request.LogoUrl, request.Notes, request.CompanyType, request.EmployeeCount);
        c.SetBlacklisted(request.IsBlacklisted);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteCompanyCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteCompanyCommand, Result>
{
    public async Task<Result> Handle(DeleteCompanyCommand request, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (c is null) return Result.Failure(Error.NotFound("公司"));

        var count = await db.Applications.CountAsync(a => a.CompanyId == c.Id, ct);
        if (count > 0)
            return Result.Failure(Error.Conflict("Company.HasApplications",
                $"该公司下还有 {count} 条投递记录,请先处理"));

        c.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ============================ Handler:投递记录 ============================

public sealed class ListApplicationsQueryHandler(JobsDbContext db)
    : IRequestHandler<ListApplicationsQuery, Result<PagedResult<ApplicationDto>>>
{
    public async Task<Result<PagedResult<ApplicationDto>>> Handle(ListApplicationsQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);

        var q = db.Applications.AsNoTracking().AsQueryable();
        if (request.CompanyId.HasValue) q = q.Where(a => a.CompanyId == request.CompanyId.Value);

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var statuses = request.Status.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => Enum.TryParse<ApplicationStatus>(s, true, out _))
                .Select(s => Enum.Parse<ApplicationStatus>(s, true)).ToArray();
            if (statuses.Length > 0) q = q.Where(a => statuses.Contains(a.Status));
        }

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && Enum.TryParse<Priority>(request.Priority, true, out var pr))
            q = q.Where(a => a.Priority == pr);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(a => a.Role.ToLower().Contains(s)
                || (a.Notes != null && a.Notes.ToLower().Contains(s))
                || db.Companies.Any(c => c.Id == a.CompanyId && c.Name.ToLower().Contains(s)));
        }

        var total = await q.CountAsync(ct);

        q = request.SortBy switch
        {
            "applied" => q.OrderByDescending(a => a.AppliedDate),
            "deadline" => q.OrderBy(a => a.Deadline),
            "priority" => q.OrderByDescending(a => a.Priority).ThenByDescending(a => a.UpdatedAt),
            "company" => q.OrderBy(a => db.Companies.First(c => c.Id == a.CompanyId).Name),
            _ => q.OrderByDescending(a => a.UpdatedAt)
        };

        var rows = await q.Skip((page - 1) * size).Take(size).ToListAsync(ct);

        var companyIds = rows.Select(r => r.CompanyId).Distinct().ToList();
        var names = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var items = rows.Select(r => r.ToDto(names.TryGetValue(r.CompanyId, out var n) ? n : "—")).ToList();
        return Result.Success(new PagedResult<ApplicationDto>(items, total, page, size));
    }
}

public sealed class GetApplicationQueryHandler(JobsDbContext db)
    : IRequestHandler<GetApplicationQuery, Result<ApplicationDto>>
{
    public async Task<Result<ApplicationDto>> Handle(GetApplicationQuery request, CancellationToken ct)
    {
        // 详情必须带出子集合(状态变更历史 + 面试轮次):
        // 子集合走 field 访问模式,不显式 Include 会得到空集合(踩过)。
        var a = await db.Applications.AsNoTracking()
            .Include(x => x.History)
            .Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure<ApplicationDto>(Error.NotFound("投递记录"));

        var name = await db.Companies.AsNoTracking().Where(c => c.Id == a.CompanyId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "—";
        return Result.Success(a.ToDto(name));
    }
}

public sealed class CreateApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<CreateApplicationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateApplicationCommand request, CancellationToken ct)
    {
        if (!await db.Companies.AnyAsync(c => c.Id == request.CompanyId, ct))
            return Result.Failure<Guid>(Error.Validation("Company.NotFound", "指定的公司不存在"));

        var app = new JobApplication(request.CompanyId, request.Role, request.Location, request.Link,
            request.Salary, request.WorkMode, request.Source, request.JdSummary);

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && Enum.TryParse<Priority>(request.Priority, true, out var p))
            app.SetPriority(p);

        db.Applications.Add(app);
        await db.SaveChangesAsync(ct);
        return Result.Success(app.Id);
    }
}

public sealed class UpdateApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<UpdateApplicationCommand, Result>
{
    public async Task<Result> Handle(UpdateApplicationCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));

        a.UpdateDetails(request.Role, request.Location, request.Link, request.Salary,
            request.WorkMode, request.Source, request.JdSummary, request.Notes);
        a.SetDeadline(request.Deadline);
        if (!string.IsNullOrWhiteSpace(request.Priority)
            && Enum.TryParse<Priority>(request.Priority, true, out var p))
            a.SetPriority(p);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ChangeApplicationStatusCommandHandler(JobsDbContext db)
    : IRequestHandler<ChangeApplicationStatusCommand, Result>
{
    public async Task<Result> Handle(ChangeApplicationStatusCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));

        if (!Enum.TryParse<ApplicationStatus>(request.Status, true, out var target))
            return Result.Failure(Error.Validation("Status.Invalid", $"无效状态:{request.Status}"));

        if (target == ApplicationStatus.Applied && a.AppliedDate is null)
        {
            a.MarkApplied(note: request.Note);
        }
        else
        {
            try
            {
                a.ChangeStatus(target, request.Note, request.RejectionReason);
            }
            catch (InvalidOperationException ex)
            {
                return Result.Failure(Error.Conflict("Status.InvalidTransition", ex.Message));
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteApplicationCommand, Result>
{
    public async Task<Result> Handle(DeleteApplicationCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class SetApplicationAnalysisCommandHandler(JobsDbContext db)
    : IRequestHandler<SetApplicationAnalysisCommand, Result>
{
    public async Task<Result> Handle(SetApplicationAnalysisCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.SetAnalysis(request.ResumeScore, request.PassRateEstimate, request.MatchKeywords);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class SetOutreachCommandHandler(JobsDbContext db)
    : IRequestHandler<SetOutreachCommand, Result>
{
    public async Task<Result> Handle(SetOutreachCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.SetOutreach(request.PosterName, request.NeedsConnectFirst, request.Message);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class MarkOutreachSentCommandHandler(JobsDbContext db)
    : IRequestHandler<MarkOutreachSentCommand, Result>
{
    public async Task<Result> Handle(MarkOutreachSentCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.MarkOutreachSent();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ScheduleFollowUpCommandHandler(JobsDbContext db)
    : IRequestHandler<ScheduleFollowUpCommand, Result>
{
    public async Task<Result> Handle(ScheduleFollowUpCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.ScheduleFollowUp(request.At);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AddInterviewRoundCommandHandler(JobsDbContext db)
    : IRequestHandler<AddInterviewRoundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddInterviewRoundCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.ApplicationId, ct);
        if (a is null) return Result.Failure<Guid>(Error.NotFound("投递记录"));

        var round = a.AddRound(request.Stage, request.ScheduledDate, request.Interviewer,
            request.Format, request.Notes);
        await db.SaveChangesAsync(ct);
        return Result.Success(round.Id);
    }
}

public sealed class UpdateInterviewRoundCommandHandler(JobsDbContext db)
    : IRequestHandler<UpdateInterviewRoundCommand, Result>
{
    public async Task<Result> Handle(UpdateInterviewRoundCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.ApplicationId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));

        if (!Enum.TryParse<RoundOutcome>(request.Outcome, true, out var outcome))
            return Result.Failure(Error.Validation("Outcome.Invalid", "无效的轮次结果"));

        try
        {
            a.UpdateRound(request.RoundId, request.Stage, request.ScheduledDate, request.Interviewer,
                request.Format, outcome, request.Notes);
        }
        catch (KeyNotFoundException ex) { return Result.Failure(Error.NotFound(ex.Message)); }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ============================ Handler:统计与看板 ============================

public sealed class GetTrackerStatsQueryHandler(JobsDbContext db)
    : IRequestHandler<GetTrackerStatsQuery, Result<TrackerStats>>
{
    public async Task<Result<TrackerStats>> Handle(GetTrackerStatsQuery request, CancellationToken ct)
    {
        var apps = await db.Applications.AsNoTracking().ToListAsync(ct);
        var total = apps.Count;

        // 状态计数
        var byStatus = apps.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count());
        int C(ApplicationStatus s) => byStatus.TryGetValue(s, out var v) ? v : 0;

        var active = apps.Count(a => a.Status is ApplicationStatus.Applied or ApplicationStatus.Screen
            or ApplicationStatus.Interview or ApplicationStatus.Offer);

        var appliedOrBeyond = apps.Count(a => a.Status != ApplicationStatus.Saved);
        var responded = apps.Count(a => a.Status is ApplicationStatus.Screen or ApplicationStatus.Interview
            or ApplicationStatus.Offer or ApplicationStatus.Accepted or ApplicationStatus.Rejected);
        var interviewed = apps.Count(a => a.Status is ApplicationStatus.Interview or ApplicationStatus.Offer
            or ApplicationStatus.Accepted);
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
            pipeline, bySource, byMonth));
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
            .Where(a => a.NextFollowUpAt != null && a.NextFollowUpAt <= until)
            .OrderBy(a => a.NextFollowUpAt)
            .Take(100).ToListAsync(ct);

        var ids = apps.Select(a => a.CompanyId).Distinct().ToList();
        var names = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return Result.Success<IReadOnlyList<ApplicationDto>>(
            apps.Select(a => a.ToDto(names.GetValueOrDefault(a.CompanyId, "—"))).ToList());
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
            .Where(a => a.Deadline != null && a.Deadline >= today && a.Deadline <= until)
            .OrderBy(a => a.Deadline).Take(100).ToListAsync(ct);

        var ids = apps.Select(a => a.CompanyId).Distinct().ToList();
        var names = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return Result.Success<IReadOnlyList<ApplicationDto>>(
            apps.Select(a => a.ToDto(names.GetValueOrDefault(a.CompanyId, "—"))).ToList());
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
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), ct);
            if (company is null)
            {
                company = new Company(name);
                db.Companies.Add(company);
                await db.SaveChangesAsync(ct);
            }

            var dup = await db.Applications.AnyAsync(a => a.CompanyId == company.Id
                && a.Role.ToLower() == row.Role.Trim().ToLower(), ct);
            if (dup)
            {
                skipped++;
                continue;
            }

            var app = new JobApplication(company.Id, row.Role.Trim(), row.Location, row.Link,
                row.Salary, null, row.Source, null);
            if (!string.IsNullOrWhiteSpace(row.Notes)) app.UpdateDetails(row.Role.Trim(), row.Location,
                row.Link, row.Salary, null, row.Source, null, row.Notes);

            if (!string.IsNullOrWhiteSpace(row.Status)
                && Enum.TryParse<ApplicationStatus>(row.Status, true, out var st))
            {
                if (st == ApplicationStatus.Applied || row.AppliedDate.HasValue)
                    app.MarkApplied(row.AppliedDate, "批量导入");
                if (st != ApplicationStatus.Saved && st != ApplicationStatus.Applied
                    && app.CanTransitionTo(st))
                    app.ChangeStatus(st, "批量导入");
            }

            db.Applications.Add(app);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(new BulkImportResult(created, skipped, errors));
    }
}
