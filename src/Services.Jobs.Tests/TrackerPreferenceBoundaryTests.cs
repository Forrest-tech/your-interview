using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票14(COD-10a)boundary 补测(总纲第七节):收藏/自备问题的校验器(纯函数)与
/// 导入幂等判定(Handler)的边界类别覆盖。每例以注释标注 boundary 类别:
///   空/null、空集合、超长上限±1、特殊字符/Unicode、0/计数、顺序/去重。
/// 不适用类别(本票 API 无日期/时间入参、无文件入参 —— CreatedAt 服务端 UTC 生成、
/// 无上传通道):日期时区临界、文件类(0 字节/损坏/超限)不设用例,理由见交付报告。
/// DB 门控部分同 TrackerPreferencesTests 先例(无 DB 跳过)。
/// </summary>
public sealed class TrackerPreferenceBoundaryTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private readonly List<Guid> _appIds = [];
    private JobsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Owner);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            var favs = await _db.Favorites.IgnoreQueryFilters().Where(f => f.UserId == Owner).ToListAsync();
            _db.Favorites.RemoveRange(favs);
            var qs = await _db.ApplicationQuestions.IgnoreQueryFilters().Where(q => q.UserId == Owner).ToListAsync();
            _db.ApplicationQuestions.RemoveRange(qs);
            var apps = await _db.Applications.IgnoreQueryFilters().Where(a => _appIds.Contains(a.Id)).ToListAsync();
            _db.Applications.RemoveRange(apps);
            var companies = await _db.Companies.IgnoreQueryFilters().Where(c => c.UserId == Owner).ToListAsync();
            _db.Companies.RemoveRange(companies);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static JobsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<JobsDbContext>().UseNpgsql(Cs).Options;
        return new JobsDbContext(opts, new TestCurrentUser(currentUser));
    }

    private async Task<JobApplication> SeedAppAsync()
    {
        await using var db = NewDb(Owner);
        var company = new Company("T14B-" + Guid.NewGuid().ToString("N")[..8], userId: Owner);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var app = new JobApplication(company.Id, "Senior .NET Developer", userId: Owner);
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        _appIds.Add(app.Id);
        return app;
    }

    // ---------- 校验器(纯函数) ----------

    // boundary: 空/null —— 问题正文 null、空串、纯空白一律拒
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void AddQuestion_Boundary_EmptyOrNullText_Rejected(string? text)
    {
        var v = new AddApplicationQuestionCommandValidator();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), text!)).IsValid.Should().BeFalse();
    }

    // boundary: 超长上限±1 —— 正文上限 4000:4000 放行、4001 拒
    [Fact]
    public void AddQuestion_Boundary_MaxLength_PlusMinusOne()
    {
        var v = new AddApplicationQuestionCommandValidator();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), new string('x', 4000)))
            .IsValid.Should().BeTrue();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), new string('x', 4001)))
            .IsValid.Should().BeFalse();
    }

    // boundary: 特殊字符/Unicode —— CJK/emoji/RTL/引号/换行均合法正文,原文放行;
    // 4000 个 CJK 字符恰在上限内(.NET 按 UTF-16 码元计长,校验与列宽口径一致)
    [Fact]
    public void AddQuestion_Boundary_UnicodeAndSpecialChars_Accepted()
    {
        var v = new AddApplicationQuestionCommandValidator();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(),
            "为什么选 C#?🚀 «micro-services» — \"quoted\" \n second line")).IsValid.Should().BeTrue();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), new string('面', 4000)))
            .IsValid.Should().BeTrue();
    }

    // boundary: 空集合 + 超长上限±1 —— 收藏导入批上限 5000:空集放行、5000 放行、5001 拒;null 拒
    [Fact]
    public void ImportFavorites_Boundary_CountLimits()
    {
        var v = new ImportFavoritesCommandValidator();
        v.Validate(new ImportFavoritesCommand(Owner, [])).IsValid.Should().BeTrue();
        v.Validate(new ImportFavoritesCommand(Owner,
            Enumerable.Range(0, 5000).Select(_ => Guid.NewGuid()).ToList())).IsValid.Should().BeTrue();
        v.Validate(new ImportFavoritesCommand(Owner,
            Enumerable.Range(0, 5001).Select(_ => Guid.NewGuid()).ToList())).IsValid.Should().BeFalse();
        v.Validate(new ImportFavoritesCommand(Owner, null!)).IsValid.Should().BeFalse(); // 空/null
    }

    // boundary: 空集合 + 超长上限±1 —— 问题导入批上限 1000 条、单条 4000 字,均 ±1 取点;null 拒
    [Fact]
    public void ImportQuestions_Boundary_CountAndItemLimits()
    {
        var v = new ImportApplicationQuestionsCommandValidator();
        v.Validate(new ImportApplicationQuestionsCommand(Owner, Guid.NewGuid(), [])).IsValid.Should().BeTrue();
        v.Validate(new ImportApplicationQuestionsCommand(Owner, Guid.NewGuid(),
            Enumerable.Range(0, 1000).Select(i => $"q{i}?").ToList())).IsValid.Should().BeTrue();
        v.Validate(new ImportApplicationQuestionsCommand(Owner, Guid.NewGuid(),
            Enumerable.Range(0, 1001).Select(i => $"q{i}?").ToList())).IsValid.Should().BeFalse();
        v.Validate(new ImportApplicationQuestionsCommand(Owner, Guid.NewGuid(),
            [new string('x', 4000)])).IsValid.Should().BeTrue();
        v.Validate(new ImportApplicationQuestionsCommand(Owner, Guid.NewGuid(),
            [new string('x', 4001)])).IsValid.Should().BeFalse();
        v.Validate(new ImportApplicationQuestionsCommand(Owner, Guid.NewGuid(), null!))
            .IsValid.Should().BeFalse(); // 空/null
    }

    // ---------- Handler 导入判定(DB 门控) ----------

    // boundary: 空集合 —— 空批导入是合法 no-op:Imported=0、Total 与导入前一致(计数不为负、不报错)
    [Fact]
    public async Task ImportFavorites_Boundary_EmptyList_IsNoOp()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await using var db = NewDb(Owner);
        await new AddFavoriteCommandHandler(db).Handle(new AddFavoriteCommand(Owner, app.Id), default);

        var r = await new ImportFavoritesCommandHandler(db)
            .Handle(new ImportFavoritesCommand(Owner, []), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Imported.Should().Be(0);
        r.Value.Total.Should().Be(1);
    }

    // boundary: 0 值(Guid.Empty)+ 未知 id —— 不属于任何投递,静默跳过、Imported=0,不整批失败
    [Fact]
    public async Task ImportFavorites_Boundary_EmptyGuidAndUnknown_Skipped()
    {
        if (!_available) return;
        await SeedAppAsync();
        await using var db = NewDb(Owner);

        var r = await new ImportFavoritesCommandHandler(db)
            .Handle(new ImportFavoritesCommand(Owner, [Guid.Empty, Guid.NewGuid()]), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Imported.Should().Be(0);
        r.Value.Total.Should().Be(0);
    }

    // boundary: 空/null 项 —— 问题导入批里空串/纯空白/null 项全部跳过,只导真实条目
    [Fact]
    public async Task ImportQuestions_Boundary_BlankItems_Skipped()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await using var db = NewDb(Owner);

        var r = await new ImportApplicationQuestionsCommandHandler(db).Handle(
            new ImportApplicationQuestionsCommand(Owner, app.Id, ["", "   ", null!, "real?"]), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Imported.Should().Be(1);
        r.Value.Total.Should().Be(1);
    }

    // boundary: 特殊字符/Unicode —— emoji/CJK/引号/换行问题导入后逐字全等读回(不转义、不截断)
    [Fact]
    public async Task ImportQuestions_Boundary_Unicode_RoundTripsExactly()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await using var db = NewDb(Owner);
        const string unicodeQ = "谈谈 DealTrax 迁移 🚀 «strangler» — \"引号\" と日本語\n第二行?";

        var r = await new ImportApplicationQuestionsCommandHandler(db).Handle(
            new ImportApplicationQuestionsCommand(Owner, app.Id, [unicodeQ]), default);
        r.Value.Imported.Should().Be(1);

        var list = await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Owner, app.Id), default);
        list.Value.Should().ContainSingle().Which.Text.Should().Be(unicodeQ);
    }

    // boundary: 超长上限值 —— 恰 4000 字的问题经 Handler 落库后长度与原文全等(列宽 4000 不截断)
    [Fact]
    public async Task AddQuestion_Boundary_Exactly4000Chars_StoredExactly()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await using var db = NewDb(Owner);
        var text = new string('面', 3999) + "?"; // 恰 4000

        var id = (await new AddApplicationQuestionCommandHandler(db)
            .Handle(new AddApplicationQuestionCommand(Owner, app.Id, text), default)).Value;
        id.Should().NotBe(Guid.Empty);

        var list = await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Owner, app.Id), default);
        list.Value.Should().ContainSingle().Which.Text.Should().Be(text);
    }

    // boundary: 计数/顺序 —— 删中间一条后新增,SortOrder 取现存 max+1 不回填、不撞号,顺序仍稳定
    [Fact]
    public async Task Questions_Boundary_SortOrderAfterMiddleDelete_NoReuse()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await using var db = NewDb(Owner);
        var add = new AddApplicationQuestionCommandHandler(db);
        await add.Handle(new AddApplicationQuestionCommand(Owner, app.Id, "one?"), default);
        var id2 = (await add.Handle(new AddApplicationQuestionCommand(Owner, app.Id, "two?"), default)).Value;
        await add.Handle(new AddApplicationQuestionCommand(Owner, app.Id, "three?"), default);
        await new DeleteApplicationQuestionCommandHandler(db)
            .Handle(new DeleteApplicationQuestionCommand(Owner, app.Id, id2), default);
        await add.Handle(new AddApplicationQuestionCommand(Owner, app.Id, "four?"), default);

        var list = await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Owner, app.Id), default);
        list.Value.Select(q => q.Text).Should().Equal("one?", "three?", "four?");
        list.Value.Select(q => q.SortOrder).Should().Equal(1, 3, 4); // 删除的 2 不被复用
    }
}
