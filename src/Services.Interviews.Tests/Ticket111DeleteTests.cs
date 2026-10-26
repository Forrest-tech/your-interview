using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票111 段3 §7(Interviews 删除 If-Match 甲案):
/// I03 删条目 / I05 删 STAR 故事 —— 陈旧 Version → DbUpdateConcurrencyException(409)且数据完好;
/// 新鲜 → 成功;不带 → 兼容成功。I04(star-stories PUT upsert)不在本票面,未触。
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
        var stories = await db.StarStories.IgnoreQueryFilters().Where(s => _users.Contains(s.UserId)).ToListAsync();
        db.StarStories.RemoveRange(stories);
        var entries = await db.Entries.IgnoreQueryFilters().Where(e => _users.Contains(e.UserId)).ToListAsync();
        db.Entries.RemoveRange(entries);
        await db.SaveChangesAsync();
    }

    private static InterviewsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        return new InterviewsDbContext(opts, new FixedCurrentUser(currentUser));
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }
    private static string Uq(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    // ---------- I03 删条目 ----------

    private async Task<Guid> CreateEntryAsync(Guid user, string company)
    {
        await using var db = NewDb(user);
        var r = await new CreateEntryCommandHandler(db).Handle(
            new CreateEntryCommand(user, null, company, "Orig Role", null,
                "orig profile", "orig jd", "orig sum", null, "OrigInt", null, null, "orig notes"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<InterviewEntryDto> GetAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetEntryQueryHandler(db).Handle(new GetEntryQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact]
    public async Task DeleteEntry_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T111Co"));
        var v = (await GetAsync(user, id)).Version;

        // A 先改条目(版本推进)
        await using (var dbA = NewDb(user))
            (await new UpdateEntryCommandHandler(dbA).Handle(
                new UpdateEntryCommand(user, id, Uq("T111Co"), "Role A", "orig profile", "orig jd", "orig sum", 1, null,
                    null, "OrigInt", null, null, "A notes", Version: v), default)).IsSuccess.Should().BeTrue();

        // B 持旧版本删除 → 冲突,条目完好
        await using var dbB = NewDb(user);
        var act = () => new DeleteEntryCommandHandler(dbB).Handle(new DeleteEntryCommand(user, id, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).Role.Should().Be("Role A");

        var v2 = (await GetAsync(user, id)).Version;
        await using (var dbF = NewDb(user))
            (await new DeleteEntryCommandHandler(dbF).Handle(new DeleteEntryCommand(user, id, v2), default))
                .IsSuccess.Should().BeTrue();

        var id2 = await CreateEntryAsync(user, Uq("T111Co2"));
        await using (var dbN = NewDb(user))
            (await new DeleteEntryCommandHandler(dbN).Handle(new DeleteEntryCommand(user, id2), default))
                .IsSuccess.Should().BeTrue();
    }

    // ---------- I05 删 STAR 故事 ----------

    private async Task<List<StarStoryDto>> ListStoriesAsync(Guid user)
    {
        await using var db = NewDb(user);
        var r = await new ListStarStoriesQueryHandler(db).Handle(new ListStarStoriesQuery(user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value.ToList();
    }

    private async Task UpsertStoryAsync(Guid user, string clientId, string title)
    {
        await using var db = NewDb(user);
        var r = await new UpsertStarStoryCommandHandler(db).Handle(
            new UpsertStarStoryCommand(user, new StarStoryInput(clientId, title, "s", "t", "a", "r", null)), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
    }

    [Fact]
    public async Task DeleteStarStory_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var clientId = "t111-" + Guid.NewGuid().ToString("N")[..8];
        await UpsertStoryAsync(user, clientId, "Story v1");
        var v = (await ListStoriesAsync(user)).Single(s => s.ClientId == clientId).Version;
        v.Should().BeGreaterThan(0u);

        // A 编辑故事(经 PUT upsert 路径,版本推进)—— I04 语义本身未动,这里只借它推进版本
        await UpsertStoryAsync(user, clientId, "Story A edit");
        var v2 = (await ListStoriesAsync(user)).Single(s => s.ClientId == clientId).Version;
        v2.Should().NotBe(v);

        // B 持旧版本删除 → 冲突,故事完好
        await using var dbB = NewDb(user);
        var act = () => new DeleteStarStoryCommandHandler(dbB).Handle(new DeleteStarStoryCommand(user, clientId, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await ListStoriesAsync(user)).Single(s => s.ClientId == clientId).Title.Should().Be("Story A edit");

        // 新鲜版本删除 → 成功(列表不再出现)
        await using (var dbF = NewDb(user))
            (await new DeleteStarStoryCommandHandler(dbF).Handle(new DeleteStarStoryCommand(user, clientId, v2), default))
                .IsSuccess.Should().BeTrue();
        (await ListStoriesAsync(user)).Should().NotContain(s => s.ClientId == clientId);

        // 不带版本 → 兼容成功
        var clientId2 = "t111-" + Guid.NewGuid().ToString("N")[..8];
        await UpsertStoryAsync(user, clientId2, "Story 2");
        await using (var dbN = NewDb(user))
            (await new DeleteStarStoryCommandHandler(dbN).Handle(new DeleteStarStoryCommand(user, clientId2), default))
                .IsSuccess.Should().BeTrue();
        (await ListStoriesAsync(user)).Should().NotContain(s => s.ClientId == clientId2);
    }
}
