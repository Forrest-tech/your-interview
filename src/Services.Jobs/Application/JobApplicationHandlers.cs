using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ Handler:投递记录 ============================

public sealed class ListApplicationsQueryHandler(JobsDbContext db)
    : IRequestHandler<ListApplicationsQuery, Result<PagedResult<ApplicationDto>>>
{
    public async Task<Result<PagedResult<ApplicationDto>>> Handle(ListApplicationsQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);

        var q = db.Applications.AsNoTracking().Where(a => a.UserId == request.UserId).AsQueryable();
        if (request.CompanyId.HasValue) q = q.Where(a => a.CompanyId == request.CompanyId.Value);

        // ★ 归档过滤:默认只看未归档,Archived=true 只看已归档,null 看全部
        if (request.Archived.HasValue)
            q = q.Where(a => a.IsArchived == request.Archived.Value);
        else
            q = q.Where(a => !a.IsArchived);

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
            "appliedAsc" => q.OrderBy(a => a.AppliedDate),
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
        var logoUrls = await db.Companies.AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.LogoUrl, ct);

        // 票101:列表行同样带读时 xmin(单查整页,避免列表来源的编辑表单钉错版)。
        var rowIds = rows.Select(r => r.Id).ToList();
        var versions = await db.Applications.AsNoTracking().Where(x => rowIds.Contains(x.Id))
            .Select(x => new { x.Id, V = EF.Property<uint>(x, "xmin") })
            .ToDictionaryAsync(x => x.Id, x => x.V, ct);
        var roundVersions = await db.Rounds.AsNoTracking().Where(r => rowIds.Contains(r.ApplicationId))
            .Select(r => new { r.ApplicationId, r.Id, V = EF.Property<uint>(r, "xmin") })
            .ToListAsync(ct);
        var roundVerByApp = roundVersions.GroupBy(x => x.ApplicationId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<Guid, uint>)g.ToDictionary(x => x.Id, x => x.V));
        var items = rows.Select(r => r.ToDto(
            names.TryGetValue(r.CompanyId, out var n) ? n : "—",
            logoUrls.TryGetValue(r.CompanyId, out var l) ? l : null,
            versions.TryGetValue(r.Id, out var v) ? v : 0,
            roundVerByApp.TryGetValue(r.Id, out var rv) ? rv : null)).ToList();
        return Result.Success(new PagedResult<ApplicationDto>(items, total, page, size));
    }
}

