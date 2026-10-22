using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票16(COD-10c)功能测试:Tech Stack 自定义分类与复习活动的 Handler 行为 ——
/// 导入对账/幂等、大小写不敏感去重、软删恢复、复习累加、同日取 max 合并、
/// 归属隔离(跨用户不可见/不互扰),以及纯规则(本地日期解析/分类清洗/批内合并)。
/// DB 门控同票14 Jobs 先例:连不上库时 DB 用例跳过(以纯规则用例为准),
/// 本环境 PG 在线时全量真跑。每例注释标注 boundary 类别(总纲第七节)。
/// </summary>
public sealed class TechStackLocalDataTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private KnowledgeDbContext _db = null!;
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
            var cats = await _db.CustomCategories.IgnoreQueryFilters()
                .Where(c => c.UserId == Owner || c.UserId == Other).ToListAsync();
            _db.CustomCategories.RemoveRange(cats);
            var acts = await _db.ReviewActivities.IgnoreQueryFilters()
                .Where(a => a.UserId == Owner || a.UserId == Other).ToListAsync();
            _db.ReviewActivities.RemoveRange(acts);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static KnowledgeDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<KnowledgeDbContext>().UseNpgsql(Cs).Options;
        return new KnowledgeDbContext(opts, new TestCurrentUser(currentUser));
    }

    // ---------- 纯规则:本地日期解析 ----------

    [Fact] // boundary: 日期临界 —— 年末/闰年真实日历日放行
    public void LocalDate_ValidDates_Accepted()
    {
        TechStackLocalDataRules.TryParseLocalDate("2026-12-31", out var d1).Should().BeTrue();
        d1.Should().Be(new DateOnly(2026, 12, 31));
        TechStackLocalDataRules.TryParseLocalDate("2024-02-29", out _).Should().BeTrue(); // 闰日
        TechStackLocalDataRules.TryParseLocalDate("2026-01-01", out _).Should().BeTrue(); // 年初
    }

    [Theory] // boundary: 日期临界/空 null —— 不存在的日历日、格式漂移、空值一律拒(不静默换算)
    [InlineData("2026-02-30")]  // 2 月无 30 日
    [InlineData("2025-02-29")]  // 非闰年无 29 日
    [InlineData("2026-13-01")]  // 月越界
    [InlineData("2026-10-32")]  // 日越界
    [InlineData("10/22/2026")]  // 美式格式不是本地键口径
    [InlineData("2026-1-5")]    // 未补零不是 YYYY-MM-DD
    [InlineData("20261022")]
    [InlineData("")]
    [InlineData(null)]
    public void LocalDate_InvalidDates_Rejected(string? raw)
        => TechStackLocalDataRules.TryParseLocalDate(raw, out _).Should().BeFalse();

    [Fact] // boundary: 日期口径 —— 解析只取字面日历日,不随时区/时钟漂移(逐字保真)
    public void LocalDate_ParsesLiteralDay()
    {
        TechStackLocalDataRules.TryParseLocalDate("2026-10-22", out var d).Should().BeTrue();
        d.Year.Should().Be(2026); d.Month.Should().Be(10); d.Day.Should().Be(22);
    }

    // ---------- 纯规则:分类清洗 ----------

    [Fact] // boundary: 空/null、空集合 —— null 列表/null 元素/纯空白全部收敛为无分类
    public void Normalize_NullsAndBlanks_Dropped()
    {
        TechStackLocalDataRules.NormalizeCategoryNames(null).Should().BeEmpty();
        TechStackLocalDataRules.NormalizeCategoryNames([]).Should().BeEmpty();
        TechStackLocalDataRules.NormalizeCategoryNames([null, "", "   "]).Should().BeEmpty();
    }

    [Fact] // boundary: 去重 —— 大小写不敏感保首现原文与顺序(与前端查重口径一致)
    public void Normalize_DedupesCaseInsensitive_KeepsFirst()
    {
        var r = TechStackLocalDataRules.NormalizeCategoryNames(
            [" SQL ", "sql", "C#", "c#", "Angular"]);
        r.Should().Equal("SQL", "C#", "Angular");
    }

    [Fact] // boundary: Unicode —— 中文/emoji 分类名原样保真
    public void Normalize_Unicode_Preserved()
    {
        var r = TechStackLocalDataRules.NormalizeCategoryNames(["微服务 🎯", "système"]);
        r.Should().Equal("微服务 🎯", "système");
    }

    // ---------- 纯规则:复习批内合并 ----------

    [Fact] // boundary: 顺序/去重 —— 同日多条取 max(只升不降),不同日各自保留
    public void MergeBatch_SameDayTakesMax()
    {
        var merged = TechStackLocalDataRules.MergeImportBatch([
            (new DateOnly(2026, 10, 20), 3),
            (new DateOnly(2026, 10, 20), 7),
            (new DateOnly(2026, 10, 20), 5),
            (new DateOnly(2026, 10, 21), 2),
        ]);
        merged[new DateOnly(2026, 10, 20)].Should().Be(7);
        merged[new DateOnly(2026, 10, 21)].Should().Be(2);
    }

    [Fact] // boundary: 空集合 —— 空批合并为空
    public void MergeBatch_Empty_IsEmpty()
        => TechStackLocalDataRules.MergeImportBatch([]).Should().BeEmpty();

    // ---------- DB:分类导入对账 / 幂等 / 隔离 ----------

    [Fact] // 功能 + 对账:首次导入 {imported,total} 一致,重复导入 imported=0(幂等)
    public async Task ImportCategories_ReconcilesAndIdempotent()
    {
        if (!_available) return;
        var handler = new ImportCustomCategoriesCommandHandler(_db);
        var r1 = await handler.Handle(new ImportCustomCategoriesCommand(Owner, ["Alpha", "Beta"]), default);
        r1.IsSuccess.Should().BeTrue();
        r1.Value.Should().Be(new LocalDataImportResultDto(2, 2));
        var r2 = await handler.Handle(new ImportCustomCategoriesCommand(Owner, ["Alpha", "Beta"]), default);
        r2.Value.Should().Be(new LocalDataImportResultDto(0, 2));
        // 大小写变体也算已存在,不重复建行
        var r3 = await handler.Handle(new ImportCustomCategoriesCommand(Owner, ["ALPHA", "Gamma"]), default);
        r3.Value.Should().Be(new LocalDataImportResultDto(1, 3));
    }

    [Fact] // 功能:新增幂等 + 删除幂等 + 软删后重加恢复同一行
    public async Task AddRemoveCategory_IdempotentAndRestore()
    {
        if (!_available) return;
        var add = new AddCustomCategoryCommandHandler(_db);
        (await add.Handle(new AddCustomCategoryCommand(Owner, "Solo"), default)).IsSuccess.Should().BeTrue();
        (await add.Handle(new AddCustomCategoryCommand(Owner, "solo"), default)).IsSuccess.Should().BeTrue();

        var list = new ListCustomCategoriesQueryHandler(_db);
        var rows = (await list.Handle(new ListCustomCategoriesQuery(Owner), default)).Value;
        rows.Count(c => c.Name.Equals("Solo", StringComparison.OrdinalIgnoreCase)).Should().Be(1);

        var remove = new RemoveCustomCategoryCommandHandler(_db);
        (await remove.Handle(new RemoveCustomCategoryCommand(Owner, "SOLO"), default)).IsSuccess.Should().BeTrue();
        (await remove.Handle(new RemoveCustomCategoryCommand(Owner, "Solo"), default)).IsSuccess.Should().BeTrue();
        rows = (await list.Handle(new ListCustomCategoriesQuery(Owner), default)).Value;
        rows.Should().NotContain(c => c.Name == "Solo");

        // 重加恢复:仍只有一行(含软删视角)
        (await add.Handle(new AddCustomCategoryCommand(Owner, "Solo"), default)).IsSuccess.Should().BeTrue();
        var all = await _db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == Owner && c.Name == "Solo").ToListAsync();
        all.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
    }

    [Fact] // 归属隔离:他人列表看不到我的分类,他人导入不影响我的对账
    public async Task Categories_CrossUserIsolated()
    {
        if (!_available) return;
        await new ImportCustomCategoriesCommandHandler(_db)
            .Handle(new ImportCustomCategoriesCommand(Owner, ["MineOnly"]), default);
        await using var otherDb = NewDb(Other);
        var otherRows = (await new ListCustomCategoriesQueryHandler(otherDb)
            .Handle(new ListCustomCategoriesQuery(Other), default)).Value;
        otherRows.Should().NotContain(c => c.Name == "MineOnly");
    }

    // ---------- DB:复习记录 / 累加 / 导入合并 ----------

    [Fact] // 功能:record 同日累加、跨日分开,返回当天计数
    public async Task Record_IncrementsSameDay()
    {
        if (!_available) return;
        var handler = new RecordReviewActivityCommandHandler(_db);
        var r1 = await handler.Handle(new RecordReviewActivityCommand(Owner, "2026-10-22"), default);
        r1.Value.Should().Be(new ReviewActivityDto("2026-10-22", 1));
        var r2 = await handler.Handle(new RecordReviewActivityCommand(Owner, "2026-10-22"), default);
        r2.Value.Should().Be(new ReviewActivityDto("2026-10-22", 2));
        var r3 = await handler.Handle(new RecordReviewActivityCommand(Owner, "2026-10-23"), default);
        r3.Value.Should().Be(new ReviewActivityDto("2026-10-23", 1));
    }

    [Fact] // 功能 + 对账:导入逐字保真、重复导入 {0,total} 且计数不虚增(max 口径)
    public async Task ImportActivity_VerbatimAndIdempotent()
    {
        if (!_available) return;
        var handler = new ImportReviewActivityCommandHandler(_db);
        var entries = new List<ReviewActivityImportItem>
        {
            new("2026-10-20", 3), new("2026-10-21", 12),
        };
        var r1 = await handler.Handle(new ImportReviewActivityCommand(Owner, entries), default);
        r1.Value.Should().Be(new LocalDataImportResultDto(2, 2));
        var r2 = await handler.Handle(new ImportReviewActivityCommand(Owner, entries), default);
        r2.Value.Should().Be(new LocalDataImportResultDto(0, 2));

        var rows = (await new GetReviewActivityQueryHandler(_db)
            .Handle(new GetReviewActivityQuery(Owner), default)).Value;
        rows.Should().Contain(new ReviewActivityDto("2026-10-20", 3));
        rows.Should().Contain(new ReviewActivityDto("2026-10-21", 12));
    }

    [Fact] // 功能:导入更小值不降已有计数、更大值升到该值,均不计 imported(已有日期)
    public async Task ImportActivity_MaxMergeOnlyRaises()
    {
        if (!_available) return;
        var record = new RecordReviewActivityCommandHandler(_db);
        await record.Handle(new RecordReviewActivityCommand(Owner, "2026-09-30"), default);
        await record.Handle(new RecordReviewActivityCommand(Owner, "2026-09-30"), default);

        var handler = new ImportReviewActivityCommandHandler(_db);
        var lower = await handler.Handle(new ImportReviewActivityCommand(Owner,
            [new ReviewActivityImportItem("2026-09-30", 1)]), default);
        lower.Value.Imported.Should().Be(0);
        var rows = (await new GetReviewActivityQueryHandler(_db)
            .Handle(new GetReviewActivityQuery(Owner), default)).Value;
        rows.Single(a => a.Date == "2026-09-30").Count.Should().Be(2);

        var higher = await handler.Handle(new ImportReviewActivityCommand(Owner,
            [new ReviewActivityImportItem("2026-09-30", 9)]), default);
        higher.Value.Imported.Should().Be(0);
        rows = (await new GetReviewActivityQueryHandler(_db)
            .Handle(new GetReviewActivityQuery(Owner), default)).Value;
        rows.Single(a => a.Date == "2026-09-30").Count.Should().Be(9);
    }

    [Fact] // 归属隔离:他人读不到我的复习记录;同日记录互不累加
    public async Task Activity_CrossUserIsolated()
    {
        if (!_available) return;
        await new RecordReviewActivityCommandHandler(_db)
            .Handle(new RecordReviewActivityCommand(Owner, "2026-08-15"), default);
        await using var otherDb = NewDb(Other);
        var otherRows = (await new GetReviewActivityQueryHandler(otherDb)
            .Handle(new GetReviewActivityQuery(Other), default)).Value;
        otherRows.Should().BeEmpty();
        var otherRecord = await new RecordReviewActivityCommandHandler(otherDb)
            .Handle(new RecordReviewActivityCommand(Other, "2026-08-15"), default);
        otherRecord.Value.Count.Should().Be(1); // 他人自己的第一条,不受我方影响
    }

    [Fact] // boundary: 空账号(Guid.Empty)——record 只落 Guid.Empty 名下,不串入真实账号(票81 补特征测试;后端逻辑未改)
    public async Task Record_EmptyUserId_IsolatedBucket()
    {
        if (!_available) return;
        var emptyDb = NewDb(Guid.Empty);
        try
        {
            var r = await new RecordReviewActivityCommandHandler(emptyDb)
                .Handle(new RecordReviewActivityCommand(Guid.Empty, "2026-07-04"), default);
            r.Value.Should().Be(new ReviewActivityDto("2026-07-04", 1));

            var emptyRows = (await new GetReviewActivityQueryHandler(emptyDb)
                .Handle(new GetReviewActivityQuery(Guid.Empty), default)).Value;
            emptyRows.Should().Contain(new ReviewActivityDto("2026-07-04", 1));

            var ownerRows = (await new GetReviewActivityQueryHandler(_db)
                .Handle(new GetReviewActivityQuery(Owner), default)).Value;
            ownerRows.Should().NotContain(a => a.Date == "2026-07-04");
        }
        finally
        {
            var rows = await emptyDb.ReviewActivities.IgnoreQueryFilters()
                .Where(a => a.UserId == Guid.Empty).ToListAsync();
            emptyDb.ReviewActivities.RemoveRange(rows);
            await emptyDb.SaveChangesAsync();
            await emptyDb.DisposeAsync();
        }
    }
}
