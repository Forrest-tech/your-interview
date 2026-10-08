using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  Tracker 智能粘贴 —— 确定性解析规则(需求 6.2.2 / 6.2.3)
//
//  这个文件刻意只依赖 BCL,不引用任何项目内类型。两个理由:
//    1. 规则是纯函数:一段文本进、一份草稿出。它不该知道数据库、HTTP 或 AI 网关。
//    2. 可独立验证:零依赖意味着能单独拉出来跑样例,不必起整套服务。
//
//  为什么坚持"先规则、后 AI":
//    薪资、地点、日期这类字段,正则比模型稳定得多(模型会自己改写数字);
//    而摘要、关键词提炼这类"理解活"才轮到模型。分工见 SmartAddHandlers.cs。
// ============================================================================



/// <summary>解析状态。Ok=字段基本齐;Partial=只解出一部分;Failed=完全没解出来(前端回退手动)。</summary>
public static class ParseOutcome
{
    public const string Ok = "Ok";
    public const string Partial = "Partial";
    public const string Failed = "Failed";
}

/// <summary>粘贴内容的类型。</summary>
public static class PasteKind
{
    public const string Url = "Url";
    public const string Text = "Text";
    public const string Image = "Image";
}

public sealed record ParsedJdDto(
    string SourceKind, string Status, string Message,
    string? Company, string? Role, string? Location, string? Salary, string? WorkMode,
    string? JdSummary, string? MatchKeywords, string? Priority, string? Link,
    string? JdText, DateOnly? Deadline, string? DetectedSource,
    Dictionary<string, int> Confidence, bool UsedAi, bool NeedsManualReview);

// ============================ 邀请邮件解析(需求 6.2.3) ============================

