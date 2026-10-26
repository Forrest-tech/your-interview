using Microsoft.Extensions.Logging;
using YourInterview.Services.Jobs.Application;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  票 150:公司 logo 取数 —— 只负责 I/O,决策在 CompanyLogoRules。
//
//  取数链(§2.2):LinkedIn li-logo(最高质量) → Website 派生 favicon。
//  派生方式按 §8 U-150-2:(a) Google s2 + 默认图标关,关过不了时 (b)
//  自研 favicon 发现。两种方式都只存“校验过的真实图标”,无图标宁可 null
//  (前端色块),绝不把通用默认图标写进库。
//
//  SSRF(§2.5/A-150-10):一切站外抓取只接受公司 id 派生的 host,
//  绝不接受调用方传入的任意 URL;每次抓取前解析 host 并拒绝内网/
//  保留 IP;HttpClient 禁自动重定向(3xx 即失败,防重定向绕过校验)。
//  失败一律返回 null(或 false),绝不抛异常中断回填整批;原因只进日志。
// ============================================================================

public interface ICompanyLogoFetcher
{
    /// <summary>重提 LinkedIn 职位页的 li-logo;任何失败返回 null。</summary>
    Task<string?> TryLinkedInLogoAsync(string url, CancellationToken ct);

    /// <summary>Website 派生 favicon(先 s2 + 默认图标关,后自研发现);无图标返回 null。</summary>
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
    ILogger<CompanyLogoFetcher> logger) : ICompanyLogoFetcher
{
    // 默认图标参照:向 s2 要一个注定无 favicon 的域名的图标 ——
    // 该服务对无 favicon 域名返回的固定默认图标即参照字节。同一实例内只取一次。
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
        if (!await CompanyLogoRules.IsSafeHostAsync(host, ct)) return null;

        // (a) Google s2 + 默认图标关
        var s2Url = Domain.Company.LogoUrlFromWebsite($"https://{host}/");
        if (s2Url is not null)
        {
            var s2Bytes = await FetchImageBytesAsync(s2Url, ct);
            if (s2Bytes is not null)
            {
                var reference = await GetDefaultIconBytesAsync(ct);
                if (reference is not null)
                {
                    // 字节与默认图标不同 = 真实图标,直接存 s2 地址(前端 img 直取)
                    if (!CompanyLogoRules.IsDefaultIcon(s2Bytes, reference)) return s2Url;
                    // 命中默认图标:不写 s2,落到 (b) 再找一次(官网自带 icon 时 s2 未必收录)
                }
                else
                {
                    // 参照取不到 = 默认图标关不可靠,按 §8 兜底改走 (b),不硬上 (a)
                    logger.LogWarning("票150:s2 默认图标参照取不到,改走自研 favicon 发现: {Host}", host);
                }
            }
            // s2 本身抓不到时同样落到 (b)
        }

        // (b) 自研 favicon 发现:官网首页 link[rel=icon] → /favicon.ico,均须校验为图片
        var pageUrl = CompanyLogoRules.NormalizeWebsiteUrl(website);
        if (pageUrl is null) return null;
        try
        {
            var client = httpFactory.CreateClient(CompanyLogoFetcherOptions.ClientName);
            using var resp = await client.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Length == 0)
                {
                    var html = await resp.Content.ReadAsStringAsync(ct);
                    if (html.Length is >= 200 and <= 2_000_000)
                    {
                        var iconUrl = CompanyLogoRules.ExtractIconUrl(html, pageUrl);
                        if (iconUrl is not null
                            && await IsFetchableImageAsync(iconUrl, ct))
                            return iconUrl;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "票150:官网首页抓取失败: {Url}", pageUrl);
        }

        // /favicon.ico 兜底:须是图片且非空才存
        try
        {
            var baseUri = new Uri(pageUrl);
            var faviconUrl = new Uri(baseUri, "/favicon.ico").ToString();
            if (await IsFetchableImageAsync(faviconUrl, ct)) return faviconUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "票150:/favicon.ico 校验失败: {Url}", pageUrl);
        }
        return null;
    }

    public async Task<bool> IsDefaultFaviconAsync(string s2Url, CancellationToken ct)
    {
        try
        {
            var bytes = await FetchImageBytesAsync(s2Url, ct);
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
        _defaultIconBytes = await FetchImageBytesAsync(probeUrl, ct);
        return _defaultIconBytes;
    }

    /// <summary>抓一张图回字节:非 200、非图片、超 1MB 一律 null。禁重定向由 client 保证。</summary>
    private async Task<byte[]?> FetchImageBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient(CompanyLogoFetcherOptions.ClientName);
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0 || bytes.Length > 1_000_000) return null;
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 校验一个候选图标 URL 是否真实可取:host 须过 SSRF 校验,响应须为图片
    /// 且非空。只读响应头 + 首字节判断,不下载整图(图标通常很小,直接读体亦可)。
    /// </summary>
    private async Task<bool> IsFetchableImageAsync(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (!await CompanyLogoRules.IsSafeHostAsync(uri.Host, ct)) return false;
            var bytes = await FetchImageBytesAsync(url, ct);
            return bytes is not null;
        }
        catch
        {
            return false;
        }
    }
}
