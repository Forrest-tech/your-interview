using System.Net;
using Microsoft.Extensions.Logging;
using YourInterview.Services.Jobs.Application;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  票 150:公司 logo 取数 —— 只负责 I/O,决策在 CompanyLogoRules。
//  票 249 改动:①重定向跟随 —— 旧 client 禁一切重定向,导致 s2 的
//  301→gstatic 与官网 naked→www 301/308 全灭;现循环内手动跟随,放行
//  判定只认 TryResolveRedirectUri(同 registrable-domain 或 s2→gstatic
//  host-exact 豁口),每跳重做 SSRF 校验,跳数上限 5,底层再由
//  ConnectCallback 做 resolve-then-fetch pinning(见 Program.cs)。
//  ②质量挑选 —— favicon 环内 s2/首页图标/favicon.ico 三路候选齐取,
//  按 PickBestLogo 留最优(SVG 恒胜、短边≥32 优先、唯一资产无论多小
//  都保留),不再首中即返。③s2 默认图标关修复 —— 旧参照探针走 200-only
//  抓取,而新 faviconV2 对无图标域名回 404 + 默认图体,致参照恒 null、
//  关失效;现参照改走状态无关体读取(404 体即默认图),候选与参照逐字
//  节相等即默认图标。失败一律返回 null(或 false),绝不抛异常中断
//  回填整批;原因只进日志。
// ============================================================================

public interface ICompanyLogoFetcher
{
    /// <summary>重提 LinkedIn 职位页的 li-logo;任何失败返回 null。</summary>
    Task<string?> TryLinkedInLogoAsync(string url, CancellationToken ct);

    /// <summary>Website 派生 favicon(s2 + 首页发现 + favicon.ico,质量最优);无图标返回 null。</summary>
    Task<string?> TryFaviconAsync(string website, CancellationToken ct);

    /// <summary>存量 s2 地址是否为默认图标(抓取/比对失败时返回 false,即不清洗)。</summary>
    Task<bool> IsDefaultFaviconAsync(string s2Url, CancellationToken ct);
}

public static class CompanyLogoFetcherOptions
{
    public const string ClientName = "CompanyLogo";
}

