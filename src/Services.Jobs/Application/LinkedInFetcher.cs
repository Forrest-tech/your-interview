using Microsoft.Playwright;

namespace YourInterview.Services.Jobs.Application;

/// <summary>
/// 2026-10-19:LinkedIn 职位页抓取 —— 用 Playwright 带用户登录态抓取，
/// 提取 JD 的 HTML 结构(保留 ul/li)，解决纯文本粘贴丢失 bullet 的问题。
/// 首次使用需用户在持久化浏览器 profile 中登录一次 LinkedIn。
/// </summary>
public sealed class LinkedInFetcher : IAsyncDisposable
{
    private readonly ILogger<LinkedInFetcher> _log;
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IBrowserContext? _ctx;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _profileDir;

    public LinkedInFetcher(ILogger<LinkedInFetcher> log, IConfiguration cfg)
    {
        _log = log;
        _profileDir = cfg["LinkedIn:ProfileDir"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".your-interview", "linkedin-profile");
    }

    private async Task EnsureAsync()
    {
        if (_ctx is not null) return;
        await _lock.WaitAsync();
        try
        {
            if (_ctx is not null) return;
            _pw = await Playwright.CreateAsync();
            _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
            });
            Directory.CreateDirectory(_profileDir);
            _ctx = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
                // 持久化 cookies，登录一次即可
                StorageStatePath = Path.Combine(_profileDir, "state.json")
            });
            // 如果已有 state 则加载
            var statePath = Path.Combine(_profileDir, "state.json");
            if (File.Exists(statePath))
            {
                await _ctx.CloseAsync();
                _ctx = await _browser.NewContextAsync(new BrowserNewContextOptions
                {
                    StorageStatePath = statePath,
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                });
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>抓取 LinkedIn 职位页，返回 JD 区域的 HTML。</summary>
    public async Task<(bool ok, string? html, string? error)> FetchJobHtmlAsync(string url, CancellationToken ct = default)
    {
        try
        {
            await EnsureAsync();
            var page = await _ctx!.NewPageAsync();
            try
            {
                await page.GotoAsync(url, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });
                // 等 JD 容器出现
                try
                {
                    await page.WaitForSelectorAsync(
                        ".jobs-description__content, .description__text, [class*='job-details']",
                        new PageWaitForSelectorOptions { Timeout = 15000 });
                }
                catch { /* 继续，尝试提取 */ }

                // 检查是否被拦到登录页
                var curUrl = page.Url;
                if (curUrl.Contains("/login") || curUrl.Contains("/checkpoint"))
                {
                    return (false, null, "needs_login");
                }

                // 提 JD HTML：优先 .jobs-description__content
                var html = await page.EvaluateAsync<string>(@"() => {
                    const el = document.querySelector('.jobs-description__content')
                        || document.querySelector('.description__text')
                        || document.querySelector('[class*=job-details]');
                    if (!el) return document.body.innerHTML.slice(0, 200000);
                    // 展开 'Show more'
                    const btn = el.querySelector('button');
                    if (btn) btn.click();
                    return el.innerHTML;
                }");
                await page.CloseAsync();

                // 保存登录态
                await _ctx.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = Path.Combine(_profileDir, "state.json")
                });

                if (string.IsNullOrWhiteSpace(html))
                    return (false, null, "empty");
                return (true, html, null);
            }
            catch
            {
                await page.CloseAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LinkedIn 抓取失败: {Url}", url);
            return (false, null, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null) await _ctx.CloseAsync();
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
        _lock.Dispose();
    }
}