public sealed record ParsedInviteDto(
    string Status, string Message,
    string? Company, string? Role, int? RoundNo, string? Stage,
    DateTimeOffset? ScheduledAt, string? Location, string? Format, string? Interviewer,
    string? RawLink, Dictionary<string, int> Confidence, bool UsedAi, bool NeedsManualReview);

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
    private static readonly Regex SalaryRange = new(
        @"(?<cur>[$€£]|USD|CAD|US\$|C\$)?\s*(?<lo>\d{2,3})(?:[kK]|,?000)?\s*(?:-|–|—|to|~)\s*(?:(?:USD|CAD|AUD|GBP|EUR)\s*)?(?<cur2>[$€£]|USD|CAD|US\$|C\$)?\s*(?<hi>\d{2,3})(?:[kK]|,?000)?", Opts);
    private static readonly Regex SalarySingle = new(
        @"(?<cur>[$€£]|USD|CAD|US\$|C\$)\s*(?<lo>\d{2,3})\s*[kK]\b", Opts);
    private static readonly Regex LocationCityState = new(
        @"\b(?<city>[A-Z][a-zA-Z]+(?:\s[A-Z][a-zA-Z]+)?)\s*,\s*(?<region>[A-Z]{2}|[A-Z][a-zA-Z]+)\b", RegexOptions.Compiled);
    private static readonly Regex UrlAnywhere = new(@"https?://\S+", Opts);
    private static readonly Regex EmailAnywhere = new(@"[\w.+-]+@[\w-]+\.[\w.]+", Opts);

    /// <summary>识别粘贴内容类型。图片只识别不解读 —— 读不出来就说读不出来。</summary>
    public static string DetectKind(string raw)
    {
        var s = raw.Trim();
        if (ImageUrl.IsMatch(s)) return PasteKind.Image;
        if (UrlToken.IsMatch(s)) return PasteKind.Url;
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

    public static ParsedJdDto Parse(string raw, string kind)
    {
        var conf = new Dictionary<string, int>();
        var text = raw.Trim();
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

        company ??= Labeled(lines, "公司", "Company", "Employer", "Organization")
                 ?? AfterAt(lines);
        if (company is not null) conf.TryAdd("company", 80);

        role ??= Labeled(lines, "职位", "岗位", "Role", "Title", "Position", "Job Title")
              ?? GuessRole(lines);
        if (role is not null) conf.TryAdd("role", 75);

        // 公司名兜底:role 下一行常常是无标签的公司名
        if (company is null && role is not null)
        {
            company = CompanyAfterRole(lines, role);
            if (company is not null) conf.TryAdd("company", 60);
        }

        var location = Labeled(lines, "地点", "Location", "办公地点", "工作地点");
        if (location is null)
        {
            var lm = LocationCityState.Match(text);
            if (lm.Success) location = $"{lm.Groups["city"].Value}, {lm.Groups["region"].Value}";
        }
        if (location is not null) conf["location"] = 80;

        var workMode = GuessWorkMode(text);
        if (workMode is not null) conf["workMode"] = 85;

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

        var filled = CountFilled(company, role, location, salary, workMode, jdSummary);

        return new ParsedJdDto(
            kind,
            filled >= 4 ? ParseOutcome.Ok : filled >= 1 ? ParseOutcome.Partial : ParseOutcome.Failed,
            string.Empty,
            company, role, location, salary, workMode, jdSummary, keywords, priority,
            link, jdText, deadline, detected ?? (isUrl ? "未知站点" : null),
            conf, false, false);
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
        "jobvite", "bamboohr", "recruitee"
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
        var cand = lines[idx + 1].Trim();
        // 排除:太长、含岗位关键词、是地点/薪资/标签行
        if (cand.Length is < 2 or > 60) return null;
        if (RoleWords.IsMatch(cand)) return null;
        if (cand.Contains(':')) return null;
        if (SalaryRange.IsMatch(cand) || SalarySingle.IsMatch(cand)) return null;
        if (LocationCityState.IsMatch(cand)) return null;
        // 排除纯描述句(太长或含动词开头)
        if (cand.Split(' ').Length > 6) return null;
        return cand;
    }

    private static string? GuessRole(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Length > 120) continue;
            if (RoleWords.IsMatch(line))
            {
                // 剥掉常见的地点/薪资尾巴,只留岗位主体
                var v = Regex.Replace(line, @"[（(].*?[)）]", string.Empty).Trim();
                v = Regex.Replace(v, @"[|\-–—]\s*(remote|hybrid|onsite|on-site).*$", string.Empty,
                    RegexOptions.IgnoreCase).Trim();
                if (v.Length is >= 3 and <= 80) return v;
            }
        }
        return null;
    }

    private static string? GuessWorkMode(string text)
    {
        if (Regex.IsMatch(text, @"\b(hybrid)\b", RegexOptions.IgnoreCase)) return "Hybrid";
        if (Regex.IsMatch(text, @"\b(remote|fully remote|wfh|work from home|远程|全远程)\b", RegexOptions.IgnoreCase)) return "Remote";
        if (Regex.IsMatch(text, @"\b(on-?site|in-?office|现场办公|坐班)\b", RegexOptions.IgnoreCase)) return "Onsite";
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
            return cur.Length == 0 ? $"{loTxt} - {hiTxt}" : $"{cur}{loTxt} - {cur}{hiTxt}";
        }

        var s = SalarySingle.Match(text);
        if (s.Success)
        {
            var lo = int.Parse(s.Groups["lo"].Value, CultureInfo.InvariantCulture);
            var cur = NormalizeCurrency(s.Groups["cur"].Value) ?? string.Empty;
            return $"{cur}{lo}k+";
        }

        return null;
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
            @"(?:apply by|deadline|截止|申请截止|投递截止|closes?)\s*[:：]?\s*([0-9]{4}[-/][0-9]{1,2}[-/][0-9]{1,2}|[A-Z][a-z]{2,8}\s+[0-9]{1,2},?\s+[0-9]{4}|[0-9]{1,2}[/-][0-9]{1,2}[/-][0-9]{2,4})",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var v = m.Groups[1].Value;
        if (DateOnly.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d1)) return d1;
        if (DateOnly.TryParse(v, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d2)) return d2;
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
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length == 0) return null;
        return flat.Length <= 220 ? flat : flat[..220] + "…";
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

// ============================================================================
//  AI 精修:JD

// ============================================================================
//  确定性解析器:邀请邮件
// ============================================================================

