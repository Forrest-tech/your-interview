using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 28(ARC-08a):SmartParseRules.JdPasteParser 特征测试(characterization)。
/// 目的:在票 29 动解析逻辑之前,先把**现状行为**钉死为回归网。
/// 期望值来源:审计 AUDIT REQ-09/FUN-09 的真实 JD 形态 + JD 原文逐字核对(独立真源),
/// 不是把代码输出复算一遍——每个形态的期望都先按 JD 原文人工判定,再与实测对账;
/// 与原文判定不符的现状即 known-gap,用例名带 KnownGap 并引用票 29。
/// boundary 类别按总纲第七节标注在用例名/注释中:
/// 空/null、空集合、超长(上限±1)、特殊字符/Unicode、0 与负数、日期临界。
/// 文件类(0 字节/损坏/超限)不适用:本解析器是纯文本函数、无文件输入——见票 28 报告。
/// </summary>
public sealed class SmartParseRulesJdTests
{
    // ================= 夹具:AUDIT 真实形态 =================

    // 形态 A:标准标题行形态(独立标题行 Requirements/Benefits/About the company)
    private const string JdStandard = """
        Senior .NET Developer
        Globex Corporation
        Toronto, ON (Hybrid)
        $140,000 - $180,000
        Full-time

        About the job
        We are hiring a Senior .NET Developer to build payment platforms used by merchants across North America every day. You will design and ship microservices with C# and Azure.

        Requirements
        - 5+ years of C# and .NET experience building production systems
        - Strong experience with SQL Server and Entity Framework
        - Angular experience is a strong plus for this role

        Benefits
        - Health and dental insurance from day one
        - RRSP matching up to five percent
        - Four weeks of paid vacation every year

        About the company
        Globex Corporation is a global payments company founded in 2005 and headquartered in Toronto Canada.
        Apply by: November 30, 2026
        """;

    // 形态 B:内联 Benefits 形态(AUDIT REQ-09 实测形态)
    private const string JdInlineBenefits = """
        Senior Backend Developer
        Toronto, ON - Hybrid - Full-time
        Salary: $120,000 - $150,000 per year
        We are looking for a Senior Backend Developer to join our platform team in Toronto building APIs with C# .NET and PostgreSQL for thousands of customers.
        Benefits: Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.
        Apply by: December 15, 2026
        """;

    // 形态 C:无标准标题形态——职位行后紧跟 "About the Job" 段落标题(AUDIT REQ-09 实测形态)
    private const string JdNoStandardHeader = """
        Senior Software Engineer
        About the Job
        We are looking for a Senior Software Engineer to join our growing fintech team in Waterloo. You will build React and TypeScript applications backed by Node.js services on AWS, working closely with product and design partners across the company.
        """;

    private static ParsedJdDto ParseWithLine(string line) => JdPasteParser.Parse(
        $"Senior Developer\nAcme Corp\nToronto, ON\n{line}\nWe are hiring a Senior Developer to build great software products for our customers in Canada.",
        PasteKind.Text);

    // ================= DetectKind =================

