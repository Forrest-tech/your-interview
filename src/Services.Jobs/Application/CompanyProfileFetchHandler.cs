using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;

namespace YourInterview.Services.Jobs.Application;

/// <summary>
/// 票142 R-142-11:抓取公司简介取数端点的 Handler。
/// 只读公司行(归属校验:他人公司回 404,与 GetCompany/SetCompanyProfile 同口径),
/// 取数不写库、不新增字段,零迁移。
/// </summary>
public sealed class FetchCompanyProfileQueryHandler(JobsDbContext db, ICompanyProfileFetcher fetcher)
    : IRequestHandler<FetchCompanyProfileQuery, Result<FetchCompanyProfileResult>>
{
    public async Task<Result<FetchCompanyProfileResult>> Handle(FetchCompanyProfileQuery request, CancellationToken ct)
    {
        var c = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (c is null) return Result.Failure<FetchCompanyProfileResult>(Error.NotFound("公司"));

        var r = await fetcher.FetchAsync(c.Name, c.Website, ct);
        return Result.Success(new FetchCompanyProfileResult(
            r.CandidateText, r.SourceUrl, r.NotFound, r.Tried));
    }
}
