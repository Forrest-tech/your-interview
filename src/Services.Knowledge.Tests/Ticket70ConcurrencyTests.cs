using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票70 §7:Knowledge 详情全量更新的乐观并发(读时 xmin 版本回传)。
/// 病根:原 Update 全程「新 DbContext 现查现改」,串行双页签 B 保存时读到的
/// 已是 A 保存后的新 xmin,B 的旧表单值全量写入必然成功 → A 的改动被静默覆盖。
/// 修复:详情 DTO 暴露 Version(读时 xmin)→ 命令回传 → Handler 把 xmin 的
/// OriginalValue 钉回读时值,过期即 DbUpdateConcurrencyException
/// (全局映射 409 concurrency_conflict,票07 口径)。Version=null 不检查(兼容)。
/// 本组走真实持久化 seam,两「页签」= 两次独立 Get(同读一版本)+ 各自新 DbContext
/// 的 Handler 调用,与活体时序同构。DB 门控同仓内先例:连不上库时跳过;
/// 每例独立随机 UserId 隔离,Dispose 清本例用户数据。
/// boundary 逐例标注:同/异字段冲突、Version=null、版本推进后连续编辑、
/// 软删条目、票61 question 空值护栏与 Version 共存。
/// </summary>
public sealed class Ticket70ConcurrencyTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb(Guid.NewGuid());
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb(_users[0]);
        var items = await db.Items.IgnoreQueryFilters().Where(i => _users.Contains(i.UserId)).ToListAsync();
        db.Items.RemoveRange(items);
        await db.SaveChangesAsync();
    }

    private static KnowledgeDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<KnowledgeDbContext>().UseNpgsql(Cs).Options;
        return new KnowledgeDbContext(opts, new TestCurrentUser(currentUser));
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private async Task<Guid> CreateAsync(Guid user)
    {
        await using var db = NewDb(user);
        var r = await new CreateKnowledgeCommandHandler(db).Handle(
            new CreateKnowledgeCommand(user, "T70 并发条目", "Backend", null,
                "T70 question?", "Personal", 3, 3), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        // 建单命令不带正文块:再用无版本全量更新把 Concept/MyAnswer 垫到 Upd 的基准值,
        // 使后续断言里的 "orig concept"/"orig answer" 是库中真实初值。
        await UpdateAsync(user, Upd(user, r.Value, null));
        return r.Value;
    }

    private async Task<KnowledgeItemDto> GetAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetKnowledgeItemQueryHandler(db, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    /// <summary>全量更新命令:除被测字段外与种子一致(与详情保存负载同形状)。</summary>
    private static UpdateKnowledgeCommand Upd(Guid user, Guid id, uint? version,
        string? concept = "orig concept", string? myAnswer = "orig answer")
        => new(user, id, "T70 并发条目", "Backend", null, "T70 question?", 3, 3,
               null, concept, myAnswer, null, null, null, null, null, Version: version);

    private async Task UpdateAsync(Guid user, UpdateKnowledgeCommand cmd)
    {
        await using var db = NewDb(user);
        var r = await new UpdateKnowledgeCommandHandler(db).Handle(cmd, default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
    }

    [Fact] // 核心:两页签同读一版本 → 先存成功 → 后存(同字段)持旧版本应冲突失败,先存数据完好
    public async Task DualTab_SameField_StaleVersion_Throws_FirstSaveIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        var tabA = await GetAsync(user, id);
        var tabB = await GetAsync(user, id);
        tabA.Version.Should().BeGreaterThan(0u);
        tabB.Version.Should().Be(tabA.Version);

        await UpdateAsync(user, Upd(user, id, tabA.Version, concept: "A concept"));

        await using var dbB = NewDb(user);
        var act = () => new UpdateKnowledgeCommandHandler(dbB).Handle(
            Upd(user, id, tabB.Version, concept: "B concept"), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var final = await GetAsync(user, id);
        final.ConceptExplanation.Should().Be("A concept");
        final.Version.Should().NotBe(tabA.Version);
    }

    [Fact] // boundary: 异字段 —— xmin 是行级令牌,B 改另一字段(MyAnswer)持旧版本同样冲突,不做字段级合并
    public async Task DualTab_DifferentField_StaleVersion_AlsoThrows()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        var v = (await GetAsync(user, id)).Version;

        await UpdateAsync(user, Upd(user, id, v, concept: "A concept v2"));

        await using var dbB = NewDb(user);
        var act = () => new UpdateKnowledgeCommandHandler(dbB).Handle(
            Upd(user, id, v, concept: "orig concept", myAnswer: "B answer"), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var final = await GetAsync(user, id);
        final.ConceptExplanation.Should().Be("A concept v2");
        final.MyAnswer.Should().Be("orig answer");
    }

    [Fact] // boundary: Version=null(老调用方/脚本)不做版本检查 —— 后存仍成功(向后兼容,行为同票70前)
    public async Task NullVersion_SkipsCheck_SecondSaveSucceeds()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        var v = (await GetAsync(user, id)).Version;

        await UpdateAsync(user, Upd(user, id, v, concept: "A concept"));
        await UpdateAsync(user, Upd(user, id, null, concept: "legacy overwrite"));

        (await GetAsync(user, id)).ConceptExplanation.Should().Be("legacy overwrite");
    }

    [Fact] // 不变项:单页签正常编辑 —— 每次保存后版本推进,拿新版本可连续再存,不误报冲突
    public async Task SingleTab_FreshVersion_SequentialEditsSucceed_VersionAdvances()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);

        var v1 = (await GetAsync(user, id)).Version;
        await UpdateAsync(user, Upd(user, id, v1, concept: "edit 1"));
        var v2 = (await GetAsync(user, id)).Version;
        v2.Should().NotBe(v1);

        await UpdateAsync(user, Upd(user, id, v2, concept: "edit 2"));
        var final = await GetAsync(user, id);
        final.ConceptExplanation.Should().Be("edit 2");
        final.Version.Should().NotBe(v2);
    }

    [Fact] // boundary: 任意伪造/过期版本(从未存在过的旧值)同样冲突,不限于「另一个真实读到的版本」
    public async Task BogusStaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        var v = (await GetAsync(user, id)).Version;

        await using var db = NewDb(user);
        var act = () => new UpdateKnowledgeCommandHandler(db).Handle(
            Upd(user, id, v + 999_999u, concept: "bogus"), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).ConceptExplanation.Should().Be("orig concept");
    }

    [Fact] // boundary: 软删条目 —— 查询过滤先行,持任何版本更新都走 NotFound,不抛并发异常、数据不复活
    public async Task SoftDeletedItem_Update_ReturnsNotFound_NotConcurrency()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateAsync(user);
        var v = (await GetAsync(user, id)).Version;

        await using (var db = NewDb(user))
        {
            var r = await new DeleteKnowledgeCommandHandler(db).Handle(
                new DeleteKnowledgeCommand(user, id), default);
            r.IsSuccess.Should().BeTrue();
        }

        await using var db2 = NewDb(user);
        var r2 = await new UpdateKnowledgeCommandHandler(db2).Handle(
            Upd(user, id, v, concept: "zombie"), default);
        r2.IsSuccess.Should().BeFalse();
        r2.Error.Type.Should().Be(ErrorType.NotFound);

        await using var db3 = NewDb(user);
        var g = await new GetKnowledgeItemQueryHandler(db3, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, id), default);
        g.IsSuccess.Should().BeFalse();
    }

    [Fact] // 不变项:票61 的 question 空值护栏与 Version 共存 —— 带版本也好、不带也好,空 question 一律被校验器拒绝
    public void QuestionEmptyGuard_Unchanged_WithAndWithoutVersion()
    {
        var validator = new UpdateKnowledgeCommandValidator();
        var withVersion = Upd(Guid.NewGuid(), Guid.NewGuid(), 123u) with { Question = "" };
        var withoutVersion = Upd(Guid.NewGuid(), Guid.NewGuid(), null) with { Question = "" };
        validator.Validate(withVersion).Errors.Should().Contain(e => e.PropertyName == "Question");
        validator.Validate(withoutVersion).Errors.Should().Contain(e => e.PropertyName == "Question");
        // 合法 question + 任一 Version 形态不被该护栏误伤
        validator.Validate(Upd(Guid.NewGuid(), Guid.NewGuid(), 123u)).Errors
            .Should().NotContain(e => e.PropertyName == "Question");
    }
}
