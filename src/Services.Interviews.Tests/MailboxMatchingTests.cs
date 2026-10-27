using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 240：邮箱匹配规则纯函数测试（方案 §2.1/§2.2）。
/// 纯函数，不依赖 DB 与网络。夹具全合成（虚构公司域与发件人，不含任何真实邮箱/链接）。
/// </summary>
public sealed class MailboxMatchingTests
{
    private static readonly Guid EntryA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EntryB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static MailboxMatching.EntryRules RuleA(
        string domains = "[\"examplecorp.com\"]", string contacts = "[]",
        string keywords = "[\"examplecorp\"]", string exclusions = "[]") =>
        MailboxMatching.ParseRules(EntryA, domains, contacts, keywords, exclusions);

    private static MailboxMatching.EntryRules RuleB(
        string domains = "[\"othercorp.com\"]", string contacts = "[]",
        string keywords = "[\"othercorp\"]", string exclusions = "[]") =>
        MailboxMatching.ParseRules(EntryB, domains, contacts, keywords, exclusions);

    // ---- 强信号 A：公司域 ----

    [Fact]
    public void FromDomain_MatchesStrong()
    {
        var r = MailboxMatching.Match("recruiter@examplecorp.com", [], "Hello", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().Be("StrongDomain");
        r.IsStrong.Should().BeTrue();
        r.EntryId.Should().Be(EntryA);
    }

    [Fact]
    public void ToDomain_MatchesStrong()
    {
        var r = MailboxMatching.Match("someone@elsewhere.com", ["me@examplecorp.com"], "Hello", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().Be("StrongDomain");
        r.EntryId.Should().Be(EntryA);
    }

    [Fact]
    public void TwoDomainHits_IsAmbiguous_NotGuessed()
    {
        var r = MailboxMatching.Match("a@examplecorp.com", ["me@othercorp.com"], "Hello", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().Be("Ambiguous");
        r.IsStrong.Should().BeFalse();
        r.EntryId.Should().BeNull();
        r.CandidateEntryIds.Should().BeEquivalentTo([EntryA, EntryB]);
    }

    // ---- 强信号 B：已知往来人 ----

    [Fact]
    public void KnownContact_MatchesStrong()
    {
        var maps = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["recruiter@talent-agency.com"] = EntryB
        };
        var r = MailboxMatching.Match("recruiter@talent-agency.com", [], "Hello", "Body",
            [RuleA(), RuleB()], maps);
        r.Signal.Should().Be("StrongContact");
        r.EntryId.Should().Be(EntryB);
    }

    // ---- 排除优先 ----

    [Fact]
    public void ExcludedSender_NeverMatches()
    {
        var ruleA = RuleA(exclusions: "[\"spam@examplecorp.com\"]");
        var r = MailboxMatching.Match("spam@examplecorp.com", [], "Examplecorp interview", "Body",
            [ruleA, RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().BeNull();
    }

    // ---- 弱信号 C/D ----

    [Fact]
    public void CompanyNameInSubject_IsWeakPending()
    {
        var r = MailboxMatching.Match("newsletter@random.com", [], "Examplecorp interview update", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().Be("WeakCompanyName");
        r.IsStrong.Should().BeFalse();
        r.EntryId.Should().Be(EntryA);
    }

    [Fact]
    public void PlatformSender_WithCompanyName_IsWeakPlatform()
    {
        var r = MailboxMatching.Match("noreply@indeed.com", [], "Examplecorp is hiring", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().Be("WeakPlatform");
        r.EntryId.Should().Be(EntryA);
    }

    [Fact]
    public void PlatformSender_WithoutCompanyName_IsNoSignal()
    {
        var r = MailboxMatching.Match("noreply@indeed.com", [], "Jobs you may like", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().BeNull();
    }

    [Fact]
    public void TwoKeywordHits_IsAmbiguous()
    {
        var r = MailboxMatching.Match("news@random.com", [], "Examplecorp and Othercorp news", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().Be("Ambiguous");
        r.CandidateEntryIds.Should().BeEquivalentTo([EntryA, EntryB]);
    }

    [Fact]
    public void NoSignal_ReturnsNull()
    {
        var r = MailboxMatching.Match("friend@random.com", [], "Weekend plans", "Body",
            [RuleA(), RuleB()], new Dictionary<string, Guid>());
        r.Signal.Should().BeNull();
    }

    // ---- 归一化与分类（前端同名函数直译） ----

    [Fact]
    public void NormalizeThreadKey_StripsPrefixes()
    {
        MailboxMatching.NormalizeThreadKey("Re: Fwd: Hello World").Should().Be("hello world");
        MailboxMatching.NormalizeThreadKey("回复：面试安排").Should().Be("面试安排");
        MailboxMatching.NormalizeThreadKey("  Hello   World  ").Should().Be("hello world");
    }

    [Fact]
    public void ClassifyEmail_PriorityOrder()
    {
        MailboxMatching.ClassifyEmail("Interview rescheduled", "new time").Should().Be("reschedule");
        MailboxMatching.ClassifyEmail("Update", "unfortunately we are not moving forward").Should().Be("rejection");
        MailboxMatching.ClassifyEmail("Interview invitation", "please confirm").Should().Be("invitation");
        MailboxMatching.ClassifyEmail("Following up", "checking in").Should().Be("followup");
        MailboxMatching.ClassifyEmail("Newsletter", "hello").Should().Be("other");
    }

    [Fact]
    public void ExtractMeetingLink_PicksFirstMeetingLink()
    {
        var body = "see https://example.com/page and join https://teams.microsoft.com/l/meetup-join/abc.";
        MailboxMatching.ExtractMeetingLink(body).Should().Be("https://teams.microsoft.com/l/meetup-join/abc");
        MailboxMatching.ExtractMeetingLink("no links here").Should().BeNull();
    }

    [Fact]
    public void ExtractInterviewWhen_ThreePatterns_AndInvalidDate()
    {
        MailboxMatching.ExtractInterviewWhen("面试时间：2026年10月13日 14:00").Should().Be("2026-10-13 14:00");
        MailboxMatching.ExtractInterviewWhen("on 2026-10-13 10:00 please join").Should().Be("2026-10-13 10:00");
        MailboxMatching.ExtractInterviewWhen("Oct 7, 2026 at 11:00 AM").Should().Be("2026-10-07 11:00");
        // 2026 不是闰年：2 月 29 日非法 → null（不续试后续路）。
        MailboxMatching.ExtractInterviewWhen("2026年2月29日 10:00").Should().BeNull();
        MailboxMatching.ExtractInterviewWhen("no date here").Should().BeNull();
    }

    [Fact]
    public void ParseRules_NormalizesDomains()
    {
        var r = MailboxMatching.ParseRules(EntryA, "[\"WWW.ExampleCorp.COM\"]", "[\"A@X.COM\"]", "[]", "[]");
        r.Domains.Should().BeEquivalentTo(["examplecorp.com"]);
        r.Contacts.Should().BeEquivalentTo(["a@x.com"]);
    }
}
