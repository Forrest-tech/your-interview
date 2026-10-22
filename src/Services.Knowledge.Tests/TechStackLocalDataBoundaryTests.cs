using FluentAssertions;
using YourInterview.Services.Knowledge.Application;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票16(COD-10c)boundary 补测(总纲第七节):分类/复习四组校验器的边界类别 ——
/// 空/null、空集合、超长上限±1(分类名 100、批上限 1000/5000、单日计数
/// 1..100000)、特殊字符/Unicode、0 与负数、日期时区临界(月末/年末/闰日/
/// 不存在的日期)。文件类不适用(本票 API 无文件入参),理由见交付报告。
/// 校验器为纯函数,不依赖 DB。
/// </summary>
public sealed class TechStackLocalDataBoundaryTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static bool HasError<T>(FluentValidation.AbstractValidator<T> v, T cmd, string prop)
        => v.Validate(cmd).Errors.Any(e => e.PropertyName.StartsWith(prop));

    // ---------- 新增分类 ----------

    [Fact] // boundary: 空/null —— 空串/纯空白拒(NotEmpty 语义)
    public void AddCategory_EmptyOrWhitespace_Rejected()
    {
        var v = new AddCustomCategoryCommandValidator();
        HasError(v, new AddCustomCategoryCommand(User, ""), "Name").Should().BeTrue();
        HasError(v, new AddCustomCategoryCommand(User, "   "), "Name").Should().BeTrue();
    }

    [Fact] // boundary: 超长上限±1 —— 100 放行、101 拒(与 Topic 上限同口径)
    public void AddCategory_NameLengthBoundary()
    {
        var v = new AddCustomCategoryCommandValidator();
        HasError(v, new AddCustomCategoryCommand(User, new string('a', 100)), "Name").Should().BeFalse();
        HasError(v, new AddCustomCategoryCommand(User, new string('a', 101)), "Name").Should().BeTrue();
    }

    [Fact] // boundary: Unicode —— 中文+emoji 分类名合法放行
    public void AddCategory_Unicode_Accepted()
    {
        var v = new AddCustomCategoryCommandValidator();
        HasError(v, new AddCustomCategoryCommand(User, "微服务 🎯"), "Name").Should().BeFalse();
    }

    // ---------- 导入分类 ----------

    [Fact] // boundary: 空/null —— null 列表拒(且 Must 谓词 null-safe 不抛 NRE,票14 教训)
    public void ImportCategories_NullList_RejectedNoThrow()
    {
        var v = new ImportCustomCategoriesCommandValidator();
        var act = () => v.Validate(new ImportCustomCategoriesCommand(User, null!));
        act.Should().NotThrow();
        HasError(v, new ImportCustomCategoriesCommand(User, null!), "Names").Should().BeTrue();
    }

    [Fact] // boundary: 空集合 —— 空列表放行(导入 0 条是对账常态)
    public void ImportCategories_EmptyList_Accepted()
    {
        var v = new ImportCustomCategoriesCommandValidator();
        v.Validate(new ImportCustomCategoriesCommand(User, [])).IsValid.Should().BeTrue();
    }

    [Fact] // boundary: 超长上限±1 —— 恰 1000 条放行、1001 条拒;单条 101 字符拒
    public void ImportCategories_CountAndItemBoundaries()
    {
        var v = new ImportCustomCategoriesCommandValidator();
        var at = Enumerable.Range(0, 1000).Select(i => $"c{i}").ToList();
        v.Validate(new ImportCustomCategoriesCommand(User, at)).IsValid.Should().BeTrue();
        var over = Enumerable.Range(0, 1001).Select(i => $"c{i}").ToList();
        HasError(v, new ImportCustomCategoriesCommand(User, over), "Names").Should().BeTrue();
        HasError(v, new ImportCustomCategoriesCommand(User, [new string('x', 101)]), "Names").Should().BeTrue();
    }

    // ---------- 记录复习 ----------

    [Theory] // boundary: 日期临界 —— 月末/年末/闰日放行
    [InlineData("2026-01-31")]
    [InlineData("2026-12-31")]
    [InlineData("2024-02-29")]
    public void Record_ValidBoundaryDates_Accepted(string date)
    {
        var v = new RecordReviewActivityCommandValidator();
        v.Validate(new RecordReviewActivityCommand(User, date)).IsValid.Should().BeTrue();
    }

    [Theory] // boundary: 日期临界 —— 不存在日期/空/格式漂移拒
    [InlineData("2026-04-31")]  // 4 月无 31 日
    [InlineData("2026-02-29")]  // 2026 非闰年
    [InlineData("")]
    [InlineData("2026/10/22")]
    public void Record_InvalidDates_Rejected(string date)
    {
        var v = new RecordReviewActivityCommandValidator();
        HasError(v, new RecordReviewActivityCommand(User, date), "Date").Should().BeTrue();
    }

    // ---------- 导入复习 ----------

    [Fact] // boundary: 空/null —— null 列表拒且不抛;空列表放行
    public void ImportActivity_NullAndEmpty()
    {
        var v = new ImportReviewActivityCommandValidator();
        var act = () => v.Validate(new ImportReviewActivityCommand(User, null!));
        act.Should().NotThrow();
        HasError(v, new ImportReviewActivityCommand(User, null!), "Entries").Should().BeTrue();
        v.Validate(new ImportReviewActivityCommand(User, [])).IsValid.Should().BeTrue();
    }

    [Fact] // boundary: 超长上限±1 —— 恰 5000 条放行、5001 条拒
    public void ImportActivity_CountBoundary()
    {
        var v = new ImportReviewActivityCommandValidator();
        var at = Enumerable.Range(0, 5000)
            .Select(i => new ReviewActivityImportItem("2026-10-22", 1)).ToList();
        v.Validate(new ImportReviewActivityCommand(User, at)).IsValid.Should().BeTrue();
        var over = Enumerable.Range(0, 5001)
            .Select(i => new ReviewActivityImportItem("2026-10-22", 1)).ToList();
        HasError(v, new ImportReviewActivityCommand(User, over), "Entries").Should().BeTrue();
    }

    [Theory] // boundary: 0 与负数 / 超上限 —— 单日计数 0、负数、100001 拒;1 与 100000 放行
    [InlineData(0, false)]
    [InlineData(-3, false)]
    [InlineData(100001, false)]
    [InlineData(1, true)]
    [InlineData(100000, true)]
    public void ImportActivity_DailyCountBoundary(int count, bool valid)
    {
        var v = new ImportReviewActivityCommandValidator();
        var r = v.Validate(new ImportReviewActivityCommand(User,
            [new ReviewActivityImportItem("2026-10-22", count)]));
        r.IsValid.Should().Be(valid);
    }

    [Fact] // boundary: 空/null(条目内) —— 日期为 null 的条目拒,不抛异常
    public void ImportActivity_NullDateItem_Rejected()
    {
        var v = new ImportReviewActivityCommandValidator();
        var act = () => v.Validate(new ImportReviewActivityCommand(User,
            [new ReviewActivityImportItem(null, 1)]));
        act.Should().NotThrow();
        act().IsValid.Should().BeFalse();
    }

    [Fact] // boundary: Unicode/特殊字符 —— 日期字段混入特殊字符按非法日期拒(不误收)
    public void ImportActivity_DateWithSpecialChars_Rejected()
    {
        var v = new ImportReviewActivityCommandValidator();
        HasError(v, new ImportReviewActivityCommand(User,
            [new ReviewActivityImportItem("2026-10-22🎯", 1)]), "Entries").Should().BeTrue();
    }
}
