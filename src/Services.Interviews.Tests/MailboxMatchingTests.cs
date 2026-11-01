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

    // ---- 票 310：邀请收紧（阈值 + 空主题 fallback + 群发 veto）----

    [Fact]
    public void ClassifyEmail_TrueInvitation_KhalilStyle()
    {
        var body = "Hi Forrest,\nWe would like to invite you to interview for the Full Stack Developer role.\n"
            + "Your interview is on October 7, 2026 at 11:00 AM.\n"
            + "Join here: https://teams.microsoft.com/l/meetup-join/abc";
        MailboxMatching.ClassifyEmail("Interview invitation - Full Stack Developer", body).Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_OtterStyleDigest_IsOther()
    {
        var body = "Get ready for your week.\nUpcoming meetings include an interview with ExampleCorp on Monday.\n"
            + "You received this email because you subscribed to weekly digests.\n"
            + "To unsubscribe, click here.\nView in browser.";
        MailboxMatching.ClassifyEmail("Your upcoming meetings", body).Should().Be("other");
    }

    [Fact]
    public void ClassifyEmail_ChineseInvitation()
    {
        var body = "您好，诚邀您参加面试。面试时间：2026年10月13日 14:00。"
            + "会议链接：https://teams.microsoft.com/l/meetup-join/abc";
        MailboxMatching.ClassifyEmail("面试邀请", body).Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_Reschedule_StillFirst()
    {
        MailboxMatching.ClassifyEmail("Interview rescheduled",
            "We need to reschedule your interview to another time next week.").Should().Be("reschedule");
    }

    [Fact]
    public void ClassifyEmail_Rejection_StillFirst()
    {
        MailboxMatching.ClassifyEmail("Update on your application",
            "Unfortunately, we are not moving forward with your application.").Should().Be("rejection");
    }

    [Fact]
    public void ClassifyEmail_Followup_MentioningPastInterview()
    {
        MailboxMatching.ClassifyEmail("Following up",
            "Hi, just checking in on how your interview went last week.").Should().Be("followup");
    }

    [Fact]
    public void ClassifyEmail_GenericSubject_RescuedByThreshold()
    {
        var body = "We would like to invite you to interview for the role.\n"
            + "Your interview is on October 7, 2026 at 11:00 AM.\n"
            + "Join: https://teams.microsoft.com/l/meetup-join/abc";
        MailboxMatching.ClassifyEmail("Update", body).Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_SpeechActOnly_GenericSubject_IsOther()
    {
        MailboxMatching.ClassifyEmail("Update",
            "We would like to invite you to interview for the role. Details to follow.").Should().Be("other");
    }

    [Fact]
    public void ClassifyEmail_BulkDigest_WithSpeechActPhrasing_Vetoed()
    {
        // 无 veto 时 (b) 言语行为 + (c) 时间 = 2 分且 (b) 成立，本应判邀请；veto 优先 → other。
        var body = "This week's meetings:\n"
            + "- ExampleCorp: We would like to invite you to interview for the developer role on October 7, 2026 at 11:00 AM.\n"
            + "You received this email because you subscribed.\nTo unsubscribe, click here.";
        MailboxMatching.ClassifyEmail("Your upcoming meetings", body).Should().Be("other");
    }

    [Fact]
    public void ClassifyEmail_AtsInvitation_WithUnsubscribeFooter_IndicativeSubject()
    {
        var body = "We would like to invite you to interview for the role.\n"
            + "Your interview is on October 7, 2026 at 11:00 AM.\n"
            + "Join: https://teams.microsoft.com/l/meetup-join/abc\n"
            + "To unsubscribe from these alerts, click here.";
        MailboxMatching.ClassifyEmail("Interview invitation - Software Developer", body).Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_SubjectMention_WithTimeAndLink_IsInvitation()
    {
        var body = "Your interview is on October 7, 2026 at 11:00 AM.\n"
            + "Join: https://teams.microsoft.com/l/meetup-join/abc";
        MailboxMatching.ClassifyEmail("Interview - ExampleCorp", body).Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_EmptySubject_ParticipantPhrasing_Fallback()
    {
        // 票 158 体例：参与人口吻无邀约言语行为，仍走空主题 fallback 判邀请（前端同例一字对齐）。
        MailboxMatching.ClassifyEmail("",
            "I recived a email that I will take part in the screening interview via teams on 26 oct 2026")
            .Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_EmptySubject_BareSchedule_NoTrigger()
    {
        MailboxMatching.ClassifyEmail("", "Please schedule a call next week to discuss.").Should().Be("other");
    }

    [Fact]
    public void ClassifyEmail_NonEmptySubject_BareSchedule_NoTrigger()
    {
        MailboxMatching.ClassifyEmail("Schedule", "Please schedule a call next week to discuss.").Should().Be("other");
    }

    [Fact]
    public void ClassifyEmail_EmptySubject_PastedDigest_FallbackGap()
    {
        // 已知残留（票 310 台账 #5）：空主题粘贴不受群发 veto，旧关键词规则仍判邀请；
        // 本断言锁定该取舍，未来修 fallback 缺口时须同步改此断言与台账。
        MailboxMatching.ClassifyEmail("",
            "Your weekly digest.\nMeetings this week include an interview with ExampleCorp.\nTo unsubscribe, click here.")
            .Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_NonBulkDigest_WithSpeechActPhrasing_Residual()
    {
        // 已知残留（票 310 台账 #2）：无群发标记的周报、所列会议含邀约言语行为时仍误判邀请；
        // 本断言锁定该取舍，未来修此缺口时须同步改此断言与台账。
        var body = "This week's meetings:\n"
            + "- ExampleCorp: We would like to invite you to interview for the developer role on October 7, 2026 at 11:00 AM.";
        MailboxMatching.ClassifyEmail("Weekly digest", body).Should().Be("invitation");
    }

    [Fact]
    public void ClassifyEmail_IcsOnly_Residual()
    {
        // 已知残留（票 310 台账 #3）：证据只在 .ics 附件里时分类器看不到，仍判 other。
        MailboxMatching.ClassifyEmail("Update",
            "Please find the calendar invitation attached (meeting.ics). Join details are in the attachment.")
            .Should().Be("other");
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
