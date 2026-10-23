using FluentAssertions;
using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票35(FUN-07):parse MessageCodes 组合与 CoverLetter MissingCode 选取(纯逻辑)。
/// zh 文案逐字对齐在前端 i18n 字典(parse.* / tracker.dClMissing* 键),此处锁码序与分支。
/// </summary>
public sealed class Ticket35MessageCodesTests
{
    // 常规:JD Ok 无附加 → 单码;经 AI → JdOkAi
    [Fact]
    public void JdOk_Codes()
    {
        ParseMessageCodes.ComposeJd(ParseOutcome.Ok, usedAi: false, fetchBlocked: false,
                ParseMessageCodes.AiNoteKind.None)
            .Should().Equal(ParseMessageCodes.JdOk);
        ParseMessageCodes.ComposeJd(ParseOutcome.Ok, usedAi: true, fetchBlocked: false,
                ParseMessageCodes.AiNoteKind.None)
            .Should().Equal(ParseMessageCodes.JdOkAi);
    }

    // 常规:JD Partial + fetch 受阻 + AI 超时 → 码序 主状态/fetch/AI(与改前拼接序一致)
    [Fact]
    public void JdPartial_WithNotes_CodeOrder()
    {
        ParseMessageCodes.ComposeJd(ParseOutcome.Partial, usedAi: false, fetchBlocked: true,
                ParseMessageCodes.AiNoteKind.Timeout)
            .Should().Equal(ParseMessageCodes.JdPartial, ParseMessageCodes.FetchBlocked,
                ParseMessageCodes.AiTimeout);
    }

    // boundary(空集合):Failed 且无任何附加 → 仅主状态码,非空
    [Fact]
    public void JdFailed_NoNotes_SingleCode_BoundaryEmptyNotes()
    {
        ParseMessageCodes.ComposeJd(ParseOutcome.Failed, usedAi: false, fetchBlocked: false,
                ParseMessageCodes.AiNoteKind.None)
            .Should().Equal(ParseMessageCodes.JdFailed);
    }

    // 常规:Invite Ok 不带 AI 码(改前口径);Partial 带 AI 码
    [Fact]
    public void Invite_OkDropsAiNote_PartialKeeps()
    {
        ParseMessageCodes.ComposeInvite(ParseOutcome.Ok, ParseMessageCodes.AiNoteKind.Failed)
            .Should().Equal(ParseMessageCodes.InviteOk);
        ParseMessageCodes.ComposeInvite(ParseOutcome.Partial, ParseMessageCodes.AiNoteKind.NoCredentials)
            .Should().Equal(ParseMessageCodes.InvitePartial, ParseMessageCodes.AiNoCredentials);
    }

    // boundary(未知状态串):JD/Invite 未知 status 落 Failed 码,不抛
    [Fact]
    public void UnknownStatus_FallsToFailed_BoundaryUnknown()
    {
        ParseMessageCodes.ComposeJd("???", false, false, ParseMessageCodes.AiNoteKind.None)
            .Should().Equal(ParseMessageCodes.JdFailed);
        ParseMessageCodes.ComposeInvite("???", ParseMessageCodes.AiNoteKind.None)
            .Should().Equal(ParseMessageCodes.InviteFailed);
    }

    // 常规+boundary:MissingCode 优先级 简历 > JD > 公司情报;全齐 → null
    [Fact]
    public void MissingCode_Priority_And_AllPresentNull()
    {
        CoverLetterMissing.For(false, false, false).Should().Be(CoverLetterMissing.Resume);
        CoverLetterMissing.For(true, false, false).Should().Be(CoverLetterMissing.Jd);
        CoverLetterMissing.For(true, true, false).Should().Be(CoverLetterMissing.CompanyProfile);
        CoverLetterMissing.For(true, true, true).Should().BeNull();
    }
}