public sealed class CompanyLogoFetcher(
    IHttpClientFactory httpFactory,
    LinkedInFetcher linkedIn,
    ILogger<CompanyLogoFetcher> logger,
    Func<string, Task<IPAddress[]>>? hostResolver = null) : ICompanyLogoFetcher
{
    // 默认图标参照:向 s2 要一个注定无 favicon 的域名的图标 ——
    // 新 faviconV2 对无图标域名回 404 + 固定默认图体,该体即参照字节。
    // 同一实例内只取一次;取不到则关不可靠,后续直接走 (b)。
    private const string DefaultProbeDomain = "invalid.invalid";
    private byte[]? _defaultIconBytes;
    private bool _defaultIconProbed;

    public async Task<string?> TryLinkedInLogoAsync(string url, CancellationToken ct)
    {
        try
        {
            var (ok, html, _) = await linkedIn.FetchJobHtmlAsync(url, ct);
            if (!ok || string.IsNullOrWhiteSpace(html)) return null;
            var kind = JdPasteParser.DetectKind(html);
            var parsed = JdPasteParser.Parse(html, kind);
            return string.IsNullOrWhiteSpace(parsed.LogoUrl) ? null : parsed.LogoUrl.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "票150:LinkedIn logo 重提失败: {Url}", url);
            return null;
        }
    }

    public async Task<string?> TryFaviconAsync(string website, CancellationToken ct)
    {
        var host = CompanyLogoRules.TryGetHost(website);
        if (string.IsNullOrWhiteSpace(host)) return null;
        if (CompanyLogoRules.IsAggregatorWebsite(website)) return null;
        if (!await CompanyLogoRules.IsSafeHostAsync(host, ct, hostResolver)) return null;

        var candidates = new List<CompanyLogoRules.LogoCandidate>();

        // (a) Google s2 + 默认图标关:候选字节须与参照不同才算真实图标。
        var s2Url = Domain.Company.LogoUrlFromWebsite($"https://{host}/");
        if (s2Url is not null)
        {
            var (s2, s2Bytes) = await FetchImageWithDimsAsync(s2Url, ct);
            if (s2 is not null && s2Bytes is not null)
            {
                var reference = await GetDefaultIconBytesAsync(ct);
                if (reference is not null)
                {
                    if (!CompanyLogoRules.IsDefaultIcon(s2Bytes, reference)) candidates.Add(s2);
                    // 命中默认图标:不入选,落到 (b) 再找一次。
                }
                else
                {
                    // 参照取不到 = 默认图标关不可靠,按 §8 兜底不硬上 (a)。
                    logger.LogWarning("票150:s2 默认图标参照取不到,改走自研 favicon 发现: {Host}", host);
                }
            }
            // s2 本身抓不到时同样落到 (b)。
        }

        // (b) 自研 favicon 发现:官网首页 link[rel=icon] → /favicon.ico,均须校验为图片。
        // 相对图标地址须按重定向后的最终 URL 解析(票 249 实测:naked→www 后仍按
        // 原 URL 解析会命中旧 host 的 404,白白丢掉已跟随到的首页图标)。
        var pageUrl = CompanyLogoRules.NormalizeWebsiteUrl(website);
        string? finalPageUrl = null;
        if (pageUrl is not null)
        {
            try
            {
                var (status, mediaType, body, finalUrl) = await GetWithRedirectsAsync(pageUrl, ct, 2_000_000);
                finalPageUrl = finalUrl;
                if (status == 200 && body is not null
                    && (string.IsNullOrWhiteSpace(mediaType)
                        || mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                        || mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)))
                {
                    var html = System.Text.Encoding.UTF8.GetString(body);
                    if (html.Length is >= 200 and <= 2_000_000)
                    {
                        var iconUrl = CompanyLogoRules.ExtractIconUrl(html, finalPageUrl ?? pageUrl);
                        if (iconUrl is not null)
                        {
                            var icon = await TryFetchCandidateAsync(iconUrl, ct);
                            if (icon is not null) candidates.Add(icon);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "票150:官网首页抓取失败: {Url}", pageUrl);
            }

            try
            {
                var baseUri = new Uri(finalPageUrl ?? pageUrl);
                var faviconUrl = new Uri(baseUri, "/favicon.ico").ToString();
                var favicon = await TryFetchCandidateAsync(faviconUrl, ct);
                if (favicon is not null) candidates.Add(favicon);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "票150:/favicon.ico 校验失败: {Url}", pageUrl);
            }
        }

        // 质量最优者胜出;唯一候选无论多小都保留(软化门,不落回首字母)。
        return CompanyLogoRules.PickBestLogo(candidates)?.Url;
    }

    public async Task<bool> IsDefaultFaviconAsync(string s2Url, CancellationToken ct)
    {
        try
        {
            // 清洗路径须认 404 默认图体:状态无关体读取,体与参照相等即默认。
            var bytes = await FetchBodyAnyStatusAsync(s2Url, ct);
            var reference = await GetDefaultIconBytesAsync(ct);
            if (bytes is null || reference is null) return false;
            return CompanyLogoRules.IsDefaultIcon(bytes, reference);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "票150:默认图标比对失败(不清洗): {Url}", s2Url);
            return false;
        }
    }

    /// <summary>取参照字节:同一实例只探一次,探不到则记为已探(后续直接走 b)。</summary>
    private async Task<byte[]?> GetDefaultIconBytesAsync(CancellationToken ct)
    {
        if (_defaultIconProbed) return _defaultIconBytes;
        _defaultIconProbed = true;
        var probeUrl = $"https://www.google.com/s2/favicons?sz=128&domain={DefaultProbeDomain}";
        _defaultIconBytes = await FetchBodyAnyStatusAsync(probeUrl, ct);
        return _defaultIconBytes;
    }

    /// <summary>
    /// 抓一张候选图:200 + image/* + 非空 + ≤1MB 才入选,附尺寸嗅探与原始字节
    /// (s2 默认图标关需字节比对)。任何失败返回 (null, null)。
    /// </summary>
    private async Task<(CompanyLogoRules.LogoCandidate? Candidate, byte[]? Bytes)> FetchImageWithDimsAsync(
        string url, CancellationToken ct)
    {
        var (status, mediaType, body, _) = await GetWithRedirectsAsync(url, ct, 1_000_000);
        if (status != 200 || body is null || body.Length == 0) return (null, null);
        if (string.IsNullOrWhiteSpace(mediaType)
            || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return (null, null);
        var (isSvg, w, h) = CompanyLogoRules.TryGetImageDimensions(body, mediaType);
        return (new CompanyLogoRules.LogoCandidate(url, isSvg, w, h), body);
    }

    /// <summary>抓一张候选图(只要候选,不要字节):首页图标与 favicon 兜底路径用。</summary>
    private async Task<CompanyLogoRules.LogoCandidate?> TryFetchCandidateAsync(string url, CancellationToken ct)
    {
        var (candidate, _) = await FetchImageWithDimsAsync(url, ct);
        return candidate;
    }

    /// <summary>
    /// 状态无关图片体读取(默认图标参照与清洗路径用):新 faviconV2 对无图标
    /// 域名回 404 + 默认图体,体本身即参照;非图片、超 1MB、无体一律 null。
    /// </summary>
    private async Task<byte[]?> FetchBodyAnyStatusAsync(string url, CancellationToken ct)
    {
        var (_, mediaType, body, _) = await GetWithRedirectsAsync(url, ct, 1_000_000);
        if (body is null || body.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(mediaType)
            || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        return body;
    }

    /// <summary>
    /// 手动重定向循环:HttpClient 禁自动重定向,此处逐跳判定。
    /// 每跳先做 SSRF host 校验(失败即停);Location 经
    /// TryResolveRedirectUri 放行才跟随(同 registrable-domain 或
    /// s2→gstatic host-exact 豁口);跳数上限 5。任何异常返回 (0, null, null)。
    /// FinalUrl 为实际取到体的 URL(相对地址解析用);失败路径下为最后尝试的 URL。
    /// </summary>
    private async Task<(int Status, string? MediaType, byte[]? Body, string? FinalUrl)> GetWithRedirectsAsync(
        string url, CancellationToken ct, int maxBodyBytes, int maxHops = 5)
    {
        var current = url;
        for (var hop = 0; hop <= maxHops; hop++)
        {
            if (!Uri.TryCreate(current, UriKind.Absolute, out var uri)) return (0, null, null, current);
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return (0, null, null, current);
            if (!await CompanyLogoRules.IsSafeHostAsync(uri.Host, ct, hostResolver)) return (0, null, null, current);
            try
            {
                var client = httpFactory.CreateClient(CompanyLogoFetcherOptions.ClientName);
                using var resp = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                var status = (int)resp.StatusCode;
                var location = resp.Headers.Location?.ToString();
                if (status is 301 or 302 or 303 or 307 or 308 && !string.IsNullOrWhiteSpace(location))
                {
                    var next = CompanyLogoRules.TryResolveRedirectUri(current, location);
                    if (next is null) return (status, null, null, current);
                    if (hop == maxHops) return (status, null, null, current);
                    current = next;
                    continue;
                }
                var mediaType = resp.Content?.Headers.ContentType?.MediaType;
                var body = resp.Content is null
                    ? []
                    : await resp.Content.ReadAsByteArrayAsync(ct);
                if (body.Length > maxBodyBytes) return (status, mediaType, null, current);
                return (status, mediaType, body, current);
            }
            catch
            {
                return (0, null, null, current);
            }
        }
        return (0, null, null, current);
    }
}
