using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票107:校验器 WithMessage 文案拆分的回归钉。
/// 背景:FluentValidation 的 WithMessage 只挂紧邻的前一条规则 —— 旧写法
/// `NotEmpty().MaximumLength(N).WithMessage("XX必填")` 把必填文案挂在了
/// 长度规则上:超限回「必填」(误导 API 直调方),空值反而只回英文默认文案。
/// 修法 = 两条规则各挂各的文案:NotEmpty 保留原必填文案逐字,MaximumLength
/// 挂「…长度不能超过 {MaxLength} 个字符」(占位符展开为上限数字)。
/// 本组逐字段钉两路:空值 → 必填文案原文;超限 → 含上限数字的长度文案;
/// 上限整值 → 该字段无错误(规则/上限未动)。本组只测校验器(纯规则,无 DB)。
/// </summary>
public sealed class ValidatorMessageSplitTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static string S(char c, int n) => new(c, n);

    private static string[] ErrorsFor(FluentValidation.Results.ValidationResult r, string property)
        => r.Errors.Where(e => e.PropertyName == property).Select(e => e.ErrorMessage).ToArray();

    // ---------- CreateCompany.Name(300) ----------

    [Fact] // 空值 → 必填文案
    public void CompanyName_Empty_RequiredMessage()
        => Assert.Equal(new[] { "公司名称必填" },
            ErrorsFor(new CreateCompanyCommandValidator().Validate(
                new CreateCompanyCommand(User, "", null, null, null, null, null)), "Name"));

    [Fact] // 超限 +1 → 长度文案(含上限数字)
    public void CompanyName_OverLimit_LengthMessage()
        => Assert.Equal(new[] { "公司名称长度不能超过 300 个字符" },
            ErrorsFor(new CreateCompanyCommandValidator().Validate(
                new CreateCompanyCommand(User, S('公', 301), null, null, null, null, null)), "Name"));

    [Fact] // 上限整值 → 放行
    public void CompanyName_AtLimit_Passes()
        => Assert.Empty(ErrorsFor(new CreateCompanyCommandValidator().Validate(
            new CreateCompanyCommand(User, S('公', 300), null, null, null, null, null)), "Name"));

    // ---------- CreateApplication.Role(300) ----------

    private static CreateApplicationCommand App(string role) => new(
        User, Guid.NewGuid(), role, null, null, null, null, null, null, JdText: null);

    [Fact]
    public void AppRole_Empty_RequiredMessage()
        => Assert.Equal(new[] { "岗位名称必填" },
            ErrorsFor(new CreateApplicationCommandValidator().Validate(App("")), "Role"));

    [Fact]
    public void AppRole_OverLimit_LengthMessage()
        => Assert.Equal(new[] { "岗位名称长度不能超过 300 个字符" },
            ErrorsFor(new CreateApplicationCommandValidator().Validate(App(S('岗', 301))), "Role"));

    // ---------- CreateAnswerTemplate.Category(100) / Question(500) / Answer(20000) ----------

    private static CreateAnswerTemplateCommand Template(string category = "c", string question = "q", string answer = "a")
        => new(User, category, question, answer);

    [Fact]
    public void TemplateCategory_TwoWays()
    {
        var v = new CreateAnswerTemplateCommandValidator();
        Assert.Equal(new[] { "分类必填" }, ErrorsFor(v.Validate(Template(category: "")), "Category"));
        Assert.Equal(new[] { "分类长度不能超过 100 个字符" },
            ErrorsFor(v.Validate(Template(category: S('c', 101))), "Category"));
    }

    [Fact]
    public void TemplateQuestion_TwoWays()
    {
        var v = new CreateAnswerTemplateCommandValidator();
        Assert.Equal(new[] { "问题必填" }, ErrorsFor(v.Validate(Template(question: "")), "Question"));
        Assert.Equal(new[] { "问题长度不能超过 500 个字符" },
            ErrorsFor(v.Validate(Template(question: S('题', 501))), "Question"));
    }

    [Fact]
    public void TemplateAnswer_TwoWays()
    {
        var v = new CreateAnswerTemplateCommandValidator();
        Assert.Equal(new[] { "答案必填" }, ErrorsFor(v.Validate(Template(answer: "")), "Answer"));
        Assert.Equal(new[] { "答案长度不能超过 20000 个字符" },
            ErrorsFor(v.Validate(Template(answer: S('a', 20001))), "Answer"));
    }

    // ---------- CreateCommunication.Content(20000) ----------

    private static CreateCommunicationCommand Comm(string content) => new(
        User, Guid.NewGuid(), "Email", null, content, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void CommContent_TwoWays()
    {
        var v = new CreateCommunicationCommandValidator();
        Assert.Equal(new[] { "内容必填" }, ErrorsFor(v.Validate(Comm("")), "Content"));
        Assert.Equal(new[] { "内容长度不能超过 20000 个字符" },
            ErrorsFor(v.Validate(Comm(S('c', 20001))), "Content"));
    }

    // ---------- AddApplicationQuestion.Text(4000,票75 主项) ----------

    [Fact]
    public void QuestionText_TwoWays()
    {
        var v = new AddApplicationQuestionCommandValidator();
        Assert.Equal(new[] { "问题必填" },
            ErrorsFor(v.Validate(new AddApplicationQuestionCommand(User, Guid.NewGuid(), "")), "Text"));
        Assert.Equal(new[] { "问题长度不能超过 4000 个字符" },
            ErrorsFor(v.Validate(new AddApplicationQuestionCommand(User, Guid.NewGuid(), S('题', 4001))), "Text"));
        Assert.Empty(ErrorsFor(v.Validate(
            new AddApplicationQuestionCommand(User, Guid.NewGuid(), S('题', 4000))), "Text"));
    }
}
