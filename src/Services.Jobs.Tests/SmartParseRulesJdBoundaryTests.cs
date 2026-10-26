using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 28(ARC-08a):SmartParseRules.JdPasteParser 特征测试之二——
/// 字段级规则(薪资/截止日期/工作身份/岗位性质)与总纲第七节 boundary 强制类别。
/// 期望值与 SmartParseRulesJdTests 同口径:JD 原文人工判定在先,实测对账在后;
/// 现状与合理预期不符处标 characterization 并注明,属解析缺口的归票 29 或报告观察项。
/// </summary>
public sealed class SmartParseRulesJdBoundaryTests
{
    private static ParsedJdDto ParseWithLine(string line) => JdPasteParser.Parse(
        $"Senior Developer\nAcme Corp\nToronto, ON\n{line}\nWe are hiring a Senior Developer to build great software products for our customers in Canada.",
        PasteKind.Text);

    // ================= 薪资 =================

    [Theory]
    [InlineData("Pay: 120-160 for this role", "120k - 160k")] // 无币种符号:不硬塞 "$"(代码注释口径)
    [InlineData("$90K/yr - $105K/yr", "$90k - $105k")] // 年薪频率后缀默认不标
    [InlineData("Rate is $45/hour for contractors", "$45/hr")]
    [InlineData("Rate is $55 per hr", "$55/hr")]
    [InlineData("€80k - €100k", "€80k - €100k")] // boundary: 特殊字符/Unicode 币种符号
    [InlineData("£95k", "£95k+")]
    [InlineData("$180,000 - $140,000", "$140k - $180k")] // 上下限倒置 → 自动交换
    [InlineData("Up to $150k for the right person", "$150k+")]
    [InlineData("Salary: $200,000 - $250,000", "$200k - $250k")]
    public void Parse_SalaryForms_Normalized(string line, string expected)
        => Assert.Equal(expected, ParseWithLine(line).Salary);

    [Fact] // 现状特征:后缀式 CAD 不被识别为币种(币种只认两端前缀位)——特征锁定
    public void Parse_SalaryCadSuffix_CurrencyDropped()
        => Assert.Equal("120k - 150k", ParseWithLine("120,000 - 150,000 CAD").Salary);

    [Fact] // 现状特征:时薪区间被区间规则抢先匹配成 k 年薪 + /hr 后缀("$40k - $50k/hr")——特征锁定,报告观察项
    public void Parse_SalaryHourlyRange_ReadAsKRangeWithHourSuffix()
        => Assert.Equal("$40k - $50k/hr", ParseWithLine("$40 - $50 per hour").Salary);

    [Fact] // 现状特征:区间下限要求 2-3 位数字,"$8k/month" 的 8 不入区间,只剩单值命中——特征锁定,报告观察项
    public void Parse_SalaryMonthlyRange_SingleDigitLowerBoundMissed()
        => Assert.Equal("$10k/mo+", ParseWithLine("$8k/month - $10k/month").Salary);

    [Fact] // boundary: 日期临界——发布截止日期不得被当成薪资(回归钉:曾解出 "$11k - $26k")
    public void Parse_PostingDate_NotMisreadAsSalary()
    {
        var d = ParseWithLine("Posting closes 2026-11-30 so apply soon");
        Assert.Null(d.Salary);
        Assert.Equal(new DateOnly(2026, 11, 30), d.Deadline);
    }

    [Fact] // boundary: 0 与负数——负数薪资带不解出任何薪资
    public void Parse_ZeroAndNegativeNumbers_NoSalary()
    {
        var d = JdPasteParser.Parse(
            "0 years of experience needed. Salary band -5 to -1 is not real. Team of 0 managers.",
            PasteKind.Text);
        Assert.Null(d.Salary);
        Assert.Equal("0 years of experience needed. Salary band -5 to -1 is not real. Team of 0 managers.", d.JdSummary);
    }

    // ================= 截止日期(boundary: 日期临界) =================