public static class InvitePasteParser
{
    public static ParsedInviteDto Parse(string raw)
    {
        var text = raw.Trim();
        var conf = new Dictionary<string, int>();

        var roundNo = GuessRound(text);
        if (roundNo is not null) conf["roundNo"] = 70;

        var stage = GuessStage(text);
        if (stage is not null) conf["stage"] = 70;

        var format = GuessFormat(text);
        if (format is not null) conf["format"] = 75;

        var at = GuessTime(text);
        if (at is not null) conf["scheduledAt"] = 75;

        var interviewer = Labeled(text, "面试官", "Interviewer", "Interviewer(s)", "面试人");
        if (interviewer is not null) conf["interviewer"] = 80;

        var location = Labeled(text, "地点", "Location", "地址", "面试地点")
                    ?? (format == "Video" ? null : GuessAddress(text));
        if (location is not null) conf["location"] = 60;

        var link = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Value.TrimEnd(')', '】', '」', ',', '。') : null;

        var company = Labeled(text, "公司", "Company")
                   ?? CompanyFromEmail(text)
                   ?? CompanyAfterAt(text);
        var role = Labeled(text, "职位", "岗位", "Role", "Position");

        return new ParsedInviteDto(
            ParseOutcome.Partial, string.Empty,
            company, role, roundNo, stage, at, location, format, interviewer, link,
            conf, false, false);
    }

    public static int CountFilled(ParsedInviteDto d)
    {
        var n = 0;
        if (d.RoundNo is not null) n++;
        if (!string.IsNullOrWhiteSpace(d.Stage)) n++;
        if (d.ScheduledAt is not null) n++;
        if (!string.IsNullOrWhiteSpace(d.Format)) n++;
        if (!string.IsNullOrWhiteSpace(d.Interviewer)) n++;
        if (!string.IsNullOrWhiteSpace(d.Location)) n++;
        return n;
    }

