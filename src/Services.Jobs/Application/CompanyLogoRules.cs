using System.Net;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  票 150:公司 logo 回填的纯规则 —— 零 I/O、可独立单测。
//  取数(I/O)部分在 Infrastructure 的 CompanyLogoFetcher,编排在
//  CompanyLogoHandlers,三者分工:规则不碰网络,取数不做决策,
//  Handler 只按规则 + 取数结果写库。
// ============================================================================
public static class CompanyLogoRules
{
    /// <summary>Google favicon 服务地址前缀 —— 清洗只认这个口径的存量值。</summary>
    public const string GoogleFaviconPrefix = "https://www.google.com/s2/favicons?";

    /// <summary>
    /// 聚合招聘站域名(与 JdPasteParser.AggregatorHosts 同口径,此处单列一份
    /// 是因为那份是 private 且 Website 可能是裸 host、走不了 JdPasteParser.HostOf
    /// 的 https?:// 正则;两份改动时须同步,单测 CompanyLogoRulesTests 钉死
    /// 关键域名在两边行为一致)。
    /// </summary>
    private static readonly string[] AggregatorHosts =
    [
        "linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com",
        "monster.com", "dice.com", "builtin.com", "otta.com", "wellfound.com",
        "angel.co", "levels.fyi"
    ];

    // ---------------- LinkedIn 链接选取(§2.2 第 2 步,按独立评分修订) ----------------

