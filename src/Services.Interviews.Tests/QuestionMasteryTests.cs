using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>A1 题目掌握度:领域校验 + 命令校验器。</summary>
public sealed class QuestionMasteryTests
{
    private static InterviewEntry NewEntry()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Corpay", "Senior .NET Developer");
        e.AddQuestion("What is a deadlock?", "My answer");
        return e;
    }

    [Fact]
    public void NewQuestion_MasteryDefaultsToZero()
    {
        var e = NewEntry();
        e.Questions.Should().ContainSingle()
            .Which.Mastery.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SetQuestionMastery_ValidValues_Sets(int mastery)
    {
        var e = NewEntry();
        var qid = e.Questions.Single().Id;

        e.SetQuestionMastery(qid, mastery);

        e.Questions.Single().Mastery.Should().Be(mastery);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void SetQuestionMastery_OutOfRange_Throws(int mastery)
    {
        var e = NewEntry();
        var qid = e.Questions.Single().Id;

        var act = () => e.SetQuestionMastery(qid, mastery);

        act.Should().Throw<InvalidOperationException>();
        e.Questions.Single().Mastery.Should().Be(0, "越界写入不应改变原值");
    }

    [Fact]
    public void SetQuestionMastery_MissingQuestion_Throws()
    {
        var e = NewEntry();

        var act = () => e.SetQuestionMastery(Guid.NewGuid(), 2);

        act.Should().Throw<InvalidOperationException>().WithMessage("*问题不存在*");
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Validator_MasteryRange(int mastery, bool expectedValid)
    {
        var cmd = new UpdateQuestionMasteryCommand(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), mastery);

        new UpdateQuestionMasteryCommandValidator().Validate(cmd).IsValid
            .Should().Be(expectedValid);
    }
}
