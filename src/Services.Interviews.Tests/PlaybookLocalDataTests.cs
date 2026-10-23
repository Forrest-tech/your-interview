using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>可指定用户的测试当前用户(跨用户隔离用例需要过滤器与命令用户一致)。</summary>
public sealed class FixedCurrentUser(Guid userId) : ICurrentUser
{
    public Guid? UserId => userId;
    public string? Email => "test@local";
    public bool IsAuthenticated => true;
    public string? IpAddress => null;
    public string? UserAgent => null;
    public IReadOnlyList<string> Roles => [];
}

/// <summary>
/// 票15(COD-10b):Playbook 本地数据上后端 —— STAR 故事库 CRUD/导入幂等、
/// 历史复盘/粘贴材料归档导入的归属隔离与对账。走真实 Handler + 真实 DB
/// (无 DB 环境跳过,同 CreateEntryDedupeTests 先例)。
/// 每类用独立随机 UserId,互不干扰;Dispose 物理清理本类数据。
/// </summary>
public sealed class PlaybookLocalDataTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private readonly List<Guid> _entryIds = [];
    private InterviewsDbContext _db = null!;
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
            await using var db = NewDb(Owner);
            var stories = await db.StarStories.IgnoreQueryFilters()
                .Where(s => s.UserId == Owner || s.UserId == Other).ToListAsync();
            db.StarStories.RemoveRange(stories);
            var debriefs = await db.LegacyDebriefs.IgnoreQueryFilters()
                .Where(d => d.UserId == Owner || d.UserId == Other).ToListAsync();
            db.LegacyDebriefs.RemoveRange(debriefs);
            var pasted = await db.LegacyPastedMaterials.IgnoreQueryFilters()
                .Where(p => p.UserId == Owner || p.UserId == Other).ToListAsync();
            db.LegacyPastedMaterials.RemoveRange(pasted);
            var entries = await db.Entries.IgnoreQueryFilters()
                .Where(e => _entryIds.Contains(e.Id)).ToListAsync();
            db.Entries.RemoveRange(entries);
            await db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static InterviewsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        return new InterviewsDbContext(opts, new FixedCurrentUser(currentUser));
    }

    private async Task<Guid> SeedEntryAsync(Guid userId)
    {
        await using var db = NewDb(userId);
        var e = new InterviewEntry(Guid.NewGuid(), "T15-" + Guid.NewGuid().ToString("N")[..8],
            "Senior .NET Developer", userId: userId);
        db.Entries.Add(e);
        await db.SaveChangesAsync();
        _entryIds.Add(e.Id);
        return e.Id;
    }

    private static StarStoryInput Story(string clientId, string title = "Led migration")
        => new(clientId, title, "S", "T", "A", "R", "migration,dotnet");

    // ---------------- STAR:upsert / list / delete ----------------

    [Fact]
    public async Task Upsert_CreateThenUpdate_KeepsSingleRow()
    {
        if (!_available) return;
        var handler = new UpsertStarStoryCommandHandler(_db);

        var first = await handler.Handle(new UpsertStarStoryCommand(Owner, Story("s1")), default);
        var second = await handler.Handle(
            new UpsertStarStoryCommand(Owner, Story("s1", "Led migration v2")), default);

        first.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value); // 同一故事同一行

        var list = await new ListStarStoriesQueryHandler(_db)
            .Handle(new ListStarStoriesQuery(Owner), default);
        list.Value.Should().ContainSingle(s => s.ClientId == "s1")
            .Which.Title.Should().Be("Led migration v2");
    }

    [Fact]
    public async Task Delete_ThenUpsertSameClientId_RestoresRow()
    {
        if (!_available) return;
        var upsert = new UpsertStarStoryCommandHandler(_db);
        await upsert.Handle(new UpsertStarStoryCommand(Owner, Story("s-del")), default);

        var del = await new DeleteStarStoryCommandHandler(_db)
            .Handle(new DeleteStarStoryCommand(Owner, "s-del"), default);
        del.IsSuccess.Should().BeTrue();

        var listHandler = new ListStarStoriesQueryHandler(_db);
        (await listHandler.Handle(new ListStarStoriesQuery(Owner), default)).Value
            .Should().NotContain(s => s.ClientId == "s-del");

        // 删除幂等:再删一次仍成功
        (await new DeleteStarStoryCommandHandler(_db)
            .Handle(new DeleteStarStoryCommand(Owner, "s-del"), default)).IsSuccess.Should().BeTrue();

        await upsert.Handle(new UpsertStarStoryCommand(Owner, Story("s-del", "Back again")), default);
        (await listHandler.Handle(new ListStarStoriesQuery(Owner), default)).Value
            .Should().ContainSingle(s => s.ClientId == "s-del" && s.Title == "Back again");
    }

    [Fact]
    public async Task StarStories_CrossUser_Isolated()
    {
        if (!_available) return;
        await new UpsertStarStoryCommandHandler(_db)
            .Handle(new UpsertStarStoryCommand(Owner, Story("s-iso")), default);

        await using var otherDb = NewDb(Other);
        var otherList = await new ListStarStoriesQueryHandler(otherDb)
            .Handle(new ListStarStoriesQuery(Other), default);
        otherList.Value.Should().NotContain(s => s.ClientId == "s-iso");

        // 他人按 clientId 删除主人的故事:命不中,主人数据不动(幂等成功但无效果)
        await new DeleteStarStoryCommandHandler(otherDb)
            .Handle(new DeleteStarStoryCommand(Other, "s-iso"), default);
        (await new ListStarStoriesQueryHandler(_db)
            .Handle(new ListStarStoriesQuery(Owner), default)).Value
            .Should().Contain(s => s.ClientId == "s-iso");
    }

    // ---------------- STAR:导入幂等/对账 ----------------

    [Fact]
    public async Task Import_Twice_SecondImportsZero_TotalStable()
    {
        if (!_available) return;
        var handler = new ImportStarStoriesCommandHandler(_db);
        var batch = new[] { Story("imp-1"), Story("imp-2"), Story("imp-2", "Dup in batch") };

        var first = await handler.Handle(new ImportStarStoriesCommand(Owner, batch), default);
        first.Value.Imported.Should().Be(2); // 批内重复按 clientId 只算一次
        first.Value.Total.Should().BeGreaterThanOrEqualTo(2);

        var before = first.Value.Total;
        var second = await handler.Handle(new ImportStarStoriesCommand(Owner, batch), default);
        second.Value.Imported.Should().Be(0);
        second.Value.Total.Should().Be(before); // 对账:总数不变
    }

    [Fact]
    public async Task Import_EmptyBatch_ImportsZero()
    {
        if (!_available) return;
        var r = await new ImportStarStoriesCommandHandler(_db)
            .Handle(new ImportStarStoriesCommand(Owner, []), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Imported.Should().Be(0);
    }

    // ---------------- 历史复盘:归档导入 ----------------

    [Fact]
    public async Task LegacyDebrief_Import_Idempotent_AndVerbatim()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); // 独立用户:total 断言不受同类其它用例干扰
        await using var db = NewDb(user);
        var entryId = await SeedEntryAsync(user);
        const string payload = "{\"questions\":\"Q1\\nQ2\",\"rating\":4,\"stuck\":\"卡点 🎯\"}";
        var handler = new ImportLegacyDebriefsCommandHandler(db);
        var items = new[] { new LegacyDebriefImportItem(entryId.ToString(), payload) };

        var first = await handler.Handle(new ImportLegacyDebriefsCommand(user, items), default);
        first.Value.Should().Be(new PlaybookImportResultDto(1, 1));

        var second = await handler.Handle(new ImportLegacyDebriefsCommand(user, items), default);
        second.Value.Should().Be(new PlaybookImportResultDto(0, 1)); // 重复导入对账

        var list = await new ListLegacyDebriefsQueryHandler(db)
            .Handle(new ListLegacyDebriefsQuery(user), default);
        // 票77:DTO EntryId = 归档键 —— 真实条目行 = 原 Guid 规范串逐字(旧口径不变)
        list.Value.Should().ContainSingle(d => d.EntryId == entryId.ToString())
            .Which.PayloadJson.Should().Be(payload); // 原值逐字,无损
    }

    [Fact]
    // 依据 = 票77 票面重新定义(按票55 先例改期望):票15 旧口径「后缀匹配不上
    // 现存条目即静默跳过」正是本票要修的缺陷 —— 孤儿键以后缀原文为归档键
    // 落库,归档行恒属导入者。他人条目 id 的后缀归档的是导入者本地的那份
    // 数据(来自其浏览器),条目主人一方不可见(隔离断言在文末)。
    public async Task LegacyDebrief_ForeignOrMissingEntry_ArchivedForImporter()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); // 独立用户:total 断言不受同类其它用例干扰
        await using var db = NewDb(user);
        var foreignEntry = await SeedEntryAsync(Other);
        var missing = Guid.NewGuid();
        var items = new[]
        {
            new LegacyDebriefImportItem(foreignEntry.ToString(), "{\"who\":\"foreign\"}"), // 他人的条目 id
            new LegacyDebriefImportItem(missing.ToString(), "{\"who\":\"missing\"}"),      // 不存在的条目 id
        };
        var r = await new ImportLegacyDebriefsCommandHandler(db)
            .Handle(new ImportLegacyDebriefsCommand(user, items), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(new PlaybookImportResultDto(2, 2)); // 两键都归档,不再跳过

        var list = await new ListLegacyDebriefsQueryHandler(db)
            .Handle(new ListLegacyDebriefsQuery(user), default);
        list.Value.Should().HaveCount(2);
        list.Value.Should().Contain(d => d.EntryId == foreignEntry.ToString());
        list.Value.Should().Contain(d => d.EntryId == missing.ToString());

        // 隔离:条目主人(Other)看不到导入者以其条目 id 归档的行
        await using var otherDb = NewDb(Other);
        var otherList = await new ListLegacyDebriefsQueryHandler(otherDb)
            .Handle(new ListLegacyDebriefsQuery(Other), default);
        otherList.Value.Should().NotContain(d => d.EntryId == foreignEntry.ToString());
    }

    [Fact]
    public async Task LegacyDebrief_SoftDeletedEntry_StillArchived()
    {
        if (!_available) return;
        var entryId = await SeedEntryAsync(Owner);
        await using (var db = NewDb(Owner))
        {
            var e = await db.Entries.FirstAsync(x => x.Id == entryId);
            e.MarkDeleted();
            await db.SaveChangesAsync();
        }
        var r = await new ImportLegacyDebriefsCommandHandler(_db)
            .Handle(new ImportLegacyDebriefsCommand(Owner,
                [new LegacyDebriefImportItem(entryId.ToString(), "{\"text\":\"old\"}")]), default);
        r.Value.Imported.Should().Be(1); // 软删条目的历史数据仍是用户数据,保全优先(票77 后更进一步:条目不存在也归档)
    }

    // ---------------- 历史粘贴材料:归档导入 ----------------

    [Fact]
    // 依据 = 票77 票面重新定义(按票55 先例改期望):旧名 …_CrossUserSkipped 的
    // 「他人条目键跳过」口径废止 —— 他人条目 id 后缀与自有条目键同等归档到
    // 导入者名下(数据来自导入者浏览器);隔离改为「条目主人看不到该行」。
    public async Task LegacyPasted_Import_Idempotent_Verbatim_ForeignKeyArchivedForImporter()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        await using var db = NewDb(user);
        var entryId = await SeedEntryAsync(user);
        var foreignEntry = await SeedEntryAsync(Other);
        const string content = "# 备战材料\n\n- STAR 故事 🎯\n- 系统设计";
        var handler = new ImportLegacyPastedCommandHandler(db);
        var items = new[]
        {
            new LegacyPastedImportItem(entryId.ToString(), content),
            new LegacyPastedImportItem(foreignEntry.ToString(), "importer's local copy"),
        };

        var first = await handler.Handle(new ImportLegacyPastedCommand(user, items), default);
        first.Value.Should().Be(new PlaybookImportResultDto(2, 2)); // 两键都归档

        var second = await handler.Handle(new ImportLegacyPastedCommand(user, items), default);
        second.Value.Should().Be(new PlaybookImportResultDto(0, 2)); // 幂等对账

        var list = await new ListLegacyPastedQueryHandler(db)
            .Handle(new ListLegacyPastedQuery(user), default);
        list.Value.Should().ContainSingle(p => p.EntryId == entryId.ToString())
            .Which.Content.Should().Be(content); // 原值逐字
        list.Value.Should().ContainSingle(p => p.EntryId == foreignEntry.ToString())
            .Which.Content.Should().Be("importer's local copy");

        // 隔离:条目主人(Other)看不到导入者以其条目 id 归档的行
        await using var otherDb = NewDb(Other);
        var otherList = await new ListLegacyPastedQueryHandler(otherDb)
            .Handle(new ListLegacyPastedQuery(Other), default);
        otherList.Value.Should().NotContain(p => p.EntryId == foreignEntry.ToString());
    }
}
