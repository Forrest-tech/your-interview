using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;
// ============================================================================
//  确定性解析器:JD
// ============================================================================

/// <summary>
/// 纯规则解析器。刻意不依赖任何外部服务 ——
/// 用户没配 AI 凭据时这一层仍然要能给出可用结果,这是"功能不能因为配置缺失而消失"的底线。
/// </summary>
public static class JdPasteParser
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>已知招聘站点。CompanyFromSubdomain=true 表示公司名在二级域名里(Greenhouse/Workday 的典型形态)。</summary>
    private static readonly (string Host, string Name, bool CompanyFromSubdomain)[] KnownHosts =
    [
        ("linkedin.com", "LinkedIn", false),
        ("indeed.com", "Indeed", false),
        ("greenhouse.io", "Greenhouse", true),
        ("lever.co", "Lever", true),
        ("myworkdayjobs.com", "Workday", true),
        ("workday.com", "Workday", true),
        ("ashbyhq.com", "Ashby", true),
        ("workable.com", "Workable", true),
        ("smartrecruiters.com", "SmartRecruiters", true),
        ("jobvite.com", "Jobvite", true),
        ("bamboohr.com", "BambooHR", true),
        ("recruitee.com", "Recruitee", true),
        ("glassdoor.com", "Glassdoor", false),
        ("ziprecruiter.com", "ZipRecruiter", false),
        ("monster.com", "Monster", false),
        ("dice.com", "Dice", false),
        ("builtin.com", "BuiltIn", false),
        ("otta.com", "Otta", false),
        ("wellfound.com", "Wellfound", false),
        ("angel.co", "AngelList", false),
        ("levels.fyi", "Levels.fyi", false),
        ("jobs.apple.com", "Apple", false),
        ("amazon.jobs", "Amazon", false),
        ("google.com", "Google", false),
        ("microsoft.com", "Microsoft", false),
        ("meta.com", "Meta", false),
        ("netflix.com", "Netflix", false),
        ("stripe.com", "Stripe", false),
        ("shopify.com", "Shopify", false),
    ];

    /// <summary>技术关键词词典 —— JD 里命中哪些,就作为"我该复习什么"的候选。</summary>
    private static readonly string[] TechKeywords =
    [
        "C#", ".NET", "ASP.NET", "EF Core", "Entity Framework", "Blazor", "WPF",
        "Angular", "React", "Vue", "TypeScript", "JavaScript", "Node.js", "RxJS", "NgRx",
        "SQL", "PostgreSQL", "MySQL", "SQL Server", "MongoDB", "Redis", "Elasticsearch",
        "Kafka", "RabbitMQ", "gRPC", "REST", "GraphQL", "Microservices",
        "Docker", "Kubernetes", "Terraform", "CI/CD", "GitHub Actions", "Azure DevOps",
        "AWS", "Azure", "GCP", "Lambda", "SignalR", "OAuth", "JWT",
        "Python", "Java", "Go", "Rust", "Ruby", "PHP", "Swift", "Kotlin",
        "Machine Learning", "LLM", "RAG", "System Design", "DDD", "Clean Architecture",
        "Agile", "Scrum", "TDD", "Unit Test"
    ];

    private static readonly Regex UrlToken = new(@"^https?://\S+$", Opts);
    private static readonly Regex ImageUrl = new(@"^data:image/|\.(png|jpe?g|gif|webp|heic)(\?|$)", Opts);
    // ⚠️ 两端都要允许出现币种符号:"$140,000 - $180,000" 是最常见的写法,
    //    只在开头允许符号会让整条匹配失败,退而去匹配日期里的 "2026-11-30"
    //    → 解出 "$11k - $26k" 这种荒谬结果(踩过,务必保留 cur2)。
    // ⚠️ 2026-10-19:补 (?<![\d$€£\-–—]) 前瞻 —— 之前 "2026-11-30" 里 "026-11"
    //    仍能命中,日期必须整体排除(连 "-" 前导的 "11-30" 段也要挡)。
    // ⚠️ 2026-10-19:金额后允许 "/yr|/year|per year|per hour" 等频率后缀,
    //    否则 "$90K/yr - $105K/yr"(LinkedIn 常见写法) 完全匹配不上。
    private static readonly Regex SalaryRange = new(
        @"(?<![\d$€£\-–—])(?<cur>[$€£]|USD|CAD|US\$|C\$)?\s*(?<lo>\d{2,3})(?:[kK]|,?000)?(?<rate1>\s*(?:/|per\s+)(?:year|yr|annum|month|mo|week|wk|hour|hr))?\s*(?:-|–|—|to|~)\s*(?:(?:USD|CAD|AUD|GBP|EUR)\s*)?(?<cur2>[$€£]|USD|CAD|US\$|C\$)?\s*(?<hi>\d{2,3})(?:[kK]|,?000)?(?<rate2>\s*(?:/|per\s+)(?:year|yr|annum|month|mo|week|wk|hour|hr))?(?!\d)", Opts);
    private static readonly Regex SalarySingle = new(
        @"(?<![\d$€£\-–—])(?<cur>[$€£]|USD|CAD|US\$|C\$)\s*(?<lo>\d{2,3})\s*[kK](?<rate>\s*(?:/|per\s+)(?:year|yr|annum|month|mo|week|wk|hour|hr))?\b", Opts);
    // 时薪无 k 写法:"$45/hour"、"$55 per hr"
    private static readonly Regex SalaryHourly = new(
        @"(?<![\d$€£\-–—])(?<cur>[$€£]|USD|CAD|C\$)\s*(?<lo>\d{2,3}(?:\.\d{1,2})?)\s*(?:/|per\s+)(?:hour|hr)\b", Opts);
    private static readonly Regex LocationCityState = new(
        @"\b(?<city>[A-Z][a-zA-Z]+(?:[ ][A-Z][a-zA-Z]+)?)[ ]*,[ ]*(?<region>[A-Z]{2}|[A-Z][a-zA-Z]+)\b", RegexOptions.Compiled);
    private static readonly Regex UrlAnywhere = new(@"https?://\S+", Opts);
    private static readonly Regex EmailAnywhere = new(@"[\w.+-]+@[\w-]+\.[\w.]+", Opts);

    /// <summary>识别粘贴内容类型。图片只识别不解读 —— 读不出来就说读不出来。</summary>
    public static string DetectKind(string raw)
    {
        var s = raw.Trim();
        if (ImageUrl.IsMatch(s)) return PasteKind.Image;
        if (UrlToken.IsMatch(s)) return PasteKind.Url;
        // 2026-10-19:含 HTML 标签的富文本粘贴(从 LinkedIn 页面复制)
        if (s.Contains('<') && s.Contains('>') && Regex.IsMatch(s, @"<(p|div|li|ul|ol|h[1-6]|br)[\s>]", RegexOptions.IgnoreCase))
            return PasteKind.Html;
        return PasteKind.Text;
    }

    /// <summary>是否聚合招聘站(而非公司自有站点)。决定要不要把链接当公司官网。</summary>
    public static bool IsAggregator(string? url)
    {
        var host = HostOf(url);
        if (string.IsNullOrWhiteSpace(host)) return false;
        return AggregatorHosts.Any(h => host.EndsWith(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>聚合招聘站域名。它们只是"发布渠道",不是公司官网。</summary>
    private static readonly string[] AggregatorHosts =
    [
        "linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com", "monster.com",
        "dice.com", "builtin.com", "otta.com", "wellfound.com", "angel.co", "levels.fyi"
    ];

    public static string? HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = Regex.Match(url, @"https?://([^/]+)", Opts);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 2026-10-19:HTML 转结构化文本 —— 保留 &lt;ul&gt;&lt;li&gt; 的 bullet 结构。
    /// 用于从 LinkedIn 页面复制的富文本粘贴(剪贴板 text/html)。
    /// li 转为 "• " 开头行,p/h1-h4 转为独立段落,其余标签剥掉。
    /// </summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var t = html;
        // 2026-10-21:整段丢弃不可见区块 —— 否则 <title>/<style>/<script> 的内容会
        // 在剥标签后混进正文(存量 Arvato JD 就是整页 HTML 糊脸的来源之一)。
        t = Regex.Replace(t, @"<!--.*?-->", "", RegexOptions.Singleline);
        t = Regex.Replace(t, @"<(head|script|style|title|noscript|svg)\b[^>]*>.*?</\1\s*>", "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        t = Regex.Replace(t, @"<(meta|link|base)\b[^>]*>", "", RegexOptions.IgnoreCase);
        // li → bullet 行
        t = Regex.Replace(t, @"<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"</li>", "", RegexOptions.IgnoreCase);
        // 块级标签 → 换行
        t = Regex.Replace(t, @"</?(p|div|h[1-6]|br|tr)[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"</?ul[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"</?ol[^>]*>", "\n", RegexOptions.IgnoreCase);
        // 去掉其余标签
        t = Regex.Replace(t, @"<[^>]+>", " ", RegexOptions.IgnoreCase);
        // HTML 实体
        t = t.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        // 清理多余空行
        t = Regex.Replace(t, @"[ \t]+", " ");
        t = Regex.Replace(t, @"\n\s*\n\s*\n+", "\n\n");
        return t.Trim();
    }

    public static ParsedJdDto Parse(string raw, string kind)
    {
        var conf = new Dictionary<string, int>();
        // 2026-10-19:HTML 富文本先转结构化文本(保留 bullet)
        var isHtml = kind == PasteKind.Html;
        // 2026-10-19:LinkedIn 抓取的 HTML 带 <title>职位 @ 公司 | LinkedIn>，先提取
        string? titleTagRole = null;
        string? titleTagCompany = null;
        string? liSalary = null;
        string? liWorkMode = null;
        string? liJobType = null;
        string? liLogo = null;
        if (isHtml)
        {
            var titleMatch = Regex.Match(raw, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (titleMatch.Success)
            {
                var title = System.Net.WebUtility.HtmlDecode(titleMatch.Groups[1].Value.Trim());
                // 格式: "Senior .Net Developer @ Arvato | LinkedIn" 或 "Senior .Net Developer hiring now | LinkedIn"
                var atIdx = title.IndexOf(" @ ");
                if (atIdx > 0)
                {
                    titleTagRole = title.Substring(0, atIdx).Trim();
                    var rest = title.Substring(atIdx + 3);
                    var pipeIdx = rest.IndexOf(" | ");
                    titleTagCompany = (pipeIdx > 0 ? rest.Substring(0, pipeIdx) : rest).Trim();
                }
            }
            // LinkedIn pill 字段
            var salaryMatch = Regex.Match(raw, @"<meta name=""li-salary"" content=""(.*?)""", RegexOptions.IgnoreCase);
            if (salaryMatch.Success) liSalary = System.Net.WebUtility.HtmlDecode(salaryMatch.Groups[1].Value);
            var wmMatch = Regex.Match(raw, @"<meta name=""li-workmode"" content=""(.*?)""", RegexOptions.IgnoreCase);
            if (wmMatch.Success) liWorkMode = System.Net.WebUtility.HtmlDecode(wmMatch.Groups[1].Value);
            var jtMatch = Regex.Match(raw, @"<meta name=""li-jobtype"" content=""(.*?)""", RegexOptions.IgnoreCase);
            if (jtMatch.Success) liJobType = System.Net.WebUtility.HtmlDecode(jtMatch.Groups[1].Value);
            var logoMatch = Regex.Match(raw, @"<meta name=""li-logo"" content=""(.*?)""", RegexOptions.IgnoreCase);
            if (logoMatch.Success) liLogo = System.Net.WebUtility.HtmlDecode(logoMatch.Groups[1].Value);
        }
        var text = isHtml ? HtmlToText(raw.Trim()) : StripLinkedInNoise(raw.Trim());
        var isUrl = kind == PasteKind.Url;

        string? company = null;
        string? role = null;
        string? link = null;
        string? detected = null;

        var urlMatch = UrlAnywhere.Match(text);
        if (urlMatch.Success) link = urlMatch.Value.TrimEnd(')', '】', '」', ',', '。');

        if (isUrl && link is not null)
        {
            var (c, r, src) = ParseJobUrl(link);
            company = c; role = r; detected = src;
            if (company is not null) conf["company"] = 70;
            if (role is not null) conf["role"] = 60;
        }

        // ---- 正文里的结构化线索 ----
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        company ??= Labeled(lines, "公司", "Company", "Employer", "Organization");

        // 链接型公司名兜底(见下方 CleanCompanyName 注释)
        if (company is null && link is not null)
        {
            var (uc, _, _) = ParseJobUrl(link);
            if (!string.IsNullOrWhiteSpace(uc))
            {
                company = uc;
                conf["company"] = 65;
            }
        }

        company ??= AfterAt(lines)
                 ?? CompanyAfterAboutCompany(lines);
        if (company is not null) conf.TryAdd("company", 80);

        role ??= Labeled(lines, "职位", "岗位", "Role", "Title", "Position", "Job Title")
              ?? GuessRole(lines);
        if (role is not null) conf.TryAdd("role", 75);

        // 公司名兜底:role 下一行常常是无标签的公司名
        if (company is null && role is not null)
        {
            company = CompanyAfterRole(lines, role);
            // 票 29 (REQ-09/FUN-09):company-first 形态公司行在 role 行之前,同样是无标签公司名
            company ??= CompanyBeforeRole(lines, role);
            if (company is not null) conf.TryAdd("company", 60);
        }

        // 最后清洗:正文里抓到的公司名可能带导航残渣
        // ("locations contact us EN FR randstad" → "Randstad")
        company = CleanCompanyName(company);
        if (company is null) conf.Remove("company");

        var location = Labeled(lines, "地点", "Location", "办公地点", "工作地点")
                    ?? GuessLocation(text);
        if (location is not null) conf["location"] = 80;

        var workMode = GuessWorkMode(text);
        if (workMode is not null) conf["workMode"] = 85;

        var jobType = ExtractJobType(lines, text);
        if (jobType is not null) conf["jobType"] = 85;

        var salary = GuessSalary(text);
        if (salary is not null) conf["salary"] = 85;

        var deadline = GuessDeadline(text);

        var keywords = MatchKeywords(text);
        if (keywords is not null) conf["matchKeywords"] = 65;

        var priority = GuessPriority(text);

        // 摘要:URL 场景下没有正文,摘要留空交给 AI 或用户;
        // 有正文时取开头若干字符,宁可短也不截断成半句。
        var jdText = isUrl ? null : text;
        var jdSummary = isUrl ? null : Summary(text);
        if (jdSummary is not null) conf["jdSummary"] = 50;

        // 公司介绍 / 任职要求 / 福利:段落级提取,供用户在弹窗里确认
        // 2026-10-19:LinkedIn 抓取的 HTML 带 linkedin-company-desc 标记，优先用
        var companyDescription = ExtractCompanyDescription(lines, company);
        if (isHtml)
        {
            var descMatch = Regex.Match(raw, @"<div class=""linkedin-company-desc"">(.*?)</div>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (descMatch.Success)
            {
                var descHtml = descMatch.Groups[1].Value;
                var descText = HtmlToText(descHtml);
                if (!string.IsNullOrWhiteSpace(descText) && descText.Length > 20)
                    companyDescription = descText;
            }
        }
        if (companyDescription is not null) conf["companyDescription"] = 70;
        var requirements = ExtractSection(lines, RequirementsHeaders);
        if (requirements is not null) conf["requirements"] = 70;
        var benefits = ExtractSection(lines, BenefitsHeaders);
        if (benefits is not null) conf["benefits"] = 70;

        // 工作身份要求:PR/公民/工签/是否担保 —— 求职者(尤其持工签者)的关键过滤项。
        var workAuthorization = Labeled(lines, "工作身份", "身份要求", "Work Authorization", "Work Authorisation", "Eligibility", "Citizenship")
                             ?? ExtractWorkAuthorization(text);
        if (workAuthorization is not null) conf["workAuthorization"] = 75;

        var filled = CountFilled(company, role, location, salary, workMode, jdSummary);

        // 2026-10-19:<title> 提取的职位/公司优先（LinkedIn 抓取场景）
        if (!string.IsNullOrWhiteSpace(titleTagRole)) role = titleTagRole;
        if (!string.IsNullOrWhiteSpace(titleTagCompany)) company = titleTagCompany;
        // 2026-10-19:LinkedIn pill 字段优先
        if (!string.IsNullOrWhiteSpace(liSalary)) salary = liSalary;
        if (!string.IsNullOrWhiteSpace(liWorkMode)) workMode = liWorkMode;
        if (!string.IsNullOrWhiteSpace(liJobType)) jobType = liJobType;
        return new ParsedJdDto(
            kind,
            filled >= 4 ? ParseOutcome.Ok : filled >= 1 ? ParseOutcome.Partial : ParseOutcome.Failed,
            Array.Empty<string>(),
            company, role, location, salary, workMode, jobType, jdSummary, keywords, priority,
            link, jdText, companyDescription, requirements, benefits,
            workAuthorization,
            deadline, detected ?? (isUrl ? "未知站点" : null),
            conf, false, false, liLogo);
    }

    public static int CountFilled(ParsedJdDto d)
        => CountFilled(d.Company, d.Role, d.Location, d.Salary, d.WorkMode, d.JdSummary);

    private static int CountFilled(string? company, string? role, string? location,
        string? salary, string? workMode, string? jdSummary)
    {
        var n = 0;
        if (!string.IsNullOrWhiteSpace(company)) n++;
        if (!string.IsNullOrWhiteSpace(role)) n++;
        if (!string.IsNullOrWhiteSpace(location)) n++;
        if (!string.IsNullOrWhiteSpace(salary)) n++;
        if (!string.IsNullOrWhiteSpace(workMode)) n++;
        if (!string.IsNullOrWhiteSpace(jdSummary)) n++;
        return n;
    }

    // ---------- URL 解析 ----------

    private static (string? Company, string? Role, string Source) ParseJobUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return (null, null, "未知站点");
        var host = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? u.Host[4..] : u.Host;

        var labels = host.Split('.');
        var segs = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var (h, name, fromSub) in KnownHosts)
        {
            if (!host.EndsWith(h, StringComparison.OrdinalIgnoreCase)) continue;

            string? company;
            if (fromSub)
            {
                // 子域名型 ATS(Greenhouse / Workday / Lever / Ashby…):
                // 公司名可能在子域名里(amazon.wd1.myworkdayjobs.com),也可能在路径第一段里
                // (boards.greenhouse.io/shopify)。先取子域名里"不是通用前缀"的那一段,
                // 取不到再退到路径 —— 顺序反了会把 "en-US" 这种语言段当成公司名(踩过)。
                company = FirstMeaningful(labels) is { Length: > 1 } sub
                    ? Title(sub)
                    : FirstMeaningful(segs) is { Length: > 1 } pathSeg ? Title(pathSeg) : null;
            }
            else if (name == "LinkedIn")
            {
                company = LinkedInCompany(u.AbsolutePath);
            }
            else
            {
                // 公司官网(amazon.jobs / stripe.com/...):域名本身就是公司名;
                // 聚合站(indeed/glassdoor…)的域名只是发布渠道,猜出 "Indeed" 当公司是误导,
                // 宁可留空让正文线索(Labeled / AfterAt)来补 —— 猜错比留空更糟。
                company = AggregatorHosts.Contains(h, StringComparer.OrdinalIgnoreCase)
                    ? null
                    : FirstMeaningful(labels) is { Length: > 1 } core ? Title(core) : null;
            }

            var role = RoleFromSlug(u.AbsolutePath);
            if (role is null && u.Query.Length > 0)
            {
                var qm = Regex.Match(u.Query, @"(?:title|jobTitle|job)=([^&]+)", RegexOptions.IgnoreCase);
                if (qm.Success) role = Title(Uri.UnescapeDataString(qm.Groups[1].Value).Replace('+', ' '));
            }

            return (company, role, name);
        }

        // 未知站点:域名一级标签当公司名(够用做草稿),路径末段当职位。
        var coreHost = FirstMeaningful(labels);
        return (coreHost is { Length: > 1 } ? Title(coreHost) : null,
                RoleFromSlug(u.AbsolutePath), "公司官网/未知站点");
    }

    /// <summary>
    /// ATS 域名与路径里常见的"无意义段":平台自己的前缀、语言区、动词。
    /// 它们长得像公司名却不是,不滤掉就会把 "En-US"、"Jobs" 当成公司。
    /// </summary>
    private static readonly HashSet<string> GenericSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "www", "boards", "job-boards", "jobs", "job", "careers", "career", "apply", "hiring",
        "wd", "wd1", "wd3", "wd5", "myworkdayjobs", "workday", "en", "en-us", "zh", "fr",
        "embed", "detail", "view", "search", "company", "companies", "index", "home", "us", "ca",
        // ATS 平台自己的名字:boards.greenhouse.io/{org} 的 "greenhouse" 不是公司,
        // 不加进来 fromSub 分支会把平台名当公司(踩过)
        "greenhouse", "lever", "ashby", "ashbyhq", "workable", "smartrecruiters",
        "jobvite", "bamboohr", "recruitee",
        // 顶级域残段:子域名型 ATS 里平台名被跳过后,剩下的 "com"/"io" 不是公司名。
        // 不加的话 boards.greenhouse.io 会解出 "Io"(踩过)
        "com", "net", "org", "io", "co", "ca", "app", "site"
    };

    private static string? FirstMeaningful(IEnumerable<string> parts)
    {
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (GenericSegments.Contains(p)) continue;
            if (Regex.IsMatch(p, @"^(?:[a-z]{2}|[a-z]{2}-[a-z]{2})$", RegexOptions.IgnoreCase)
                && p.Length <= 5) continue;      // en / en-US / zh-CN
            return p;
        }
        return null;
    }

    /// <summary>
    /// LinkedIn 岗位链接的公司名。
    /// /company/xxx 直给;而 /jobs/view/senior-backend-engineer-at-shopify-4012339988
    /// 这种最常见的形态里,公司藏在 slug 的 "-at-" 之后 —— 这也是真实浏览器地址栏里的样子,
    /// 不处理的话从 LinkedIn 粘贴进来永远拿不到公司名。
    /// </summary>
    private static string? LinkedInCompany(string path)
    {
        var m = Regex.Match(path, @"/company/([^/]+)", RegexOptions.IgnoreCase);
        if (m.Success) return Title(m.Groups[1].Value);

        m = Regex.Match(path, @"-at-([a-z0-9][a-z0-9-]{1,40})-\d+", RegexOptions.IgnoreCase);
        return m.Success ? Title(m.Groups[1].Value.Replace('-', ' ')) : null;
    }

    private static string? RoleFromSlug(string path)
    {
        var segs = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return null;

        var last = segs[^1];
        if (Regex.IsMatch(last, @"^\d+$")) return null;             // 纯 id 段没有语义

        // 去掉 id:既可能是尾部的(…-4012339988),也可能是开头的(5567891-senior-engineer)。
        // 只处理尾部会让 Greenhouse 的链接解出 "5567891 Senior Software Engineer"。
        last = Regex.Replace(last, @"[_-][A-Za-z]*\d+$", string.Empty);
        last = Regex.Replace(last, @"^\d+[_-]", string.Empty);
        // LinkedIn 的 "-at-shopify" 是公司名,不该混进职位里。
        // slug 里分隔符是连字符(at-shopify),只认空白会匹配不上(踩过)。
        last = Regex.Replace(last, @"\s*-?\s*at[\s-]+[a-z0-9][a-z0-9 -]{1,40}$", string.Empty,
            RegexOptions.IgnoreCase);

        last = Uri.UnescapeDataString(last).Replace('-', ' ').Replace('_', ' ');
        last = Regex.Replace(last, @"\s+", " ").Trim();
        if (last.Length < 3 || last.Length > 80) return null;
        if (!RoleWords.IsMatch(last)) return null;                  // 不是岗位词就不冒充职位
        return Title(last);
    }

    private static readonly Regex RoleWords = new(
        @"\b(engineer|developer|manager|analyst|designer|scientist|architect|lead|senior|junior|staff|principal|director|intern|specialist|consultant|sre|devops|qa|product|data|frontend|front-end|backend|back-end|fullstack|full-stack|software|programmer|technician|researcher)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------- 文本解析 ----------

    private static string? Labeled(IEnumerable<string> lines, params string[] labels)
    {
        foreach (var line in lines)
        {
            foreach (var label in labels)
            {
                var m = Regex.Match(line, $@"^\s*{Regex.Escape(label)}\s*[:：]\s*(.+)$",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var v = m.Groups[1].Value.Trim();
                    if (v.Length > 0 && v.Length <= 120) return v;
                }
            }
        }
        return null;
    }

    private static string? AfterAt(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"\bat\s+([A-Z][A-Za-z0-9&.\- ]{1,40})", RegexOptions.Compiled);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    /// <summary>
    /// LinkedIn 形态:"About the company" 标题后紧跟公司名(再往下是 follower 数等)。
    /// 例:
    ///   About the company
    ///   Arvato
    ///   210,272 followers
    /// 取标题后第一个"像公司名"的非空行:长度 2-60、不含岗位词、不是纯数字/ follower 行。
    /// </summary>
    private static string? CompanyAfterAboutCompany(IList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i], @"^\s*about\s+the\s+company\s*$", RegexOptions.IgnoreCase))
                continue;
            // 标题后找第一个像公司名的行(最多往下看 3 行,跳过空行)
            for (var j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
            {
                var cand = lines[j].Trim();
                if (cand.Length == 0) continue;
                if (cand.Length is < 2 or > 60) continue;
                if (RoleWords.IsMatch(cand)) continue;
                if (Regex.IsMatch(cand, @"[\d,]+\s*(followers|employees)", RegexOptions.IgnoreCase)) continue;
                if (Regex.IsMatch(cand, @"^\d[\d,\s]*$", RegexOptions.Compiled)) continue;
                if (cand.Contains(':')) continue;
                // 公司名通常是短词组,太长的大概率是描述句
                if (cand.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5) continue;
                return cand;
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// 公司名导航残渣清洗。抓取页面正文里混入的导航行可能被前面的启发式
    /// 误当成公司名(如 "locations contact us EN FR randstad")。
    /// 剥掉导航词组和裸语言 token,剩下的才是公司名。
    /// 注意:不剥 "home"/"jobs" —— "Home Depot" 这种真公司名会被误伤。
    /// </summary>
    private static readonly Regex CompanyNavTokens = new(
        @"\b(locations?|contact(\s+us)?|sign\s*in|log\s*in|my\s+\w+|en|fr)\b",
        Opts);

    private static string StripLinkedInNoise(string text)
    {
        // LinkedIn 粘贴文本里的 UI 噪音:Premium 广告、Set alert、follower 数等。
        // 注意:用 [^\n]* 而不是 .*，避免 Singleline 模式下跨行匹配。
        var noisePatterns = new[]
        {
            @"Set alert for similar jobs[^\n]*",
            @"Try Premium for CA\$0[^\n]*",
            @"and millions of other members use Premium[^\n]*",
            @"1-month free trial with 24/7 support[^\n]*",
            @"We.ll remind you a week before your trial ends[^\n]*",
            @"Access company insights like[^\n]*",
            // "About the company" 段由 ExtractCompanyDescription 处理,这里不删 follower 行
            // (ExtractCompanyDescription 已会跳过 follower/员工数/行业行)
            @"\d[\d,]* on LinkedIn[^\n]*",
            @"No response insights available yet[^\n]*",
            @"Over \d+ applicants[^\n]*",
            @"\d+ week ago[^\n]*",
        };
        foreach (var pattern in noisePatterns)
        {
            text = Regex.Replace(text, pattern, "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        // 清理多余空行
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string? CleanCompanyName(string? company)
    {
        if (string.IsNullOrWhiteSpace(company)) return null;
        var v = CompanyNavTokens.Replace(company, " ");
        v = Regex.Replace(v, @"\s{2,}", " ").Trim(' ', '|', '·', '-', '—', ':');
        if (v.Length < 2) return null;
        return Title(v);
    }

    /// <summary>
    /// 公司名兜底:很多 JD 第一行是岗位、第二行是公司名(无标签)。
    /// 找到 role 行,取其下一行 —— 长度合理、不含岗位关键词、不是地点/薪资行。
    /// </summary>
    private static string? CompanyAfterRole(IList<string> lines, string? role)
    {
        if (role is null) return null;
        var idx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(role, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        if (idx < 0 || idx + 1 >= lines.Count) return null;
        return ValidateCompanyCandidate(lines[idx + 1]);
    }

    /// <summary>
    /// 票 29 (REQ-09/FUN-09):company-first 形态 —— 公司名在 role 行的上一行
    /// ("Globex Corporation\nSenior Backend Developer\nToronto, ON (Hybrid)")。
    /// 旧兜底只看 role 的下一行,这种最常见的公司官网排版永远解不出公司名,
    /// 草稿公司框为空、保存被卡 (FUN-09)。校验口径与 CompanyAfterRole 共用。
    /// </summary>
    private static string? CompanyBeforeRole(IList<string> lines, string? role)
    {
        if (role is null) return null;
        var idx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(role, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        if (idx <= 0) return null;
        return ValidateCompanyCandidate(lines[idx - 1]);
    }

    /// <summary>
    /// 无标签公司名候选行校验 (role 相邻行共用)。
    /// 票 29 (REQ-09) 两处修正:
    ///  1. 段落标题行不是公司名 —— "About the Job" 曾被误判为公司 (形态 C);
    ///  2. 公司名后带 "· 地点 (模式)" / "| 地点" / " - 地点" 后缀时 (FUN-09 role-first 形态
    ///     "Globex Corporation · Toronto, ON (Hybrid)"),旧逻辑被 LocationCityState 整行
    ///     排除 —— 先剥元数据后缀段再校验第一段。
    /// </summary>
    private static string? ValidateCompanyCandidate(string rawLine)
    {
        var cand = rawLine.Trim();
        // 剥元数据后缀段:"公司 · 地点" / "公司 | 地点" / "公司 - 地点 (模式)"
        var seg = Regex.Split(cand, @"\s*[·|]\s*|\s+-\s+");
        if (seg.Length > 1) cand = seg[0].Trim();
        // 排除:太长、含岗位关键词、是地点/薪资/标签行
        if (cand.Length is < 2 or > 60) return null;
        if (RoleWords.IsMatch(cand)) return null;
        if (cand.Contains(':') || cand.Contains('：')) return null;
        if (IsSectionHeader(cand)) return null;
        if (SalaryRange.IsMatch(cand) || SalarySingle.IsMatch(cand)) return null;
        if (LocationCityState.IsMatch(cand)) return null;
        // 排除纯描述句(太长或含动词开头)
        if (cand.Split(' ').Length > 6) return null;
        return cand;
    }

    /// <summary>
    /// 地点:城市组是贪婪双词,可能把前面的词吞进来
    /// ("Full Stack .Net Developer Toronto, Ontario" → city="Developer Toronto")。
    /// 城市里含岗位词的匹配一律跳过,取下一个。
    /// </summary>
    private static string? GuessLocation(string text)
    {
        foreach (Match lm in LocationCityState.Matches(text))
        {
            var city = lm.Groups["city"].Value;
            if (RoleWords.IsMatch(city)) continue;
            return $"{city}, {lm.Groups["region"].Value}";
        }
        return null;
    }

    private static string? GuessRole(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length > 120) continue;
            if (RoleWords.IsMatch(line))
            {
                // 剥掉括号尾巴:只去掉"地点/模式"类,技术限定语(如 Node.js/TypeScript)保留。
                // 规则:括号里含技术词/岗位词 → 是限定语,保留;否则是地点/模式尾巴,去掉。
                var v = Regex.Replace(line, @"[（(]([^()]*?)[)）]", m =>
                {
                    var inner = m.Groups[1].Value;
                    if (TechKeywords.Any(kw => inner.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                        return m.Value;
                    if (RoleWords.IsMatch(inner))
                        return m.Value;
                    return string.Empty;
                }).Trim();
                v = Regex.Replace(v, @"[|\-–—]\s*(remote|hybrid|onsite|on-site).*$", string.Empty,
                    RegexOptions.IgnoreCase).Trim();
                // 剥掉页面标题式前缀:"Job opening - X" / "Job: X" / "Opening: X"
                // (无分隔符的不动 —— "Job Coach" 这种真职位不能误伤)
                v = Regex.Replace(v, @"^(jobs?\s*(opening|posting|opportunity|ad|listing)?\s*[-–—:|]\s*)",
                    string.Empty, RegexOptions.IgnoreCase).Trim();
                // 剥掉尾部 "| 站点/公司名" 后缀。不含逗号的是站点后缀("| Randstad Canada");
                // 含逗号的多半是地点("| Toronto, ON"),留着。
                v = Regex.Replace(v, @"\s*\|\s*[^,|]{1,60}$", string.Empty).Trim();
                if (v.Length is >= 3 and <= 80) return v;
            }
        }
        return null;
    }

    private static string? GuessWorkMode(string text)
    {
        if (Regex.IsMatch(text, @"\b(hybrid)\b", RegexOptions.IgnoreCase)) return "Hybrid";
        if (Regex.IsMatch(text, @"\b(remote|fully remote|wfh|work from home|telecommute|远程|全远程)\b", RegexOptions.IgnoreCase)) return "Remote";
        if (Regex.IsMatch(text, @"\b(on-?site|in-?office|现场办公|坐班)\b", RegexOptions.IgnoreCase)) return "Onsite";
        return null;
    }

    /// <summary>
    /// 岗位性质:Full-time / Part-time / Permanent / Contract / Temporary / Freelance / Internship。
    /// 优先级:标签行("Job Type: Contract") &gt; 独立短行("contract") &gt; 正文关键词。
    /// 保守策略:"permanent" 只认标签/独立行(避开 "permanent resident"),
    /// "contract" 只认标签/独立行(避开 "contract negotiation" 类正文)。
    /// full-time/part-time 在正文里出现基本就是岗位性质,可放行。
    /// </summary>
    private static string? ExtractJobType(IList<string> lines, string text)
    {
        // 1) 标签行
        var labeled = Labeled(lines, "Job Type", "Employment Type", "JobType", "岗位性质", "雇佣类型", "工作性质");
        var norm = NormalizeJobType(labeled);
        if (norm is not null) return norm;

        // 2) 独立短行:整行就是岗位性质(如 Randstad 页的 "contract")
        foreach (var raw in lines)
        {
            var t = raw.Trim().TrimEnd('.', '·', '•');
            if (t.Length is 0 or > 20) continue;
            norm = NormalizeJobType(t);
            if (norm is not null) return norm;
        }

        // 3) 正文关键词:只放行误伤率极低的 full-time / part-time
        if (Regex.IsMatch(text, @"\bfull[\s\-]?time\b", RegexOptions.IgnoreCase)) return "Full-time";
        if (Regex.IsMatch(text, @"\bpart[\s\-]?time\b", RegexOptions.IgnoreCase)) return "Part-time";
        return null;
    }

    private static string? NormalizeJobType(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = v.Trim().TrimEnd('.', '·', '•', ',', ';').ToLowerInvariant();
        // 标签行可能带后缀("Contract - Technologies"),只取第一段
        t = Regex.Split(t, @"[\s|·•\-–—/]+")[0];
        return t switch
        {
            "full-time" or "fulltime" or "full" => "Full-time",
            "part-time" or "parttime" or "part" => "Part-time",
            "permanent" or "perm" => "Permanent",
            "contract" or "contractor" => "Contract",
            "temporary" or "temp" => "Temporary",
            "freelance" or "freelancer" => "Freelance",
            "internship" or "intern" => "Internship",
            "casual" => "Casual",
            "seasonal" => "Seasonal",
            _ => null,
        };
    }

    /// <summary>
    /// 工作身份要求:从 JD 正文里抓取国籍/PR/工签/担保相关的要求,归一为短文本。
    /// 检查顺序按\"限制强度\"从强到弱 —— 公民/PR 要求优先于笼统的\"合法工作身份\"。
    /// 注意避开误伤:\"PR team\"(公关团队)里的 PR 不是 permanent resident。
    /// </summary>
    private static string? ExtractWorkAuthorization(string text)
    {
        // 1) 公民或 PR(组合先判,避免被单项提前命中)
        if (Regex.IsMatch(text, @"\bcitizens?(ship)?\b.{0,40}\bpermanent\s+residen|\bpermanent\s+residen.{0,40}\bcitizens?(ship)?\b|\bPR\s+or\s+citizen\b|\bcitizen\s+or\s+PR\b", Opts))
            return "Citizen or PR required";
        // 2) 加拿大公民
        if (Regex.IsMatch(text, @"\bcanadian\s+citizens?(ship)?\b", Opts))
            return "Canadian citizen required";
        // 3) PR(避开 \"PR team\" 这类公关语境 —— 要求 PR 附近有身份语境词)
        if (Regex.IsMatch(text, @"\bpermanent\s+residen", Opts)
            || Regex.IsMatch(text, @"\b(canadian\s+)?PR\s+(holder|status|card|required)\b", Opts))
            return "PR required";
        // 4) 开放工签可接受
        if (Regex.IsMatch(text, @"\bopen\s+work\s+permit\b", Opts))
            return "Open work permit accepted";
        // 5) 有效工签
        if (Regex.IsMatch(text, @"\bvalid\s+work\s+permit\b", Opts))
            return "Valid work permit required";
        // 6) 必须在加拿大全法工作身份
        if (Regex.IsMatch(text, @"legally\s+(authorized|entitled)\s+to\s+work\s+in\s+canada|authorized\s+to\s+work\s+in\s+canada|eligible\s+to\s+work\s+in\s+canada", Opts))
            return "Must be legally authorized to work in Canada";
        // 7) 泛泛的工作许可要求
        if (Regex.IsMatch(text, @"\bwork\s+authorization\b.{0,30}\brequir\w*\b|\brequir\w*\b.{0,30}\bwork\s+authorization\b", Opts))
            return "Work authorization required";
        // 8) 不担保(明确拒绝)
        if (Regex.IsMatch(text, @"\bno\s+sponsorship\b|will\s+not\s+sponsor|\bunable\s+to\s+sponsor\b|cannot\s+sponsor|not\s+eligible\s+for\s+sponsorship", Opts))
            return "No sponsorship available";
        // 9) 提供担保
        if (Regex.IsMatch(text, @"\bsponsorship\s+(available|offered|provided)\b", Opts))
            return "Sponsorship available";
        // 10) 安全许可(政府/涉密岗位常见的身份门槛)
        if (Regex.IsMatch(text, @"\bsecurity\s+clearance\b", Opts))
            return "Security clearance required";
        // 11) 明确写了无限制
        if (Regex.IsMatch(text, @"\bno\s+(work\s+)?authorization\s+(requirements?|restrictions?)\b|open\s+to\s+all\s+(candidates|applicants)", Opts))
            return "No restriction";
        return null;
    }

    private static string? GuessSalary(string text)
    {
        var m = SalaryRange.Match(text);
        if (m.Success)
        {
            var lo = int.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture);
            var hi = int.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture);
            if (hi < lo) (lo, hi) = (hi, lo);
            // 数字小于 1000 视为 k(年薪 12 万写成 "120-160")
            var loTxt = lo < 1000 ? $"{lo}k" : $"{lo / 1000}k";
            var hiTxt = hi < 1000 ? $"{hi}k" : $"{hi / 1000}k";
            // 币种:两端可能各写一次($140,000 - $180,000),任一端认出来就算数。
            // 都没认出来就别硬塞 "$" —— 人民币薪资标成美元比留空更糟。
            var cur = NormalizeCurrency(m.Groups["cur"].Value)
                   ?? NormalizeCurrency(m.Groups["cur2"].Value)
                   ?? string.Empty;
            // 频率后缀:年薪默认不标,/hr·/mo·/wk 要保留才诚实
            var rate = NormalizeRate(m.Groups["rate1"].Value)
                    ?? NormalizeRate(m.Groups["rate2"].Value)
                    ?? string.Empty;
            return cur.Length == 0 ? $"{loTxt} - {hiTxt}{rate}" : $"{cur}{loTxt} - {cur}{hiTxt}{rate}";
        }

        var h = SalaryHourly.Match(text);
        if (h.Success)
        {
            var cur = NormalizeCurrency(h.Groups["cur"].Value) ?? string.Empty;
            return $"{cur}{h.Groups["lo"].Value}/hr";
        }

        var s = SalarySingle.Match(text);
        if (s.Success)
        {
            var lo = int.Parse(s.Groups["lo"].Value, CultureInfo.InvariantCulture);
            var cur = NormalizeCurrency(s.Groups["cur"].Value) ?? string.Empty;
            var rate = NormalizeRate(s.Groups["rate"].Value) ?? string.Empty;
            return $"{cur}{lo}k{rate}+";
        }

        return null;
    }

    /// <summary>频率后缀归一:年薪(/yr·per year·annum)是默认,不加后缀;
    /// 时薪/月薪/周薪必须保留后缀,否则数字会误导。</summary>
    private static string? NormalizeRate(string raw)
    {
        var r = raw.Trim().TrimStart('/').Trim();
        if (r.StartsWith("per ", StringComparison.OrdinalIgnoreCase)) r = r[4..].Trim();
        return r.ToLowerInvariant() switch
        {
            "hour" or "hr" => "/hr",
            "month" or "mo" => "/mo",
            "week" or "wk" => "/wk",
            _ => null, // year/yr/annum → 年薪默认
        };
    }

    /// <summary>币种归一。认不出来返回 null —— 让调用方决定"不标币种",而不是默认美元。</summary>
    private static string? NormalizeCurrency(string raw)
        => raw switch
        {
            "$" or "USD" or "US$" => "$",
            "C$" or "CAD" => "C$",
            "€" or "EUR" => "€",
            "£" or "GBP" => "£",
            _ => null
        };

    private static DateOnly? GuessDeadline(string text)
    {
        var m = Regex.Match(text,
            @"(?:apply by|deadlines?|closing dates?|applications? closing|(?:posting |job )?expir(?:y|ation)(?: dates?)?|valid (?:through|until)|截止|申请截止|投递截止|clos(?:e|es|ing))\s*[:：]?\s*([0-9]{4}[-/][0-9]{1,2}[-/][0-9]{1,2}|[A-Z][a-z]{2,8}\s+[0-9]{1,2},?\s+[0-9]{4}|[0-9]{1,2}[/-][0-9]{1,2}[/-][0-9]{2,4})",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var v = m.Groups[1].Value;
        // 显式多格式:TryParse 默认按 MM/dd 走,"15/12/2026"(dd/MM) 会被误判非法。
        // day>12 的只能是 dd/MM,先试 dd/MM 再试 MM/dd,避免 "01/02/2026" 这类
        // 歧义日期被固定解读 —— 歧义时优先北美常见 MM/dd。
        var formats = new[]
        {
            "yyyy-M-d", "yyyy/M/d",
            "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy",
            "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy",
            "MMMM d, yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMM d yyyy",
        };
        foreach (var f in formats)
            if (DateOnly.TryParseExact(v, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        return null;
    }

    private static string? MatchKeywords(string text)
    {
        var hits = new List<string>();
        foreach (var kw in TechKeywords)
        {
            // 短词(如 "Go"、"C#")必须按词边界匹配,否则 "Google" 里会命中 "Go"
            var pattern = kw.Length <= 3
                ? $@"(?<![\w#]){Regex.Escape(kw)}(?![\w#])"
                : Regex.Escape(kw);
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) hits.Add(kw);
        }
        return hits.Count == 0 ? null : string.Join(", ", hits.Take(14));
    }

    private static string? GuessPriority(string text)
    {
        if (Regex.IsMatch(text, @"(urgent|immediately|asap|急招|急聘|尽快到岗)", RegexOptions.IgnoreCase))
            return "High";
        return null;
    }

    private static string? Summary(string text)
    {
        // 摘要应从真正的职位描述开始:优先找正文段落标题。
        var m = Regex.Match(text,
            @"(?:About the job|Job Description|Job summary|About the role|The role|Job overview|Position overview|Role overview|职位描述|岗位职责)\s*\n(.+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var source = m.Success ? m.Groups[1].Value : text;
        // 抓取页面的首行常常是页面标题("Job opening - X | Randstad Canada"),
        // 不是摘要内容 —— 剥掉它。
        var firstNl = source.IndexOf('\n');
        if (firstNl > 0 && Regex.IsMatch(source[..firstNl].Trim(),
                @"^jobs?\s*(opening|posting|opportunity|ad|listing)?\b", RegexOptions.IgnoreCase))
            source = source[(firstNl + 1)..];
        // 再跳过头部元数据碎片("posted June 8th, 2026"、"contract"、"job details summary" 等):
        // 短行(<8 词)不是正文段落;词数够但命中元数据模式的也是页面碎片。
        var srcLines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var proseStart = 0;
        while (proseStart < srcLines.Length && IsSummarySkipLine(srcLines[proseStart]))
            proseStart++;
        if (proseStart < srcLines.Length)
            source = string.Join("\n", srcLines.Skip(proseStart));
        // 页脚横幅可能出现在正文之后(如 Randstad 的 job-alerts banner 在相关职位列表前):
        // 从第一个页脚行起截断,后面的站点 chrome 不要。
        var bodyLines = source.Split('\n');
        var cutAt = Array.FindIndex(bodyLines, IsFooterLine);
        if (cutAt >= 0)
            source = string.Join("\n", bodyLines.Take(cutAt));
        var flat = Regex.Replace(source, @"\s+", " ").Trim();
        if (flat.Length == 0) return null;
        // 2026-10-19:用户要求完整内容,不截断。
        // 2026-10-19:摘要在 Qualifications / Additional Information / Benefits 等小节标题处停止,
        // 避免与独立字段重复。
        var stopM = Regex.Match(flat,
            @"\b(qualifications|additional information|benefits|what we offer|what you'll bring|preferred qualifications|minimum qualifications)\b",
            RegexOptions.IgnoreCase);
        if (stopM.Success && stopM.Index > 100)
            flat = flat[..stopM.Index].Trim();
        return flat;
    }

    /// <summary>
    /// 摘要前导行过滤:短碎片行和元数据行(如 "posted June 8th, 2026"、
    /// "Contract sector Technologies reference number C1O1V15PROEN job details")不是正文。
    /// 只用于跳过"前导"行 —— 正文中间的短行不受影响。
    /// 2026-10-19:招聘站页脚横幅(职位提醒订阅/newsletter/"similar jobs" 推广)常混进正文流,
    /// 如 Randstad 的 "We will keep you updated when we have similar job postings." ——
    /// 这些行再长也不是 JD,一并跳过。
    /// </summary>
    private static bool IsSummarySkipLine(string line)
    {
        if (IsFooterLine(line)) return true;
        // 票 29 (REQ-09):元数据行不是正文 —— 无标准标题形态下摘要曾以
        // "Toronto, ON - Hybrid - Full-time" / "Salary: ..." 开头 (形态 B)
        if (IsMetadataLine(line)) return true;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 2026-10-19:4 词以下才是碎片;4-7 词的短句可能是正文(如 "We are hiring a Senior .NET Developer.")
        if (words.Length < 4) return true;
        return Regex.IsMatch(line,
            @"posted\s|reference number|job details?|apply now|show more",
            RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 元数据行判定 (只用于摘要前导跳行,正文中间不受影响):
    ///  1. 带标签的字段行:"Salary: ..." / "Location: ..." / "Apply by: ..." 等;
    ///  2. 复合元数据行:由 " - " / "·" / "|" 分隔的 2+ 段,每段都是地点/工作模式/
    ///     岗位性质/薪资之一 (如 "Toronto, ON - Hybrid - Full-time")。
    /// 正文句不会整行由元数据段拼成,故不会误伤。
    /// </summary>
    private static bool IsMetadataLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t.Length > 120) return false;
        if (Regex.IsMatch(t,
                @"^(salary|pay|compensation|location|job\s*type|employment\s*type|work\s*mode|apply\s*by|deadline|closing\s*date)\s*[:：]",
                RegexOptions.IgnoreCase))
            return true;
        var segs = Regex.Split(t, @"\s*·\s*|\s*\|\s*|\s+-\s+");
        if (segs.Length < 2) return false;
        foreach (var s in segs)
        {
            var seg = s.Trim().Trim('(', ')');
            if (seg.Length == 0) return false;
            if (LocationCityState.IsMatch(seg)) continue;
            if (Regex.IsMatch(seg, @"^(hybrid|remote|onsite|on-site|in-office|work from home)$", RegexOptions.IgnoreCase)) continue;
            if (NormalizeJobType(seg) is not null) continue;
            if (SalaryRange.IsMatch(seg) || SalarySingle.IsMatch(seg) || SalaryHourly.IsMatch(seg)) continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// 页脚/推广横幅行判定:职位提醒订阅、newsletter、"similar jobs" 推广、
    /// "keep you updated" 这类站点 chrome,绝不可能是 JD 正文。
    /// 用多词精确短语,避免误伤正文("subscribe" 单词本身不出场)。
    /// </summary>
    private static bool IsFooterLine(string line)
        => Regex.IsMatch(line,
            @"similar job postings|job alerts|personalised|personalized|keep you updated|" +
            @"let similar jobs come to you|subscribing to your|sign up for job|job newsletter",
            RegexOptions.IgnoreCase);

    // ---------- 段落级字段:公司介绍 / 任职要求 / 福利 ----------

    /// <summary>
    /// 已知的段落标题(归一化后比对)。命中即视为新段落的开始 ——
    /// 段落提取就靠它来定边界,所以只收录"几乎不可能是正文"的标题。
    /// </summary>
    private static readonly string[] KnownSectionHeaders =
    [
        "about the company", "about the job", "job description", "job summary",
        "about the role", "the role", "job overview", "position overview", "role overview",
        "requirements", "qualifications", "preferred qualifications", "minimum qualifications",
        "what you'll need", "what you bring", "must have", "nice to have", "key skills", "skills",
        "responsibilities", "what you'll do", "key responsibilities", "your responsibilities",
        "your day to day", "day to day",
        "benefits", "what we offer", "perks", "compensation and benefits", "why join us",
        "additional information",
        "about us", "who we are", "our company", "equal opportunity", "how to apply",
        "application process", "disclosure",
        "职位描述", "岗位职责", "工作内容", "任职要求", "任职资格", "岗位要求",
        "技能要求", "福利待遇", "薪资福利", "公司介绍", "关于我们",
    ];

    private static readonly string[] RequirementsHeaders =
    [
        "requirements", "qualifications", "preferred qualifications", "minimum qualifications",
        "what you'll need", "what you bring", "must have", "nice to have", "key skills",
        "任职要求", "任职资格", "岗位要求", "技能要求",
    ];

    private static readonly string[] BenefitsHeaders =
    [
        "benefits", "what we offer", "perks", "compensation and benefits", "why join us",
        "additional information", "advantages", "福利待遇", "薪资福利",
    ];

    /// <summary>
    /// 是否像一个段落标题:命中已知标题表,或短行+冒号结尾。
    /// 长度上限 60 挡住正文段落;冒号分支要求 ≤8 词,挡住 "Note: ..." 这类正文行。
    /// </summary>
    private static bool IsSectionHeader(string line)
    {
        var t = line.Trim().TrimEnd(':', '：');
        if (t.Length == 0 || t.Length > 60) return false;
        foreach (var h in KnownSectionHeaders)
            if (string.Equals(t, h, StringComparison.OrdinalIgnoreCase)) return true;
        var raw = line.TrimEnd();
        if (raw.EndsWith(':') || raw.EndsWith('：'))
        {
            var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is >= 1 and <= 8) return true;
        }
        return false;
    }

    /// <summary>
    /// 通用段落提取:找到标题行后,收集到下一个段落标题之前的所有非空行。
    /// 找不到标题返回 null;内容不足 10 字符也视为无(标题下直接就是下一标题的碎片页)。
    /// </summary>
    private static string? ExtractSection(IList<string> lines, string[] headers)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim().TrimEnd(':', '：');
            var hit = false;
            string? inline = null;
            foreach (var h in headers)
            {
                if (string.Equals(t, h, StringComparison.OrdinalIgnoreCase)) { hit = true; break; }
                // 票 29 (REQ-09):同行内联形态 "Benefits: <内容>" —— 标题与内容同行,
                // 旧逻辑只认整行等于标题,内联内容永远解不出 (形态 B benefits=null)。
                var im = Regex.Match(lines[i],
                    $@"^\s*{Regex.Escape(h)}\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase);
                if (im.Success) { hit = true; inline = im.Groups[1].Value.Trim(); break; }
            }
            if (!hit) continue;

            var sb = new List<string>();
            if (!string.IsNullOrWhiteSpace(inline)) sb.Add(inline);
            for (var j = i + 1; j < lines.Count; j++)
            {
                var l = lines[j].Trim();
                if (IsSectionHeader(l)) break;
                // 带字段标签的行 (Apply by:/Salary:/Location:...) 是元数据,不是段落内容 ——
                // 段落收集到此为止 (内联形态下段后紧跟的截止日期行曾被吞进 benefits)
                if (IsLabeledFieldLine(l)) break;
                // 2026-10-19:福利/任职要求段遇到页脚噪音时停止(LinkedIn 粘贴文本尾部)
                if (IsSectionFooterNoise(l)) break;
                if (l.Length > 0) sb.Add(l);
            }
            var v = string.Join("\n", sb).Trim();
            if (v.Length >= 10) return v.Length <= 8000 ? v : v[..8000] + "…";
            return null;
        }
        return null;
    }

    /// <summary>以已知字段标签开头的行 (元数据行),段落内容收集到此为止。</summary>
    private static bool IsLabeledFieldLine(string line)
        => Regex.IsMatch(line.Trim(),
            @"^(apply\s*by|deadline|closing\s*date|salary|pay|compensation|location|company|employer|organization|role|title|position|job\s*title|job\s*type|employment\s*type|work\s*authorization|work\s*authorisation|eligibility|citizenship|公司|地点|职位|岗位|工作地点|办公地点|申请截止|截止)\s*[:：]",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// 段落尾部噪音:LInkedIn 粘贴文本的页脚(GDPR、SmartRecruiters、Premium 广告等),遇到即停止收集。
    /// </summary>
    private static bool IsSectionFooterNoise(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        var lower = t.ToLowerInvariant();
        return lower.StartsWith("recruiting-platform powered by")
            || lower.StartsWith("disclosure requirements pertaining")
            || lower.StartsWith("set alert for similar jobs")
            || lower.Contains("and millions of other members use premium")
            || lower.StartsWith("try premium for")
            || lower.StartsWith("job search faster with premium");
    }

    /// <summary>
    /// 公司介绍:取 "About the company" 标题之后、下一个段落标题之前的内容。
    /// LinkedIn 形态下标题和正文之间夹着公司名、follower 数、行业行等元数据 ——
    /// 先跳过这些(公司名用已解出的 company 比对,follower/员工数/行业分类行按模式跳过),
    /// 剩下的第一个"像正文"的段落才是介绍。
    /// </summary>
    private static string? ExtractCompanyDescription(IList<string> lines, string? company)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!Regex.IsMatch(lines[i].Trim(), @"^about\s+the\s+company$", RegexOptions.IgnoreCase))
                continue;

            var j = i + 1;
            // 跳过元数据行:公司名 / follower·员工数 / "Following" / 行业分类行 / "N on LinkedIn"
            while (j < lines.Count && IsCompanyMetaLine(lines[j], company)) j++;

            var sb = new List<string>();
            for (; j < lines.Count; j++)
            {
                var l = lines[j].Trim();
                if (IsSectionHeader(l)) break;
                if (l.Length > 0) sb.Add(l);
            }
            var v = string.Join("\n", sb).Trim();
            if (v.Length >= 20) return v.Length <= 8000 ? v : v[..8000] + "…";
            return null;
        }
        return null;
    }

    private static bool IsCompanyMetaLine(string line, string? company)
    {
        var t = line.Trim();
        if (t.Length == 0) return true;
        if (company is not null && string.Equals(t, company, StringComparison.OrdinalIgnoreCase))
            return true;
        // follower / 员工数("210,272 followers" / "10001+ employees")
        if (Regex.IsMatch(t, @"[\d,+.]+\s*(followers|employees)", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(t, @"^\d[\d,\s]*$", RegexOptions.Compiled)) return true;
        if (Regex.IsMatch(t, @"^(following|follow|•|\*)$", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(t, @"\bon\s+linkedin\b", RegexOptions.IgnoreCase)) return true;
        // 行业分类行:逗号分隔、无句号结尾的短行("Transportation, Logistics, Supply Chain and Storage")
        if (t.Contains(',') && !t.EndsWith('.') && t.Length < 100) return true;
        return false;
    }

    private static string Title(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keepLower = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "and", "of", "for", "the", "de" };
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var w = words[i];
            if (i > 0 && keepLower.Contains(w)) sb.Append(w.ToLowerInvariant());
            else if (w.Length > 0) sb.Append(char.ToUpperInvariant(w[0])).Append(w[1..]);
        }
        return sb.ToString();
    }
}

