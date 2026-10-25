using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  票 150:公司 logo 回填端点的编排层。
//
//  触发(§2.3 + §8 U-150-1 组合):批量 + 单家。
//  - 批量 POST /api/jobs/companies/backfill-logos(AdminSystemWrite):
//    幂等,只处理 LogoUrl 为空的公司;先清洗存量 s2 默认图标(命中则置 null,
//    §8 U-150-4),再按 §2.2 顺序回填(清洗后变空的公司同批参与回填)。
//    第二次跑无新写入(成功数 0,除非第一次失败的公司此次成功)。
//  - 单家 POST /api/jobs/companies/{id}/refresh-logo(JobsWrite):
//    按 §2.2 顺序(第 1 步除外,即已有值也不跳过)重算一家;算出新值则覆盖,
//    算不出且存量是 s2 默认图标则清洗置 null,算不出且存量为显式值则保持。
//  失败铁律:单家失败不中断整批,原因只进服务端日志,前端只见色块。
// ============================================================================

public sealed record BackfillLogosCommand(Guid UserId) : IRequest<Result<BackfillLogosResultDto>>;

/// <summary>批量回填结果:成功/跳过/失败三数 + 清洗数 + 失败公司名清单(只进响应体与日志)。</summary>
public sealed record BackfillLogosResultDto(
    int Success, int Skipped, int Failed, int Cleaned, IReadOnlyList<string> FailedCompanies);

public sealed record RefreshLogoCommand(Guid UserId, Guid CompanyId) : IRequest<Result<RefreshLogoResultDto>>;

/// <summary>单家刷新结果:Status ∈ Updated / Cleaned / Unchanged / NoLogoFound。</summary>
public sealed record RefreshLogoResultDto(Guid CompanyId, string Status, string? LogoUrl);

public sealed class BackfillLogosCommandHandler(
    JobsDbContext db,
    ICompanyLogoFetcher fetcher,
    ILogger<BackfillLogosCommandHandler> logger)
    : IRequestHandler<BackfillLogosCommand, Result<BackfillLogosResultDto>>
{
    public async Task<Result<BackfillLogosResultDto>> Handle(BackfillLogosCommand request, CancellationToken ct)
    {
        var companies = await db.Companies
            .Where(c => c.UserId == request.UserId)
            .ToListAsync(ct);

        // ① 清洗:存量 s2 地址命中默认图标 → 置 null(§8 U-150-4)。
        // 清洗失败(抓取/比对异常)只记日志、不计数、不中断:证据不足时不删数据。
        var cleaned = 0;
        foreach (var c in companies.Where(c => CompanyLogoRules.IsGoogleFaviconUrl(c.LogoUrl)))
        {
            try
            {
                if (await fetcher.IsDefaultFaviconAsync(c.LogoUrl!, ct))
                {
                    c.SetLogoUrl(null);
                    cleaned++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "票150:默认图标清洗失败,保留原值: {Company}", c.Name);
            }
        }

        // ② 回填:只处理空值(含刚清洗出的空值),按 §2.2 顺序逐家尝试。
        var success = 0;
        var skipped = 0;
        var failed = 0;
        var failedCompanies = new List<string>();
        foreach (var c in companies.Where(c => string.IsNullOrWhiteSpace(c.LogoUrl)))
        {
            try
            {
                var logo = await CompanyLogoResolver.ResolveLogoAsync(db, fetcher, c, request.UserId, ct);
                if (logo is null)
                {
                    skipped++;
                }
                else
                {
                    c.SetLogoUrl(logo);
                    success++;
                }
            }
            catch (Exception ex)
            {
                // 单家异常不中断整批:保持 null → 前端色块,原因只进日志。
                logger.LogWarning(ex, "票150:回填失败,保持空值: {Company}", c.Name);
                failed++;
                failedCompanies.Add(c.Name);
            }
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(new BackfillLogosResultDto(success, skipped, failed, cleaned, failedCompanies));
    }
}

/// <summary>两端点共用的 §2.2 取数顺序:LinkedIn 重提 → Website 派生;全无则 null。</summary>
internal static class CompanyLogoResolver
{
    internal static async Task<string?> ResolveLogoAsync(
        JobsDbContext db, ICompanyLogoFetcher fetcher,
        Domain.Company company, Guid userId, CancellationToken ct)
    {
        var links = await db.Applications
            .Where(a => a.CompanyId == company.Id && a.UserId == userId)
            .Select(a => new ValueTuple<string?, DateTimeOffset>(a.Link, a.CreatedAt))
            .ToListAsync(ct);
        foreach (var link in CompanyLogoRules.SelectLinkedInLinks(
                     links.Select(x => (x.Item1, x.Item2))))
        {
            var liLogo = await fetcher.TryLinkedInLogoAsync(link, ct);
            if (!string.IsNullOrWhiteSpace(liLogo)) return liLogo;
        }
        if (!string.IsNullOrWhiteSpace(company.Website)
            && !CompanyLogoRules.IsAggregatorWebsite(company.Website))
        {
            var favicon = await fetcher.TryFaviconAsync(company.Website, ct);
            if (!string.IsNullOrWhiteSpace(favicon)) return favicon;
        }
        return null;
    }
}

public sealed class RefreshLogoCommandHandler(
    JobsDbContext db,
    ICompanyLogoFetcher fetcher,
    ILogger<RefreshLogoCommandHandler> logger)
    : IRequestHandler<RefreshLogoCommand, Result<RefreshLogoResultDto>>
{
    public async Task<Result<RefreshLogoResultDto>> Handle(RefreshLogoCommand request, CancellationToken ct)
    {
        var company = await db.Companies
            .FirstOrDefaultAsync(c => c.Id == request.CompanyId && c.UserId == request.UserId, ct);
        if (company is null) return Result.Failure<RefreshLogoResultDto>(Error.NotFound("公司"));

        // 第 1 步除外:已有值也不跳过,用户显式触发即重算。
        string? logo = null;
        try
        {
            logo = await CompanyLogoResolver.ResolveLogoAsync(db, fetcher, company, request.UserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "票150:单家刷新取数失败: {Company}", company.Name);
        }

        if (!string.IsNullOrWhiteSpace(logo))
        {
            company.SetLogoUrl(logo);
            await db.SaveChangesAsync(ct);
            return Result.Success(new RefreshLogoResultDto(company.Id, "Updated", logo));
        }

        // 算不出新值:存量是 s2 默认图标则清洗置 null,否则保持原值。
        if (CompanyLogoRules.IsGoogleFaviconUrl(company.LogoUrl))
        {
            bool isDefault;
            try { isDefault = await fetcher.IsDefaultFaviconAsync(company.LogoUrl!, ct); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "票150:单家刷新默认图标比对失败,保持原值: {Company}", company.Name);
                isDefault = false;
            }
            if (isDefault)
            {
                company.SetLogoUrl(null);
                await db.SaveChangesAsync(ct);
                return Result.Success(new RefreshLogoResultDto(company.Id, "Cleaned", null));
            }
        }

        return string.IsNullOrWhiteSpace(company.LogoUrl)
            ? Result.Success(new RefreshLogoResultDto(company.Id, "NoLogoFound", null))
            : Result.Success(new RefreshLogoResultDto(company.Id, "Unchanged", company.LogoUrl));
    }
}
