using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 204：Company Overview 标题双轨认领（后端治本轨）。
/// ExtractCompanyDescription 认 "Company Overview"（容忍尾冒号）；KnownSectionHeaders
/// 同步加项，使小节提取器也能产出该节（与字段产出各自独立、分开验收）。
/// 夹具全合成，零真实数据。
/// </summary>
public sealed class Ticket204CompanyOverviewTests
{
    private const string Intro =
        "Acme is a global technology company building software platforms for customers worldwide.";
    private const string Job =
        "We are hiring a Senior Developer to build great software products for our customers.";

    [Fact]
    public void Parse_CompanyOverviewSection_ReturnsDescriptionWithoutJobOverview()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nCompany Overview\n" + Intro + "\nJob Overview\n" + Job,
            PasteKind.Text);
        Assert.Equal(Intro, d.CompanyDescription);
        Assert.DoesNotContain("hiring a Senior Developer", d.CompanyDescription);
    }

    [Fact]
    public void Parse_CompanyOverviewWithColon_ReturnsDescription()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nCompany Overview:\n" + Intro + "\nJob Overview\n" + Job,
            PasteKind.Text);
        Assert.Equal(Intro, d.CompanyDescription);
    }

    [Fact]
    public void Parse_WithoutCompanyTitle_ReturnsNullDescription()
    {
        var d = JdPasteParser.Parse(
            "Senior Developer\nAcme Corp\nToronto, ON\nJob Overview\n" + Job,
            PasteKind.Text);
        Assert.Null(d.CompanyDescription);
    }

    [Fact]
    public void ExtractSections_CompanyOverview_ProducesSection()
    {
        var lines = new[] { "Senior Developer", "Company Overview", Intro, "Job Overview", Job };
        var sections = JdPasteParser.ExtractSections(lines);
        var section = Assert.Single(sections, s => s.Title == "Company Overview");
        Assert.Equal(Intro, section.Body);
    }
}