    private static string? Labeled(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var m = Regex.Match(text, $@"{Regex.Escape(label)}\s*[:：]\s*(.+)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = m.Groups[1].Value.Trim();
                if (v.Length is > 0 and <= 120) return v;
            }
        }
        return null;
    }

    private static int? GuessRound(string text)
    {
        var cn = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["一"] = 1, ["二"] = 2, ["三"] = 3, ["四"] = 4, ["五"] = 5,
            ["两"] = 2, ["首"] = 1, ["终"] = 99
        };

        var m = Regex.Match(text, @"第\s*([0-9一二三四五两首]+)\s*(?:轮|面)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var v = m.Groups[1].Value;
            if (int.TryParse(v, out var n)) return n;
            if (cn.TryGetValue(v, out var c)) return c == 99 ? null : c;
        }

        m = Regex.Match(text, @"round\s*#?\s*([1-9])", RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

        // "终面 / final round" 不给具体轮次 —— 猜错轮次比留空更糟
        return null;
    }

    private static string? GuessStage(string text)
    {
        if (Regex.IsMatch(text, @"(system\s*design|系统设计|架构设计)", RegexOptions.IgnoreCase)) return "SystemDesign";
        if (Regex.IsMatch(text, @"(coding|algorithm|算法|白板|技术面|technical|机考|oa\b)", RegexOptions.IgnoreCase)) return "Technical";
        if (Regex.IsMatch(text, @"(behavioral|behavioural|行为面|bq|hr面|文化|价值观)", RegexOptions.IgnoreCase)) return "Behavioral";
        if (Regex.IsMatch(text, @"(final|终面|onsite|on-site|现场面|总监面|hiring manager)", RegexOptions.IgnoreCase)) return "Final";
        if (Regex.IsMatch(text, @"(phone\s*screen|screening|电面|初筛|电话面|hr\s*screen)", RegexOptions.IgnoreCase)) return "Screen";
        return null;
    }

    private static string? GuessFormat(string text)
    {
        if (Regex.IsMatch(text, @"(zoom|google\s*meet|microsoft\s*teams|teams\b|webex|视频|线上|video)", RegexOptions.IgnoreCase)) return "Video";
        if (Regex.IsMatch(text, @"(phone|电话|call\s*you)", RegexOptions.IgnoreCase)) return "Phone";
        if (Regex.IsMatch(text, @"(on-?site|现场|到园区|到公司|in\s*person)", RegexOptions.IgnoreCase)) return "Onsite";
        return null;
    }

    private static string? GuessAddress(string text)
    {
        var m = Regex.Match(text, @"\b(\d{1,5}\s+[A-Z][A-Za-z]+\s+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr)\b[^,\n]{0,40})");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static DateTimeOffset? GuessTime(string text)
    {
        // 1) 2026-10-05 14:30 / 2026/10/05 14:30
        // ⚠️ 日/月的可选项必须"两位在前":0?[1-9] 会把 "18" 截成 "1",
        //    而后面的时间是可选组,整条正则照样成功 → 得到 10/1 凌晨(踩过)。
        var m = Regex.Match(text,
            @"(?<d>(?:20\d{2})[-/.](?:1[0-2]|0?[1-9])[-/.](?:[12]\d|3[01]|0?[1-9]))\s*(?:at\s*)?(?<t>(?:[01]?\d|2[0-3]):[0-5]\d(?:\s*(?:AM|PM|am|pm))?)?");
        if (m.Success)
        {
            var datePart = m.Groups["d"].Value;
            var timePart = m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value.Trim() : "09:00";
            if (DateTimeOffset.TryParse($"{datePart} {timePart}", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dto))
                return dto;
        }

        // 2) Oct 5, 2026 at 2:30 PM (EST)
        m = Regex.Match(text,
            @"(?<mon>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<y>20\d{2})\s*(?:at\s*)?(?<t>\d{1,2}(?::[0-5]\d)?\s*(?:AM|PM|am|pm)?)?");
        if (m.Success)
        {
            var s = $"{m.Groups["mon"].Value} {m.Groups["day"].Value}, {m.Groups["y"].Value} "
                    + (m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value.Trim() : "9:00 AM");
            if (DateTimeOffset.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out var dto2))
                return dto2;
        }

        // 3) 10/05/2026 2:00pm (同样两位优先)
        m = Regex.Match(text,
            @"(?<d>(?:1[0-2]|0?[1-9])/(?:[12]\d|3[01]|0?[1-9])/(?:20\d{2}))\s*(?:at\s*)?(?<t>\d{1,2}(?::[0-5]\d)?\s*(?:AM|PM|am|pm)?)?");
        if (m.Success)
        {
            var s = m.Groups["d"].Value + " "
                    + (m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value.Trim() : "9:00 AM");
            if (DateTimeOffset.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AssumeLocal, out var dto3))
                return dto3;
        }

        // 4) 中文:10月5日 14:30
        m = Regex.Match(text, @"(?<mo>\d{1,2})月(?<d>\d{1,2})日\s*(?<t>(?:[01]?\d|2[0-3]):[0-5]\d)?");
        if (m.Success)
        {
            var year = DateTimeOffset.Now.Year;
            var mo = int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture);
            var dd = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            var time = m.Groups["t"].Success && m.Groups["t"].Length > 0
                ? m.Groups["t"].Value : "09:00";
            var (hh, mm) = SplitTime(time);
            try
            {
                var local = new DateTime(year, mo, dd, hh, mm, 0, DateTimeKind.Local);
                // 跨年兜底:解析出比今天早一个月的日期,多半是明年年初的面试
                if (local < DateTime.Now.AddMonths(-1)) local = local.AddYears(1);
                return new DateTimeOffset(local);
            }
            catch (ArgumentOutOfRangeException) { /* 日期非法就当没解析到 */ }
        }

        return null;
    }

    private static (int H, int M) SplitTime(string t)
    {
        var parts = t.Split(':');
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return (h, m);
    }

    private static string? CompanyFromEmail(string text)
    {
        var m = Regex.Match(text, @"@(?<d>[a-z0-9\-]+)\.(?:com|io|ai|co|ca|net|org)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var d = m.Groups["d"].Value;
            if (d.Length > 1 && !FreeMail.Contains(d.ToLowerInvariant()))
                return char.ToUpperInvariant(d[0]) + d[1..];
        }
        return null;
    }

    // "You have an interview at Shopify..." —— 没有公司标签也没有邮箱域名时的兜底。
    // 只接受大写开头的词,小写词(on/the/with)自然截断;会议软件名一律跳过。
    private static string? CompanyAfterAt(string text)
    {
        foreach (var m in Regex.Matches(text,
                     @"\bat\s+((?:[A-Z][A-Za-z0-9&.\-']+\s+){0,2}[A-Z][A-Za-z0-9&.\-']+)",
                     RegexOptions.Compiled).Cast<Match>())
        {
            var v = m.Groups[1].Value.Trim();
            if (v.Length is < 2 or > 60) continue;
            if (VideoWords.IsMatch(v)) continue;
            return v;
        }
        return null;
    }

    private static readonly Regex VideoWords =
        new(@"(?i)meet|zoom|teams|hangouts|webex|call|link", RegexOptions.Compiled);

    private static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "outlook", "hotmail", "yahoo", "icloud", "qq", "163", "126", "foxmail", "protonmail"
    };
}

// ============================================================================
//  AI 精修:邀请邮件
