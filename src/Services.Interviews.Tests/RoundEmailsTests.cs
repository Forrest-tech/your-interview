using System.Text.Json;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>A7 轮次邮件时间线:聚合根写入 + 命令校验器。</summary>
public sealed class RoundEmailsTests
{
    [Fact]
    public void SetRoundEmails_WritesEmailsJson()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Corpay", "Senior .NET Developer");
        var r = e.AddRound("Technical");
        var emails = new List<RoundEmailItem>
        {
            new("khalil@corpay.com", "Interview invite", "2026-10-10", "Hi Forrest, ..."),
            new("forrest@example.com", "Re: Interview invite", "2026-10-11", "Thanks!")
        };
        var json = JsonSerializer.Serialize(emails, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        e.SetRoundEmails(r.Id, json);

        var stored = e.Rounds.Single().EmailsJson;
        stored.Should().Contain("khalil@corpay.com");
        var doc = JsonDocument.Parse(stored!);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[0].GetProperty("from").GetString().Should().Be("khalil@corpay.com");
        doc.RootElement[0].GetProperty("subject").GetString().Should().Be("Interview invite");
    }

    [Fact]
    public void SetRoundEmails_MissingRound_Throws()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Corpay", "Senior .NET Developer");

        var act = () => e.SetRoundEmails(Guid.NewGuid(), "[]");

        act.Should().Throw<InvalidOperationException>().WithMessage("*轮次不存在*");
    }

    [Fact]
    public void Validator_NullEmails_Invalid()
    {
        var cmd = new UpdateRoundEmailsCommand(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), null!);

        new UpdateRoundEmailsCommandValidator().Validate(cmd).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_EmptyList_Valid()
    {
        // 空数组 = 清空时间线,是合法操作
        var cmd = new UpdateRoundEmailsCommand(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), []);

        new UpdateRoundEmailsCommandValidator().Validate(cmd).IsValid.Should().BeTrue();
    }
}
