using YourInterview.Services.Knowledge.Application;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票107:校验器 WithMessage 文案拆分的回归钉(口径同 Jobs 侧同名测试)。
/// RenameTopic / AddCustomCategory 原合并句「分类名必填且不超过 100 字符」实际
/// 只挂在 MaximumLength 上(空值回英文默认)。修后拆开:NotEmpty 挂「分类名
/// 必填」(必填半句逐字),MaximumLength 挂「分类名长度不能超过 {MaxLength}
/// 个字符」。本组只测校验器(纯规则,无 DB)。
/// </summary>
public sealed class ValidatorMessageSplitTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static string S(char c, int n) => new(c, n);

    private static string[] ErrorsFor(FluentValidation.Results.ValidationResult r, string property)
        => r.Errors.Where(e => e.PropertyName == property).Select(e => e.ErrorMessage).ToArray();

    [Fact]
    public void RenameTopicNewName_TwoWays()
    {
        var v = new RenameTopicCommandValidator();
        Assert.Equal(new[] { "分类名必填" },
            ErrorsFor(v.Validate(new RenameTopicCommand(User, "old", "")), "NewName"));
        Assert.Equal(new[] { "分类名长度不能超过 100 个字符" },
            ErrorsFor(v.Validate(new RenameTopicCommand(User, "old", S('类', 101))), "NewName"));
        Assert.Empty(ErrorsFor(v.Validate(new RenameTopicCommand(User, "old", S('类', 100))), "NewName"));
    }

    [Fact]
    public void AddCustomCategoryName_TwoWays()
    {
        var v = new AddCustomCategoryCommandValidator();
        Assert.Equal(new[] { "分类名必填" },
            ErrorsFor(v.Validate(new AddCustomCategoryCommand(User, "")), "Name"));
        Assert.Equal(new[] { "分类名长度不能超过 100 个字符" },
            ErrorsFor(v.Validate(new AddCustomCategoryCommand(User, S('类', 101))), "Name"));
    }
}
