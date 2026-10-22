using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 29 (REQ-09 + FUN-09):SmartParseRules 形态缺口修复的新增用例。
/// 覆盖票面 boundary 强制项:标题行变体 (大小写/冒号全半角)、内联多字段同行、
/// 公司名与 About 段共存、超长 JD 闸门不回退。
/// 期望值口径与票 28 一致:JD 原文人工判定在先。
/// </summary>
public sealed class SmartParseRulesTicket29Tests
{
    private const string Prose =
        "We are looking for a Senior Backend Developer to join our platform team in Toronto building APIs with C# .NET and PostgreSQL for thousands of customers.";

    // ================= 内联段落:标题行变体 (大小写/全半角冒号) =================

    [Theory]
    [InlineData("benefits: Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.")]
    [InlineData("BENEFITS: Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.")]
    [InlineData("Benefits：Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.")] // 全角冒号
    [InlineData("Benefits : Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.")] // 冒号前空格
    public void Parse_InlineBenefits_HeaderVariants_Extracted(string benefitsLine) // boundary: 标题行变体
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nToronto, ON\n{Prose}\n{benefitsLine}",
            PasteKind.Text);
        Assert.Equal(
            "Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.",
            d.Benefits);
    }

    [Theory]
    [InlineData("BENEFITS")] // 独立标题行大写
    [InlineData("Benefits：")] // 独立标题行全角冒号
    public void Parse_StandaloneBenefitsHeader_Variants_Extracted(string header) // boundary: 标题行变体
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nToronto, ON\n{header}\n- Health and dental insurance from day one\n- RRSP matching up to five percent",
            PasteKind.Text);
        Assert.Equal("- Health and dental insurance from day one\n- RRSP matching up to five percent", d.Benefits);
    }

    [Fact] // 内联首行 + 后续 bullet 行续收,且在 "Apply by:" 标签行处停止 (标签行不吞入段内)
    public void Parse_InlineBenefits_ContinuesIntoBullets_StopsAtLabeledLine()
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nToronto, ON\n{Prose}\n" +
            "Benefits: Health insurance and dental coverage for all employees.\n" +
            "- RRSP matching up to five percent\n" +
            "- Four weeks of paid vacation every year\n" +
            "Apply by: December 15, 2026",
            PasteKind.Text);
        Assert.Equal(
            "Health insurance and dental coverage for all employees.\n" +
            "- RRSP matching up to five percent\n" +
            "- Four weeks of paid vacation every year",
            d.Benefits);
        Assert.Equal(new DateOnly(2026, 12, 15), d.Deadline);
    }

    [Fact] // 内联机制对 Requirements 同样生效 (同为段落级字段,同一提取函数)
    public void Parse_InlineRequirements_Extracted()
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nToronto, ON\n{Prose}\n" +
            "Requirements: 5+ years of C# and .NET experience building production systems.",
            PasteKind.Text);
        Assert.Equal("5+ years of C# and .NET experience building production systems.", d.Requirements);
    }

    [Fact] // 中文内联标题 + 全角冒号
    public void Parse_InlineChineseBenefits_Extracted()
    {
        var d = JdPasteParser.Parse(
            "高级工程师\n环球支付\n上海\n我们正在招聘高级工程师负责支付平台开发使用 C# 与 SQL Server 构建系统。\n福利待遇：五险一金与补充医疗保险，弹性工作时间与年度体检",
            PasteKind.Text);
        Assert.Equal("五险一金与补充医疗保险，弹性工作时间与年度体检", d.Benefits);
    }

    [Fact] // boundary: 空集合/空内容——"Benefits:" 后无任何内容行 → null,不编造
    public void Parse_InlineBenefitsLabelOnlyNoContent_ReturnsNull()
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nToronto, ON\n{Prose}\nBenefits:",
            PasteKind.Text);
        Assert.Null(d.Benefits);
    }

    [Fact] // boundary: 超长——内联段落内容同样受 8000 截断闸门约束 (总长 8001 + 省略号)
    public void Parse_InlineBenefitsOver8000Chars_TruncatedWithEllipsis()
    {
        var jd = $"Senior Backend Developer\nToronto, ON\nBenefits: " + new string('x', 9000);
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.NotNull(d.Benefits);
        Assert.Equal(8001, d.Benefits!.Length);
        Assert.EndsWith("…", d.Benefits);
    }

    // ================= 摘要:内联多字段同行 (复合元数据行变体) =================

    [Theory]
    [InlineData("Toronto, ON - Hybrid - Full-time")]
    [InlineData("Toronto, ON | Hybrid | Full-time")]
    [InlineData("Toronto, ON · Hybrid · Full-time")]
    public void Parse_PackedMetadataLine_SummaryStartsWithProse(string metaLine) // boundary: 内联多字段同行
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\n{metaLine}\nSalary: $120,000 - $150,000 per year\n{Prose}",
            PasteKind.Text);
        Assert.Equal(Prose, d.JdSummary);
        Assert.Equal("Toronto, ON", d.Location);
        Assert.Equal("Hybrid", d.WorkMode);
    }

    // ================= 公司名:FUN-09 两形态 + About 段共存 =================

    [Fact] // FUN-09 形态一:company-first (公司行在职位行之前)
    public void Parse_CompanyFirstForm_CompanyExtracted()
    {
        var d = JdPasteParser.Parse(
            $"Globex Corporation\nSenior Backend Developer\nToronto, ON (Hybrid)\n{Prose}",
            PasteKind.Text);
        Assert.Equal("Globex Corporation", d.Company);
        Assert.Equal("Senior Backend Developer", d.Role);
        Assert.Equal("Toronto, ON", d.Location);
    }

    [Fact] // FUN-09 形态二:role-first,公司行带 "· 地点 (模式)" 后缀
    public void Parse_RoleFirstForm_CompanyWithLocationSuffix_Extracted()
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nGlobex Corporation · Toronto, ON (Hybrid)\n{Prose}",
            PasteKind.Text);
        Assert.Equal("Globex Corporation", d.Company);
        Assert.Equal("Senior Backend Developer", d.Role);
        Assert.Equal("Toronto, ON", d.Location);
        Assert.Equal("Hybrid", d.WorkMode);
    }

    [Fact] // 公司名与 About 段共存:真公司名在 role 行之前,"About the Job" 在 role 行之后 — 取真公司名
    public void Parse_CompanyBeforeRole_AboutTheJobAfterRole_CompanyIsRealOne() // boundary: 公司名与 About 段共存
    {
        var d = JdPasteParser.Parse(
            "Globex Corporation\nSenior Software Engineer\nAbout the Job\n" +
            "We are looking for a Senior Software Engineer to join our growing fintech team in Waterloo. You will build React and TypeScript applications backed by Node.js services on AWS, working closely with product and design partners across the company.",
            PasteKind.Text);
        Assert.Equal("Globex Corporation", d.Company);
        Assert.Equal("Senior Software Engineer", d.Role);
    }

    [Fact] // 公司名与 About 段共存:"About the company" 段存在时公司介绍照常提取,且标题不被误当公司名
    public void Parse_AboutCompanySectionPresent_CompanyNotMisreadFromHeaders()
    {
        var d = JdPasteParser.Parse(
            "Senior Software Engineer\nAbout the Job\n" +
            "We are looking for a Senior Software Engineer to join our growing fintech team in Waterloo. You will build React and TypeScript applications backed by Node.js services on AWS, working closely with product and design partners across the company.\n" +
            "About the company\n" +
            "Globex Corporation is a global payments company founded in 2005 and headquartered in Toronto Canada.",
            PasteKind.Text);
        Assert.Null(d.Company);
        Assert.Equal(
            "Globex Corporation is a global payments company founded in 2005 and headquartered in Toronto Canada.",
            d.CompanyDescription);
    }

    [Fact] // 段落标题紧跟 role 行时不冒充公司名 (Benefits 标题同理)
    public void Parse_SectionHeaderAfterRole_NotCompany()
    {
        var d = JdPasteParser.Parse(
            $"Senior Backend Developer\nBenefits\n- Health and dental insurance from day one\n{Prose}",
            PasteKind.Text);
        Assert.Null(d.Company);
        Assert.NotNull(d.Benefits);
    }

    // ================= 超长 JD 闸门不回退 (boundary: 上限 ±1,新形态在长 JD 内仍解出) =================

    [Fact] // boundary: 超长——JD 接近 200k 闸门时内联 Benefits 与公司名 (company-first) 仍正确解析
    public void Parse_LongJdNearGate_InlineBenefitsAndCompanyStillExtracted()
    {
        const string header = "Globex Corporation\nSenior Backend Developer\nToronto, ON\n";
        const string tail = "\nBenefits: Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.";
        var filler = new string('a', 199_000 - header.Length - tail.Length);
        var jd = header + filler + tail;
        var d = JdPasteParser.Parse(jd, PasteKind.Text);
        Assert.Equal("Globex Corporation", d.Company);
        Assert.Equal(
            "Health insurance, dental coverage, RRSP matching, and flexible working hours for all employees.",
            d.Benefits);
        Assert.Equal(jd.Length, d.JdText!.Length);
    }
}
