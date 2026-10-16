using FluentAssertions;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>F3 QuestionsToAskJson:可选字段语义 —— 不带不覆盖,带了才更新。</summary>
public sealed class QuestionsToAskTests
{
    private static InterviewEntry NewEntry()
        => new(Guid.NewGuid(), "Corpay", "Senior .NET Developer");

    [Fact]
    public void UpdateBasicInfo_WithoutQuestionsToAsk_KeepsExisting()
    {
        var e = NewEntry();
        e.UpdateBasicInfo("Corpay", "Senior .NET Developer", null, null, null, 1, null,
            null, null, null, null, null, questionsToAskJson: "[{\"text\":\"Q1\"}]");

        // 12 参旧式调用(不带新字段)不应清空
        e.UpdateBasicInfo("Corpay", "Senior .NET Developer", null, null, null, 1, null,
            null, null, null, null, null);

        e.QuestionsToAskJson.Should().Be("[{\"text\":\"Q1\"}]");
    }

    [Fact]
    public void UpdateBasicInfo_WithQuestionsToAsk_Updates()
    {
        var e = NewEntry();

        e.UpdateBasicInfo("Corpay", "Senior .NET Developer", null, null, null, 1, null,
            null, null, null, null, null, questionsToAskJson: "[{\"text\":\"Q2\"}]");

        e.QuestionsToAskJson.Should().Be("[{\"text\":\"Q2\"}]");
    }

    [Fact]
    public void NewEntry_QuestionsToAskDefaultsNull()
    {
        NewEntry().QuestionsToAskJson.Should().BeNull();
    }
}
