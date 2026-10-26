using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Knowledge.Application;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票111 段3 §7(K07 知识条目删除 If-Match 甲案):
/// 陈旧 Version → DbUpdateConcurrencyException(409)且条目完好;新鲜 → 成功;不带 → 兼容成功。
/// DB 门控同 Ticket101 先例。
/// </summary>
public sealed class Ticket111DeleteTests : IAsyncLifetime
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
            new CreateKnowledgeCommand(user, "T111 删除条目", "Backend", null,
                "T111 question?", "Personal", 3, 3), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        await using var db2 = NewDb(user);
        var g = await new GetKnowledgeItemQueryHandler(db2, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, r.Value), default);
        g.IsSuccess.Should().BeTrue(g.Error.Description);
        return (r.Value, g.Value.Version);
    }

    [Fact]
    public async Task DeleteKnowledge_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var (id, v) = await SeedAsync(user);

        // A 先改条目(版本推进)
        await using (var dbA = NewDb(user))
            (await new UpdateMyAnswerCommandHandler(dbA).Handle(
                new UpdateMyAnswerCommand(user, id, "A answer", v), default)).IsSuccess.Should().BeTrue();

        // B 持旧版本删除 → 冲突,条目完好
        await using var dbB = NewDb(user);
        var act = () => new DeleteKnowledgeCommandHandler(dbB).Handle(new DeleteKnowledgeCommand(user, id, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using (var dbR = NewDb(user))
        {
            var g = await new GetKnowledgeItemQueryHandler(dbR, TimeProvider.System).Handle(
                new GetKnowledgeItemQuery(user, id), default);
            g.IsSuccess.Should().BeTrue();
            g.Value.MyAnswer.Should().Be("A answer");
        }

        var v2 = await GetVersionAsync(user, id);
        await using (var dbF = NewDb(user))
            (await new DeleteKnowledgeCommandHandler(dbF).Handle(new DeleteKnowledgeCommand(user, id, v2), default))
                .IsSuccess.Should().BeTrue();

        var (id2, _) = await SeedAsync(user);
        await using (var dbN = NewDb(user))
            (await new DeleteKnowledgeCommandHandler(dbN).Handle(new DeleteKnowledgeCommand(user, id2), default))
                .IsSuccess.Should().BeTrue();
    }

    private async Task<uint> GetVersionAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var g = await new GetKnowledgeItemQueryHandler(db, TimeProvider.System).Handle(
            new GetKnowledgeItemQuery(user, id), default);
        g.IsSuccess.Should().BeTrue(g.Error.Description);
        return g.Value.Version;
    }
}
