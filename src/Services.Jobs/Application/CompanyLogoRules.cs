using System.Globalization;
using System.Net;
using System.Text;
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

    // ---------------- 票 249:Simple Icons titleToSlug 移植 ----------------

    /// <summary>
    /// Simple Icons sdk.mjs 的 titleToSlug 语义移植:小写 → 字符替换(含显式重音转写表)
    /// → 只留 a-z0-9,与上游“替换 → NFD 分解 → /[^a-z\d]/ 剔除”同口径。
    /// 注意:本函数刻意不调用 string.Normalize(FormD)。本仓 Directory.Build.props
    /// 全仓 InvariantGlobalization=true,实测该模式下 Normalize(FormD) 原样返回输入
    /// (不分解)——正是 CI 上 Škoda→koda、Müller→mller、Łódź→ld 的原因(重音字母被
    /// 末尾过滤整字丢掉)。显式表复现了 NFD 的效果,且行为与全球化模式无关。
    /// 已分解输入(基字+组合符)仍由末尾只留 a-z0-9 的过滤正确处理。显式 slug 的
    /// 图标(如 F#→fsharp)不走本函数,查表时以数据文件里的 slug 为准。
    /// </summary>
    // 上游替换表 + 显式重音转写表(见 TitleToSlug 注释:NFD 在本仓不可用,故用表复现)。
    // 显式表 = 全部小写字母中、其规范 NFD 分解恰为 [ASCII 基字 + 组合符] 的字符;
    // 上游 NFD 下不分解因而被 strip 丢掉的字符(æ、œ、ð、þ、ŋ 等)刻意不在此表,语义与上游一致。
    private static readonly Dictionary<char, string> TitleSlugReplacements = new()
    {
        // 上游 sdk.mjs 替换表(原样)。
        ['+'] = "plus", ['.'] = "dot", ['&'] = "and",
        ['đ'] = "d", ['ħ'] = "h", ['ı'] = "i", ['ĸ'] = "k",
        ['ŀ'] = "l", ['ł'] = "l", ['ß'] = "ss", ['ŧ'] = "t", ['ø'] = "o",
        // 规范分解为 a 的带重音小写字母。
        ['\u00E0'] = "a", ['\u00E1'] = "a", ['\u00E2'] = "a", ['\u00E3'] = "a", ['\u00E4'] = "a", ['\u00E5'] = "a",
        ['\u0101'] = "a", ['\u0103'] = "a", ['\u0105'] = "a", ['\u01CE'] = "a", ['\u01DF'] = "a", ['\u01E1'] = "a",
        ['\u01FB'] = "a", ['\u0201'] = "a", ['\u0203'] = "a", ['\u0227'] = "a", ['\u1E01'] = "a", ['\u1EA1'] = "a",
        ['\u1EA3'] = "a", ['\u1EA5'] = "a", ['\u1EA7'] = "a", ['\u1EA9'] = "a", ['\u1EAB'] = "a", ['\u1EAD'] = "a",
        ['\u1EAF'] = "a", ['\u1EB1'] = "a", ['\u1EB3'] = "a", ['\u1EB5'] = "a", ['\u1EB7'] = "a",
        // 规范分解为 b 的带重音小写字母。
        ['\u1E03'] = "b", ['\u1E05'] = "b", ['\u1E07'] = "b",
        // 规范分解为 c 的带重音小写字母。
        ['\u00E7'] = "c", ['\u0107'] = "c", ['\u0109'] = "c", ['\u010B'] = "c", ['\u010D'] = "c", ['\u1E09'] = "c",
        // 规范分解为 d 的带重音小写字母。
        ['\u010F'] = "d", ['\u1E0B'] = "d", ['\u1E0D'] = "d", ['\u1E0F'] = "d", ['\u1E11'] = "d", ['\u1E13'] = "d",
        // 规范分解为 e 的带重音小写字母。
        ['\u00E8'] = "e", ['\u00E9'] = "e", ['\u00EA'] = "e", ['\u00EB'] = "e", ['\u0113'] = "e", ['\u0115'] = "e",
        ['\u0117'] = "e", ['\u0119'] = "e", ['\u011B'] = "e", ['\u0205'] = "e", ['\u0207'] = "e", ['\u0229'] = "e",
        ['\u1E15'] = "e", ['\u1E17'] = "e", ['\u1E19'] = "e", ['\u1E1B'] = "e", ['\u1E1D'] = "e", ['\u1EB9'] = "e",
        ['\u1EBB'] = "e", ['\u1EBD'] = "e", ['\u1EBF'] = "e", ['\u1EC1'] = "e", ['\u1EC3'] = "e", ['\u1EC5'] = "e",
        ['\u1EC7'] = "e",
        // 规范分解为 f 的带重音小写字母。
        ['\u1E1F'] = "f",
        // 规范分解为 g 的带重音小写字母。
        ['\u011D'] = "g", ['\u011F'] = "g", ['\u0121'] = "g", ['\u0123'] = "g", ['\u01E7'] = "g", ['\u01F5'] = "g",
        ['\u1E21'] = "g",
        // 规范分解为 h 的带重音小写字母。
        ['\u0125'] = "h", ['\u021F'] = "h", ['\u1E23'] = "h", ['\u1E25'] = "h", ['\u1E27'] = "h", ['\u1E29'] = "h",
        ['\u1E2B'] = "h", ['\u1E96'] = "h",
        // 规范分解为 i 的带重音小写字母。
        ['\u00EC'] = "i", ['\u00ED'] = "i", ['\u00EE'] = "i", ['\u00EF'] = "i", ['\u0129'] = "i", ['\u012B'] = "i",
        ['\u012D'] = "i", ['\u012F'] = "i", ['\u01D0'] = "i", ['\u0209'] = "i", ['\u020B'] = "i", ['\u1E2D'] = "i",
        ['\u1E2F'] = "i", ['\u1EC9'] = "i", ['\u1ECB'] = "i",
        // 规范分解为 j 的带重音小写字母。
        ['\u0135'] = "j", ['\u01F0'] = "j",
        // 规范分解为 k 的带重音小写字母。
        ['\u0137'] = "k", ['\u01E9'] = "k", ['\u1E31'] = "k", ['\u1E33'] = "k", ['\u1E35'] = "k",
        // 规范分解为 l 的带重音小写字母。
        ['\u013A'] = "l", ['\u013C'] = "l", ['\u013E'] = "l", ['\u1E37'] = "l", ['\u1E39'] = "l", ['\u1E3B'] = "l",
        ['\u1E3D'] = "l",
        // 规范分解为 m 的带重音小写字母。
        ['\u1E3F'] = "m", ['\u1E41'] = "m", ['\u1E43'] = "m",
        // 规范分解为 n 的带重音小写字母。
        ['\u00F1'] = "n", ['\u0144'] = "n", ['\u0146'] = "n", ['\u0148'] = "n", ['\u01F9'] = "n", ['\u1E45'] = "n",
        ['\u1E47'] = "n", ['\u1E49'] = "n", ['\u1E4B'] = "n",
        // 规范分解为 o 的带重音小写字母。
        ['\u00F2'] = "o", ['\u00F3'] = "o", ['\u00F4'] = "o", ['\u00F5'] = "o", ['\u00F6'] = "o", ['\u014D'] = "o",
        ['\u014F'] = "o", ['\u0151'] = "o", ['\u01A1'] = "o", ['\u01D2'] = "o", ['\u01EB'] = "o", ['\u01ED'] = "o",
        ['\u020D'] = "o", ['\u020F'] = "o", ['\u022B'] = "o", ['\u022D'] = "o", ['\u022F'] = "o", ['\u0231'] = "o",
        ['\u1E4D'] = "o", ['\u1E4F'] = "o", ['\u1E51'] = "o", ['\u1E53'] = "o", ['\u1ECD'] = "o", ['\u1ECF'] = "o",
        ['\u1ED1'] = "o", ['\u1ED3'] = "o", ['\u1ED5'] = "o", ['\u1ED7'] = "o", ['\u1ED9'] = "o", ['\u1EDB'] = "o",
        ['\u1EDD'] = "o", ['\u1EDF'] = "o", ['\u1EE1'] = "o", ['\u1EE3'] = "o",
        // 规范分解为 p 的带重音小写字母。
        ['\u1E55'] = "p", ['\u1E57'] = "p",
        // 规范分解为 r 的带重音小写字母。
        ['\u0155'] = "r", ['\u0157'] = "r", ['\u0159'] = "r", ['\u0211'] = "r", ['\u0213'] = "r", ['\u1E59'] = "r",
        ['\u1E5B'] = "r", ['\u1E5D'] = "r", ['\u1E5F'] = "r",
        // 规范分解为 s 的带重音小写字母。
        ['\u015B'] = "s", ['\u015D'] = "s", ['\u015F'] = "s", ['\u0161'] = "s", ['\u0219'] = "s", ['\u1E61'] = "s",
        ['\u1E63'] = "s", ['\u1E65'] = "s", ['\u1E67'] = "s", ['\u1E69'] = "s",
        // 规范分解为 t 的带重音小写字母。
        ['\u0163'] = "t", ['\u0165'] = "t", ['\u021B'] = "t", ['\u1E6B'] = "t", ['\u1E6D'] = "t", ['\u1E6F'] = "t",
        ['\u1E71'] = "t", ['\u1E97'] = "t",
        // 规范分解为 u 的带重音小写字母。
        ['\u00F9'] = "u", ['\u00FA'] = "u", ['\u00FB'] = "u", ['\u00FC'] = "u", ['\u0169'] = "u", ['\u016B'] = "u",
        ['\u016D'] = "u", ['\u016F'] = "u", ['\u0171'] = "u", ['\u0173'] = "u", ['\u01B0'] = "u", ['\u01D4'] = "u",
        ['\u01D6'] = "u", ['\u01D8'] = "u", ['\u01DA'] = "u", ['\u01DC'] = "u", ['\u0215'] = "u", ['\u0217'] = "u",
        ['\u1E73'] = "u", ['\u1E75'] = "u", ['\u1E77'] = "u", ['\u1E79'] = "u", ['\u1E7B'] = "u", ['\u1EE5'] = "u",
        ['\u1EE7'] = "u", ['\u1EE9'] = "u", ['\u1EEB'] = "u", ['\u1EED'] = "u", ['\u1EEF'] = "u", ['\u1EF1'] = "u",
        // 规范分解为 v 的带重音小写字母。
        ['\u1E7D'] = "v", ['\u1E7F'] = "v",
        // 规范分解为 w 的带重音小写字母。
        ['\u0175'] = "w", ['\u1E81'] = "w", ['\u1E83'] = "w", ['\u1E85'] = "w", ['\u1E87'] = "w", ['\u1E89'] = "w",
        ['\u1E98'] = "w",
        // 规范分解为 x 的带重音小写字母。
        ['\u1E8B'] = "x", ['\u1E8D'] = "x",
        // 规范分解为 y 的带重音小写字母。
        ['\u00FD'] = "y", ['\u00FF'] = "y", ['\u0177'] = "y", ['\u0233'] = "y", ['\u1E8F'] = "y", ['\u1E99'] = "y",
        ['\u1EF3'] = "y", ['\u1EF5'] = "y", ['\u1EF7'] = "y", ['\u1EF9'] = "y",
        // 规范分解为 z 的带重音小写字母。
        ['\u017A'] = "z", ['\u017C'] = "z", ['\u017E'] = "z", ['\u1E91'] = "z", ['\u1E93'] = "z", ['\u1E95'] = "z",
    };

    public static string TitleToSlug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var lowered = title.Trim().ToLowerInvariant();
        var replaced = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
            replaced.Append(TitleSlugReplacements.TryGetValue(ch, out var rep) ? rep : ch.ToString());
        var replacedText = replaced.ToString();
        var slug = new StringBuilder(replacedText.Length);
        foreach (var ch in replacedText)
        {
            // 只留 ASCII a-z0-9:未映射的非 ASCII 字母与组合附加符号一并去掉,
            // 与上游 NFD 后 /[^a-z\d]/ 剔除同口径(æ、œ、ð、þ 等无规范分解的
            // 字符在上游同样被丢掉,此处不映射,语义不变)。
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) slug.Append(ch);
        }
        return slug.ToString();
    }

    // ---------------- 票 249:registrable-domain 与重定向判定(纯函数) ----------------

    /// <summary>
    /// 多段公共后缀(小表,覆盖常见国家/地区二段后缀;未列出的默认按末两段计)。
    /// eTLD+1 判定只用于“是否同一注册域”,不做完整 PSL。
    /// </summary>
    private static readonly string[] MultiLabelSuffixes =
    [
        "co.uk", "org.uk", "ac.uk", "gov.uk", "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
        "com.au", "net.au", "org.au", "co.nz", "com.br", "com.cn", "com.tw", "co.in",
        "co.za", "com.mx", "com.ar", "com.co", "com.pl", "com.tr", "com.sg", "com.hk",
        "com.my", "co.id", "co.th", "com.vn", "com.ph", "com.pk", "co.il", "com.eg",
        "com.sa", "com.ae", "co.ke", "co.ng", "com.pe", "com.ve", "com.ec", "com.gt",
        "com.uy", "com.py", "com.bo", "com.do", "com.sv", "com.hn", "com.ni", "com.cr",
        "com.pa", "co.kr", "com.es", "nom.es", "org.es", "com.pt", "com.gr", "com.ru",
        "com.ua", "com.kz", "com.by", "co.ua", "com.hr", "com.rs", "com.si", "com.sk",
        "com.hu", "com.ro", "com.bg", "com.lt", "com.lv", "com.ee", "com.fi", "com.no",
        "com.se", "com.dk", "com.is", "com.ie",
    ];

    /// <summary>
    /// 取 host 的 registrable-domain(eTLD+1):IP 与单段 host 原样返回;
    /// 多段公共后缀 host 取末三段,否则取末两段;统一小写、去尾点。
    /// </summary>
    public static string? GetRegistrableDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return null;
        if (IPAddress.TryParse(h, out _)) return h;
        var labels = h.Split('.');
        if (labels.Length == 1) return h;
        foreach (var suffix in MultiLabelSuffixes)
        {
            var suffixLabels = suffix.Split('.').Length;
            if (labels.Length > suffixLabels
                && h.EndsWith("." + suffix, StringComparison.Ordinal))
                return string.Join(".", labels[^3..]);
        }
        return string.Join(".", labels[^2..]);
    }

    /// <summary>
    /// 重定向放行判定(纯函数,供抓取循环与单测共用):返回跟随后的绝对地址,
    /// 不放行返回 null。放行条件三选一:①同 host;②同一 registrable-domain
    /// ( naked→www 即属此列,exact-host 读法修不了它);③s2→gstatic 专用
    /// 豁口——链起于 www.google.com 的 /s2/favicons 且目标 host 恰好是
    /// t1/t2.gstatic.com(host-exact,禁 suffix 匹配花招,如 evil-t1.gstatic.com
    /// 一律拒绝)。其他情形(跨域、非 http(s)、缺 Location)一律 null。
    /// </summary>
    public static string? TryResolveRedirectUri(string currentUrl, string? locationHeader)
    {
        if (string.IsNullOrWhiteSpace(currentUrl) || string.IsNullOrWhiteSpace(locationHeader)) return null;
        if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out var current)) return null;
        if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps) return null;
        if (!Uri.TryCreate(current, locationHeader.Trim(), out var next)) return null;
        if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return null;
        var currentHost = current.Host.ToLowerInvariant();
        var nextHost = next.Host.ToLowerInvariant();
        if (nextHost == currentHost) return next.ToString();
        var currentDomain = GetRegistrableDomain(currentHost);
        var nextDomain = GetRegistrableDomain(nextHost);
        if (currentDomain is not null && currentDomain == nextDomain) return next.ToString();
        if (currentHost == "www.google.com"
            && current.AbsolutePath.StartsWith("/s2/favicons", StringComparison.OrdinalIgnoreCase)
            && (nextHost == "t1.gstatic.com" || nextHost == "t2.gstatic.com"))
            return next.ToString();
        return null;
    }

    // ---------------- 票 249:图片尺寸嗅探与最优挑选(纯函数) ----------------

    /// <summary>抓取到的候选图标:Url 为存储值;IsSvg 为分辨率无关;未知尺寸为 null。</summary>
    public sealed record LogoCandidate(string Url, bool IsSvg, int? Width, int? Height)
    {
        /// <summary>已知尺寸取短边,未知尺寸记 -1(排在已知小图之后、但仍是候选)。</summary>
        public int QualityScore => IsSvg ? int.MaxValue
            : Width is { } w && Height is { } h ? Math.Min(w, h) : -1;
    }

    /// <summary>
    /// 从图片字节嗅探格式与尺寸:SVG(媒体类型或内容 &lt;svg)分辨率无关;
    /// PNG/GIF/ICO/JPEG/BMP/WebP 读文件头尺寸;无法识别返回未知尺寸。
    /// 一律 defensive:任何解析异常都落回未知,绝不抛。
    /// </summary>
    public static (bool IsSvg, int? Width, int? Height) TryGetImageDimensions(byte[] bytes, string? mediaType)
    {
        // 媒体类型即判,不读体(调用方已保证体非空;空体 + svg 类型亦判为 SVG)。
        if (!string.IsNullOrWhiteSpace(mediaType)
            && mediaType.Contains("svg", StringComparison.OrdinalIgnoreCase))
            return (true, null, null);
        if (bytes is null || bytes.Length == 0) return (false, null, null);
        // 内容嗅探:去 BOM/空白后 <svg,或 <?xml ... <svg(限前 1KB)
        try
        {
            var head = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).TrimStart();
            if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) return (true, null, null);
            if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                && head.Contains("<svg", StringComparison.OrdinalIgnoreCase)) return (true, null, null);
        }
        catch
        {
            return (false, null, null);
        }
        try
        {
            // PNG:8 字节签名 + IHDR,宽高为 BE uint32 @16/@20
            if (bytes.Length >= 24
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                var w = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                var h = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                if (w > 0 && h > 0) return (false, w, h);
                return (false, null, null);
            }
            // GIF:GIF87a/GIF89a,宽高为 LE uint16 @6/@8
            if (bytes.Length >= 10
                && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            {
                var w = bytes[6] | (bytes[7] << 8);
                var h = bytes[8] | (bytes[9] << 8);
                if (w > 0 && h > 0) return (false, w, h);
                return (false, null, null);
            }
            // ICO:00 00 01 00 + 首表项,宽高字节(0=256)@6/@7
            if (bytes.Length >= 8 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
            {
                var count = bytes[4] | (bytes[5] << 8);
                if (count > 0 && bytes.Length >= 8)
                {
                    var w = bytes[6] == 0 ? 256 : bytes[6];
                    var h = bytes[7] == 0 ? 256 : bytes[7];
                    return (false, w, h);
                }
                return (false, null, null);
            }
            // JPEG:扫 marker 找 SOF0/SOF1/SOF2,高 BE uint16 @+5、宽 @+7
            if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8)
            {
                var i = 2;
                while (i + 9 < bytes.Length)
                {
                    if (bytes[i] != 0xFF) break;
                    var marker = bytes[i + 1];
                    if (marker == 0xD9 || marker == 0xDA) break;
                    if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) { i += 2; continue; }
                    var len = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (len < 2 || i + 2 + len > bytes.Length) break;
                    if (marker == 0xC0 || marker == 0xC1 || marker == 0xC2)
                    {
                        var h = (bytes[i + 5] << 8) | bytes[i + 6];
                        var w = (bytes[i + 7] << 8) | bytes[i + 8];
                        if (w > 0 && h > 0) return (false, w, h);
                        return (false, null, null);
                    }
                    i += 2 + len;
                }
                return (false, null, null);
            }
            // BMP:"BM" + LE int32 @18/@22
            if (bytes.Length >= 26 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M')
            {
                var w = BitConverter.ToInt32(bytes, 18);
                var h = Math.Abs(BitConverter.ToInt32(bytes, 22));
                if (w > 0 && h > 0) return (false, w, h);
                return (false, null, null);
            }
            // WebP:"RIFF....WEBP":VP8X(宽高 24bit LE @24/@27,各+1);
            // VP8L(14bit @21);VP8(14bit @26/@28)
            if (bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                if (bytes.Length >= 30 && bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)'X')
                {
                    var w = (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16)) + 1;
                    var h = (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16)) + 1;
                    if (w > 0 && h > 0) return (false, w, h);
                }
                if (bytes.Length >= 25 && bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)'L')
                {
                    var v = bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24);
                    var w = (v & 0x3FFF) + 1;
                    var h = ((v >> 14) & 0x3FFF) + 1;
                    if (w > 0 && h > 0) return (false, w, h);
                }
                if (bytes.Length >= 30 && bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)' ')
                {
                    var w = ((bytes[26] | (bytes[27] << 8)) & 0x3FFF) + 1;
                    var h = ((bytes[28] | (bytes[29] << 8)) & 0x3FFF) + 1;
                    if (w > 0 && h > 0) return (false, w, h);
                }
                return (false, null, null);
            }
        }
        catch
        {
            return (false, null, null);
        }
        return (false, null, null);
    }

    /// <summary>
    /// 票 249 软化质量门(纯规则):SVG 恒胜(分辨率无关);其次短边 ≥32 者中
    /// 取最大短边;若全在 32 以下,取已知最大者(未知尺寸排最后);唯一
    /// 候选时无论多小都保留,绝不因此落回首字母。空集返回 null。
    /// </summary>
    public static LogoCandidate? PickBestLogo(IReadOnlyList<LogoCandidate> candidates)
    {
        if (candidates is null || candidates.Count == 0) return null;
        LogoCandidate? best = null;
        foreach (var c in candidates)
        {
            if (best is null || c.QualityScore > best.QualityScore) best = c;
        }
        return best;
    }
}
