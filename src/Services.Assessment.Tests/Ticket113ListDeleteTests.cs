using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票113(A08 残余):会话列表项 DTO 补 Version 回传 —— 列表删会话可带 If-Match 钉版。
/// 断言:列表项 Version 非空且与详情 Version 同值(同一读时 xmin);以列表版本删除:
/// 陈旧 → DbUpdateConcurrencyException(409)且会话完好;新鲜 → 成功;不带 → 兼容成功。
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
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb();
        var sessions = await db.Sessions.IgnoreQueryFilters().Where(s => _users.Contains(s.UserId)).ToListAsync();
        db.Sessions.RemoveRange(sessions);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private async Task<Guid> CreateSessionAsync(Guid user, string title)
    {
        await using var db = NewDb();
        var r = await new CreateSessionCommandHandler(db).Handle(
            new CreateSessionCommand(user, title, SessionMode.TopicDrill, "EF Core", null, 5, "en"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<SessionListItemDto> ListItemAsync(Guid user, Guid sessionId)
    {
        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value.Items.Single(x => x.Id == sessionId);
    }

    private async Task<SessionDetailDto> GetDetailAsync(Guid user, Guid sessionId)
    {
        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(sessionId, user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact]
    public async Task ListItem_ExposesVersion_MatchingDetail()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user, "T113 list version");

        var item = await ListItemAsync(user, sid);
        item.Version.Should().NotBeNull("票113:列表项 DTO 须回传会话行 Version,列表删才有版本可带");
        item.Version.Should().Be((await GetDetailAsync(user, sid)).Version,
            "列表项与详情回传的是同一读时 xmin");
    }

    [Fact]
    public async Task DeleteSession_WithListVersion_StaleThrows_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user, "T113 list delete");
        var listVersion = (await ListItemAsync(user, sid)).Version;
        listVersion.Should().NotBeNull();
        var v = listVersion!.Value;

        // A 先完成会话(会话行版本推进)—— B 手里的列表版本随之陈旧
        await using (var dbA = NewDb())
            (await new CompleteSessionCommandHandler(dbA).Handle(
                new CompleteSessionCommand(sid, user, "done", null), default)).IsSuccess.Should().BeTrue();
        (await ListItemAsync(user, sid)).Version.Should().NotBe(v);

        // B 持列表旧版本删除 → 冲突,会话完好
        await using var dbB = NewDb();
        var act = () => new DeleteSessionCommandHandler(dbB).Handle(new DeleteSessionCommand(sid, user, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetDetailAsync(user, sid)).Status.Should().Be("Completed");

        // 新鲜列表版本删除 → 成功
        var v2 = (await ListItemAsync(user, sid)).Version;
        await using (var dbF = NewDb())
            (await new DeleteSessionCommandHandler(dbF).Handle(new DeleteSessionCommand(sid, user, v2), default))
                .IsSuccess.Should().BeTrue();

        // 不带版本 → 兼容成功
        var sid2 = await CreateSessionAsync(user, "T113 list delete 2");
        await using (var dbN = NewDb())
            (await new DeleteSessionCommandHandler(dbN).Handle(new DeleteSessionCommand(sid2, user), default))
                .IsSuccess.Should().BeTrue();
    }
}
