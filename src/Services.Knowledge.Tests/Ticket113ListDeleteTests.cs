using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票113(K07 残余):知识列表项 DTO 补 Version 回传 —— 批量删走逐项 DELETE
/// (前端 forkJoin 逐项调用,后端无批量端点,开工盘点定版),逐项以列表项版本
/// If-Match 钉版。三路:陈旧 → DbUpdateConcurrencyException(409)且条目完好;
/// 新鲜 → 成功;不带 → 兼容成功。逐项独立:一项陈旧不影响同批另一项新鲜删除。
/// DB 门控同 Ticket111 先例。
/// </summary>
public sealed class Ticket113ListDeleteTests : IAsyncLifetime
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

    private async Task<Guid> SeedAsync(Guid user, string title)
    {
        await using var db = NewDb(user);
        var r = await new CreateKnowledgeCommandHandler(db).Handle(
            new CreateKnowledgeCommand(user, title, "Backend", null,
                "T113 question?", "Personal", 3, 3), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<KnowledgeListItemDto> ListItemAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new ListKnowledgeQueryHandler(db, TimeProvider.System).Handle(
            new ListKnowledgeQuery(user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value.Items.Single(x => x.Id == id);
    }

    private async Task<KnowledgeItemDto> GetAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetKnowledgeItemQueryHandler(db, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact]
    public async Task ListItem_ExposesVersion_MatchingDetail()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await SeedAsync(user, "T113 列表版本条目");

        var item = await ListItemAsync(user, id);
        item.Version.Should().NotBeNull("票113:列表项 DTO 须回传条目行 Version,批量删逐项才有版本可带");
        item.Version.Should().Be((uint?)(await GetAsync(user, id)).Version,
            "列表项与详情回传的是同一读时 xmin");
    }

    [Fact]
    public async Task BatchDelete_PerItem_StaleThrowsAndIntact_FreshSucceeds_NoVersionCompat()
    {
        if (!_available) return;
        var user = NewUser();
        var idStale = await SeedAsync(user, "T113 批删陈旧项");
        var idFresh = await SeedAsync(user, "T113 批删新鲜项");
        var vStale = (await ListItemAsync(user, idStale)).Version;
        var vFresh = (await ListItemAsync(user, idFresh)).Version;
        vStale.Should().NotBeNull();
        vFresh.Should().NotBeNull();

        // A 先改陈旧项(版本推进);新鲜项不动 —— 模拟批删期间单项被另一页签改动
        await using (var dbA = NewDb(user))
            (await new UpdateMyAnswerCommandHandler(dbA).Handle(
                new UpdateMyAnswerCommand(user, idStale, "A answer", (await GetAsync(user, idStale)).Version), default))
                .IsSuccess.Should().BeTrue();
        (await ListItemAsync(user, idStale)).Version.Should().NotBe(vStale);

        // 逐项删:新鲜项成功(陈旧项的冲突不殃及同批其他项)
        await using (var dbF = NewDb(user))
            (await new DeleteKnowledgeCommandHandler(dbF).Handle(
                new DeleteKnowledgeCommand(user, idFresh, vFresh), default)).IsSuccess.Should().BeTrue();

        // 逐项删:陈旧项 → 冲突,条目完好(A 的改动还在)
        await using var dbB = NewDb(user);
        var act = () => new DeleteKnowledgeCommandHandler(dbB).Handle(
            new DeleteKnowledgeCommand(user, idStale, vStale), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, idStale)).MyAnswer.Should().Be("A answer");

        // 陈旧项以新鲜列表版本重试 → 成功(前端保留勾选+刷新后重试的口径)
        var vRetry = (await ListItemAsync(user, idStale)).Version;
        await using (var dbR = NewDb(user))
            (await new DeleteKnowledgeCommandHandler(dbR).Handle(
                new DeleteKnowledgeCommand(user, idStale, vRetry), default)).IsSuccess.Should().BeTrue();

        // 不带版本 → 兼容成功
        var idCompat = await SeedAsync(user, "T113 批删兼容项");
        await using (var dbN = NewDb(user))
            (await new DeleteKnowledgeCommandHandler(dbN).Handle(
                new DeleteKnowledgeCommand(user, idCompat), default)).IsSuccess.Should().BeTrue();
    }
}