/// <summary>
/// 写 JD 全文。
/// ⚠️ 用 FirstOrDefaultAsync(跟踪实体)而非 AsNoTracking —— 要改状态并保存。
/// </summary>
public sealed class SetJdContentCommandHandler(JobsDbContext db)
    : IRequestHandler<SetJdContentCommand, Result>
{
    public async Task<Result> Handle(SetJdContentCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        ConcurrencyPin.Pin(db, a, request.Version);

        a.SetJdContent(request.JdText, request.JdSourceUrl);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>写公司情报长文本。</summary>
public sealed class SetCompanyProfileCommandHandler(JobsDbContext db)
    : IRequestHandler<SetCompanyProfileCommand, Result>
{
    public async Task<Result> Handle(SetCompanyProfileCommand request, CancellationToken ct)
    {
        var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (c is null) return Result.Failure(Error.NotFound("公司"));
        ConcurrencyPin.Pin(db, c, request.Version);

        c.UpdateProfile(request.Profile, request.ProfileSourcesJson);
        await db.SaveChangesAsync(ct);
        return Result.Success();
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
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure<ApplicationDto>(Error.NotFound("投递记录"));

        var company = await db.Companies.AsNoTracking().Where(c => c.Id == a.CompanyId)
            .Select(c => new { c.Name, c.LogoUrl }).FirstOrDefaultAsync(ct);
        // 票101:读时 xmin(投递行 + 各轮次行)随 DTO 带出。
        var version = await db.Applications.AsNoTracking().Where(x => x.Id == a.Id)
            .Select(x => EF.Property<uint>(x, "xmin")).FirstAsync(ct);
        var roundVersions = await db.Rounds.AsNoTracking().Where(r => r.ApplicationId == a.Id)
            .Select(r => new { r.Id, V = EF.Property<uint>(r, "xmin") })
            .ToDictionaryAsync(x => x.Id, x => x.V, ct);
        return Result.Success(a.ToDto(company?.Name ?? "—", company?.LogoUrl, version, roundVersions));
    }
}

public sealed class CreateApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<CreateApplicationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateApplicationCommand request, CancellationToken ct)
    {
        if (!await db.Companies.AnyAsync(c => c.Id == request.CompanyId && c.UserId == request.UserId, ct))
            return Result.Failure<Guid>(Error.Validation("Company.NotFound", "指定的公司不存在"));

        var app = new JobApplication(request.CompanyId, request.Role, request.Location, request.Link,
            request.Salary, request.WorkMode, request.Source, request.JdSummary, userId: request.UserId);

        if (!string.IsNullOrWhiteSpace(request.CompanyDescription))
            app.SetCompanyDescription(request.CompanyDescription);

        if (!string.IsNullOrWhiteSpace(request.WorkAuthorization))
            app.SetWorkAuthorization(request.WorkAuthorization);

        if (!string.IsNullOrWhiteSpace(request.JobType))
            app.SetJobType(request.JobType);

        if (!string.IsNullOrWhiteSpace(request.Requirements))
            app.SetRequirements(request.Requirements);

        if (!string.IsNullOrWhiteSpace(request.Benefits))
            app.SetBenefits(request.Benefits);

        // FUN-02:手动建单的 JD 全文与备注随建单落库 (出处 URL 不借用 Link 字段,保持 null)。
        if (!string.IsNullOrWhiteSpace(request.JdText))
            app.SetJdContent(request.JdText, null);

        if (!string.IsNullOrWhiteSpace(request.Notes))
            app.SetNotes(request.Notes);

        if (request.AppliedDate.HasValue)
            app.SetAppliedDate(request.AppliedDate);

        if (!string.IsNullOrWhiteSpace(request.Priority)
            && Enum.TryParse<Priority>(request.Priority, true, out var p))
            app.SetPriority(p);

        // CSV 导入等场景允许指定初始状态(默认 Saved)。
        // ★ F-09:沿状态机路径逐级走到目标状态,而不是"一步不可达就静默丢弃"——
        //   丢弃会让 Tracker 状态与真实进度自相矛盾(已面试却显示 Saved)。
        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<ApplicationStatus>(request.Status, true, out var st)
            && st != app.Status)
        {
            app.TryWalkToStatus(st, "CSV import");
        }

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
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        ConcurrencyPin.Pin(db, a, request.Version);

        a.UpdateDetails(request.Role, request.Location, request.Link, request.Salary,
            request.WorkMode, request.Source, request.JdSummary, request.Notes,
            request.CompanyDescription, request.WorkAuthorization, request.JobType,
            request.Requirements, request.Benefits);
        // FUN-02:JdText 为 null 表示调用方未提供 (局部保存),保持原值与出处;
        // 非 null 才按新值覆盖 (空串在 SetJdContent 内归一为清空)。
        if (request.JdText is not null)
            a.SetJdContent(request.JdText, a.JdSourceUrl);
        a.SetDeadline(request.Deadline);
        if (request.AppliedDate.HasValue)
            a.SetAppliedDate(request.AppliedDate);
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
        // Include Rounds:切到 Interview 时的"第几轮邀请"按已有轮次计数,
        // 不带 Rounds 进来会永远算成第 1 轮(与 AddRound 同源的加载缺陷)
        var a = await db.Applications.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
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
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ArchiveApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<ArchiveApplicationCommand, Result>
{
    public async Task<Result> Handle(ArchiveApplicationCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.Archive();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class UnarchiveApplicationCommandHandler(JobsDbContext db)
    : IRequestHandler<UnarchiveApplicationCommand, Result>
{
    public async Task<Result> Handle(UnarchiveApplicationCommand request, CancellationToken ct)
    {
        // IgnoreQueryFilters 绕过全局过滤器(含归属过滤)—— 必须自带 UserId 谓词(COD-01 双保险缺口点)
        var a = await db.Applications.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId && !x.IsDeleted, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        a.Unarchive();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class SetApplicationAnalysisCommandHandler(JobsDbContext db)
    : IRequestHandler<SetApplicationAnalysisCommand, Result>
{
    public async Task<Result> Handle(SetApplicationAnalysisCommand request, CancellationToken ct)
    {
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        ConcurrencyPin.Pin(db, a, request.Version);
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
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        ConcurrencyPin.Pin(db, a, request.Version);
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
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
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
        var a = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));
        ConcurrencyPin.Pin(db, a, request.Version);
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
        // ⚠️ 必须 Include Rounds:轮次号 = 已有轮数 + 1,
        // 不带进来 _rounds 恒为空 → 每一轮都算第 1 轮(既有 bug,本轮修复)
        var a = await db.Applications.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId && x.UserId == request.UserId, ct);
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
        // ⚠️ 必须 Include Rounds:UpdateRound 在聚合里按 Id 找轮次,
        // 不带进来永远抛"面试轮次不存在"(既有 bug,本轮修复)
        var a = await db.Applications.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId && x.UserId == request.UserId, ct);
        if (a is null) return Result.Failure(Error.NotFound("投递记录"));

        if (!Enum.TryParse<RoundOutcome>(request.Outcome, true, out var outcome))
            return Result.Failure(Error.Validation("Outcome.Invalid", "无效的轮次结果"));

        // 票101:钉版对象是轮次行本身(其 xmin 独立于投递行)。
        if (request.Version is { } rv)
        {
            var round = a.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
            if (round is not null) ConcurrencyPin.Pin(db, round, rv);
        }

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

