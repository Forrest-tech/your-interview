using System.Text.RegularExpressions;

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
        @"<(script|style|noscript|header|footer|nav)[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new(
        @"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(
        @"[ \t\xa0]{2,}", RegexOptions.Compiled);

    private static readonly Regex MultiNewline = new(
        @"(\r?\n){3,}", RegexOptions.Compiled);

    public async Task<string?> FetchPageTextAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..] : uri.Host;
        if (BlockedHosts.Any(b => host.EndsWith(b, StringComparison.OrdinalIgnoreCase)))
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
        var noTags = HtmlTag.Replace(noScript, " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        var collapsed = MultiSpace.Replace(decoded, " ");
        var trimmed = MultiNewline.Replace(collapsed, "\n\n");
        return trimmed.Trim();
    }
}

public static class JobPageFetcherOptions
{
    public const string ClientName = "JobPageFetcher";
}
