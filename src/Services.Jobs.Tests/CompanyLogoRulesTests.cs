using System.Net;
using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 150:公司 logo 回填纯规则测试 —— 零 I/O,只钉 CompanyLogoRules。
/// 关键口径与 JdPasteParser 同源处(聚合站域名表、EndsWith 后缀判定)双边钉死,
/// 两边改动时须同步。
/// </summary>
public sealed class CompanyLogoRulesTests
{
    // ---------------- LinkedIn 链接选取(§2.2 第 2 步) ----------------

    [Fact]
    public void SelectLinkedInLinks_OnlyJobsViewLinks_CreatedAtDesc()
    {
        var links = CompanyLogoRules.SelectLinkedInLinks([
            ("https://www.linkedin.com/jobs/view/111", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ("https://www.linkedin.com/company/acme", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            ("https://www.linkedin.com/jobs/view/222", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)),
            ("https://www.indeed.com/viewjob?jk=abc", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
            (null, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.Equal(
            ["https://www.linkedin.com/jobs/view/222", "https://www.linkedin.com/jobs/view/111"],
            links);
    }

    [Fact]
    public void SelectLinkedInLinks_Duplicates_Deduped()
    {
        var links = CompanyLogoRules.SelectLinkedInLinks([
            ("https://www.linkedin.com/jobs/view/111", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            ("https://www.linkedin.com/jobs/view/111", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.Single(links);
    }

    // ---------------- SmartAdd 透传决策(§2.6) ----------------

    [Fact]
    public void ResolveNewCompanyLogo_ExplicitWins_ElseWebsiteDerived()
    {
        Assert.Equal("https://cdn.example.com/logo.png",
            CompanyLogoRules.ResolveNewCompanyLogo("  https://cdn.example.com/logo.png  ", "acme.com"));
        Assert.Equal("https://www.google.com/s2/favicons?sz=128&domain=acme.com",
            CompanyLogoRules.ResolveNewCompanyLogo(null, "acme.com"));
        Assert.Null(CompanyLogoRules.ResolveNewCompanyLogo(null, null));
        // 票 163：空白显式等同未提供（与 ShouldFillExistingLogo 的 (null, "   ", false)
        // 同口径），故其次 Website 派生；旧断言期望 null 与定稿 §2.6 不一致，错在测试。
        Assert.Equal("https://www.google.com/s2/favicons?sz=128&domain=acme.com",
            CompanyLogoRules.ResolveNewCompanyLogo("   ", "acme.com"));
    }

    [Theory]
    [InlineData(null, "https://x.png", true)]   // 存量空 + 显式值 → 补
    [InlineData("", "https://x.png", true)]
    [InlineData("https://old.png", "https://x.png", false)] // 已有值绝不覆盖
    [InlineData(null, null, false)]
    [InlineData(null, "   ", false)]
    public void ShouldFillExistingLogo_OnlyEmptyPlusExplicit(string? existing, string? explicitLogo, bool expected)
        => Assert.Equal(expected, CompanyLogoRules.ShouldFillExistingLogo(existing, explicitLogo));

    // ---------------- 聚合站 guard(与 JdPasteParser 同口径) ----------------

    [Theory]
    [InlineData("https://www.linkedin.com/jobs/view/111", true)]
    [InlineData("linkedin.com", true)]
    [InlineData("https://ca.indeed.com/viewjob", true)]
    [InlineData("https://www.glassdoor.com/job", true)]
    [InlineData("https://acme.com/careers", false)]
    [InlineData("https://arvato.com", false)]
    [InlineData(null, false)]
    public void IsAggregatorWebsite_MatchesJdPasteParserHosts(string? website, bool expected)
        => Assert.Equal(expected, CompanyLogoRules.IsAggregatorWebsite(website));

    [Fact]
    public void AggregatorHosts_MatchJdPasteParserList()
    {
        // 双边同表:任一关键域名在两边行为一致(改动时须同步)。
        foreach (var host in new[] { "linkedin.com", "indeed.com", "glassdoor.com", "ziprecruiter.com",
                     "monster.com", "dice.com", "builtin.com", "otta.com", "wellfound.com",
                     "angel.co", "levels.fyi" })
        {
            Assert.True(JdPasteParser.IsAggregator($"https://{host}/x"), host);
            Assert.True(CompanyLogoRules.IsAggregatorWebsite(host), host);
        }
    }

    // ---------------- Google s2 识别与默认图标比对(§2.4) ----------------

    [Theory]
    [InlineData("https://www.google.com/s2/favicons?sz=128&domain=acme.com", true)]
    [InlineData("https://cdn.example.com/logo.png", false)]
    [InlineData(null, false)]
    public void IsGoogleFaviconUrl_OnlyS2Prefix(string? url, bool expected)
        => Assert.Equal(expected, CompanyLogoRules.IsGoogleFaviconUrl(url));

    [Fact]
    public void IsDefaultIcon_ByteEqualOnly()
    {
        Assert.True(CompanyLogoRules.IsDefaultIcon([1, 2, 3], [1, 2, 3]));
        Assert.False(CompanyLogoRules.IsDefaultIcon([1, 2, 3], [1, 2, 4]));
        Assert.False(CompanyLogoRules.IsDefaultIcon([1, 2], [1, 2, 3]));
    }

    // ---------------- favicon 发现(方式 b) ----------------

    [Fact]
    public void ExtractIconUrl_RelativeHref_ResolvedAgainstPage()
    {
        const string html = """<html><head><link rel="shortcut icon" href="/assets/favicon.ico"></head></html>""";
        Assert.Equal("https://acme.com/assets/favicon.ico",
            CompanyLogoRules.ExtractIconUrl(html, "https://acme.com/careers"));
    }

    [Fact]
    public void ExtractIconUrl_HrefBeforeRel_AlsoRecognized()
    {
        const string html = """<link href="https://cdn.acme.com/icon.png" rel="apple-touch-icon">""";
        Assert.Equal("https://cdn.acme.com/icon.png",
            CompanyLogoRules.ExtractIconUrl(html, "https://acme.com/"));
    }

    [Theory]
    [InlineData("""<link rel="stylesheet" href="/x.css">""")]
    [InlineData("""<link rel="icon" href="javascript:alert(1)">""")]
    [InlineData("""<link rel="icon">""")]
    public void ExtractIconUrl_NonIconOrUnsafe_Null(string html)
        => Assert.Null(CompanyLogoRules.ExtractIconUrl(html, "https://acme.com/"));

    // ---------------- SSRF 校验(§2.5 / A-150-10) ----------------

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    public void IsPrivateOrReservedIp_PrivateRanges(string ip, bool expected)
        => Assert.Equal(expected, CompanyLogoRules.IsPrivateOrReservedIp(IPAddress.Parse(ip)));

    [Fact]
    public async Task IsSafeHostAsync_MixedOrFailedResolution_Refused()
    {
        // 任一解析地址命中内网段即拒绝(防 DNS 重绑定式混杂结果)。
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("x.example", default,
            _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.1.2.3") })));
        Assert.True(await CompanyLogoRules.IsSafeHostAsync("x.example", default,
            _ => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") })));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("x.example", default,
            _ => throw new Exception("dns down")));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("10.2.3.4", default));
        Assert.False(await CompanyLogoRules.IsSafeHostAsync("", default));
    }
}
