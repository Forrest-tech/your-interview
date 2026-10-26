using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票227：抽取结果会议元数据过滤 —— 垃圾（会议信息成题、Asked by 为 Join）被挡，真实题目通过。
/// 纯函数测试，不依赖 DB 与 AI。夹具全合成，不含任何真实会议链接/ID/密码。
/// </summary>
public sealed class QuestionExtractFilterTests
{
    // ---- 应丢弃：会议元数据 ----

    [Fact]
    public void AskedByJoin_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Some meeting info text", "Join").Should().BeTrue();
    }

    [Fact]
    public void AskedByJoinCaseInsensitive_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Some meeting info text", "JOIN").Should().BeTrue();
    }

    [Fact]
    public void AskedByJoinMeeting_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Some meeting info text", "Join Meeting").Should().BeTrue();
    }

    [Fact]
    public void AskedByChineseJoin_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Some meeting info text", "加入会议").Should().BeTrue();
    }

    [Fact]
    public void AskedByUrl_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Some meeting info text", "https://example.com/join").Should().BeTrue();
    }

    [Fact]
    public void QuestionWithTeamsLink_IsDropped()
    {
        // 域名为真平台域名、路径为明显合成占位，非真实会议链接。
        QuestionExtractFilter.IsMeetingMetadata(
            "Join the call at https://teams.microsoft.com/synthetic", "Khalil").Should().BeTrue();
    }

    [Fact]
    public void QuestionWithZoomLink_IsDropped()
    {
        // 全零号码为明显合成占位，非真实会议号。
        QuestionExtractFilter.IsMeetingMetadata(
            "See https://zoom.us/j/000000000 for details", "Khalil").Should().BeTrue();
    }

    [Fact]
    public void QuestionWithMeetingId_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Meeting ID: 000 000 000", "Khalil").Should().BeTrue();
    }

    [Fact]
    public void QuestionWithPasscode_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Passcode: 000000", "Khalil").Should().BeTrue();
    }

    [Fact]
    public void QuestionWithDialIn_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Dial-in number: +1 000 000 0000", "Khalil").Should().BeTrue();
    }

    [Fact]
    public void QuestionWithChineseMeetingLink_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("参会链接如下，请准时参加", "Khalil").Should().BeTrue();
    }

    [Fact]
    public void QuestionStartingWithJoinMeeting_IsDropped()
    {
        QuestionExtractFilter.IsMeetingMetadata("Join Microsoft Teams Meeting", "Khalil").Should().BeTrue();
    }

    // ---- 应保留：真实题目 ----

    [Fact]
    public void RealTechnicalQuestion_IsKept()
    {
        QuestionExtractFilter.IsMeetingMetadata(
            "What is the difference between TCP and UDP?", "Khalil").Should().BeFalse();
    }

    [Fact]
    public void ZoomMeetingsExperienceQuestion_IsKept()
    {
        // 复数 meetings 不命中“zoom meeting”类物流结构，不得误杀。
        QuestionExtractFilter.IsMeetingMetadata(
            "Have you used Zoom meetings before?", "Khalil").Should().BeFalse();
    }

    [Fact]
    public void JoinTeamQuestion_IsKept()
    {
        // 问句以 How 开头、非 Join 指令，不得误杀。
        QuestionExtractFilter.IsMeetingMetadata(
            "How do I join the team?", "Khalil").Should().BeFalse();
    }

    [Fact]
    public void ChineseQuestionWithoutQuestionMark_IsKept()
    {
        QuestionExtractFilter.IsMeetingMetadata("你怎么看微服务架构", "面试官").Should().BeFalse();
    }

    [Fact]
    public void DocLinkQuestion_IsKept()
    {
        // 非会议平台链接不是会议元数据。
        QuestionExtractFilter.IsMeetingMetadata(
            "What does the code at https://example.com/docs do?", "Khalil").Should().BeFalse();
    }

    [Fact]
    public void PersonAskedBy_IsKept()
    {
        QuestionExtractFilter.IsMeetingMetadata(
            "Tell me about a challenging project.", "Hajir").Should().BeFalse();
    }

    [Fact]
    public void PasscodePolicyQuestion_IsKept()
    {
        // 裸词 passcode 出现在真实安全提问中不得误杀；只有标签+取值形态才算物流。
        QuestionExtractFilter.IsMeetingMetadata(
            "How do you handle passcodes in production?", "Khalil").Should().BeFalse();
    }
}