    /// <summary>
    /// 取该公司全部申请中 Link 含 linkedin.com/jobs/view/ 的,按 CreatedAt
    /// 倒序(审计创建时间,非空且单调;不用可空且用户可改的 AppliedDate)。
    /// 调用方按此顺序逐条重提 li-logo,失效顺延。
    /// </summary>
    public static IReadOnlyList<string> SelectLinkedInLinks(
        IEnumerable<(string? Link, DateTimeOffset CreatedAt)> applications)
        => applications
            .Where(a => !string.IsNullOrWhiteSpace(a.Link)
                && a.Link!.Contains("linkedin.com/jobs/view/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.Link!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---------------- SmartAdd 透传决策(§2.6) ----------------

    /// <summary>新建公司:显式 LogoUrl 优先,其次 Website 派生。</summary>
    public static string? ResolveNewCompanyLogo(string? explicitLogoUrl, string? website)
        => !string.IsNullOrWhiteSpace(explicitLogoUrl)
            ? explicitLogoUrl.Trim()
            : Domain.Company.LogoUrlFromWebsite(NormalizeWebsiteUrl(website));

    /// <summary>已存在公司:只有存量为空且带了显式值时才补,已有值绝不覆盖。</summary>
    public static bool ShouldFillExistingLogo(string? existingLogoUrl, string? explicitLogoUrl)
        => string.IsNullOrWhiteSpace(existingLogoUrl) && !string.IsNullOrWhiteSpace(explicitLogoUrl);

    // ---------------- Website 归一与聚合站 guard ----------------

    /// <summary>
    /// Website 可能是裸 host(arvato.com,SmartAdd 的 HostOf 口径)或完整 URL。
    /// 归一为可抓取的 https 绝对地址;既非 URL 又非裸 host 时返回 null。
    /// </summary>
    public static string? NormalizeWebsiteUrl(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        var w = website.Trim();
        if (Uri.TryCreate(w, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(u.Host))
            return u.GetLeftPart(UriPartial.Authority) + "/";
        // 裸 host:无空白、无斜杠、无冒号端口以外的字符
        if (!w.Contains(' ') && !w.Contains('/') && Uri.CheckHostName(w) != UriHostNameType.Unknown)
            return $"https://{w}/";
        return null;
    }

    /// <summary>从完整 URL 或裸 host 中取 host;取不出返回 null。</summary>
    public static string? TryGetHost(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        var w = website.Trim();
        if (Uri.TryCreate(w, UriKind.Absolute, out var u) && !string.IsNullOrWhiteSpace(u.Host))
            return u.Host;
        if (!w.Contains(' ') && !w.Contains('/') && Uri.CheckHostName(w) != UriHostNameType.Unknown)
            return w;
        return null;
    }

    /// <summary>聚合站域名绝不当官网 —— 完整 URL 与裸 host 两种形态都拦。</summary>
    public static bool IsAggregatorWebsite(string? website)
    {
        var host = TryGetHost(website);
        if (string.IsNullOrWhiteSpace(host)) return false;
        return AggregatorHosts.Any(h => host.EndsWith(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>存量 LogoUrl 是否 Google s2 派生地址(清洗只动这类)。</summary>
    public static bool IsGoogleFaviconUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
            && url.StartsWith(GoogleFaviconPrefix, StringComparison.OrdinalIgnoreCase);

    // ---------------- 默认图标比对(§2.4) ----------------

    /// <summary>候选图标字节与默认图标参照字节逐字节相等即命中默认图标。</summary>
    public static bool IsDefaultIcon(byte[] candidate, byte[] reference)
        => candidate.Length == reference.Length && candidate.SequenceEqual(reference);

    // ---------------- favicon 发现(方式 b) ----------------

    private static readonly Regex LinkTag = new(
        @"<link\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RelAttr = new(
        @"rel\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefAttr = new(
        @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 从官网首页 HTML 中找 rel 含 icon 的 link,取 href 并相对 pageUrl 解析为
    /// 绝对地址。rel 在前还是 href 在前都认;apple-touch-icon 也算 icon。
    /// 非 http(s) 结果、解析失败一律返回 null。
    /// </summary>
    public static string? ExtractIconUrl(string html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(pageUrl)) return null;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return null;
        foreach (Match tag in LinkTag.Matches(html))
        {
            var rel = RelAttr.Match(tag.Value);
            if (!rel.Success) continue;
            var relValue = rel.Groups[1].Value;
            if (!relValue.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Any(p => p.Contains("icon", StringComparison.OrdinalIgnoreCase)))
                continue;
            var href = HrefAttr.Match(tag.Value);
            if (!href.Success) continue;
            var raw = href.Groups[1].Value.Trim();
            if (raw.Length == 0) continue;
            if (!Uri.TryCreate(baseUri, raw, out var resolved)) continue;
            if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps) continue;
            return resolved.ToString();
        }
        return null;
    }

    // ---------------- SSRF 校验(§2.5:解析后内网/保留 IP 拒绝) ----------------

    /// <summary>
    /// 内网/保留 IP 段:localhost、10/8、172.16/12、192.168/16、169.254/16、
    /// 以及回环/未指定/唯一本地/链路本地/文档/弃用段。命中即不得抓取。
    /// </summary>
    public static bool IsPrivateOrReservedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 0.0.0.0/8
            if (b[0] == 0) return true;
            // 10.0.0.0/8
            if (b[0] == 10) return true;
            // 100.64.0.0/10 (CGNAT)
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            // 127.0.0.0/8 (IsLoopback 已覆,但显式列出防 IPv4 映射)
            if (b[0] == 127) return true;
            // 169.254.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;
            // 172.16.0.0/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.0.0.0/24, 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 (文档段)
            if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true;
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
            // 192.168.0.0/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 198.18.0.0/15 (基准测试)
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
            // 240.0.0.0/4 (保留)
            if (b[0] >= 240) return true;
            return false;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true;
            var b = ip.GetAddressBytes();
            // ::1 已由 IsLoopback 覆;fc00::/7 唯一本地;fe80::/10 链路本地(上已覆);
            // 2001:db8::/32 文档段;::ffff:a.b.c.d 的 IPv4 映射段按内嵌地址判
            if (ip.IsIPv4MappedToIPv6)
            {
                var v4 = ip.MapToIPv4();
                return IsPrivateOrReservedIp(v4);
            }
            if (b[0] == 0xfc || b[0] == 0xfd) return true;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return true;
            return false;
        }
        return true;
    }

    /// <summary>
    /// host 安全判定:字面 IP 直接按段判;域名经解析后全部地址都必须过关
    /// (任一命中内网段即拒绝 —— 防 DNS 重绑定式解析结果混杂);
    /// 解析失败/空结果一律不安全。resolver 参数只为单测可注入,生产传 null
    /// 即走 Dns.GetHostAddressesAsync。
    /// </summary>
    public static async Task<bool> IsSafeHostAsync(string host, CancellationToken ct,
        Func<string, Task<IPAddress[]>>? resolver = null)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (IPAddress.TryParse(host.Trim(), out var literal))
            return !IsPrivateOrReservedIp(literal);
        IPAddress[] addresses;
        try
        {
            addresses = resolver is not null
                ? await resolver(host)
                : await Dns.GetHostAddressesAsync(host, ct);
        }
        catch
        {
            return false;
        }
        if (addresses.Length == 0) return false;
        return addresses.All(a => !IsPrivateOrReservedIp(a));
    }
}
