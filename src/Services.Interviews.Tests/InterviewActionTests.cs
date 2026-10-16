using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>A2 待办事项:聚合根 CRUD + 命令校验器(纯领域测试,不连数据库)。</summary>
public sealed class InterviewActionTests
{
    private static InterviewEntry NewEntry()
        => new(Guid.NewGuid(), "Corpay", "Senior .NET Developer");

    [Fact]
    public void AddAction_SetsFieldsAndDefaults()
    {
        var e = NewEntry();
        var due = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

        var a = e.AddAction("Follow up with recruiter", "Send thank-you email", due);

        a.Title.Should().Be("Follow up with recruiter");
        a.Detail.Should().Be("Send thank-you email");
        a.DueDate.Should().Be(due);
        a.IsDone.Should().BeFalse();
        a.InterviewEntryId.Should().Be(e.Id);
        a.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        e.Actions.Should().ContainSingle().Which.Id.Should().Be(a.Id);
    }

    [Fact]
    public void AddAction_BlankTitle_Throws()
    {
        var e = NewEntry();

        var act = () => e.AddAction("   ", null, null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*标题不能为空*");
    }

    [Fact]
    public void UpdateAction_MarkDone_OnlyTouchesIsDone()
    {
        var e = NewEntry();
        var a = e.AddAction("Practice STAR stories", "original detail", null);

        e.UpdateAction(a.Id, null, true, null);

        var updated = e.Actions.Single();
        updated.IsDone.Should().BeTrue();
        updated.Title.Should().Be("Practice STAR stories", "title 传 null 应保持不动");
        updated.Detail.Should().Be("original detail", "PATCH 不带 detail 字段,保持不动");
    }

    [Fact]
    public void UpdateAction_NewTitleAndDueDate_Applies()
    {
        var e = NewEntry();
        var a = e.AddAction("Old title", null, null);
        var due = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        e.UpdateAction(a.Id, "New title", null, due);

        var updated = e.Actions.Single();
        updated.Title.Should().Be("New title");
        updated.DueDate.Should().Be(due);
        updated.IsDone.Should().BeFalse("isDone 传 null 应保持不动");
    }

    [Fact]
    public void UpdateAction_BlankTitle_KeepsOld()
    {
        var e = NewEntry();
        var a = e.AddAction("Keep me", null, null);

        e.UpdateAction(a.Id, "  ", null, null);

        e.Actions.Single().Title.Should().Be("Keep me");
    }

    [Fact]
    public void RemoveAction_Removes()
    {
        var e = NewEntry();
        var a = e.AddAction("Temp", null, null);

        e.RemoveAction(a.Id);

        e.Actions.Should().BeEmpty();
    }

    [Fact]
    public void RemoveAction_Missing_Throws()
    {
        var e = NewEntry();

        var act = () => e.RemoveAction(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>().WithMessage("*待办不存在*");
    }

    [Fact]
    public void Validator_BlankTitle_Invalid()
    {
        var cmd = new CreateActionCommand(Guid.NewGuid(), "  ");

        new CreateActionCommandValidator().Validate(cmd).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validator_ValidCommand_Passes()
    {
        var cmd = new CreateActionCommand(Guid.NewGuid(), "Review system design",
            "Redo the photo-sharing design", DateTimeOffset.UtcNow.AddDays(3));

        new CreateActionCommandValidator().Validate(cmd).IsValid.Should().BeTrue();
    }
}
