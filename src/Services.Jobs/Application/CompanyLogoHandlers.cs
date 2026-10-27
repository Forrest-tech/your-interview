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

/// <summary>
/// 批量回填结果:成功/跳过/失败三数 + 清洗数 + 失败公司名清单(只进响应体与日志)。
/// 票 249:跳过与待消歧公司名亦进响应体,供回填报告 UI 区分展示
/// (跳过 = 本轮未找到可用 logo;待消歧 = Wikidata 平局未写域名;失败 = 尝试中抛异常)。
/// 三类名清单互斥:待消歧不计入跳过数,跳过数只含非待消歧的空结果。
/// </summary>
public sealed record BackfillLogosResultDto(
    int Success, int Skipped, int Failed, int Cleaned, IReadOnlyList<string> FailedCompanies,
    IReadOnlyList<string>? SkippedCompanies = null, IReadOnlyList<string>? AmbiguousCompanies = null);

public sealed record RefreshLogoCommand(Guid UserId, Guid CompanyId) : IRequest<Result<RefreshLogoResultDto>>;

/// <summary>单家刷新结果:Status ∈ Updated / Cleaned / Unchanged / NoLogoFound。</summary>
public sealed record RefreshLogoResultDto(Guid CompanyId, string Status, string? LogoUrl);

public sealed class BackfillLogosCommandHandler(
    JobsDbContext db,
    ICompanyLogoFetcher fetcher,
    ILocalLogoCatalog catalog,
    IWikidataDomainResolver wikidata,
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

        // ② 回填:只处理空值(含刚清洗出的空值),按票 249 链序逐家尝试。
        var success = 0;
        var skipped = 0;
        var failed = 0;
        var failedCompanies = new List<string>();
        var skippedCompanies = new List<string>();
        var ambiguousCompanies = new List<string>();
        foreach (var c in companies.Where(c => string.IsNullOrWhiteSpace(c.LogoUrl)))
        {
            try
            {
                var r = await CompanyLogoResolver.ResolveLogoAsync(
                    db, fetcher, catalog, wikidata, c, request.UserId, ct);
                if (r.Ambiguous)
                {
                    // Wikidata 平局:域名未写,本轮不计跳过,单列待消歧。
                    ambiguousCompanies.Add(c.Name);
                }
                else if (r.LogoUrl is not null)
                {
                    c.SetLogoUrl(r.LogoUrl);
                    success++;
                }
                else
                {
                    skipped++;
                    skippedCompanies.Add(c.Name);
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

        // R2 解析到的域名挂在被追踪实体上,随本 SaveChanges 一并持久化
        // (即使该公司本轮仍无 logo,下轮可直接走 favicon 环)。
        await db.SaveChangesAsync(ct);
        return Result.Success(new BackfillLogosResultDto(
            success, skipped, failed, cleaned, failedCompanies, skippedCompanies, ambiguousCompanies));
    }
}

/// <summary>
/// 票 249 链序(每环 fall-through,全环失败才算最终 miss):
/// R1 本地开源层(按公司名) → LinkedIn 重提 → R2(Wikidata 域名解析,
/// 仅 Website 为空时) → Website favicon 环(C1 修好的 s2/首页/favicon)。
/// WebsiteResolved 表示本轮新解析并写进了公司 Website(回填终态 SaveChanges
/// 本就覆盖;单家刷新须据此单独 SaveChanges)。Ambiguous 表示 Wikidata
/// 平局:域名未写、亦未尝试 favicon,调用方单列待消歧。
/// </summary>
internal sealed record LogoResolution(string? LogoUrl, bool WebsiteResolved, bool Ambiguous);

internal static class CompanyLogoResolver
{
    internal static async Task<LogoResolution> ResolveLogoAsync(
        JobsDbContext db, ICompanyLogoFetcher fetcher, ILocalLogoCatalog catalog,
        IWikidataDomainResolver wikidata, Domain.Company company, Guid userId, CancellationToken ct)
    {
        // R1 本地层:离线、无 key,命中即存网关相对地址(经 /api/jobs 前缀回 Jobs)。
        var local = catalog.TryResolve(company.Name);
        if (local is not null)
            return new LogoResolution(
                LocalLogoCatalog.LocalLogoUrl(local.Value.Provider, local.Value.Key), false, false);

        var links = await db.Applications
            .Where(a => a.CompanyId == company.Id && a.UserId == userId)
            .Select(a => new ValueTuple<string?, DateTimeOffset>(a.Link, a.CreatedAt))
            .ToListAsync(ct);
        foreach (var link in CompanyLogoRules.SelectLinkedInLinks(
                     links.Select(x => (x.Item1, x.Item2))))
        {
            var liLogo = await fetcher.TryLinkedInLogoAsync(link, ct);
            if (!string.IsNullOrWhiteSpace(liLogo)) return new LogoResolution(liLogo, false, false);
        }

        // R2:仅 Website 为空时解析域名;解析到则写进实体并继续走 favicon 环。
        var website = company.Website;
        if (string.IsNullOrWhiteSpace(website))
        {
            var r = await wikidata.TryResolveDomainAsync(company.Name, ct);
            if (r.Ambiguous) return new LogoResolution(null, false, true);
            if (!string.IsNullOrWhiteSpace(r.Domain))
            {
                company.SetWebsite(r.Domain);
                website = r.Domain;
                var favicon = await fetcher.TryFaviconAsync(website, ct);
                return string.IsNullOrWhiteSpace(favicon)
                    ? new LogoResolution(null, true, false)
                    : new LogoResolution(favicon, true, false);
            }
            return new LogoResolution(null, false, false);
        }

        // Website 已有(含聚合站值,聚合站在 TryFaviconAsync 内拒绝) → 直接走 favicon 环。
        var existing = await fetcher.TryFaviconAsync(website, ct);
        return string.IsNullOrWhiteSpace(existing)
            ? new LogoResolution(null, false, false)
            : new LogoResolution(existing, false, false);
    }
}

public sealed class RefreshLogoCommandHandler(
    JobsDbContext db,
    ICompanyLogoFetcher fetcher,
    ILocalLogoCatalog catalog,
    IWikidataDomainResolver wikidata,
    ILogger<RefreshLogoCommandHandler> logger)
    : IRequestHandler<RefreshLogoCommand, Result<RefreshLogoResultDto>>
{
    public async Task<Result<RefreshLogoResultDto>> Handle(RefreshLogoCommand request, CancellationToken ct)
    {
        var company = await db.Companies
            .FirstOrDefaultAsync(c => c.Id == request.CompanyId && c.UserId == request.UserId, ct);
        if (company is null) return Result.Failure<RefreshLogoResultDto>(Error.NotFound("公司"));

        // 第 1 步除外:已有值也不跳过,用户显式触发即按票 249 链序重算。
        string? logo = null;
        var websiteResolved = false;
        try
        {
            var r = await CompanyLogoResolver.ResolveLogoAsync(
                db, fetcher, catalog, wikidata, company, request.UserId, ct);
            logo = r.LogoUrl;
            websiteResolved = r.WebsiteResolved;
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

        // 本轮新解析到域名即使未找到 logo 也要持久化,下轮可直接走 favicon 环。
        if (websiteResolved) await db.SaveChangesAsync(ct);

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