    [Theory]
    [InlineData("Apply by: 2026-11-30", 2026, 11, 30)]
    [InlineData("Closing date: January 5, 2027", 2027, 1, 5)]
    [InlineData("Application closing: 15/12/2026", 2026, 12, 15)] // day>12 只能是 dd/MM
    [InlineData("Apply by 12/15/2026", 2026, 12, 15)] // MM/dd
    [InlineData("Apply by 01/02/2026", 2026, 1, 2)] // 歧义日期优先北美 MM/dd(代码注释口径)
    [InlineData("申请截止：2026-12-01", 2026, 12, 1)] // 中文标签 + 全角冒号
    public void Parse_DeadlineForms(string line, int y, int m, int dd)
        => Assert.Equal(new DateOnly(y, m, dd), ParseWithLine(line).Deadline);

    [Theory]
    [InlineData("Apply by: 2026-13-45")] // 非法日期(月 13 日 45)
    [InlineData("Apply soon")] // 无日期
    public void Parse_DeadlineAbsentOrInvalid_ReturnsNull(string line)
        => Assert.Null(ParseWithLine(line).Deadline);

    // ================= 工作身份 =================

    [Theory]
    [InlineData("Applicants must be a Canadian citizen or permanent resident of Canada.", "Citizen or PR required")]
    [InlineData("Only a Canadian citizen may apply for this position.", "Canadian citizen required")]
    [InlineData("You must hold permanent resident status in Canada.", "PR required")]
    [InlineData("An open work permit is accepted for this role.", "Open work permit accepted")]
    [InlineData("A valid work permit is required to start this role.", "Valid work permit required")]
    [InlineData("You must be legally authorized to work in Canada.", "Must be legally authorized to work in Canada")]
    [InlineData("Work authorization is required for this position.", "Work authorization required")]
    [InlineData("We are unable to sponsor work visas for this position.", "No sponsorship available")]
    [InlineData("Sponsorship available for exceptional candidates.", "Sponsorship available")]
    [InlineData("A security clearance is required for this role.", "Security clearance required")]
    [InlineData("There are no work authorization restrictions for this role.", "No restriction")]
    [InlineData("Work Authorization: Canadian citizen or PR only", "Canadian citizen or PR only")] // 标签行原样透传
    public void Parse_WorkAuthorizationForms_Normalized(string line, string expected)
        => Assert.Equal(expected, ParseWithLine(line).WorkAuthorization);

    [Fact]
    public void Parse_WorkAuthorizationAbsent_ReturnsNull()
        => Assert.Null(ParseWithLine("We welcome everyone to apply for this exciting role.").WorkAuthorization);

    // ================= 岗位性质 / 工作模式 / 优先级 =================

    [Theory]
    [InlineData("Job Type: Contract", "Contract")] // 标签行优先
    [InlineData("permanent", "Permanent")] // 独立短行
    [InlineData("This is a part-time position with flexible hours.", "Part-time")] // 正文只放行 full/part-time
    public void Parse_JobTypeForms(string line, string expected)
        => Assert.Equal(expected, ParseWithLine(line).JobType);

    [Fact] // 保守口径回归钉:正文里的 "permanent resident" 不是岗位性质
    public void Parse_PermanentResidentInBody_NotJobType()
    {
        var d = ParseWithLine("Must be a permanent resident of Canada to apply.");
        Assert.Null(d.JobType);
        Assert.Equal("PR required", d.WorkAuthorization);
    }

    [Fact] // 保守口径回归钉:正文里的 "contract negotiation" 不是岗位性质
    public void Parse_ContractNegotiationInBody_NotJobType()
        => Assert.Null(ParseWithLine("You will handle contract negotiation with vendors daily.").JobType);

    [Fact]
    public void Parse_UrgentPosting_PriorityHigh()
        => Assert.Equal("High", ParseWithLine("Urgent hire - start ASAP").Priority);

    [Fact]
    public void Parse_OnSiteBody_WorkModeOnsite()
        => Assert.Equal("Onsite", ParseWithLine("This is an on-site role in our downtown office.").WorkMode);

