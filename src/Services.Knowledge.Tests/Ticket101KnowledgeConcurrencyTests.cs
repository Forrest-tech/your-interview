using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票101 §7:Knowledge 单字段端点(我的答案/推荐答案/题目/FromTech/掌握度)的
/// 乐观并发,口径同票70 全量更新。定性背景(第一段活体):详情已回传 xmin
/// Version,但单字段命令不收不验——同条目不同字段在两页签交错编辑时,B 页签
/// 持旧表单保存即便只改自己字段也无冲突信号,且与票70 已修的全量保存互不知情。
/// 两「页签」= 两次独立 Get 同读一版本 + 各自新 DbContext Handler。
/// DB 门控同 Ticket70 先例:连不上库跳过;每例独立随机 UserId,Dispose 清数据。
/// </summary>
public sealed class Ticket101KnowledgeConcurrencyTests : IAsyncLifetime
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

    private async Task<(Guid Id, uint Version)> SeedAsync(Guid user)
    {
        await using var db = NewDb(user);
        var r = await new CreateKnowledgeCommandHandler(db).Handle(
            new CreateKnowledgeCommand(user, "T101 并发条目", "Backend", null,
                "T101 question?", "Personal", 3, 3), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        var dto = await GetAsync(user, r.Value);
        return (r.Value, dto.Version);
    }

    private async Task<KnowledgeItemDto> GetAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetKnowledgeItemQueryHandler(db, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact] // K01 我的答案:B 持旧版本(条目已被 A 改)应冲突
    public async Task Answer_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        await using (var dbA = NewDb(user))
            (await new UpdateMyAnswerCommandHandler(dbA).Handle(
                new UpdateMyAnswerCommand(user, id, "A answer", v), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateMyAnswerCommandHandler(dbB).Handle(
            new UpdateMyAnswerCommand(user, id, "B answer", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).MyAnswer.Should().Be("A answer");
    }

    [Fact] // K02 推荐答案:同上
    public async Task BetterAnswer_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        await using (var dbA = NewDb(user))
            (await new UpdateBetterAnswerCommandHandler(dbA).Handle(
                new UpdateBetterAnswerCommand(user, id, "A better", v), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateBetterAnswerCommandHandler(dbB).Handle(
            new UpdateBetterAnswerCommand(user, id, "B better", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // K03 题目:同上
    public async Task Question_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        await using (var dbA = NewDb(user))
            (await new UpdateQuestionCommandHandler(dbA).Handle(
                new UpdateQuestionCommand(user, id, "A question?", v), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateQuestionCommandHandler(dbB).Handle(
            new UpdateQuestionCommand(user, id, "B question?", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // K04 FromTech 一次写回:同上
    public async Task FromTech_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        await using (var dbA = NewDb(user))
            (await new UpdateFromTechCommandHandler(dbA).Handle(
                new UpdateFromTechCommand(user, id, "A title", "A better", v), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateFromTechCommandHandler(dbB).Handle(
            new UpdateFromTechCommand(user, id, "B title", "B better", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // K06 掌握度:B 持旧版本应冲突,A 的掌握度完好
    public async Task Mastery_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        await using (var dbA = NewDb(user))
            (await new ChangeMasteryCommandHandler(dbA).Handle(
                new ChangeMasteryCommand(user, id, "Mastered", v), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new ChangeMasteryCommandHandler(dbB).Handle(
            new ChangeMasteryCommand(user, id, "Learning", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).Mastery.Should().Be("Mastered");
    }

    [Fact] // boundary: 单字段端点 Version=null 兼容 —— 不钉版照常成功
    public async Task SingleField_NullVersion_SkipsCheck()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        await using (var dbA = NewDb(user))
            (await new UpdateMyAnswerCommandHandler(dbA).Handle(
                new UpdateMyAnswerCommand(user, id, "A answer", v), default)).IsSuccess.Should().BeTrue();
        await using (var dbB = NewDb(user))
            (await new UpdateMyAnswerCommandHandler(dbB).Handle(
                new UpdateMyAnswerCommand(user, id, "legacy", null), default)).IsSuccess.Should().BeTrue();
        (await GetAsync(user, id)).MyAnswer.Should().Be("legacy");
    }
}
