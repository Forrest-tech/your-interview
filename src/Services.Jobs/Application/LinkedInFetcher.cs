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

    /// <summary>2026-10-19:检查浏览器是否存在，不存在则返回错误（避免运行时下载杀进程）。</summary>
    private bool IsBrowserInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var browserPath = Path.Combine(home, ".cache", "ms-playwright", "chromium_headless_shell-1155");
        return Directory.Exists(browserPath);
    }

    private async Task EnsureAsync()
    {
        if (_ctx is not null) return;
        await _lock.WaitAsync();
        try
        {
            if (_ctx is not null) return;
            // 2026-10-19:浏览器必须在 Docker 构建时装好，缺失则抛错（前端显示友好提示）
            if (!IsBrowserInstalled())
                throw new InvalidOperationException("Playwright browser not installed. Rebuild Docker image.");
            _pw = await Playwright.CreateAsync();
            _browser = await _pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" }
            });
            Directory.CreateDirectory(_profileDir);
            var statePath = Path.Combine(_profileDir, "state.json");
            // 2026-10-19:有登录态则用，无则建空上下文（LinkedIn 部分职位页可匿名看）
            var proxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
            BrowserNewContextOptions ctxOpts;
            if (File.Exists(statePath))
            {
                ctxOpts = new BrowserNewContextOptions
                {
                    StorageStatePath = statePath,
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                };
            }
            else
            {
                ctxOpts = new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
                };
            }
            // 2026-10-19:沙盒/容器经代理出站，Playwright 需显式配置
            if (!string.IsNullOrEmpty(proxy))
            {
                // 解析 http://user:pass@host:port 格式
                try
                {
                    var uri = new Uri(proxy);
                    var userInfo = uri.UserInfo.Split(':');
                    ctxOpts.Proxy = new Proxy
                    {
                        Server = $"{uri.Scheme}://{uri.Host}:{uri.Port}",
                        Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : null,
                        Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
                    };
                }
                catch
                {
                    ctxOpts.Proxy = new Proxy { Server = proxy };
                }
            }
            _ctx = await _browser.NewContextAsync(ctxOpts);
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