    [Fact]
    public void Parse_NoPrioritySignals_PriorityNull()
        => Assert.Null(ParseWithLine("A steady role with a friendly team.").Priority);

    // ================= 职位/公司名清洗 =================

    [Fact]
    public void Parse_RoleParenLocationTail_Stripped()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer (Toronto, ON)\nAcme Corp\nWe are hiring a Senior Developer to build great software products for our customers in Canada today.",
            PasteKind.Text);
        Assert.Equal("Senior Developer", d.Role);
    }

    [Fact]
    public void Parse_RoleParenTechQualifier_Kept()
    {
        var d = JdPasteParser.Parse(
            "Backend Developer (Node.js/TypeScript)\nAcme Corp\nToronto, ON\nWe are hiring a Backend Developer to build great software products for our customers in Canada.",
            PasteKind.Text);
        Assert.Equal("Backend Developer (Node.js/TypeScript)", d.Role);
    }

    [Fact]
    public void Parse_JobOpeningPrefix_StrippedFromRole()
    {
        var d = JdPasteParser.Parse(
            "Job opening - Senior Developer | Randstad Canada\nToronto, ON\nWe are hiring a Senior Developer to build great software products for our customers in Canada today.",
            PasteKind.Text);
        Assert.Equal("Senior Developer", d.Role);
    }

    [Fact] // 导航残渣清洗:抓取页导航词被剥掉,只剩公司名
    public void Parse_CompanyNavResidue_CleanedToCompanyName()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nlocations contact us EN FR randstad\nToronto, ON\nWe are hiring a Senior Developer to build great software products for our customers in Canada today.",
            PasteKind.Text);
        Assert.Equal("Randstad", d.Company);
    }

    [Fact] // 现状特征:"| 地点" 后缀含逗号不剥,留在职位名里——特征锁定,报告观察项
    public void Parse_RolePipeLocationSuffix_KeptInRole()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer | Toronto, ON\nAcme Corp\nWe are hiring a Senior Developer to build great software products for our customers in Canada today.",
            PasteKind.Text);
        Assert.Equal("Senior Developer | Toronto, ON", d.Role);
        Assert.Equal("Toronto, ON", d.Location);
    }

    [Fact] // 现状特征:地点行与职位同行时,贪婪城市组吞岗位词后整段匹配被消耗,地点解不出——特征锁定,报告观察项
    public void Parse_RoleAndLocationSameLine_LocationMissed()
    {
        var d = JdPasteParser.Parse(
            "Full Stack .Net Developer Toronto, Ontario\nWe are hiring talented people to build great software products for our customers in Canada today.",
            PasteKind.Text);
        Assert.Null(d.Location);
        Assert.Equal("Full Stack .Net Developer Toronto, Ontario", d.Role);
    }

    // ================= 关键词 =================

    [Fact] // 上限:关键词最多取 14 个(词典序)
    public void Parse_Keywords_CappedAtFourteen()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nC# .NET ASP.NET Angular React Vue TypeScript JavaScript Node.js SQL PostgreSQL MySQL MongoDB Redis Kafka RabbitMQ Docker Kubernetes AWS Azure Python Java Go Rust",
            PasteKind.Text);
        Assert.Equal(
            "C#, .NET, ASP.NET, Angular, React, Vue, TypeScript, JavaScript, Node.js, SQL, PostgreSQL, MySQL, MongoDB, Redis",
            d.MatchKeywords);
    }

    [Fact] // 短词词边界:"Google"/"Golang" 不得误命中 "Go"
    public void Parse_KeywordShortWordBoundary_GoNotHitInsideGoogle()
    {
        var trap = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nWe use Google Cloud and Golang tools every day here.",
            PasteKind.Text);
        Assert.Null(trap.MatchKeywords);
        var hit = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nWe write services in Go, Python and Rust.",
            PasteKind.Text);
        Assert.Equal("Python, Go, Rust", hit.MatchKeywords);
    }

    // ================= 段落提取边界 =================

    [Fact]
    public void Parse_SectionHeaderWithColon_Extracted()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nRequirements:\n- Five years of C# experience building production systems\n- Deep knowledge of SQL Server tuning",
            PasteKind.Text);
        Assert.Equal("- Five years of C# experience building production systems\n- Deep knowledge of SQL Server tuning", d.Requirements);
    }

    [Fact] // boundary: 超长——段落内容上限 8000 字符截断 + 省略号(总长 8001)
    public void Parse_SectionOver8000Chars_TruncatedWithEllipsis()
    {
        var jd = "Senior Developer\nAcme Corp\nToronto, ON\nRequirements\n" + new string('x', 9000);
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.NotNull(d.Requirements);
        Assert.Equal(8001, d.Requirements!.Length);
        Assert.EndsWith("…", d.Requirements);
    }

    [Fact] // boundary: 段落内容不足 10 字符视为无;无下一标题时正文行会被并入上一段(现状特征)
    public void Parse_ShortSectionUnder10Chars_ReturnsNull()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nRequirements\nShort\nBenefits\n- Health and dental insurance from day one\nWe are hiring a Senior Developer to build great software products for our customers.",
            PasteKind.Text);
        Assert.Null(d.Requirements);
        Assert.Equal(
            "- Health and dental insurance from day one\nWe are hiring a Senior Developer to build great software products for our customers.",
            d.Benefits);
    }

    [Fact] // 段落尾部页脚噪音行终止收集,噪音后的行不入段
    public void Parse_SectionStopsAtFooterNoise()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nBenefits\n- Health and dental insurance from day one\n- RRSP matching up to five percent\nRecruiting-platform powered by SmartRecruiters\n- This line should not appear in benefits",
            PasteKind.Text);
        Assert.Equal("- Health and dental insurance from day one\n- RRSP matching up to five percent", d.Benefits);
    }

    [Fact] // boundary: 公司介绍不足 20 字符视为无
    public void Parse_CompanyDescriptionUnder20Chars_ReturnsNull()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nAbout the company\nShort co.\nAbout the job\nWe are hiring a Senior Developer to build great software products for our customers in Canada today.",
            PasteKind.Text);
        Assert.Null(d.CompanyDescription);
    }

    // ================= 摘要边界 =================

    [Fact] // 页脚横幅行截断摘要,其后的行不入摘要
    public void Parse_SummaryCutAtFooterBannerLine()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nWe are hiring a Senior Developer to build platforms for customers across Canada with great tools and a great team.\nWe will keep you updated when we have similar job postings.\nThis trailing line must not appear in the summary at all.",
            PasteKind.Text);
        Assert.Equal("We are hiring a Senior Developer to build platforms for customers across Canada with great tools and a great team.", d.JdSummary);
    }

    [Fact] // 摘要跳过页面标题行与 "posted …" 元数据行
    public void Parse_SummarySkipsTitleAndPostedLines()
    {
        var d = JdPasteParser.Parse(
            "Job opening - Senior Developer\nposted June 8th, 2026\nWe are hiring a Senior Developer to build platforms for customers across Canada with great tools.",
            PasteKind.Text);
        Assert.Equal("Senior Developer", d.Role);
        Assert.Equal("We are hiring a Senior Developer to build platforms for customers across Canada with great tools.", d.JdSummary);
    }

    // ================= boundary: 空/null/单字符 =================

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n ")]
    public void Parse_EmptyOrWhitespace_BoundaryEmpty_ReturnsFailed(string raw) // boundary: 空输入
    {
        var d = JdPasteParser.Parse(raw, PasteKind.Text);
        Assert.Equal(ParseOutcome.Failed, d.Status);
        Assert.Null(d.Company);
        Assert.Null(d.Role);
        Assert.Null(d.JdSummary);
        Assert.Equal(string.Empty, d.JdText);
        Assert.Equal(0, JdPasteParser.CountFilled(d));
    }

    [Fact] // boundary: null 输入——现状为抛 NullReferenceException
    public void Parse_Null_BoundaryNull_ThrowsNullReference()
        => Assert.Throws<NullReferenceException>(() => JdPasteParser.Parse(null!, PasteKind.Text));

    [Fact]
    public void Parse_SingleChar_BoundaryMinimal_SummaryIsTheChar()
    {
        var d = JdPasteParser.Parse("x", PasteKind.Text);
        Assert.Equal(ParseOutcome.Partial, d.Status);
        Assert.Equal("x", d.JdSummary);
    }

    // ================= boundary: 标签值上限 120(±1) =================

    [Fact] // boundary: 超长上限——标签值恰好 120 字符放行
    public void Parse_LabeledCompanyExactly120Chars_BoundaryLimit_Accepted()
    {
        var d = JdPasteParser.Parse(
            $"Company: {new string('C', 120)}\nSenior Developer\nToronto, ON\nWe are hiring a Senior Developer to build great software products for our customers.",
            PasteKind.Text);
        Assert.NotNull(d.Company);
        Assert.Equal(120, d.Company!.Length);
    }

    [Fact] // boundary: 超长上限+1——标签值 121 字符被标签路径拒绝(且无其他兜底命中)
    public void Parse_LabeledCompany121Chars_BoundaryLimitPlusOne_Rejected()
    {
        var d = JdPasteParser.Parse(
            $"Company: {new string('C', 121)}\nSenior Developer\nToronto, ON\nWe are hiring a Senior Developer to build great software products for our customers.",
            PasteKind.Text);
        Assert.Null(d.Company);
    }

    // ================= boundary: JD 全文 200,000 闸门(±1) =================
    // 解析器本身不设长度闸门(全文原样进 JdText);200,000 闸门在 ParseJdCommand 校验层
    // (SmartAddHandlers,已由 LongTextLengthGateTests 覆盖)。此处钉解析器侧在闸门上下的行为。
    [Theory]
    [InlineData(199_999)]
    [InlineData(200_000)]
    [InlineData(200_001)]
    public void Parse_JdTextAroundUnifiedGate_BoundaryOverlong_ParserKeepsFullText(int totalLength)
    {
        const string header = "Senior Developer\nAcme Corp\nToronto, ON\n";
        var raw = header + new string('a', totalLength - header.Length);
        var d = JdPasteParser.Parse(raw, PasteKind.Text);
        Assert.NotNull(d.JdText);
        Assert.Equal(totalLength, d.JdText!.Length);
    }

    // ================= boundary: 特殊字符 & < > ' " 实测形态 =================

    [Fact] // boundary: 特殊字符——职位/公司名中的 & < > " ' 原样保留(DetectKind 不误判为 HTML:无块级标签)
    public void Parse_SpecialCharactersInRoleAndCompany_PreservedVerbatim()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer & Designer <Lead> \"Principal\" 'Staff'\nA&B <Corp> \"Inc\" 'Ltd'\nToronto, ON\nWe are hiring a Senior Developer & Designer to build software with C# for customers & partners <everywhere> \"worldwide\" 'today'.",
            PasteKind.Text);
        Assert.Equal("Senior Developer & Designer <Lead> \"Principal\" 'Staff'", d.Role);
        Assert.Equal("A&B <Corp> \"Inc\" 'Ltd'", d.Company);
        Assert.Equal("Toronto, ON", d.Location);
    }

    [Fact] // boundary: Unicode——重音城市名不入地点正则([A-Z][a-zA-Z] 不含 é),被公司名兜底收走(现状特征)
    public void Parse_UnicodeAccentedCity_NotMatchedAsLocation()
    {
        var d = JdPasteParser.Parse(
            "Développeur Senior 🚀\nMontréal, QC\nNous recherchons un Développeur Senior pour construire des plateformes avec C# et Azure — café ☕ inclus. Travail hybride à Montréal.",
            PasteKind.Text);
        Assert.Equal("Développeur Senior 🚀", d.Role);
        Assert.Equal("Montréal, QC", d.Company);
        Assert.Null(d.Location);
    }
}
