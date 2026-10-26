using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票77:孤儿键归档 —— 本地键后缀匹配不上现存条目时,以后缀原文为归档键
/// (EntryKey)落库,不再静默跳过。覆盖:随机后缀归档成功(复盘+粘贴)、
/// 幂等重导不重复且首导为准、跨用户隔离(双用户同后缀互不可见)、
/// Guid 规范形归一、票78 整批重跑收敛环在孤儿键上的并发回归。
/// DB 用例走真实 Handler + 真实 DB(无 DB 环境跳过,同 PlaybookLocalDataTests
/// 先例);归一纯函数用例不依赖 DB。每例用独立随机 UserId,Dispose 清理。
/// </summary>
public sealed class LegacyOrphanKeyTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private readonly List<Guid> _users = [];
    private InterviewsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Guid.NewGuid());
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available && _users.Count > 0)
        {
            await using var db = NewDb(_users[0]);
            var debriefs = await db.LegacyDebriefs.IgnoreQueryFilters()
                .Where(d => _users.Contains(d.UserId)).ToListAsync();
            db.LegacyDebriefs.RemoveRange(debriefs);
            var pasted = await db.LegacyPastedMaterials.IgnoreQueryFilters()
                .Where(p => _users.Contains(p.UserId)).ToListAsync();
            db.LegacyPastedMaterials.RemoveRange(pasted);
            await db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static InterviewsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        return new InterviewsDbContext(opts, new FixedCurrentUser(currentUser));
    }

    private Guid NewUser()
    {
        var u = Guid.NewGuid();
        _users.Add(u);
        return u;
    }

    private static string OrphanKey(string tag)
        => "t77-" + tag + "-" + Guid.NewGuid().ToString("N")[..12];

    // ---------------- 归一纯函数(无 DB) ----------------

    [Theory] // boundary: Guid 规范形归一 —— 大小写/N 格式/大括号格式收敛为规范串
    [InlineData("D7DE067E-FC5F-45AD-BD34-64D001ABFC30", "d7de067e-fc5f-45ad-bd34-64d001abfc30")]
    [InlineData("d7de067efc5f45adbd3464d001abfc30", "d7de067e-fc5f-45ad-bd34-64d001abfc30")]
    [InlineData("{d7de067e-fc5f-45ad-bd34-64d001abfc30}", "d7de067e-fc5f-45ad-bd34-64d001abfc30")]
    [InlineData("d7de067e-fc5f-45ad-bd34-64d001abfc30", "d7de067e-fc5f-45ad-bd34-64d001abfc30")]
    public void Normalize_GuidForms_Canonical(string raw, string expected)
        => LegacyEntryKeyRules.Normalize(raw).Should().Be(expected);

    [Theory] // boundary: 孤儿后缀逐字 —— 随机串/Unicode/近 Guid 形(差一位)不改写
    [InlineData("a6test-7f3d")]
    [InlineData("卡点🎯-复盘")]
    [InlineData("d7de067e-fc5f-45ad-bd34-64d001abfc3")] // 35 位,不是 Guid
    [InlineData("")]
    public void Normalize_OrphanSuffix_Verbatim(string raw)
        => LegacyEntryKeyRules.Normalize(raw).Should().Be(raw);

    // ---------------- 孤儿键归档成功 ----------------

    [Fact]
    public async Task OrphanDebrief_RandomSuffix_ArchivedVerbatim()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var key = OrphanKey("deb");
        const string payload = "{\"questions\":\"orphan 🎯\",\"rating\":3}";
        var handler = new ImportLegacyDebriefsCommandHandler(db);

        var r = await handler.Handle(new ImportLegacyDebriefsCommand(user,
            [new LegacyDebriefImportItem(key, payload)]), default);
        r.Value.Should().Be(new PlaybookImportResultDto(1, 1));

        var list = await new ListLegacyDebriefsQueryHandler(db)
            .Handle(new ListLegacyDebriefsQuery(user), default);
        list.Value.Should().ContainSingle(d => d.EntryId == key)
            .Which.PayloadJson.Should().Be(payload); // 逐字

        // 落库形态:EntryKey = 后缀原文,EntryId 旧列 = Guid.Empty 占位
        await using var raw = NewDb(user);
        var row = await raw.LegacyDebriefs.IgnoreQueryFilters()
            .SingleAsync(d => d.UserId == user && d.EntryKey == key);
        row.EntryId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task OrphanPasted_RandomSuffix_ArchivedVerbatim()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var key = OrphanKey("pst");
        const string content = "# 孤儿粘贴材料 🎯\n- 要点";
        var r = await new ImportLegacyPastedCommandHandler(db)
            .Handle(new ImportLegacyPastedCommand(user,
                [new LegacyPastedImportItem(key, content)]), default);
        r.Value.Should().Be(new PlaybookImportResultDto(1, 1));

        var list = await new ListLegacyPastedQueryHandler(db)
            .Handle(new ListLegacyPastedQuery(user), default);
        list.Value.Should().ContainSingle(p => p.EntryId == key)
            .Which.Content.Should().Be(content);

        await using var raw = NewDb(user);
        var row = await raw.LegacyPastedMaterials.IgnoreQueryFilters()
            .SingleAsync(p => p.UserId == user && p.EntryKey == key);
        row.EntryId.Should().Be(Guid.Empty);
    }

    [Fact] // boundary: Unicode 后缀 —— 中文/emoji 后缀归档且读回逐字
    public async Task OrphanDebrief_UnicodeSuffix_ArchivedVerbatim()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var key = "复盘🎯-" + Guid.NewGuid().ToString("N")[..6];
        var r = await new ImportLegacyDebriefsCommandHandler(db)
            .Handle(new ImportLegacyDebriefsCommand(user,
                [new LegacyDebriefImportItem(key, "{}")]), default);
        r.Value.Imported.Should().Be(1);
        var list = await new ListLegacyDebriefsQueryHandler(db)
            .Handle(new ListLegacyDebriefsQuery(user), default);
        list.Value.Should().ContainSingle(d => d.EntryId == key);
    }

    // ---------------- 幂等 / 首导为准 ----------------

    [Fact]
    public async Task OrphanDebrief_Reimport_Idempotent_FirstWins()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var key = OrphanKey("idem");
        var handler = new ImportLegacyDebriefsCommandHandler(db);

        var first = await handler.Handle(new ImportLegacyDebriefsCommand(user,
            [new LegacyDebriefImportItem(key, "{\"v\":1}")]), default);
        first.Value.Should().Be(new PlaybookImportResultDto(1, 1));

        // 同键不同值重导:不重复、不覆盖(首导为准)
        var second = await handler.Handle(new ImportLegacyDebriefsCommand(user,
            [new LegacyDebriefImportItem(key, "{\"v\":2}")]), default);
        second.Value.Should().Be(new PlaybookImportResultDto(0, 1));

        var list = await new ListLegacyDebriefsQueryHandler(db)
            .Handle(new ListLegacyDebriefsQuery(user), default);
        list.Value.Should().ContainSingle(d => d.EntryId == key)
            .Which.PayloadJson.Should().Be("{\"v\":1}");
    }

    [Fact] // boundary: 空后缀 —— Handler 层防御跳过(校验层之外直接调用也不落空键行)
    public async Task OrphanImport_EmptySuffix_SkippedByHandler()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var r = await new ImportLegacyDebriefsCommandHandler(db)
            .Handle(new ImportLegacyDebriefsCommand(user,
                [new LegacyDebriefImportItem("", "{}")]), default);
        r.Value.Should().Be(new PlaybookImportResultDto(0, 0));
    }

    // ---------------- 跨用户隔离:双用户同后缀 ----------------

    [Fact]
    public async Task OrphanKey_SameSuffix_TwoUsers_Isolated()
    {
        if (!_available) return;
        var ua = NewUser();
        var ub = NewUser();
        var key = OrphanKey("iso");
        await using var dba = NewDb(ua);
        await using var dbb = NewDb(ub);

        var ra = await new ImportLegacyDebriefsCommandHandler(dba)
            .Handle(new ImportLegacyDebriefsCommand(ua,
                [new LegacyDebriefImportItem(key, "{\"owner\":\"A\"}")]), default);
        var rb = await new ImportLegacyDebriefsCommandHandler(dbb)
            .Handle(new ImportLegacyDebriefsCommand(ub,
                [new LegacyDebriefImportItem(key, "{\"owner\":\"B\"}")]), default);
        // 同后缀在两个用户名下各归档一份(唯一键含 UserId,互不顶替)
        ra.Value.Should().Be(new PlaybookImportResultDto(1, 1));
        rb.Value.Should().Be(new PlaybookImportResultDto(1, 1));

        var listA = await new ListLegacyDebriefsQueryHandler(dba)
            .Handle(new ListLegacyDebriefsQuery(ua), default);
        listA.Value.Should().ContainSingle(d => d.EntryId == key)
            .Which.PayloadJson.Should().Be("{\"owner\":\"A\"}");
        var listB = await new ListLegacyDebriefsQueryHandler(dbb)
            .Handle(new ListLegacyDebriefsQuery(ub), default);
        listB.Value.Should().ContainSingle(d => d.EntryId == key)
            .Which.PayloadJson.Should().Be("{\"owner\":\"B\"}");
    }

    // ---------------- Guid 规范形归一(落库面) ----------------

    [Fact]
    public async Task GuidSuffix_UppercaseImport_StoredCanonical_AndDedupesAcrossForms()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb(user);
        var guid = Guid.NewGuid();
        var handler = new ImportLegacyDebriefsCommandHandler(db);

        var first = await handler.Handle(new ImportLegacyDebriefsCommand(user,
            [new LegacyDebriefImportItem(guid.ToString().ToUpperInvariant(), "{\"v\":1}")]), default);
        first.Value.Should().Be(new PlaybookImportResultDto(1, 1));

        // 同一 Guid 的小写形重导 = 同一归档键,不重复
        var second = await handler.Handle(new ImportLegacyDebriefsCommand(user,
            [new LegacyDebriefImportItem(guid.ToString(), "{\"v\":2}")]), default);
        second.Value.Should().Be(new PlaybookImportResultDto(0, 1));

        var list = await new ListLegacyDebriefsQueryHandler(db)
            .Handle(new ListLegacyDebriefsQuery(user), default);
        // 读回 = 规范串逐字,与票15 旧口径(Guid 值)读回一致
        list.Value.Should().ContainSingle(d => d.EntryId == guid.ToString())
            .Which.PayloadJson.Should().Be("{\"v\":1}");

        await using var raw = NewDb(user);
        var row = await raw.LegacyDebriefs.IgnoreQueryFilters()
            .SingleAsync(d => d.UserId == user && d.EntryKey == guid.ToString());
        row.EntryId.Should().Be(guid); // Guid 后缀时旧列仍是该 Guid
    }

    // ---------------- 票78 收敛环回归(孤儿键并发) ----------------

    [Fact] // boundary: 孤儿键并发同发同批 —— 整批重跑收敛,都成功、恰 1 行、首导对账
    public async Task OrphanDebrief_ConcurrentSameBatch_BothSucceedSingleRow()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db1 = NewDb(user);
        await using var db2 = NewDb(user);
        var key = OrphanKey("race");
        var items = new[] { new LegacyDebriefImportItem(key, "{\"q\":1}") };
        var gate = new TaskCompletionSource();
        async Task<YourInterview.BuildingBlocks.Results.Result<PlaybookImportResultDto>> Run(
            InterviewsDbContext db)
        {
            await gate.Task;
            return await new ImportLegacyDebriefsCommandHandler(db)
                .Handle(new ImportLegacyDebriefsCommand(user, items), default);
        }
        var t1 = Task.Run(() => Run(db1));
        var t2 = Task.Run(() => Run(db2));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        results.Should().OnlyContain(r => r.Value.Total == 1);
        results.Sum(r => r.Value.Imported).Should().Be(1);
    }
}
