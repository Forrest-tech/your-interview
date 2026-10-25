using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  公司简介抓取器(票142 R-142-11)。
//
//  只取数、不写库:调用方(取数端点)把候选文本返回给前端进编辑草稿,
//  用户确认后才走现有 PUT companies/{id}/profile 保存。抓取永远不直接写库。
//
//  抓取链(按序,任一步成功即停):
//    ① Wikipedia 摘要(en.wikipedia.org,无 key,opensearch 标题匹配 + REST 摘要)
//    ② 公司官网 About 系路径(仅 Company.website 已填时,不猜测域名,遵守 robots.txt)
//  明确排除:LinkedIn(协议禁爬虫,BlockedHosts 同口径)、Crunchbase(需订阅 key)、
//  Google Knowledge Graph(需 key)、搜索引擎结果页抓取、Clearbit(不再发新 key)。
//
//  安全:取数端点只接受公司 id,不接受任意 URL;官网 URL 来自已存 website 字段,
//  Wikipedia 标题来自公司名;出站请求限 http/https,解析后拒绝内网/保留 IP
//  (JobPageFetcher 现行仅校验 scheme 与屏蔽域名,本抓取器补 IP 段校验)。
//  注意残留风险:DNS 解析与实际建连之间存在 TOCTOU 窗口(DNS 重绑定),
//  本校验挡的是常规内网地址直连,不是密码学级保证 —— 取数端点本身仍要求
//  JobsWrite 且只读已存字段,不接受调用方传入的任意 URL。
//  失败一律返回空结果、不抛异常中断;单腿 15 秒(与 JobPageFetcher 同口径)。
// ============================================================================

/// <summary>抓取结果。CandidateText 为 null 时 NotFound=true,Tried 列出实际尝试过的腿。</summary>
public sealed record CompanyProfileFetchResult(string? CandidateText, string? SourceUrl, IReadOnlyList<string> Tried)
{
    public bool NotFound => CandidateText is null;
}

public interface ICompanyProfileFetcher
{
    Task<CompanyProfileFetchResult> FetchAsync(string companyName, string? website, CancellationToken ct);
}

public sealed class CompanyProfileFetcher(IHttpClientFactory httpFactory) : ICompanyProfileFetcher
{
    public const string ClientName = "CompanyProfileFetcher";

    /// <summary>官网 About 系候选路径(按序尝试)。</summary>
    internal static readonly string[] AboutPaths = ["/about", "/about-us", "/company", "/about/company"];

    public async Task<CompanyProfileFetchResult> FetchAsync(string companyName, string? website, CancellationToken ct)
    {
        var tried = new List<string>();

        // ---- 腿① Wikipedia 摘要 ----
        tried.Add("wikipedia");
        var wiki = await FetchWikipediaAsync(companyName, ct);
        if (wiki is not null)
            return new CompanyProfileFetchResult(wiki.Value.Text, wiki.Value.SourceUrl, tried);

        // ---- 腿② 公司官网 About 页(仅 website 已填时;为空跳过本腿、不猜测域名) ----
        if (!string.IsNullOrWhiteSpace(website))
        {
            tried.Add("website");
            var page = await FetchWebsiteAboutAsync(website, ct);
            if (page is not null)
                return new CompanyProfileFetchResult(page.Value.Text, page.Value.SourceUrl, tried);
        }

        return new CompanyProfileFetchResult(null, null, tried);
    }

    private async Task<(string Text, string SourceUrl)?> FetchWikipediaAsync(string companyName, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var client = httpFactory.CreateClient(ClientName);

            // opensearch:返回 [搜索词, [标题...], [描述...], [链接...]]
            var searchUrl = "https://en.wikipedia.org/w/api.php?action=opensearch&search="
                + Uri.EscapeDataString(companyName) + "&limit=5&namespace=0&format=json";
            using var searchReq = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            searchReq.Headers.Accept.ParseAdd("application/json");
            using var searchResp = await client.SendAsync(searchReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!searchResp.IsSuccessStatusCode)
                return null;
            var searchJson = await searchResp.Content.ReadAsStringAsync(cts.Token);
            string? title = null;
            using (var doc = JsonDocument.Parse(searchJson))
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
                    return null;
                foreach (var t in doc.RootElement[1].EnumerateArray())
                {
                    var candidate = t.GetString();
                    if (candidate is not null && TitlesMatch(companyName, candidate))
                    {
                        title = candidate;
                        break;
                    }
                }
            }
            if (title is null)
                return null; // 不匹配则判未找到、不猜测

