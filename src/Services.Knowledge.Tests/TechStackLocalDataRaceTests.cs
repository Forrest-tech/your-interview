using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;
using static YourInterview.Services.Knowledge.Application.TechStackLocalDataRules;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票92 §7:AddCustomCategory 并发兜底 —— 纯裁决矩阵(先查/竞态回查 ×
/// 无行/活跃/软删 全组合)+ DB 门控真并发(两 DbContext 同刻同发)。
/// boundary 类别:并发重复(同名同发)、顺序重复幂等(既有 TechStackLocalDataTests
/// 已覆盖,此处只验并发形态)、不同名并发不误伤、软删恢复竞态、
/// 超长名与票83 的 100 上限口径不冲突(校验器先行,竞态裁决不截断/不放行)。
/// DB 门控同既有先例:连不上库时 DB 用例跳过,纯裁决用例为准。
/// </summary>
public sealed class TechStackLocalDataRaceTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres;Search Path=knowledge";

    private readonly Guid _user = Guid.NewGuid();
    private KnowledgeDbContext _db = null!;
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
            var cats = await _db.CustomCategories.IgnoreQueryFilters()
                .Where(c => c.UserId == _user).ToListAsync();
            _db.CustomCategories.RemoveRange(cats);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static KnowledgeDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<KnowledgeDbContext>().UseNpgsql(Cs).Options;
        return new KnowledgeDbContext(opts, new TestCurrentUser(currentUser));
    }

    // ---------- 纯裁决:先查(raceLost=false) ----------

    [Theory] // boundary: 并发重复/顺序重复 —— 先查三态各自的处置(不查库、不写库即可裁决)
    [InlineData(ExistingCategoryState.None, false, CategoryAddDecision.Create)]
    [InlineData(ExistingCategoryState.Active, false, CategoryAddDecision.Satisfied)]
    [InlineData(ExistingCategoryState.SoftDeleted, false, CategoryAddDecision.Restore)]
    // ---------- 纯裁决:竞态回查(raceLost=true) ----------
    [InlineData(ExistingCategoryState.None, true, CategoryAddDecision.Conflict)] // 撞了别的约束,不冒充成功
    [InlineData(ExistingCategoryState.Active, true, CategoryAddDecision.Satisfied)] // 赢家已建 → 幂等成功
    [InlineData(ExistingCategoryState.SoftDeleted, true, CategoryAddDecision.Restore)] // 赢家行是软删行 → 恢复
    public void DecideAddCategory_Matrix(ExistingCategoryState state, bool raceLost, CategoryAddDecision expected)
        => DecideAddCategory(state, raceLost).Should().Be(expected);

    [Fact] // boundary: 超长名口径不冲突 —— 恰 100 的名走校验放行后裁决仍是 Create;101 由校验器先拒(票83),裁决层不截断
    public void DecideAddCategory_LengthBoundaryUnchanged()
    {
        var v = new AddCustomCategoryCommandValidator();
        v.Validate(new AddCustomCategoryCommand(_user, new string('a', 100))).IsValid.Should().BeTrue();
        v.Validate(new AddCustomCategoryCommand(_user, new string('a', 101))).IsValid.Should().BeFalse();
        DecideAddCategory(ExistingCategoryState.None, false).Should().Be(CategoryAddDecision.Create);
    }

    // ---------- DB 真并发 ----------

    [Fact] // boundary: 并发重复 —— 两 Context 同刻同名 Add:都成功(204 语义),终态恰 1 行
    public async Task Add_ConcurrentSameName_BothSucceedExactlyOneRow()
    {
        if (!_available) return;
        await using var db2 = NewDb(_user);
        var h1 = new AddCustomCategoryCommandHandler(_db);
        var h2 = new AddCustomCategoryCommandHandler(db2);
        var gate = new TaskCompletionSource();
        async Task<YourInterview.BuildingBlocks.Results.Result> Run(AddCustomCategoryCommandHandler h)
        {
            await gate.Task;
            return await h.Handle(new AddCustomCategoryCommand(_user, "RaceSame"), default);
        }
        var t1 = Run(h1); var t2 = Run(h2);
        gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);

        var rows = await _db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == _user && c.Name == "RaceSame").ToListAsync();
        rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
    }

    [Fact] // boundary: 不同名并发 —— 互不误伤,各建一行,无假冲突
    public async Task Add_ConcurrentDifferentNames_BothCreate()
    {
        if (!_available) return;
        await using var db2 = NewDb(_user);
        var t1 = new AddCustomCategoryCommandHandler(_db)
            .Handle(new AddCustomCategoryCommand(_user, "RaceA"), default);
        var t2 = new AddCustomCategoryCommandHandler(db2)
            .Handle(new AddCustomCategoryCommand(_user, "RaceB"), default);
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        var rows = await _db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == _user && (c.Name == "RaceA" || c.Name == "RaceB")).ToListAsync();
        rows.Should().HaveCount(2);
    }

    [Fact] // boundary: 并发重复(四方同发)—— 屏障同刻 4 Context 同名 Add:全成功、恰 1 行
    public async Task Add_ConcurrentFourContexts_ExactlyOneRow()
    {
        if (!_available) return;
        var contexts = new[] { _db, NewDb(_user), NewDb(_user), NewDb(_user) };
        try
        {
            var gate = new TaskCompletionSource();
            var tasks = contexts.Select(db => Task.Run(async () =>
            {
                await gate.Task;
                return await new AddCustomCategoryCommandHandler(db)
                    .Handle(new AddCustomCategoryCommand(_user, "RaceFour"), default);
            })).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(tasks);
            results.Should().OnlyContain(r => r.IsSuccess);
            var rows = await _db.CustomCategories.IgnoreQueryFilters()
                .Where(c => c.UserId == _user && c.Name == "RaceFour").ToListAsync();
            rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        }
        finally
        {
            foreach (var c in contexts.Skip(1)) await c.DisposeAsync();
        }
    }

    [Fact] // boundary: 大小写变体(顺序)—— 既有口径锁定:后到变体按已存在收敛,恰 1 行
    // 注:大小写变体「并发」同发是另一形态 —— DB 唯一索引大小写敏感、无约束可撞,
    // 应用层查重兜不住同刻变体(实测 2 行),需 lower() 函数索引迁移,非本票范围,
    // 已在 evidence-92/CLOSE-REPORT §8 记为观察项建议另票。
    public async Task Add_SequentialCaseVariant_ExactlyOneRow()
    {
        if (!_available) return;
        var add = new AddCustomCategoryCommandHandler(_db);
        (await add.Handle(new AddCustomCategoryCommand(_user, "SeqCase"), default)).IsSuccess.Should().BeTrue();
        (await add.Handle(new AddCustomCategoryCommand(_user, "SEQCASE"), default)).IsSuccess.Should().BeTrue();
        var rows = await _db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == _user && c.Name.ToLower() == "seqcase").ToListAsync();
        rows.Should().ContainSingle();
    }

    [Fact] // boundary: 大小写变体(并发)—— 票96 §7:四 Context 屏障同发,两发 "RaceCase"、两发 "RACECASE":都成功(204 语义),终态恰 1 行
    public async Task Add_ConcurrentCaseVariant_Barrier_ExactlyOneRow()
    {
        if (!_available) return;
        var contexts = new[] { _db, NewDb(_user), NewDb(_user), NewDb(_user) };
        var names = new[] { "RaceCase", "RaceCase", "RACECASE", "RACECASE" };
        try
        {
            var gate = new TaskCompletionSource();
            var tasks = contexts.Select((db, i) => Task.Run(async () =>
            {
                await gate.Task;
                return await new AddCustomCategoryCommandHandler(db)
                    .Handle(new AddCustomCategoryCommand(_user, names[i]), default);
            })).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(tasks);
            results.Should().OnlyContain(r => r.IsSuccess);
            var rows = await _db.CustomCategories.IgnoreQueryFilters()
                .Where(c => c.UserId == _user && c.Name.ToLower() == "racecase").ToListAsync();
            rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
        }
        finally
        {
            foreach (var c in contexts.Skip(1)) await c.DisposeAsync();
        }
    }

    [Fact] // boundary: 软删后并发重加 —— 恢复同一行(不建第二行),两方都成功
    public async Task Add_ConcurrentAfterSoftDelete_RestoresSingleRow()
    {
        if (!_available) return;
        var add = new AddCustomCategoryCommandHandler(_db);
        (await add.Handle(new AddCustomCategoryCommand(_user, "RaceRevive"), default)).IsSuccess.Should().BeTrue();
        (await new RemoveCustomCategoryCommandHandler(_db)
            .Handle(new RemoveCustomCategoryCommand(_user, "RaceRevive"), default)).IsSuccess.Should().BeTrue();

        await using var db2 = NewDb(_user);
        var t1 = new AddCustomCategoryCommandHandler(_db)
            .Handle(new AddCustomCategoryCommand(_user, "RaceRevive"), default);
        var t2 = new AddCustomCategoryCommandHandler(db2)
            .Handle(new AddCustomCategoryCommand(_user, "RaceRevive"), default);
        var results = await Task.WhenAll(t1, t2);
        results.Should().OnlyContain(r => r.IsSuccess);
        var rows = await _db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == _user && c.Name == "RaceRevive").ToListAsync();
        rows.Should().ContainSingle().Which.IsDeleted.Should().BeFalse();
    }
}
