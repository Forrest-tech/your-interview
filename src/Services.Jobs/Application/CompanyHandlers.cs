using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ Handler:公司 ============================

public sealed class ListCompaniesQueryHandler(JobsDbContext db)
    : IRequestHandler<ListCompaniesQuery, Result<IReadOnlyList<CompanyDto>>>
{
    public async Task<Result<IReadOnlyList<CompanyDto>>> Handle(ListCompaniesQuery request, CancellationToken ct)
    {
        var q = db.Companies.AsNoTracking().Where(c => c.UserId == request.UserId).AsQueryable();
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
                db.Applications.Count(a => a.CompanyId == c.Id),
                c.Profile, c.ProfileSourcesJson, c.CreatedAt, EF.Property<uint>(c, "xmin"),
                c.EmployeeCount))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<CompanyDto>>(items);
    }
}

public sealed class GetCompanyQueryHandler(JobsDbContext db)
    : IRequestHandler<GetCompanyQuery, Result<CompanyDto>>
{
    public async Task<Result<CompanyDto>> Handle(GetCompanyQuery request, CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (c is null) return Result.Failure<CompanyDto>(Error.NotFound("公司"));
        var count = await db.Applications.CountAsync(a => a.CompanyId == c.Id && a.UserId == request.UserId, ct);
        // 票101:读时 xmin 随 DTO 带出(投影直查,AsNoTracking 实体不带影子值)。
        var version = await db.Companies.AsNoTracking().Where(x => x.Id == c.Id)
            .Select(x => EF.Property<uint>(x, "xmin")).FirstAsync(ct);
        return Result.Success(c.ToDto(count, version));
    }
}

public sealed class CreateCompanyCommandHandler(JobsDbContext db)
    : IRequestHandler<CreateCompanyCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateCompanyCommand request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        var existing = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower() && c.UserId == request.UserId, ct);
        if (existing is not null)
            return Result.Success(existing.Id); // 幂等:已存在直接返回

        var logoUrl = string.IsNullOrWhiteSpace(request.LogoUrl)
            ? Company.LogoUrlFromWebsite(request.Website)
            : request.LogoUrl;

        var company = new Company(name, request.Website, request.Industry, request.Location,
            logoUrl, request.Notes, userId: request.UserId)
        {
            // CompanyType/EmployeeCount 通过 Update 设置(保持聚合封装)
        };
        company.Update(name, request.Website, request.Industry, request.Location,
            logoUrl, request.Notes, request.CompanyType, request.EmployeeCount);

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
        var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (c is null) return Result.Failure(Error.NotFound("公司"));
        ConcurrencyPin.Pin(db, c, request.Version);

        // Logo 自动解析:用户没显式给 Logo 且填了官网时,按官网 host 推导 favicon 地址。
        var logoUrl = string.IsNullOrWhiteSpace(request.LogoUrl)
            ? Company.LogoUrlFromWebsite(request.Website)
            : request.LogoUrl;

        c.Update(request.Name, request.Website, request.Industry, request.Location,
            logoUrl, request.Notes, request.CompanyType, request.EmployeeCount);
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
        var c = await db.Companies.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (c is null) return Result.Failure(Error.NotFound("公司"));

        ConcurrencyPin.Pin(db, c, request.Version);

        var count = await db.Applications.CountAsync(a => a.CompanyId == c.Id, ct);
        if (count > 0)
            return Result.Failure(Error.Conflict("Company.HasApplications",
                $"该公司下还有 {count} 条投递记录,请先处理"));

        c.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