            var summaryUrl = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(title);
            using var summaryReq = new HttpRequestMessage(HttpMethod.Get, summaryUrl);
            summaryReq.Headers.Accept.ParseAdd("application/json");
            using var summaryResp = await client.SendAsync(summaryReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!summaryResp.IsSuccessStatusCode)
                return null;
            var summaryJson = await summaryResp.Content.ReadAsStringAsync(cts.Token);
            using var summaryDoc = JsonDocument.Parse(summaryJson);
            var extract = summaryDoc.RootElement.TryGetProperty("extract", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(extract))
                return null;
            return (extract.Trim(), "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title));
        }
        catch
        {
            return null;
        }
    }

    private async Task<(string Text, string SourceUrl)?> FetchWebsiteAboutAsync(string website, CancellationToken ct)
    {
        try
        {
            var raw = website.Trim();
            if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                raw = "https://" + raw;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var baseUri))
                return null;
            if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
                return null;
            if (JobPageFetcher.IsBlockedHost(baseUri.Host))
                return null;
            if (!await HostResolvesPublicAsync(baseUri.Host, ct))
                return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var client = httpFactory.CreateClient(ClientName);

            // robots.txt:取不到(404/网络失败)按允许处理;路径被禁则跳过该路径。
            var disallows = await LoadRobotsDisallowsAsync(client, baseUri, cts.Token);

            foreach (var path in AboutPaths)
            {
                if (IsPathDisallowed(disallows, path))
                    continue;
                var pageUri = new Uri(baseUri, path);
                using var req = new HttpRequestMessage(HttpMethod.Get, pageUri);
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    continue;
                var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (mediaType.Length > 0
                    && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase))
                    continue;
                var html = await resp.Content.ReadAsStringAsync(cts.Token);
                if (html.Length < 200 || html.Length > 2_000_000)
                    continue;
                // 复用 JobPageFetcher 式提纯(main/article 优先、去导航行);<300 字判 JS 空壳。
                var text = JobPageFetcher.ExtractText(html);
                if (text.Length >= 300)
                    return (text, pageUri.ToString());
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<string>> LoadRobotsDisallowsAsync(HttpClient client, Uri baseUri, CancellationToken ct)
    {
        var disallows = new List<string>();
        try
        {
            var robotsUri = new Uri(baseUri, "/robots.txt");
            using var resp = await client.GetAsync(robotsUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return disallows;
            var txt = await resp.Content.ReadAsStringAsync(ct);
            // 仅取 User-agent: * 组(无更具体的组命中本 UA 时 * 组即适用口径)。
            // 简化解析:按组切分,组内含 "*" 的 Disallow 行全部采纳;其余组忽略。
            var currentAgents = new List<string>();
            var currentDisallows = new List<string>();
            var starDisallows = new List<string>();
            void FlushGroup()
            {
                if (currentAgents.Any(a => a == "*"))
                    starDisallows.AddRange(currentDisallows);
                currentAgents.Clear();
                currentDisallows.Clear();
            }
            foreach (var rawLine in txt.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    FlushGroup();
                    continue;
                }
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line[..hash].Trim();
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (key == "user-agent")
                {
                    if (currentDisallows.Count > 0 || currentAgents.Count > 0)
                        FlushGroup();
                    currentAgents.Add(value);
                }
                else if (key == "disallow" && value.Length > 0)
                {
                    currentDisallows.Add(value);
                }
            }
            FlushGroup();
            disallows.AddRange(starDisallows);
        }
        catch
        {
            // robots 取不到按允许处理
        }
        return disallows;
    }

    // ================= 纯函数(单测覆盖) =================

    /// <summary>
    /// Wikipedia 标题匹配:忽略大小写,忽略尾部消歧后缀 "(company)" 类。
    /// 不匹配则判未找到、不猜测(不做包含/模糊匹配)。
    /// </summary>
    internal static bool TitlesMatch(string companyName, string title)
    {
        return NormalizeTitle(companyName) == NormalizeTitle(title);
    }

    internal static string NormalizeTitle(string s)
    {
        var t = Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
        // 剥掉尾部 "(...)" 消歧后缀,可多层(如 "Foo (company) (band)" 极少见,循环剥干净)。
        while (t.EndsWith(")", StringComparison.Ordinal))
        {
            var open = t.LastIndexOf('(');
            if (open < 0) break;
            t = t[..open].TrimEnd();
        }
        return t;
    }

    /// <summary>robots Disallow 判定:前缀匹配;规则尾 "$" 表示精确匹配;空规则不禁任何路径。</summary>
    internal static bool IsPathDisallowed(IReadOnlyList<string> disallows, string path)
    {
        foreach (var rule in disallows)
        {
            if (rule.Length == 0) continue;
            if (rule.EndsWith("$", StringComparison.Ordinal))
            {
                if (path == rule[..^1]) return true;
            }
            else
            {
                var prefix = rule.Split('*')[0];
                if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 主机是否解析为全公开 IP:任一地址为内网/保留/回环/链路本地即判不公开。
    /// 域名解析失败判不公开(不发请求)。
    /// </summary>
    internal static async Task<bool> HostResolvesPublicAsync(string host, CancellationToken ct)
    {
        try
        {
            // IP 字面量直接判定,不走 DNS。
            if (IPAddress.TryParse(host, out var literal))
                return IsPublicAddress(literal);
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b0 = bytes[0]; var b1 = bytes[1];
            // 10/8、172.16/12、192.168/16 私有;169.254/16 链路本地;100.64/10 CGNAT;
            // 192.0.0.0/24、192.0.2.0/24、198.51.100.0/24、203.0.113.0/24 文档保留;240/4 保留。
            if (b0 == 10) return false;
            if (b0 == 172 && b1 >= 16 && b1 <= 31) return false;
            if (b0 == 192 && b1 == 168) return false;
            if (b0 == 169 && b1 == 254) return false;
            if (b0 == 100 && b1 >= 64 && b1 <= 127) return false;
            if (b0 == 192 && (b1 == 0 || b1 == 2)) return false;
            if (b0 == 198 && b1 == 51 && bytes[2] == 100) return false;
            if (b0 == 203 && b1 == 0 && bytes[2] == 113) return false;
            if (b0 == 0 || b0 >= 240) return false;
            return true;
        }
        // IPv6:回环 ::1、未指定 ::、链路本地 fe80::/10、唯一本地 fc00::/7、多播 ff00::/8、文档 2001:db8::/32。
        if (address.Equals(IPAddress.IPv6Loopback)) return false;
        var first = (bytes[0] << 8) | bytes[1];
        if ((first & 0xFFC0) == 0xFE80) return false; // fe80::/10
        if ((first & 0xFE00) == 0xFC00) return false; // fc00::/7
        if (bytes[0] == 0xFF) return false;           // ff00::/8
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) return false;
        return true;
    }
}
