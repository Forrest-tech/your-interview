using YourInterview.Services.Knowledge.Application;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票109:ImportInterviewCandidatesCommand 此前无校验器,qa-import /
/// prep-report 入库链的字段只有 EF 硬限 —— 超限穿透 DB 回 500。
/// 新校验器只补长度(上限 = KnowledgeItem 的 EF 映射逐字):
/// 命令级 Company≤300 / RoundStage≤60;行级 Title≤500 / Topic≤100 /
/// SubTopic≤200 / Question≤8000 / TagsJson≤1000 / ClientKey≤200。
/// 逐字段断言超限(+1)拒绝且文案为票107 定版长度文案、整值放行;
/// 守恒断言:不加 NotEmpty(空行由 Handler 跳过)、text 字段不设限。
/// </summary>
public sealed class ImportCandidatesValidatorTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static InterviewCandidateRow Row() => new(
        "What is a record?", "Backend", "What is a record in C#?");

    private static ImportInterviewCandidatesCommand Cmd(
        IReadOnlyList<InterviewCandidateRow>? items = null,
        string? company = "Acme", string? roundStage = "HR Screen") => new(
        User, Guid.NewGuid(), company, new DateOnly(2026, 10, 20), 1, roundStage,
        null, items ?? new[] { Row() });

    private static List<FluentValidation.Results.ValidationFailure> Errors(
        ImportInterviewCandidatesCommand cmd)
        => new ImportInterviewCandidatesCommandValidator().Validate(cmd).Errors.ToList();

    private static void AssertLengthError(ImportInterviewCandidatesCommand cmd,
        string property, string message)
    {
        var e = Assert.Single(Errors(cmd), x => x.PropertyName == property);
        Assert.Equal(message, e.ErrorMessage);
    }

    private static void AssertNoError(ImportInterviewCandidatesCommand cmd, string property)
        => Assert.DoesNotContain(Errors(cmd), x => x.PropertyName == property);

    [Fact]
    public void ValidCommand_Passes() => Assert.Empty(Errors(Cmd()));

    // ---------- 命令级:Company ≤300 / RoundStage ≤60 ----------
    [Fact] // boundary: 上限 +1
    public void Company_301_Rejected()
        => AssertLengthError(Cmd(company: new string('c', 301)),
            "Company", "公司名称长度不能超过 300 个字符");

    [Fact] // boundary: 上限整值
    public void Company_300_Accepted()
        => AssertNoError(Cmd(company: new string('c', 300)), "Company");

    [Fact]
    public void RoundStage_61_Rejected()
        => AssertLengthError(Cmd(roundStage: new string('r', 61)),
            "RoundStage", "轮次阶段长度不能超过 60 个字符");

    [Fact]
    public void RoundStage_60_Accepted()
        => AssertNoError(Cmd(roundStage: new string('r', 60)), "RoundStage");

    // ---------- 行级(错误键 Items[0].<字段>) ----------
    [Fact]
    public void Title_501_Rejected()
        => AssertLengthError(Cmd(items: new[] { Row() with { Title = new string('x', 501) } }),
            "Items[0].Title", "标题长度不能超过 500 个字符");

    [Fact]
    public void Title_500_Accepted()
        => AssertNoError(Cmd(items: new[] { Row() with { Title = new string('x', 500) } }),
            "Items[0].Title");

    [Fact]
    public void Topic_101_Rejected()
        => AssertLengthError(Cmd(items: new[] { Row() with { Topic = new string('t', 101) } }),
            "Items[0].Topic", "分类长度不能超过 100 个字符");

    [Fact]
    public void Topic_100_Accepted()
        => AssertNoError(Cmd(items: new[] { Row() with { Topic = new string('t', 100) } }),
            "Items[0].Topic");

    [Fact]
    public void SubTopic_201_Rejected()
        => AssertLengthError(Cmd(items: new[] { Row() with { SubTopic = new string('s', 201) } }),
            "Items[0].SubTopic", "子分类长度不能超过 200 个字符");

    [Fact]
    public void SubTopic_200_Accepted()
        => AssertNoError(Cmd(items: new[] { Row() with { SubTopic = new string('s', 200) } }),
            "Items[0].SubTopic");

    [Fact]
    public void Question_8001_Rejected()
        => AssertLengthError(Cmd(items: new[] { Row() with { Question = new string('q', 8001) } }),
            "Items[0].Question", "问题长度不能超过 8000 个字符");

    [Fact]
    public void Question_8000_Accepted()
        => AssertNoError(Cmd(items: new[] { Row() with { Question = new string('q', 8000) } }),
            "Items[0].Question");

    [Fact]
    public void TagsJson_1001_Rejected()
        => AssertLengthError(Cmd(items: new[] { Row() with { TagsJson = new string('g', 1001) } }),
            "Items[0].TagsJson", "标签长度不能超过 1000 个字符");

    [Fact]
    public void TagsJson_1000_Accepted()
        => AssertNoError(Cmd(items: new[] { Row() with { TagsJson = new string('g', 1000) } }),
            "Items[0].TagsJson");

    [Fact]
    public void ClientKey_201_Rejected()
        => AssertLengthError(Cmd(items: new[] { Row() with { ClientKey = new string('k', 201) } }),
            "Items[0].ClientKey", "导入键长度不能超过 200 个字符");

    [Fact]
    public void ClientKey_200_Accepted()
        => AssertNoError(Cmd(items: new[] { Row() with { ClientKey = new string('k', 200) } }),
            "Items[0].ClientKey");

    // ---------- 守恒:只补长度,不改既有语义 ----------
    [Fact] // 空 Title/Topic 行由 Handler 跳过 —— 校验器不得新增 NotEmpty 把整单变 400
    public void EmptyTitleTopicRow_NotRejectedByValidator()
        => Assert.Empty(Errors(Cmd(items: new[] { Row() with { Title = "", Topic = "" } })));

    [Fact] // text 字段(EF 无上限)不得误设限
    public void TextFields_VeryLong_Accepted()
        => Assert.Empty(Errors(Cmd(items: new[]
        {
            Row() with
            {
                ConceptExplanation = new string('c', 50000),
                BetterAnswer = new string('b', 50000),
                KeyPointsJson = new string('p', 50000)
            }
        })));

    [Fact] // 可空字段全 null 合法(与基线 Handler 行为一致)
    public void NullOptionals_Accepted()
        => Assert.Empty(Errors(Cmd(
            items: new[] { Row() with { SubTopic = null, TagsJson = null, ClientKey = null } },
            company: null, roundStage: null)));
}
