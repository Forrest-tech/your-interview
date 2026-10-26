using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票14(COD-10a):Tracker 收藏 + 按投递自备问题上后端。
/// 契约:数据归属当前用户(票03 口径)—— 跨用户读不到、写不进;
/// localStorage 一次性导入幂等 —— 同批数据重复导入 Imported=0、总数不变;
/// 收藏加/删幂等,软删后重加恢复同一行(唯一索引不撞)。
/// 走真实 JobsDbContext + 真实 Handler(无 DB 环境跳过,同 OutcomeWritebackConsumerTests 先例)。
/// </summary>
public sealed class TrackerPreferencesTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
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
            var favs = await _db.Favorites.IgnoreQueryFilters().Where(f => f.UserId == Owner || f.UserId == Other).ToListAsync();
            _db.Favorites.RemoveRange(favs);
            var qs = await _db.ApplicationQuestions.IgnoreQueryFilters().Where(q => q.UserId == Owner || q.UserId == Other).ToListAsync();
            _db.ApplicationQuestions.RemoveRange(qs);
            var apps = await _db.Applications.IgnoreQueryFilters().Where(a => _appIds.Contains(a.Id)).ToListAsync();
            _db.Applications.RemoveRange(apps);
            var companies = await _db.Companies.IgnoreQueryFilters().Where(c => c.UserId == Owner || c.UserId == Other).ToListAsync();
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

    private async Task<JobApplication> SeedAppAsync(Guid owner)
    {
        await using var db = NewDb(owner);
        var company = new Company("T14-" + Guid.NewGuid().ToString("N")[..8], userId: owner);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var app = new JobApplication(company.Id, "Senior .NET Developer", userId: owner);
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        _appIds.Add(app.Id);
        return app;
    }

    [Fact]
    public async Task AddFavorite_Idempotent_AndRemoveThenReAdd_Restores()
    {
        if (!_available) return;
        var app = await SeedAppAsync(Owner);
        await using var db = NewDb(Owner);

        (await new AddFavoriteCommandHandler(db).Handle(new AddFavoriteCommand(Owner, app.Id), default)).IsSuccess.Should().BeTrue();
        (await new AddFavoriteCommandHandler(db).Handle(new AddFavoriteCommand(Owner, app.Id), default)).IsSuccess.Should().BeTrue();

        var list = await new ListFavoritesQueryHandler(db).Handle(new ListFavoritesQuery(Owner), default);
        list.Value.Should().ContainSingle(f => f.ApplicationId == app.Id);

        (await new RemoveFavoriteCommandHandler(db).Handle(new RemoveFavoriteCommand(Owner, app.Id), default)).IsSuccess.Should().BeTrue();
        (await new RemoveFavoriteCommandHandler(db).Handle(new RemoveFavoriteCommand(Owner, app.Id), default)).IsSuccess.Should().BeTrue();
        list = await new ListFavoritesQueryHandler(db).Handle(new ListFavoritesQuery(Owner), default);
        list.Value.Should().BeEmpty();

        // 软删后重加:恢复同一行,不撞唯一索引
        (await new AddFavoriteCommandHandler(db).Handle(new AddFavoriteCommand(Owner, app.Id), default)).IsSuccess.Should().BeTrue();
        var rows = await db.Favorites.IgnoreQueryFilters().CountAsync(f => f.UserId == Owner && f.ApplicationId == app.Id);
        rows.Should().Be(1);
        list = await new ListFavoritesQueryHandler(db).Handle(new ListFavoritesQuery(Owner), default);
        list.Value.Should().ContainSingle();
    }

    [Fact]
    public async Task ImportFavorites_Idempotent_CountsReconcile_SkipsForeignAndDuplicate()
    {
        if (!_available) return;
        var a1 = await SeedAppAsync(Owner);
        var a2 = await SeedAppAsync(Owner);
        var foreign = await SeedAppAsync(Other);
        await using var db = NewDb(Owner);
        var handler = new ImportFavoritesCommandHandler(db);

        var first = await handler.Handle(
            new ImportFavoritesCommand(Owner, [a1.Id, a2.Id, a1.Id, foreign.Id]), default);
        first.Value.Imported.Should().Be(2); // 输入内重复去重 + 他人投递跳过
        first.Value.Total.Should().Be(2);

        var second = await handler.Handle(
            new ImportFavoritesCommand(Owner, [a1.Id, a2.Id, foreign.Id]), default);
        second.Value.Imported.Should().Be(0); // 幂等复跑
        second.Value.Total.Should().Be(2);
    }

    [Fact]
    public async Task Favorites_CrossUser_Isolated()
    {
        if (!_available) return;
        var app = await SeedAppAsync(Owner);
        await using var ownerDb = NewDb(Owner);
        (await new AddFavoriteCommandHandler(ownerDb).Handle(new AddFavoriteCommand(Owner, app.Id), default)).IsSuccess.Should().BeTrue();

        await using var otherDb = NewDb(Other);
        var otherList = await new ListFavoritesQueryHandler(otherDb).Handle(new ListFavoritesQuery(Other), default);
        otherList.Value.Should().BeEmpty();
        // 他人给不属于自己的投递加收藏 → NotFound
        (await new AddFavoriteCommandHandler(otherDb).Handle(new AddFavoriteCommand(Other, app.Id), default))
            .IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Questions_AddListDelete_OrderPreserved()
    {
        if (!_available) return;
        var app = await SeedAppAsync(Owner);
        await using var db = NewDb(Owner);

        var id1 = (await new AddApplicationQuestionCommandHandler(db)
            .Handle(new AddApplicationQuestionCommand(Owner, app.Id, "  Q1?  "), default)).Value;
        await new AddApplicationQuestionCommandHandler(db)
            .Handle(new AddApplicationQuestionCommand(Owner, app.Id, "Q2?"), default);

        var list = await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Owner, app.Id), default);
        list.Value.Select(q => q.Text).Should().Equal("Q1?", "Q2?"); // Trim 后按 SortOrder

        (await new DeleteApplicationQuestionCommandHandler(db)
            .Handle(new DeleteApplicationQuestionCommand(Owner, app.Id, id1), default)).IsSuccess.Should().BeTrue();
        list = await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Owner, app.Id), default);
        list.Value.Select(q => q.Text).Should().Equal("Q2?");
    }

    [Fact]
    public async Task ImportQuestions_Idempotent_CountsReconcile_OrderAndDedup()
    {
        if (!_available) return;
        var app = await SeedAppAsync(Owner);
        await using var db = NewDb(Owner);
        var handler = new ImportApplicationQuestionsCommandHandler(db);

        var first = await handler.Handle(new ImportApplicationQuestionsCommand(
            Owner, app.Id, ["A?", "B?", "A?", "  ", "C?"]), default);
        first.Value.Imported.Should().Be(3); // 批内重复 + 空行跳过
        first.Value.Total.Should().Be(3);

        var second = await handler.Handle(new ImportApplicationQuestionsCommand(
            Owner, app.Id, ["A?", "B?", "C?"]), default);
        second.Value.Imported.Should().Be(0); // 幂等复跑
        second.Value.Total.Should().Be(3);

        var third = await handler.Handle(new ImportApplicationQuestionsCommand(
            Owner, app.Id, ["B?", "D?"]), default);
        third.Value.Imported.Should().Be(1); // 增量只导新条
        third.Value.Total.Should().Be(4);

        var list = await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Owner, app.Id), default);
        list.Value.Select(q => q.Text).Should().Equal("A?", "B?", "C?", "D?");
    }

    [Fact]
    public async Task Questions_CrossUser_NotFound()
    {
        if (!_available) return;
        var app = await SeedAppAsync(Owner);
        await using var db = NewDb(Other);

        (await new ListApplicationQuestionsQueryHandler(db)
            .Handle(new ListApplicationQuestionsQuery(Other, app.Id), default)).IsSuccess.Should().BeFalse();
        (await new AddApplicationQuestionCommandHandler(db)
            .Handle(new AddApplicationQuestionCommand(Other, app.Id, "intrusion?"), default)).IsSuccess.Should().BeFalse();
        (await new ImportApplicationQuestionsCommandHandler(db)
            .Handle(new ImportApplicationQuestionsCommand(Other, app.Id, ["intrusion?"]), default)).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Questions_Validators_RejectEmptyAndOverlong()
    {
        var v = new AddApplicationQuestionCommandValidator();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), "  ")).IsValid.Should().BeFalse();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), new string('x', 4001))).IsValid.Should().BeFalse();
        v.Validate(new AddApplicationQuestionCommand(Owner, Guid.NewGuid(), "ok?")).IsValid.Should().BeTrue();
        await Task.CompletedTask;
    }
}
