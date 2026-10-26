using System.Net;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Infrastructure.Services;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票142:公司简介抓取器纯函数 + 超长公司段完整提取。
/// 抓取腿本身需出站网络(沙盒禁出站),不做活体断言;活体链路以 CI + Mac 实测为准。
/// </summary>
public sealed class CompanyProfileFetchTests
{
    // ---------- Wikipedia 标题匹配 ----------

    [Theory]
    [InlineData("Arvato", "Arvato", true)]
    [InlineData("arvato", "Arvato", true)]
    [InlineData("Arvato", "Arvato (company)", true)]
    [InlineData("Arvato", "Arvato (Company)", true)]
    [InlineData("Arvato Systems", "Arvato Systems (company)", true)]
    [InlineData("Arvato", "Arvato Systems", false)] // 不猜测:不等即未找到
    [InlineData("Arvato", "Arvato AG", false)]
    [InlineData("Arvato", "Bertelsmann", false)]
    public void TitlesMatch_ExactOrDisambiguationSuffixOnly(string company, string title, bool expected)
    {
        Assert.Equal(expected, CompanyProfileFetcher.TitlesMatch(company, title));
    }

    // ---------- robots Disallow ----------

    [Theory]
    [InlineData("/about", "/about", true)]
    [InlineData("/about", "/about-us", true)] // 前缀匹配:/about 禁则 /about-us 同禁
    [InlineData("/about/", "/about", false)]
    [InlineData("/about$", "/about", true)]   // $ 精确匹配
    [InlineData("/about$", "/about-us", false)]
    [InlineData("/private", "/about", false)]
    public void IsPathDisallowed_Rules(string rule, string path, bool expected)
    {
        Assert.Equal(expected, CompanyProfileFetcher.IsPathDisallowed([rule], path));
    }

    // ---------- 内网/保留 IP 拒绝 ----------

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("172.32.0.1", true)] // 172.16/12 之外即公开
    public void IsPublicAddress_Ipv4Ranges(string ip, bool expected)
    {
        Assert.Equal(expected, CompanyProfileFetcher.IsPublicAddress(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void IsPublicAddress_Ipv6Ranges(string ip, bool expected)
    {
        Assert.Equal(expected, CompanyProfileFetcher.IsPublicAddress(IPAddress.Parse(ip)));
    }

    // ---------- 票142 R-142-9:超长公司段不再截断 ----------

    [Fact]
    public void Parse_LongCompanyDescription_ReturnedInFullWithoutEllipsis()
    {
        // 构造超 8000 字的公司段(每句约 50 字 × 200 句)。
        var sentence = "This company builds global supply chain and logistics platforms for brands worldwide. ";
        var longBody = string.Concat(Enumerable.Repeat(sentence, 200));
        Assert.True(longBody.Length > 8000);
        var raw = "Senior .NET Developer\n\nAbout the company\n" + longBody + "\n\nAbout the job\nBuild things.";
        var d = JdPasteParser.Parse(raw, PasteKind.Text);
        Assert.NotNull(d.CompanyDescription);
        Assert.DoesNotContain("…", d.CompanyDescription);
        Assert.Equal(longBody.Trim(), d.CompanyDescription);
    }

    [Fact]
    public void Parse_ShortCompanyDescription_StillRequires20Chars()
    {
        var raw = "Role\n\nAbout the company\nToo short.\n\nAbout the job\nBuild things.";
        var d = JdPasteParser.Parse(raw, PasteKind.Text);
        Assert.Null(d.CompanyDescription);
    }
}
