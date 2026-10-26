using YourInterview.Services.Knowledge.Application;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票61:UpdateKnowledge 的 Question 必填闸门(与 Create 对齐)的回归钉,
/// 兼 UpdateKnowledgeCommandValidator 既有规则的特征测试(总纲 §7.2:
/// 该校验器此前零覆盖,改行为前先锁定现状 —— 本组对 Title/Topic/Id/
/// Difficulty/Importance 的断言均按基线实现原口径,未放宽)。
///
/// 背景:详情编辑保存走全量 PUT,DETAIL_BLOCKS 无 question 块时前端恒发
/// 空串;Update 校验器原先不校验 Question,空串一路落库把已存题干清空
/// (数据丢失)。修复 = 前端回填现存值 + 本校验器补 Question NotEmpty/8000。
///
/// Boundary 类别(总纲 §7.3,逐例标注):
/// - 空/null 输入:Question null / "" / 纯空白;特征侧 Title/Topic 空、Id 空 Guid
/// - 超长上限 ±1:Question 8000 放行 / 8001 拒绝
/// - 特殊字符与 Unicode:中文 + emoji + 引号/尖括号混排题干放行
/// - 0 与负数:Difficulty/Importance 的 0、-1 拒绝,边界值 1 与 5 放行
/// - 不适用类别(本命令无对应输入面):空集合(无集合字段)、日期/时区临界
///   (无日期字段)、文件类(无文件输入)——故未设对应用例。
/// 本组只测校验器(纯规则,无 DB)。
/// </summary>
public sealed class UpdateKnowledgeValidatorTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Id = Guid.NewGuid();

    /// <summary>除被测字段外全合法的命令;单字段变异用 with 表达式。</summary>
    private static UpdateKnowledgeCommand Valid(string? question = "What is idempotency in API design?") => new(
        User, Id, "Idempotency", "Backend / API Design", null,
        question!, 3, 3, null, null, null, null, null, null, null, null);

    private static bool HasError(UpdateKnowledgeCommand cmd, string property)
        => new UpdateKnowledgeCommandValidator().Validate(cmd).Errors
            .Any(e => e.PropertyName == property);

    // ---------- 正常值(特征基线) ----------

    [Fact]
    public void ValidCommand_Passes()
    {
        Assert.Empty(new UpdateKnowledgeCommandValidator().Validate(Valid()).Errors);
    }

    // ---------- Question 新闸门:空/null 类 ----------

    [Fact] // boundary: 空/null 输入(null)
    public void Question_Null_Rejected()
        => Assert.True(HasError(Valid(question: null), nameof(UpdateKnowledgeCommand.Question)));

    [Fact] // boundary: 空/null 输入(空串 —— 票61 数据丢失的实际载荷)
    public void Question_Empty_Rejected()
        => Assert.True(HasError(Valid(question: ""), nameof(UpdateKnowledgeCommand.Question)));

    [Fact] // boundary: 空/null 输入(纯空白,NotEmpty 语义含空白)
    public void Question_Whitespace_Rejected()
        => Assert.True(HasError(Valid(question: "   "), nameof(UpdateKnowledgeCommand.Question)));

    // ---------- Question 新闸门:超长上限 ±1 类 ----------

    [Fact] // boundary: 超长上限值本身(8000,与 Create 的 MaximumLength(8000) 同口径)
    public void Question_AtMaxLength8000_Accepted()
        => Assert.False(HasError(Valid(question: new string('q', 8000)),
            nameof(UpdateKnowledgeCommand.Question)));

    [Fact] // boundary: 超长上限 +1(8001)
    public void Question_OverMaxLength8001_Rejected()
        => Assert.True(HasError(Valid(question: new string('q', 8001)),
            nameof(UpdateKnowledgeCommand.Question)));

    // ---------- Question 新闸门:特殊字符/Unicode 类 ----------

    [Fact] // boundary: 特殊字符与 Unicode(中文 + emoji + 引号/尖括号/& 混排,合法题干不得误杀)
    public void Question_UnicodeAndSpecialChars_Accepted()
        => Assert.Empty(new UpdateKnowledgeCommandValidator()
            .Validate(Valid(question: "什么是「幂等」? 🚀 <retry> & 'safety' — объясни")).Errors);

    // ---------- 与 Create 的 Question 口径对齐(票面要求) ----------

    [Theory]
    [InlineData(null)]   // boundary: 空/null
    [InlineData("")]     // boundary: 空/null
    [InlineData("   ")]  // boundary: 空/null(空白)
    public void Question_EmptyLike_Rejected_LikeCreate(string? question)
    {
        var create = new CreateKnowledgeCommand(User, "Idempotency", "Backend / API Design",
            null, question!);
        Assert.Equal(
            new CreateKnowledgeCommandValidator().Validate(create).Errors
                .Any(e => e.PropertyName == nameof(CreateKnowledgeCommand.Question)),
            HasError(Valid(question), nameof(UpdateKnowledgeCommand.Question)));
        Assert.True(HasError(Valid(question), nameof(UpdateKnowledgeCommand.Question)));
    }

    [Fact] // boundary: 超长上限 +1 —— Create 同样拒绝,口径一致
    public void Question_OverMaxLength_Rejected_LikeCreate()
    {
        var over = new string('q', 8001);
        var create = new CreateKnowledgeCommand(User, "Idempotency", "Backend / API Design",
            null, over);
        Assert.True(new CreateKnowledgeCommandValidator().Validate(create).Errors
            .Any(e => e.PropertyName == nameof(CreateKnowledgeCommand.Question)));
        Assert.True(HasError(Valid(over), nameof(UpdateKnowledgeCommand.Question)));
    }

    // ---------- 既有规则特征测试(基线口径,未改未放宽) ----------

    [Fact] // 特征 + boundary: 空/null(Title)
    public void Title_Empty_Rejected()
        => Assert.True(HasError(Valid() with { Title = "" }, nameof(UpdateKnowledgeCommand.Title)));

    [Fact] // 特征 + boundary: 空/null(Topic)
    public void Topic_Empty_Rejected()
        => Assert.True(HasError(Valid() with { Topic = "" }, nameof(UpdateKnowledgeCommand.Topic)));

    [Fact] // 特征 + boundary: 空/null(Id = Guid.Empty)
    public void Id_EmptyGuid_Rejected()
        => Assert.True(HasError(Valid() with { Id = Guid.Empty }, nameof(UpdateKnowledgeCommand.Id)));

    [Theory] // 特征 + boundary: 0 与负数、超上限(Difficulty,InclusiveBetween 1..5)
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public void Difficulty_OutOfRange_Rejected(int difficulty)
        => Assert.True(HasError(Valid() with { Difficulty = difficulty },
            nameof(UpdateKnowledgeCommand.Difficulty)));

    [Theory] // 特征 + boundary: 区间两端合法值(Difficulty)
    [InlineData(1)]
    [InlineData(5)]
    public void Difficulty_Bounds_Accepted(int difficulty)
        => Assert.Empty(new UpdateKnowledgeCommandValidator()
            .Validate(Valid() with { Difficulty = difficulty }).Errors);

    [Theory] // 特征 + boundary: 0 与负数、超上限(Importance,InclusiveBetween 1..5)
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public void Importance_OutOfRange_Rejected(int importance)
        => Assert.True(HasError(Valid() with { Importance = importance },
            nameof(UpdateKnowledgeCommand.Importance)));
}
