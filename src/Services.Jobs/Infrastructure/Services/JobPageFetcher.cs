using System.Text.RegularExpressions;
using YourInterview.Services.Jobs.Application;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

// ============================================================================
//  招聘页面抓取器(Smart paste 的"粘贴链接"链路)。
//
//  用户粘贴 https://boards.greenhouse.io/... 这类链接时,光靠 URL 模式只能猜出
//  公司名,拿不到 JD 正文。这里把页面抓下来、提纯文本,再交给 JdPasteParser 解析。
//
//  诚实边界:
//  - LinkedIn / Indeed / Glassdoor 等站有反爬,抓了一定失败 —— 直接返回 null,
//    让调用方提示用户改粘贴 JD 文本,而不是假装去抓然后超时。
//  - 抓取失败(超时/404/非 HTML)一律返回 null,绝不抛异常中断解析链路。
// ============================================================================

public interface IJobPageFetcher
{
    /// <summary>抓取招聘页面正文。拿不到时返回 null(调用方降级为 URL 模式解析)。</summary>
    Task<string?> FetchPageTextAsync(string url, CancellationToken ct);
}

public sealed class JobPageFetcher(IHttpClientFactory httpFactory) : IJobPageFetcher
{
    // 这些站点的反爬会让抓取必定失败 —— 提前拒绝,省掉一次注定超时的请求。
    private static readonly string[] BlockedHosts =
    [
        "linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com",
        "monster.com", "dice.com"
    ];

    private static readonly Regex ScriptStyle = new(
        @"<(script|style|noscript|header|footer|nav|aside|form|iframe|select|button|svg|picture)[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex MainContent = new(
        @"<(main|article)\b[^>]*>(.*?)</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new(
        @"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// 票 154 R-154-9：块级标签。抓取文本里小节标题能否成行，不应依赖源码换行 ——
    /// 块级标签替换为换行（其余标签仍替换为空格），使标题行独立成行、
    /// 152 的行级模块切除信号更可靠。只改换行产出，不改 RemoveNavLines 与 152 切除的判定口径。
    /// </summary>
    private static readonly Regex BlockTag = new(
        @"</?(h[1-6]|p|div|li|tr|section|article)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(
        @"[ \t\xa0]{2,}", RegexOptions.Compiled);

    private static readonly Regex MultiNewline = new(
        @"(\r?\n){3,}", RegexOptions.Compiled);

    // 导航面包屑里的典型词组:一行里命中 ≥2 个即视为导航行。
    // 例:"locations contact us EN FR randstad"、"0 my randstad my randstad randstad"
    private static readonly Regex NavPhrases = new(
        @"locations?|contact us|my\s+\w+(\s+\w+)?|sign\s*in|log\s*in|create account|privacy policy|cookies?|terms of use",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 纯语言切换行:"EN FR" / "EN | FR"
    private static readonly Regex LangToggleLine = new(
        @"^[\s|·•\-–—/]*(en|fr)(\s*[|·•\-–—/]\s*(en|fr))?[\s|·•\-–—/]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SeparatorsOnly = new(
        @"^[\s|·•\-–—/\\]+$", RegexOptions.Compiled);

    /// <summary>票142:反爬站判定供公司简介抓取腿复用(同一口径,避免两份名单漂移)。</summary>
    internal static bool IsBlockedHost(string host)
    {
        var h = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return BlockedHosts.Any(b => h.EndsWith(b, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string?> FetchPageTextAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        if (IsBlockedHost(uri.Host))
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var client = httpFactory.CreateClient(JobPageFetcherOptions.ClientName);
            using var resp = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return null;

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)
                && mediaType.Length > 0)
                return null;

            var html = await resp.Content.ReadAsStringAsync(cts.Token);
            if (html.Length < 200 || html.Length > 2_000_000)
                return null;

            var text = ExtractText(html);
            // 正文太短说明页面是 JS 渲染的壳(抓到的是空模板),没有解析价值。
            return text.Length >= 300 ? text : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string ExtractText(string html)
    {
        var noScript = ScriptStyle.Replace(html, "\n");
        // 主内容优先:页面有 <main>/<article> 时只取里面,导航栏自然被排除
        var mm = MainContent.Match(noScript);
        var content = mm.Success && mm.Groups[2].Value.Length >= 300
            ? mm.Groups[2].Value : noScript;
        // 票 154 R-154-9：块级标签先换行、其余标签再空格（顺序定死，否则块级也被空格吃掉）。
        var blockBroken = BlockTag.Replace(content, "\n");
        var noTags = HtmlTag.Replace(blockBroken, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var collapsed = MultiSpace.Replace(decoded, " ");
        // 票 152 R-152-1：提纯层切除相关文章/博客模块（标签剥离后、RemoveNavLines 之前，
        // 顺序定死）。信号定义见 RelatedModuleCut，三层共用同一处。
        var cut = RelatedModuleCut.Cut(collapsed);
        var deNav = RemoveNavLines(cut);
        var trimmed = MultiNewline.Replace(deNav, "\n\n");
        return trimmed.Trim();
    }

    /// <summary>
    /// 逐行剔除导航残渣。ATS/招聘站的导航常是纯文本 div(如 randstad 的
    /// "locations contact us EN FR randstad"),标签剥离器处理不了,只能按行过滤。
    /// 长行一律视为正文不动 —— 导航行都是短碎片。
    /// </summary>
    private static string RemoveNavLines(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (IsNavLine(line)) continue;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static bool IsNavLine(string line)
    {
        if (line.Length > 200) return false;
        if (LangToggleLine.IsMatch(line)) return true;
        if (SeparatorsOnly.IsMatch(line)) return true;
        // 页脚横幅:职位提醒订阅 / newsletter / "similar jobs" 推广 —— 页面 chrome,不是 JD。
        // (Randstad 的 "We will keep you updated when we have similar job postings.")
        if (Regex.IsMatch(line,
                @"similar job postings|job alerts|personalised|personalized|keep you updated|let similar jobs come to you|subscribing to your|job newsletter",
                RegexOptions.IgnoreCase)) return true;
        // 整行就是账号链接("my randstad")或孤立数字("0") —— 不是正文
        if (Regex.IsMatch(line, @"^my\s+\w+(\s+\w+)?$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(line, @"^[\d\s.,/\-–—x×]{1,10}$")) return true;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words <= 12 && NavPhrases.Matches(line).Count >= 2) return true;
        return false;
    }
}

public static class JobPageFetcherOptions
{
    public const string ClientName = "JobPageFetcher";
}
