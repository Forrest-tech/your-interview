using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票54(ARC-08b):Assessment 练习类别 Handler 行为测试 —— 建/改名/删/重排/
/// 首访播种,经真实持久化 seam 测公开行为(名长上限 ±1、大小写不敏感查重、
/// 归属校验、排序追加与外来 Id 跳过)。DB 门控同仓内先例,随机 UserId 隔离。
/// boundary 类别逐例标注;日期/文件类不适用(类别无日期与文件输入)。
/// </summary>
public sealed class PracticeCategoryHandlerTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb();
        var cats = await db.Categories.Where(c => _users.Contains(c.UserId)).ToListAsync();
        db.Categories.RemoveRange(cats);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private async Task<PracticeCategoryDto> CreateAsync(Guid user, string name)
    {
        await using var db = NewDb();
        var r = await new CreateCategoryCommandHandler(db).Handle(new CreateCategoryCommand(user, name), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<IReadOnlyList<PracticeCategoryDto>> ListAsync(Guid user)
    {
        await using var db = NewDb();
        return await new ListCategoriesQueryHandler(db).Handle(new ListCategoriesQuery(user), default);
    }

    [Fact] // boundary: 空集合 —— 新用户首访播种两个默认类别(序 0/1),再访不重复播种
    public async Task List_EmptyUser_SeedsDefaultsOnce()
    {
        if (!_available) return;
        var user = NewUser();
        var first = await ListAsync(user);
        first.Select(c => c.Name).Should().Equal("工作面试", "生活英语");
        first.Select(c => c.SortOrder).Should().Equal(0, 1);
        (await ListAsync(user)).Should().HaveCount(2);
    }

    [Theory] // boundary: 空/null —— 空名/纯空白建类失败
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_EmptyName_Fails(string name)
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb();
        var r = await new CreateCategoryCommandHandler(db).Handle(new CreateCategoryCommand(user, name), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact] // boundary: 超长上限 ±1 —— 名长 100 成功 / 101 失败(Handler 内守卫)
    public async Task Create_NameLength_Boundary()
    {
        if (!_available) return;
        var user = NewUser();
        var ok = await CreateAsync(user, new string('类', 100));
        ok.Name.Should().HaveLength(100);
        await using var db = NewDb();
        var r = await new CreateCategoryCommandHandler(db).Handle(
            new CreateCategoryCommand(user, new string('x', 101)), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("category.name");
    }

    [Fact] // boundary: 特殊字符/Unicode —— 含 emoji 与空格的名去首尾空白后逐字落库
    public async Task Create_UnicodeName_TrimmedAndPreserved()
    {
        if (!_available) return;
        var user = NewUser();
        var dto = await CreateAsync(user, "  面试 🎯 café  ");
        dto.Name.Should().Be("面试 🎯 café");
    }

    [Fact] // boundary: 去重 —— 同名(大小写不敏感)重复建类失败;不同用户同名互不干扰
    public async Task Create_DuplicateCaseInsensitive_Fails()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        await CreateAsync(user, "System Design");
        await using (var db = NewDb())
        {
            var r = await new CreateCategoryCommandHandler(db).Handle(
                new CreateCategoryCommand(user, "system design"), default);
            r.IsFailure.Should().BeTrue();
            r.Error.Code.Should().Be("category.duplicate");
        }
        (await CreateAsync(other, "system design")).Name.Should().Be("system design");
    }

    [Fact] // boundary: 计数/顺序 —— 连续建类追加到末尾(序 0、1 递增)
    public async Task Create_AppendsSortOrder()
    {
        if (!_available) return;
        var user = NewUser();
        var a = await CreateAsync(user, "A");
        var b = await CreateAsync(user, "B");
        a.SortOrder.Should().Be(0);
        b.SortOrder.Should().Be(1);
    }

    [Fact] // boundary: 改名 —— 合法改名落库;与他人既有类重名失败;101 字失败;空名失败
    public async Task Rename_Rules()
    {
        if (!_available) return;
        var user = NewUser();
        var a = await CreateAsync(user, "Alpha");
        await CreateAsync(user, "Beta");
        await using (var db = NewDb())
        {
            var dup = await new RenameCategoryCommandHandler(db).Handle(
                new RenameCategoryCommand(user, a.Id, "beta"), default);
            dup.IsFailure.Should().BeTrue(); dup.Error.Code.Should().Be("category.duplicate");
            var tooLong = await new RenameCategoryCommandHandler(db).Handle(
                new RenameCategoryCommand(user, a.Id, new string('x', 101)), default);
            tooLong.IsFailure.Should().BeTrue();
            var empty = await new RenameCategoryCommandHandler(db).Handle(
                new RenameCategoryCommand(user, a.Id, "  "), default);
            empty.IsFailure.Should().BeTrue();
            var ok = await new RenameCategoryCommandHandler(db).Handle(
                new RenameCategoryCommand(user, a.Id, "Alpha Prime"), default);
            ok.IsSuccess.Should().BeTrue();
        }
        (await ListAsync(user)).Select(c => c.Name).Should().Contain("Alpha Prime");
    }

    [Fact] // boundary: 归属校验 —— 跨用户改名/删除均 NotFound 且数据完好
    public async Task RenameDelete_CrossUser_NotFound()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var a = await CreateAsync(user, "Mine");
        await using (var db = NewDb())
        {
            var rn = await new RenameCategoryCommandHandler(db).Handle(
                new RenameCategoryCommand(other, a.Id, "Hacked"), default);
            rn.IsFailure.Should().BeTrue(); rn.Error.Type.Should().Be(ErrorType.NotFound);
            var del = await new DeleteCategoryCommandHandler(db).Handle(
                new DeleteCategoryCommand(other, a.Id), default);
            del.IsFailure.Should().BeTrue(); del.Error.Type.Should().Be(ErrorType.NotFound);
        }
        (await ListAsync(user)).Select(c => c.Name).Should().Equal("Mine");
    }

    [Fact] // boundary: 状态流转 —— 本人删类成功且列表消失;重复删 NotFound
    public async Task Delete_Own_SucceedsThenNotFound()
    {
        if (!_available) return;
        var user = NewUser();
        var a = await CreateAsync(user, "Temp");
        await using (var db = NewDb())
        {
            (await new DeleteCategoryCommandHandler(db).Handle(new DeleteCategoryCommand(user, a.Id), default))
                .IsSuccess.Should().BeTrue();
            var again = await new DeleteCategoryCommandHandler(db).Handle(new DeleteCategoryCommand(user, a.Id), default);
            again.IsFailure.Should().BeTrue(); again.Error.Type.Should().Be(ErrorType.NotFound);
        }
        // 注意:列表为空会触发默认播种,故以「不含被删类」断言而非空列表
        (await ListAsync(user)).Select(c => c.Id).Should().NotContain(a.Id);
    }

    [Fact] // boundary: 空集合/外来 Id —— 重排按提交序;不属于本人的 Id 跳过;未提及类按原序追加末尾
    public async Task Reorder_SubmittedOrder_ForeignSkipped_RestAppended()
    {
        if (!_available) return;
        var user = NewUser();
        var a = await CreateAsync(user, "A");
        var b = await CreateAsync(user, "B");
        var c = await CreateAsync(user, "C");
        await using (var db = NewDb())
        {
            var r = await new ReorderCategoriesCommandHandler(db).Handle(
                new ReorderCategoriesCommand(user, [c.Id, Guid.NewGuid(), a.Id]), default);
            r.IsSuccess.Should().BeTrue();
        }
        var list = await ListAsync(user);
        list.Select(x => x.Name).Should().Equal("C", "A", "B");
        list.Select(x => x.SortOrder).Should().Equal(0, 1, 2);
    }
}
