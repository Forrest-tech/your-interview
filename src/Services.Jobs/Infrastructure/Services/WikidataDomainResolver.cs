using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  票 249 R2:Wikidata 域名解析 —— 无 key,仅域名解析,不取 P154 logo。
//
//  流程:wbsearchentities 按名搜 → 要求 label 精确匹配(OrdinalIgnoreCase)
//  → wbgetentities 读 P856 官网 → 推导域名。平局规则(强制):两条目同
//  精确 label 且均带 P856 时什么都不存,返回 Ambiguous,由回填报告列为
//  skipped-ambiguous 待人工消歧。任何网络/解析失败返回 (null, false),
//  绝不抛异常中断回填。解析到的域名由调用方一次性写进公司 Website。
// ============================================================================

public sealed record WikidataResolution(string? Domain, bool Ambiguous);

public interface IWikidataDomainResolver
{
    Task<WikidataResolution> TryResolveDomainAsync(string companyName, CancellationToken ct);
}

public static class WikidataDomainResolverOptions
{
    public const string ClientName = "Wikidata";
}

public sealed class WikidataDomainResolver(
    IHttpClientFactory httpFactory,
    ILogger<WikidataDomainResolver> logger) : IWikidataDomainResolver
{
    public async Task<WikidataResolution> TryResolveDomainAsync(string companyName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(companyName)) return new WikidataResolution(null, false);
        var name = companyName.Trim();
        try
        {
            var client = httpFactory.CreateClient(WikidataDomainResolverOptions.ClientName);
            var searchUrl = "https://www.wikidata.org/w/api.php?action=wbsearchentities"
                + $"&search={Uri.EscapeDataString(name)}&language=en&type=item&limit=20&format=json&formatversion=2";
            using var searchResp = await client.GetAsync(searchUrl, ct);
            if (!searchResp.IsSuccessStatusCode) return new WikidataResolution(null, false);
            var searchJson = await searchResp.Content.ReadAsStringAsync(ct);
            var exactIds = new List<string>();
            using (var doc = JsonDocument.Parse(searchJson))
            {
                if (!doc.RootElement.TryGetProperty("search", out var results)) return new WikidataResolution(null, false);
                foreach (var r in results.EnumerateArray())
                {
                    var id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var label = r.TryGetProperty("label", out var l) ? l.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(label)) continue;
                    if (string.Equals(label.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        exactIds.Add(id);
                }
            }
            if (exactIds.Count == 0) return new WikidataResolution(null, false);

            var entitiesUrl = "https://www.wikidata.org/w/api.php?action=wbgetentities"
                + $"&ids={Uri.EscapeDataString(string.Join("|", exactIds.Distinct()))}"
                + "&props=claims&format=json&formatversion=2";
            using var entitiesResp = await client.GetAsync(entitiesUrl, ct);
            if (!entitiesResp.IsSuccessStatusCode) return new WikidataResolution(null, false);
            var entitiesJson = await entitiesResp.Content.ReadAsStringAsync(ct);
            var withWebsite = new List<string>();
            using (var doc = JsonDocument.Parse(entitiesJson))
            {
                if (!doc.RootElement.TryGetProperty("entities", out var entities)) return new WikidataResolution(null, false);
                foreach (var id in exactIds.Distinct())
                {
                    if (!entities.TryGetProperty(id, out var entity)) continue;
                    if (!entity.TryGetProperty("claims", out var claims)) continue;
                    if (!claims.TryGetProperty("P856", out var p856)) continue;
                    foreach (var claim in p856.EnumerateArray())
                    {
                        if (!claim.TryGetProperty("mainsnak", out var mainsnak)) continue;
                        if (!mainsnak.TryGetProperty("datavalue", out var dv)) continue;
                        if (!dv.TryGetProperty("value", out var v)) continue;
                        var url = v.GetString();
                        if (string.IsNullOrWhiteSpace(url)) continue;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
                        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
                        if (string.IsNullOrWhiteSpace(uri.Host)) continue;
                        withWebsite.Add(uri.Host.ToLowerInvariant());
                        break;
                    }
                }
            }
            if (withWebsite.Count == 1) return new WikidataResolution(withWebsite[0], false);
            if (withWebsite.Count >= 2) return new WikidataResolution(null, true);
            return new WikidataResolution(null, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "票249:Wikidata 域名解析失败: {Company}", name);
            return new WikidataResolution(null, false);
        }
    }
}