    [Theory]
    [InlineData("https://www.linkedin.com/jobs/view/123", PasteKind.Url)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", PasteKind.Image)]
    [InlineData("https://example.com/pic.png", PasteKind.Image)]
    [InlineData("https://example.com/a/b.PNG?v=2", PasteKind.Image)] // 特殊字符:扩展名后带查询串
    [InlineData("<div><p>Hello</p></div>", PasteKind.Html)]
    [InlineData("a < b and c > d", PasteKind.Text)] // 只有尖括号、无块级标签 → 不是 HTML
    [InlineData("https://example.com/a b", PasteKind.Text)] // URL 中带空格 → 不是纯 URL
    [InlineData("Senior Developer wanted", PasteKind.Text)]
    public void DetectKind_ClassifiesPasteForms(string raw, string expected)
        => Assert.Equal(expected, JdPasteParser.DetectKind(raw));

    [Fact] // boundary: 空输入
    public void DetectKind_Empty_BoundaryEmpty_ReturnsText()
        => Assert.Equal(PasteKind.Text, JdPasteParser.DetectKind(""));

    [Fact] // boundary: null 输入——现状为抛 NullReferenceException(调用方须保证非 null)
    public void DetectKind_Null_BoundaryNull_ThrowsNullReference()
        => Assert.Throws<NullReferenceException>(() => JdPasteParser.DetectKind(null!));

    // ================= HostOf / IsAggregator =================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    public void HostOf_NoUrl_BoundaryNullEmpty_ReturnsNull(string? url) // boundary: null/空
    {
        Assert.Null(JdPasteParser.HostOf(url));
        Assert.False(JdPasteParser.IsAggregator(url));
    }

    [Fact]
    public void HostOf_LinkedIn_IsAggregator()
    {
        Assert.Equal("www.linkedin.com", JdPasteParser.HostOf("https://www.linkedin.com/jobs/view/1"));
        Assert.True(JdPasteParser.IsAggregator("https://www.linkedin.com/jobs/view/1"));
    }

    [Fact]
    public void HostOf_Greenhouse_IsNotAggregator()
    {
        Assert.Equal("boards.greenhouse.io", JdPasteParser.HostOf("https://boards.greenhouse.io/shopify/jobs/1"));
        Assert.False(JdPasteParser.IsAggregator("https://boards.greenhouse.io/shopify/jobs/1"));
    }

    // ================= HtmlToText =================

    [Fact] // 特殊字符:HTML 实体 & < > " ' 的实测解码形态 + 不可见区块整段丢弃
    public void HtmlToText_StripsInvisibleBlocks_KeepsListStructure_DecodesEntities()
    {
        var html = "<html><head><title>T</title><style>.a{color:red}</style><script>var x=1;</script></head>"
                 + "<body><h1>Role</h1><ul><li>One</li><li>Two &amp; Three</li></ul>"
                 + "<p>Para &lt;tag&gt; &quot;q&quot; &#39;s&#39; &nbsp;end</p><!-- comment --></body></html>";
        Assert.Equal("Role\n\n• One\n• Two & Three\n\nPara <tag> \"q\" 's' end", JdPasteParser.HtmlToText(html));
    }

    [Fact]
    public void HtmlToText_BrBecomesNewline()
        => Assert.Equal("Line one\nLine two & <three>",
            JdPasteParser.HtmlToText("<p>Line one<br>Line two &amp; &lt;three&gt;</p>"));

    [Fact] // boundary: 空与 null——HtmlToText 与 DetectKind 不同,null 安全返回空串
    public void HtmlToText_EmptyAndNull_BoundaryEmptyNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, JdPasteParser.HtmlToText(""));
        Assert.Equal(string.Empty, JdPasteParser.HtmlToText(null!));
    }

    // ================= 形态 A:标准标题行 =================

    [Fact]
    public void Parse_StandardForm_AllFieldsExtractedFromJdText()
    {
        var d = JdPasteParser.Parse(JdStandard, PasteKind.Text);
        Assert.Equal(ParseOutcome.Ok, d.Status);
        Assert.Equal("Globex Corporation", d.Company);
        Assert.Equal("Senior .NET Developer", d.Role);
        Assert.Equal("Toronto, ON", d.Location);
        Assert.Equal("$140k - $180k", d.Salary);
        Assert.Equal("Hybrid", d.WorkMode);
        Assert.Equal("Full-time", d.JobType);
        Assert.Equal(new DateOnly(2026, 11, 30), d.Deadline);
        Assert.Equal(
            "- 5+ years of C# and .NET experience building production systems\n" +
            "- Strong experience with SQL Server and Entity Framework\n" +
            "- Angular experience is a strong plus for this role", d.Requirements);
        Assert.Equal(
            "- Health and dental insurance from day one\n" +
            "- RRSP matching up to five percent\n" +
            "- Four weeks of paid vacation every year", d.Benefits);
        // 现状:公司介绍段会把其后的截止日期行一并收进来(段落提取只在段落标题处停)——特征锁定,非 known-gap 判定项
        Assert.Equal(
            "Globex Corporation is a global payments company founded in 2005 and headquartered in Toronto Canada.\n" +
            "Apply by: November 30, 2026", d.CompanyDescription);
        Assert.Equal(
            "We are hiring a Senior .NET Developer to build payment platforms used by merchants across North America every day. " +
            "You will design and ship microservices with C# and Azure. Requirements " +
            "- 5+ years of C# and .NET experience building production systems " +
            "- Strong experience with SQL Server and Entity Framework " +
            "- Angular experience is a strong plus for this role", d.JdSummary);
        Assert.Equal("C#, .NET, Entity Framework, Angular, SQL, SQL Server, Microservices, Azure", d.MatchKeywords);
        Assert.Equal(JdStandard, d.JdText);
        Assert.False(d.UsedAi);
        Assert.False(d.NeedsManualReview);
        Assert.Equal(6, JdPasteParser.CountFilled(d));
    }

    // ================= 形态 B:内联 Benefits(REQ-09 缺口形态) =================

    [Fact]
    public void Parse_InlineBenefitsForm_CoreFieldsStillExtracted()
    {
        var d = JdPasteParser.Parse(JdInlineBenefits, PasteKind.Text);
        Assert.Equal("Senior Backend Developer", d.Role);
        Assert.Equal("Toronto, ON", d.Location);
        Assert.Equal("$120k - $150k", d.Salary);
        Assert.Equal("Hybrid", d.WorkMode);
        Assert.Equal("Full-time", d.JobType);
        Assert.Equal(new DateOnly(2026, 12, 15), d.Deadline);
        Assert.Equal("C#, .NET, PostgreSQL", d.MatchKeywords);
    }

    [Fact] // known-gap → 票 29:同行内联 "Benefits: …" 解不出(AUDIT REQ-09 形态 B,SmartParseRules 段落提取只认独立标题行)
    public void Parse_InlineBenefitsForm_KnownGap_Ticket29_BenefitsNull()
    {
        var d = JdPasteParser.Parse(JdInlineBenefits, PasteKind.Text);
        Assert.Null(d.Benefits);
    }

    [Fact] // known-gap → 票 29:无 "About the job" 标题时摘要以元数据行开头(AUDIT REQ-09 形态 B)
    public void Parse_InlineBenefitsForm_KnownGap_Ticket29_SummaryStartsWithMetadataLine()
    {
        var d = JdPasteParser.Parse(JdInlineBenefits, PasteKind.Text);
        Assert.Equal(
            "Toronto, ON - Hybrid - Full-time Salary: $120,000 - $150,000 per year " +
            "We are looking for a Senior Backend Developer to join our platform team in Toronto building APIs with C# .NET and PostgreSQL for thousands of customers.",
            d.JdSummary);
    }

    [Fact] // known-gap → 票 29 / FUN-09:此形态下公司名解不出(响应无 company、UI 公司框为空)
    public void Parse_InlineBenefitsForm_KnownGap_Ticket29_CompanyMissing()
    {
        var d = JdPasteParser.Parse(JdInlineBenefits, PasteKind.Text);
        Assert.Null(d.Company);
    }

    // ================= 形态 C:公司名误判(REQ-09 缺口形态) =================

    [Fact] // known-gap → 票 29:公司名误判为段落标题 "About the Job"(AUDIT REQ-09 形态 C 原文)
    public void Parse_NoStandardHeaderForm_KnownGap_Ticket29_CompanyMisreadAsAboutTheJob()
    {
        var d = JdPasteParser.Parse(JdNoStandardHeader, PasteKind.Text);
        Assert.Equal("About the Job", d.Company);
        Assert.Equal("Senior Software Engineer", d.Role);
        Assert.Equal(ParseOutcome.Partial, d.Status);
        Assert.Equal("React, TypeScript, Node.js, AWS", d.MatchKeywords);
        Assert.Equal(
            "We are looking for a Senior Software Engineer to join our growing fintech team in Waterloo. " +
            "You will build React and TypeScript applications backed by Node.js services on AWS, " +
            "working closely with product and design partners across the company.", d.JdSummary);
    }

    // ================= LinkedIn "About the company" 形态 =================

    [Fact]
    public void Parse_LinkedInAboutCompanyForm_CompanyAndDescriptionExtracted()
    {
        var jd = """
            Senior Software Engineer
            About the company
            Arvato
            210,272 followers
            Transportation, Logistics, Supply Chain and Storage
            Arvato is a global services company. We provide supply chain management solutions for many well known brands around the world and we are growing fast.
            About the job
            We are hiring a Senior Software Engineer to build logistics platforms with C# and Azure for our clients.
            """;
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.Equal("Arvato", d.Company);
        Assert.Equal(
            "Arvato is a global services company. We provide supply chain management solutions for many well known brands around the world and we are growing fast.",
            d.CompanyDescription);
        Assert.Equal("We are hiring a Senior Software Engineer to build logistics platforms with C# and Azure for our clients.", d.JdSummary);
        // 现状特征:行业分类行被 GuessLocation 当成地点(城市组贪婪双词)——特征锁定,待后续票定性
        Assert.Equal("Transportation, Logistics", d.Location);
    }

    [Fact]
    public void Parse_LinkedInNoiseLines_StrippedFromJdText()
    {
        var jd = """
            Senior Software Engineer
            Acme Corp
            Toronto, ON
            Set alert for similar jobs
            Try Premium for CA$0
            Over 100 applicants
            We are hiring a Senior Software Engineer to build cloud platforms with C# and Azure for enterprise customers across North America.
            """;
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.DoesNotContain("Premium", d.JdText);
        Assert.DoesNotContain("Set alert", d.JdText);
        Assert.Equal("Senior Software Engineer", d.Role);
        Assert.Equal("Acme Corp", d.Company);
    }

    // ================= 中文 JD 形态 =================

    [Fact]
    public void Parse_ChineseLabeledForm_CompanyAndLocationFromLabels()
    {
        var jd = """
            高级 .NET 开发工程师
            公司： 环球支付有限公司
            地点： 上海
            我们正在招聘高级 .NET 开发工程师，负责支付平台的后端开发，使用 C# 与 SQL Server 构建高并发系统，支持远程办公，欢迎投递简历。
            """;
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.Equal("环球支付有限公司", d.Company);
        Assert.Equal("上海", d.Location);
        Assert.Equal("C#, .NET, SQL, SQL Server", d.MatchKeywords);
        // 现状特征:岗位词典只有英文词,中文岗位名解不出 Role——特征锁定
        Assert.Null(d.Role);
        // 现状特征:中文"远程"嵌在句中时 \b 词边界不成立,WorkMode 解不出——特征锁定
        Assert.Null(d.WorkMode);
    }

    [Fact]
    public void Parse_ChineseSectionHeaders_RequirementsAndBenefitsExtracted()
    {
        var jd = "高级工程师\n环球支付\n上海\n任职要求\n- 五年以上 C# 开发经验，熟悉 SQL Server\n- 熟悉微服务架构设计\n福利待遇\n- 五险一金与补充医疗保险\n- 弹性工作时间与年度体检";
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.Equal("- 五年以上 C# 开发经验，熟悉 SQL Server\n- 熟悉微服务架构设计", d.Requirements);
        Assert.Equal("- 五险一金与补充医疗保险\n- 弹性工作时间与年度体检", d.Benefits);
    }

    [Fact] // boundary: 特殊字符/Unicode——中文"远程"在分段末尾(词边界成立)可解出
    public void Parse_ChineseRemoteAtSegmentEnd_WorkModeRemote()
        => Assert.Equal("Remote", ParseWithLine("工作方式灵活，远程").WorkMode);

    // ================= URL 形态 =================

    [Fact]
    public void Parse_GreenhouseUrl_CompanyFromPath_RoleFromSlug()
    {
        var d = JdPasteParser.Parse("https://boards.greenhouse.io/shopify/jobs/5567891-senior-software-engineer", PasteKind.Url);
        Assert.Equal("Shopify", d.Company);
        Assert.Equal("Senior Software Engineer", d.Role);
        Assert.Equal("Greenhouse", d.DetectedSource);
        Assert.Null(d.JdText); // URL 形态无正文:摘要/全文留空
        Assert.Null(d.JdSummary);
        Assert.Equal(ParseOutcome.Partial, d.Status);
    }

    [Fact]
    public void Parse_GreenhouseUrl_NumericSlugOnly_RoleNull() // boundary: slug 只有纯数字 id 段
    {
        var d = JdPasteParser.Parse("https://boards.greenhouse.io/shopify/jobs/5567891", PasteKind.Url);
        Assert.Equal("Shopify", d.Company);
        Assert.Null(d.Role);
    }

    [Fact]
    public void Parse_LinkedInViewUrl_CompanyFromAtSlug()
    {
        var d = JdPasteParser.Parse("https://www.linkedin.com/jobs/view/senior-backend-engineer-at-shopify-4012339988", PasteKind.Url);
        Assert.Equal("Shopify", d.Company);
        Assert.Equal("Senior Backend Engineer", d.Role);
        Assert.Equal("LinkedIn", d.DetectedSource);
    }

    [Fact] // 聚合站域名不冒充公司名:猜错比留空更糟(代码注释口径)
    public void Parse_IndeedUrl_AggregatorNotUsedAsCompany()
    {
        var d = JdPasteParser.Parse("https://www.indeed.com/viewjob?jk=abc123", PasteKind.Url);
        Assert.Null(d.Company);
        Assert.Equal("Indeed", d.DetectedSource);
        Assert.Equal(ParseOutcome.Failed, d.Status);
    }

    [Fact]
    public void Parse_UnknownSiteUrl_DomainAsCompany()
    {
        var d = JdPasteParser.Parse("https://careers.acmecorp.com/roles/principal-data-scientist", PasteKind.Url);
        Assert.Equal("Acmecorp", d.Company);
        Assert.Equal("Principal Data Scientist", d.Role);
        Assert.Equal("公司官网/未知站点", d.DetectedSource);
    }

    [Fact]
    public void Parse_CompanySiteUrl_DomainIsCompany()
    {
        var d = JdPasteParser.Parse("https://stripe.com/jobs/listing/staff-software-engineer-998877", PasteKind.Url);
        Assert.Equal("Stripe", d.Company);
        Assert.Equal("Staff Software Engineer", d.Role);
        Assert.Equal("Stripe", d.DetectedSource);
    }

    [Fact]
    public void Parse_WorkdayUrl_SubdomainCompany_LanguageSegmentSkipped()
    {
        var d = JdPasteParser.Parse("https://amazon.wd1.myworkdayjobs.com/en-US/AmazonCareers/job/Toronto/Senior-Developer_12345", PasteKind.Url);
        Assert.Equal("Amazon", d.Company);
        Assert.Equal("Senior Developer", d.Role);
        Assert.Equal("Workday", d.DetectedSource);
    }

    [Fact]
    public void Parse_LeverUrl_RoleFromQueryParam()
    {
        var d = JdPasteParser.Parse("https://jobs.lever.co/acme/9f8e7d6c?title=Senior+Engineer", PasteKind.Url);
        Assert.Equal("Acme", d.Company);
        Assert.Equal("Senior Engineer", d.Role);
        Assert.Equal("Lever", d.DetectedSource);
    }

    [Fact]
    public void Parse_TextContainingUrl_LinkExtractedAndCompanyFromUrlFallback()
    {
        var d = JdPasteParser.Parse(
            "Check this out https://boards.greenhouse.io/shopify/jobs/5567891-senior-software-engineer today",
            PasteKind.Text);
        Assert.Equal("https://boards.greenhouse.io/shopify/jobs/5567891-senior-software-engineer", d.Link);
        Assert.Equal("Shopify", d.Company);
        Assert.Null(d.DetectedSource); // 非 Url 形态不标来源
    }

    // ================= HTML 形态(LinkedIn 抓取) =================

    [Fact]
    public void Parse_HtmlForm_TitleTagAndPillsTakePrecedence()
    {
        var html = """
            <html><head><title>Senior .Net Developer @ Arvato | LinkedIn</title>
            <meta name="li-salary" content="$95k - $120k">
            <meta name="li-workmode" content="Remote">
            <meta name="li-jobtype" content="Contract">
            </head><body><div class="linkedin-company-desc">Arvato is a global services company providing supply chain solutions for brands worldwide.</div>
            <p>Senior .Net Developer role building logistics platforms with C# and SQL Server.</p></body></html>
            """;
        var d = JdPasteParser.Parse(html, PasteKind.Html);
        Assert.Equal("Arvato", d.Company);
        Assert.Equal("Senior .Net Developer", d.Role);
        Assert.Equal("$95k - $120k", d.Salary);
        Assert.Equal("Remote", d.WorkMode);
        Assert.Equal("Contract", d.JobType);
        Assert.Equal("Arvato is a global services company providing supply chain solutions for brands worldwide.", d.CompanyDescription);
        // 现状特征:Status 在 pill/title 覆盖之前按正文已解字段数算出(此处正文只够 Partial),
        // 覆盖后 CountFilled(dto)=5 也不回改 Status——特征锁定
        Assert.Equal(ParseOutcome.Partial, d.Status);
        Assert.Equal(5, JdPasteParser.CountFilled(d));
    }

    [Fact] // 现状特征:kind=Image 时 Parse 按纯文本路径解析(图片只在 DetectKind 层识别)
    public void Parse_ImageKind_TreatedAsText()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nWe are hiring a Senior Developer to build great software products for our customers in Canada.",
            PasteKind.Image);
        Assert.Equal(PasteKind.Image, d.SourceKind);
        Assert.Equal("Senior Developer", d.Role);
    }
}
