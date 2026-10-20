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

    /// <summary>2026-10-19:检查浏览器是否存在。</summary>
    private bool IsBrowserInstalled()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var basePath = Path.Combine(home, ".cache", "ms-playwright");
        // .NET Playwright 1.44.0 用 chromium-1117（与 Mac 宿主机 Python Playwright 一致）
        // 兼容 headless shell 和完整版
        return Directory.Exists(Path.Combine(basePath, "chromium-1117"))
            || Directory.Exists(Path.Combine(basePath, "chromium_headless_shell-1117"));
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
                // 2026-10-19:同时提取页面标题、公司介绍、薪资/工作模式等结构化字段
                var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(@"() => {
                    const el = document.querySelector('.jobs-description__content')
                        || document.querySelector('.description__text')
                        || document.querySelector('[class*=job-details]')
                        || document.querySelector('.jobs-details__main-content');
                    let html = '';
                    if (el) {
                        const btn = el.querySelector('button');
                        if (btn) btn.click();
                        html = el.innerHTML;
                    } else {
                        html = document.body.innerHTML.slice(0, 200000);
                    }
                    // 公司介绍：About the company 卡片
                    let companyDesc = '';
                    const aboutHeaders = document.querySelectorAll('h2, h3');
                    for (const h of aboutHeaders) {
                        if (/about the company/i.test(h.textContent)) {
                            let sib = h.nextElementSibling;
                            let parts = [];
                            while (sib && parts.length < 5) {
                                if (sib.textContent.trim()) parts.push(sib.innerHTML);
                                sib = sib.nextElementSibling;
                            }
                            companyDesc = parts.join('\n');
                            break;
                        }
                    }
                    // 元数据：薪资/工作模式/职位类型（pill chips）
                    // LinkedIn 职位头部的 pill：$90K/yr、Hybrid、Full-time 等
                    let salary = '', workMode = '', jobType = '';
                    const pills = document.querySelectorAll('li, span, div');
                    for (const p of pills) {
                        const t = (p.textContent || '').trim();
                        if (!t || t.length > 50) continue;
                        // 薪资：含 $ 或 /yr / /hr
                        if (!salary && /\$[\d,]+/.test(t) && /\/yr|\/hr|per year|per hour/i.test(t)) {
                            salary = t;
                        }
                        // 工作模式
                        if (!workMode && /^(Hybrid|Remote|On-site)$/i.test(t)) {
                            workMode = t;
                        }
                        // 职位类型
                        if (!jobType && /^(Full-time|Part-time|Contract|Temporary)$/i.test(t)) {
                            jobType = t;
                        }
                        if (salary && workMode && jobType) break;
                    }
                    // 公司 logo：职位头部公司 logo img
                    let logoUrl = '';
                    const logoImg = document.querySelector('.jobs-details__main-content img')
                        || document.querySelector('[class*=company-logo] img')
                        || document.querySelector('img[alt*=logo i]');
                    if (logoImg) logoUrl = logoImg.src || '';
                    return {
                        html: html,
                        title: document.title,
                        salary: salary,
                        workMode: workMode,
                        jobType: jobType,
                        companyDesc: companyDesc,
                        logoUrl: logoUrl
                    };
                }");
                var html = result.GetProperty("html").GetString() ?? "";
                var pageTitle = result.GetProperty("title").GetString() ?? "";
                var companyDesc = result.TryGetProperty("companyDesc", out var cd) ? cd.GetString() ?? "" : "";
                var liSalary = result.TryGetProperty("salary", out var s) ? s.GetString() ?? "" : "";
                var liWorkMode = result.TryGetProperty("workMode", out var w) ? w.GetString() ?? "" : "";
                var liJobType = result.TryGetProperty("jobType", out var j) ? j.GetString() ?? "" : "";
                var liLogo = result.TryGetProperty("logoUrl", out var l) ? l.GetString() ?? "" : "";
                await page.CloseAsync();

                // 保存登录态
                await _ctx.StorageStateAsync(new BrowserContextStorageStateOptions
                {
                    Path = Path.Combine(_profileDir, "state.json")
                });

                if (string.IsNullOrWhiteSpace(html))
                    return (false, null, "empty");
                // 页面标题格式: "职位 @ 公司 | LinkedIn"，拼到 HTML 前面供解析器用
                if (!string.IsNullOrWhiteSpace(pageTitle))
                    html = $"<title>{System.Net.WebUtility.HtmlEncode(pageTitle)}</title>\n" + html;
                // LinkedIn pill 字段：薪资/工作模式/职位类型，用 meta 标记传递
                if (!string.IsNullOrWhiteSpace(liSalary))
                    html = $"<meta name=\"li-salary\" content=\"{System.Net.WebUtility.HtmlEncode(liSalary)}\">\n" + html;
                if (!string.IsNullOrWhiteSpace(liWorkMode))
                    html = $"<meta name=\"li-workmode\" content=\"{System.Net.WebUtility.HtmlEncode(liWorkMode)}\">\n" + html;
                if (!string.IsNullOrWhiteSpace(liJobType))
                    html = $"<meta name=\"li-jobtype\" content=\"{System.Net.WebUtility.HtmlEncode(liJobType)}\">\n" + html;
                // 公司 logo
                if (!string.IsNullOrWhiteSpace(liLogo))
                    html = $"<meta name=\"li-logo\" content=\"{System.Net.WebUtility.HtmlEncode(liLogo)}\">\n" + html;
                // 公司介绍拼到后面，解析器提取
                if (!string.IsNullOrWhiteSpace(companyDesc))
                    html += $"\n<!--COMPANY_DESC-->\n<div class=\"linkedin-company-desc\">{companyDesc}</div>";
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
