using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;
using static YourInterview.Services.Interviews.Application.StarStoryUpsertRules;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票78 §7:STAR 故事 upsert 并发收敛 —— 纯裁决矩阵(先查/竞态回查 ×
/// 无行/活跃/软删 全组合)+ DB 门控真并发(多 DbContext 屏障同发)。
/// boundary 类别:并发双写同 clientId(都成功、单行、同一行 Id、后写胜出)、
/// 四方同发、已存行并发编辑、软删后并发重存、跨用户同 clientId 不串扰、
/// 同值双发免写收敛、三类导入并发同发(票78 顺手核查面)。
/// 空标题校验属票79 范畴,本票不碰校验文案(既有 PlaybookLocalDataBoundaryTests
/// 锁定),此处不重复设例。DB 门控同既有先例:连不上库时 DB 用例跳过。
/// </summary>
public sealed class StarStoryUpsertRaceTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";

    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly List<Guid> _entryIds = [];
    private InterviewsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(_user);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            await using var db = NewDb(_user);
            var stories = await db.StarStories.IgnoreQueryFilters()
                .Where(s => s.UserId == _user || s.UserId == _other).ToListAsync();
            db.StarStories.RemoveRange(stories);
            var debriefs = await db.LegacyDebriefs.IgnoreQueryFilters()
                .Where(d => d.UserId == _user || d.UserId == _other).ToListAsync();
            db.LegacyDebriefs.RemoveRange(debriefs);
            var pasted = await db.LegacyPastedMaterials.IgnoreQueryFilters()
                .Where(p => p.UserId == _user || p.UserId == _other).ToListAsync();
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

    private static StarStoryInput Story(string clientId, string title)
        => new(clientId, title, "S", "T", "A", "R", "race");

    private async Task<Guid> SeedEntryAsync(Guid userId)
    {
        await using var db = NewDb(userId);
        var e = new InterviewEntry(Guid.NewGuid(), "T78-" + Guid.NewGuid().ToString("N")[..8],
            "Senior .NET Developer", userId: userId);
        db.Entries.Add(e);
        await db.SaveChangesAsync();
        _entryIds.Add(e.Id);
        return e.Id;
    }

    private async Task<List<StarStory>> RowsAsync(Guid userId, string clientId)
    {
        await using var db = NewDb(userId);
        return await db.StarStories.IgnoreQueryFilters()
            .Where(s => s.UserId == userId && s.ClientId == clientId).ToListAsync();
    }

    /// <summary>屏障同发:contexts 各自一个 handler,gate 放行后同刻执行。</summary>
    private static async Task<Result<Guid>[]> BurstUpsert(
        IReadOnlyList<InterviewsDbContext> contexts, Guid user, StarStoryInput input)
    {
        var gate = new TaskCompletionSource();
        var tasks = contexts.Select(db => Task.Run(async () =>
        {
            await gate.Task;
            return await new UpsertStarStoryCommandHandler(db)
                .Handle(new UpsertStarStoryCommand(user, input), default);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    // ---------- 纯裁决矩阵 ----------

    [Theory] // boundary: 并发重复 —— 先查三态各自的处置
    [InlineData(ExistingStoryState.None, false, UpsertDecision.Create)]
    [InlineData(ExistingStoryState.Active, false, UpsertDecision.Update)]
    [InlineData(ExistingStoryState.SoftDeleted, false, UpsertDecision.Update)]
    // boundary: 竞态回查 —— 赢家行已在则更新收敛(LWW);仍无行 = 撞别的约束 → Conflict
    [InlineData(ExistingStoryState.None, true, UpsertDecision.Conflict)]
    [InlineData(ExistingStoryState.Active, true, UpsertDecision.Update)]
    [InlineData(ExistingStoryState.SoftDeleted, true, UpsertDecision.Update)]
    public void DecideUpsert_Matrix(ExistingStoryState state, bool raceLost, UpsertDecision expected)
        => DecideUpsert(state, raceLost).Should().Be(expected);

    [Fact] // boundary: 同值免写判定 —— 逐字段相等才 true,任一字段不同即 false
    public void HasSameContent_FieldByField()
    {
        var row = new StarStory(_user, "s-eq", "T1", "S", "T", "A", "R", "tags", 1);
        HasSameContent(row, Story("s-eq", "T1")).Should().BeFalse(); // tags 不同("race" vs "tags")
        HasSameContent(row, new StarStoryInput("s-eq", "T1", "S", "T", "A", "R", "tags"))
            .Should().BeTrue();
        HasSameContent(row, new StarStoryInput("s-eq", "T2", "S", "T", "A", "R", "tags"))
            .Should().BeFalse();
    }

    // ---------- DB 真并发:upsert ----------

    [Fact] // boundary: 并发双写同 clientId —— 都成功、同一行 Id、恰 1 行、内容完整(二选一不混写)
    public async Task Upsert_ConcurrentSameClientId_BothSucceedSingleRow()
    {
        if (!_available) return;
        await using var db2 = NewDb(_user);
        var a = Story("race-two", "Title A");
        var b = Story("race-two", "Title B");
        var gate = new TaskCompletionSource();
        async Task<Result<Guid>> Run(InterviewsDbContext db, StarStoryInput input)
        {
            await gate.Task;
            return await new UpsertStarStoryCommandHandler(db)
                .Handle(new UpsertStarStoryCommand(_user, input), default);
        }
        var t1 = Task.Run(() => Run(_db, a));
        var t2 = Task.Run(() => Run(db2, b));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);

        results.Should().OnlyContain(r => r.IsSuccess);
        results[0].Value.Should().Be(results[1].Value); // 同一行
        var rows = await RowsAsync(_user, "race-two");
        rows.Should().ContainSingle().Which.Title.Should().BeOneOf("Title A", "Title B");
    }

    [Fact] // boundary: 并发双写同值(连点同帧双发形态)—— 都成功、恰 1 行
    public async Task Upsert_ConcurrentIdenticalPayload_BothSucceedSingleRow()
    {
        if (!_available) return;
        var contexts = new[] { _db, NewDb(_user), NewDb(_user), NewDb(_user) };
        try
        {
            var results = await BurstUpsert(contexts, _user, Story("race-same", "Same Title"));
            results.Should().OnlyContain(r => r.IsSuccess);
            results.Select(r => r.Value).Distinct().Should().ContainSingle();
            (await RowsAsync(_user, "race-same")).Should().ContainSingle()
                .Which.Title.Should().Be("Same Title");
        }
        finally { foreach (var c in contexts.Skip(1)) await c.DisposeAsync(); }
    }

    [Fact] // boundary: 四方同发不同值 —— 全成功、恰 1 行、终值是其中之一(序列化后的后写)
    public async Task Upsert_ConcurrentFourContexts_SingleRowLastWriteWins()
    {
        if (!_available) return;
        var contexts = new[] { _db, NewDb(_user), NewDb(_user), NewDb(_user) };
        try
        {
            var gate = new TaskCompletionSource();
            var titles = new[] { "W1", "W2", "W3", "W4" };
            var tasks = contexts.Select((db, i) => Task.Run(async () =>
            {
                await gate.Task;
                return await new UpsertStarStoryCommandHandler(db)
                    .Handle(new UpsertStarStoryCommand(_user, Story("race-four", titles[i])), default);
            })).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(tasks);
            results.Should().OnlyContain(r => r.IsSuccess);
            var rows = await RowsAsync(_user, "race-four");
            rows.Should().ContainSingle().Which.Title.Should().BeOneOf(titles);
        }
        finally { foreach (var c in contexts.Skip(1)) await c.DisposeAsync(); }
    }

    [Fact] // boundary: 已存行并发编辑 —— 都成功、恰 1 行、后写值胜出(其一)
    public async Task Upsert_ConcurrentEditExisting_BothSucceedSingleRow()
    {
        if (!_available) return;
        (await new UpsertStarStoryCommandHandler(_db)
            .Handle(new UpsertStarStoryCommand(_user, Story("race-edit", "Original")), default))
            .IsSuccess.Should().BeTrue();

        await using var db2 = NewDb(_user);
        var gate = new TaskCompletionSource();
        async Task<Result<Guid>> Run(InterviewsDbContext db, string title)
        {
            await gate.Task;
            return await new UpsertStarStoryCommandHandler(db)
                .Handle(new UpsertStarStoryCommand(_user, Story("race-edit", title)), default);
        }
        var t1 = Task.Run(() => Run(_db, "Edit A"));
        var t2 = Task.Run(() => Run(db2, "Edit B"));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        var rows = await RowsAsync(_user, "race-edit");
        rows.Should().ContainSingle().Which.Title.Should().BeOneOf("Edit A", "Edit B");
    }

    [Fact] // boundary: 软删后并发重存 —— 恢复同一行、都成功、不建第二行
    public async Task Upsert_ConcurrentAfterSoftDelete_RestoresSingleRow()
    {
        if (!_available) return;
        var upsert = new UpsertStarStoryCommandHandler(_db);
        (await upsert.Handle(new UpsertStarStoryCommand(_user, Story("race-revive", "V1")), default))
            .IsSuccess.Should().BeTrue();
        (await new DeleteStarStoryCommandHandler(_db)
            .Handle(new DeleteStarStoryCommand(_user, "race-revive"), default))
            .IsSuccess.Should().BeTrue();

        await using var db2 = NewDb(_user);
        var results = await BurstUpsert([_db, db2], _user, Story("race-revive", "Revived"));
        results.Should().OnlyContain(r => r.IsSuccess);
        var rows = await RowsAsync(_user, "race-revive");
        rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        rows[0].Title.Should().Be("Revived");
    }

    [Fact] // boundary: 跨用户同 clientId 并发 —— 不串扰,各建一行、各归其主
    public async Task Upsert_ConcurrentCrossUserSameClientId_Isolated()
    {
        if (!_available) return;
        await using var otherDb = NewDb(_other);
        var gate = new TaskCompletionSource();
        async Task<Result<Guid>> Run(InterviewsDbContext db, Guid user, string title)
        {
            await gate.Task;
            return await new UpsertStarStoryCommandHandler(db)
                .Handle(new UpsertStarStoryCommand(user, Story("race-iso", title)), default);
        }
        var t1 = Task.Run(() => Run(_db, _user, "Mine"));
        var t2 = Task.Run(() => Run(otherDb, _other, "Theirs"));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        (await RowsAsync(_user, "race-iso")).Should().ContainSingle()
            .Which.Title.Should().Be("Mine");
        (await RowsAsync(_other, "race-iso")).Should().ContainSingle()
            .Which.Title.Should().Be("Theirs");
    }

    [Fact] // boundary: 不同 clientId 并发新建 —— 互不误伤,各一行(同用户 SortOrder 撞值无唯一约束)
    public async Task Upsert_ConcurrentDifferentClientIds_BothCreate()
    {
        if (!_available) return;
        await using var db2 = NewDb(_user);
        var gate = new TaskCompletionSource();
        async Task<Result<Guid>> Run(InterviewsDbContext db, string cid)
        {
            await gate.Task;
            return await new UpsertStarStoryCommandHandler(db)
                .Handle(new UpsertStarStoryCommand(_user, Story(cid, "T-" + cid)), default);
        }
        var t1 = Task.Run(() => Run(_db, "race-diff-a"));
        var t2 = Task.Run(() => Run(db2, "race-diff-b"));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        (await RowsAsync(_user, "race-diff-a")).Should().ContainSingle();
        (await RowsAsync(_user, "race-diff-b")).Should().ContainSingle();
    }

    // ---------- DB 真并发:导入同族面(票78 顺手核查) ----------

    [Fact] // boundary: 故事导入并发同发同批 —— 都成功、终态恰批内行数、total 对账一致
    public async Task ImportStories_ConcurrentSameBatch_BothSucceed()
    {
        if (!_available) return;
        await using var db2 = NewDb(_user);
        var batch = new[] { Story("imp-race-1", "I1"), Story("imp-race-2", "I2"), Story("imp-race-3", "I3") };
        var gate = new TaskCompletionSource();
        async Task<Result<PlaybookImportResultDto>> Run(InterviewsDbContext db)
        {
            await gate.Task;
            return await new ImportStarStoriesCommandHandler(db)
                .Handle(new ImportStarStoriesCommand(_user, batch), default);
        }
        var t1 = Task.Run(() => Run(_db));
        var t2 = Task.Run(() => Run(db2));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        results.Should().OnlyContain(r => r.Value.Total == 3);
        // imported 对账:两方之和恰为批内新建数(赢家数全量,收敛方数 0 或重跑口径内)
        results.Sum(r => r.Value.Imported).Should().Be(3);
        foreach (var cid in new[] { "imp-race-1", "imp-race-2", "imp-race-3" })
            (await RowsAsync(_user, cid)).Should().ContainSingle();
    }

    [Fact] // boundary: 复盘导入并发同发 —— 都成功、恰 1 行归档、首导为准对账
    public async Task ImportDebriefs_ConcurrentSameItem_BothSucceedSingleRow()
    {
        if (!_available) return;
        var entryId = await SeedEntryAsync(_user);
        await using var db2 = NewDb(_user);
        var items = new[] { new LegacyDebriefImportItem(entryId.ToString(), "{\"q\":1}") };
        var gate = new TaskCompletionSource();
        async Task<Result<PlaybookImportResultDto>> Run(InterviewsDbContext db)
        {
            await gate.Task;
            return await new ImportLegacyDebriefsCommandHandler(db)
                .Handle(new ImportLegacyDebriefsCommand(_user, items), default);
        }
        var t1 = Task.Run(() => Run(_db));
        var t2 = Task.Run(() => Run(db2));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        results.Should().OnlyContain(r => r.Value.Total == 1);
        results.Sum(r => r.Value.Imported).Should().Be(1);
    }

    [Fact] // boundary: 粘贴材料导入并发同发 —— 同复盘口径
    public async Task ImportPasted_ConcurrentSameItem_BothSucceedSingleRow()
    {
        if (!_available) return;
        var entryId = await SeedEntryAsync(_user);
        await using var db2 = NewDb(_user);
        var items = new[] { new LegacyPastedImportItem(entryId.ToString(), "pasted") };
        var gate = new TaskCompletionSource();
        async Task<Result<PlaybookImportResultDto>> Run(InterviewsDbContext db)
        {
            await gate.Task;
            return await new ImportLegacyPastedCommandHandler(db)
                .Handle(new ImportLegacyPastedCommand(_user, items), default);
        }
        var t1 = Task.Run(() => Run(_db));
        var t2 = Task.Run(() => Run(db2));
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        results.Should().OnlyContain(r => r.Value.Total == 1);
        results.Sum(r => r.Value.Imported).Should().Be(1);
    }
}
