
using System.Linq;
using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 223：页脚区公司介绍类标题不再成节（Inceed 式：无 main/article、页脚裸 About Us）。
/// 规则：页脚 chrome 簇（10 行内出现第 2 行 chrome）首次出现之后，公司介绍类标题
/// （about us / who we are / our company / about the company）不再成节；其余标题仍成节，
/// 已开小节的正文收集一字不动。Tracker 铁律：去噪可以，编造不行 —— 本文件只删节，
/// 不改任何正文行。
/// </summary>
public class Ticket223FooterSectionsTests
{
    [Fact]
    public void FooterZone_CompanyIntroAfterChromeCluster_NotASection()
    {
        const string doc = """
            Requirements
            5+ years of professional software engineering experience.
            Benefits
            Comprehensive benefits package.
            #LI-Remote
            #IND
            To Apply for this Job Click Here
            I want more jobs Like this in My inbox.
            Share This Job
            Apply Now
            About Us
            Inceed is a staffing and recruiting team full of people-loving people.
            """;
        var d = JdPasteParser.Parse(doc, PasteKind.Text);
        Assert.Equal(new[] { "Requirements", "Benefits" },
            d.Sections!.Select(s => s.Title).ToArray());
        // 真实小节正文一字不动：Benefits 正文仍含 chrome 行（本票只拦新节开始，不截正文）。
        Assert.Contains("Comprehensive benefits package.", d.Sections[1].Body);
        Assert.DoesNotContain("Inceed is a staffing", d.Sections[1].Body);
    }

    [Fact]
    public void FooterZone_RealAboutUsBeforeChrome_StillASection()
    {
        const string doc = """
            About Us
            We build tools for developers.
            Requirements
            5+ years of professional software engineering experience.
            #LI-Remote
            #IND
            Share This Job
            """;
        var d = JdPasteParser.Parse(doc, PasteKind.Text);
        Assert.Equal(new[] { "About Us", "Requirements" },
            d.Sections!.Select(s => s.Title).ToArray());
    }

    [Fact]
    public void FooterZone_LoneChromeLine_NoCut()
    {
        // 单行 chrome 不成簇（10 行内无第 2 行 chrome），不截断 —— 避免正文中的
        // 孤立按钮残留误杀其后的真实小节。
        const string doc = """
            Requirements
            5+ years of professional software engineering experience.
            Share This Job
            About Us
            We build tools for developers.
            """;
        var d = JdPasteParser.Parse(doc, PasteKind.Text);
        Assert.Equal(new[] { "Requirements", "About Us" },
            d.Sections!.Select(s => s.Title).ToArray());
    }

    [Fact]
    public void FooterZone_NonIntroTitleAfterChrome_StillForms()
    {
        // 页脚区内只拦公司介绍类标题；其余标题仍成节（保守口径，不泛杀）。
        const string doc = """
            Requirements
            5+ years of professional software engineering experience.
            #LI-Remote
            #IND
            To Apply for this Job Click Here
            Benefits
            Comprehensive benefits package.
            """;
        var d = JdPasteParser.Parse(doc, PasteKind.Text);
        Assert.Equal(new[] { "Requirements", "Benefits" },
            d.Sections!.Select(s => s.Title).ToArray());
    }
}
