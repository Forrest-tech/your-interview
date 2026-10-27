using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Application.Admin;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Identity.Tests;

/// <summary>测试用当前用户(ICurrentUser 最小实现)。</summary>
public sealed class StubCurrentUser(Guid? userId) : ICurrentUser
{
    public Guid? UserId => userId;
    public string? Email => "admin@test.local";
    public bool IsAuthenticated => true;
    public string? IpAddress => null;
    public string? UserAgent => null;
    public IReadOnlyList<string> Roles => ["Admin"];
}

/// <summary>
/// 票101 §7:Identity 详情编辑的乐观并发(口径同票70)。本项目为本票新建
/// (Identity 此前无 Tests 项目;IdentityDbContext 无当前用户全局过滤器,
/// Handler 以命令内 UserId 定位)。定性背景(第一段活体):资料 PUT、管理员
/// 改用户、管理员改角色均全量覆盖且不回传版本,串行双页签静默覆盖成立。
/// 两「页签」= 两次独立读同一 Version + 各自新 DbContext Handler。
/// DB 门控同仓内先例:连不上库跳过;每例独立随机用户/角色,Dispose 清理。
/// </summary>
public sealed class Ticket101ConcurrencyTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _userIds = [];
    private readonly List<Guid> _roleIds = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        await using var db = NewDb();
        var users = await db.Users.IgnoreQueryFilters().Where(u => _userIds.Contains(u.Id)).ToListAsync();
        db.Users.RemoveRange(users);
        var roles = await db.Roles.IgnoreQueryFilters().Where(r => _roleIds.Contains(r.Id)).ToListAsync();
        db.Roles.RemoveRange(roles);
        await db.SaveChangesAsync();
    }

    private static IdentityDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(Cs).Options;
        return new IdentityDbContext(opts);
    }

    private static string Uq(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";

    private async Task<Guid> SeedUserAsync(string displayName)
    {
        await using var db = NewDb();
        var u = new AppUser(Uq("t101"), displayName, "x-hash");
        db.Users.Add(u);
        await db.SaveChangesAsync();
        _userIds.Add(u.Id);
        return u.Id;
    }

    private async Task<uint> UserVersionAsync(Guid id)
    {
        await using var db = NewDb();
        var r = await new GetCurrentUserQueryHandler(db, new UserPermissionResolver(db))
            .Handle(new GetCurrentUserQuery(id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value.Version;
    }

    [Fact] // D01 资料:两页签同读一版本,A 先存,B 持旧版本应冲突,A 显示名完好
    public async Task Profile_DualTab_StaleVersion_Throws_FirstSaveIntact()
    {
        if (!_available) return;
        var id = await SeedUserAsync("Orig Name");
        var v = await UserVersionAsync(id);
        v.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb())
            (await new UpdateProfileCommandHandler(dbA, new UserPermissionResolver(dbA)).Handle(
                new UpdateProfileCommand(id, "A Name", null, null, null, v), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb();
        var act = () => new UpdateProfileCommandHandler(dbB, new UserPermissionResolver(dbB)).Handle(
            new UpdateProfileCommand(id, "B Name", null, null, null, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var dbR = NewDb();
        (await dbR.Users.AsNoTracking().SingleAsync(u => u.Id == id)).DisplayName.Should().Be("A Name");
    }

    [Fact] // D01 boundary: Version=null 旧调用方兼容
    public async Task Profile_NullVersion_SkipsCheck()
    {
        if (!_available) return;
        var id = await SeedUserAsync("Orig Name");
        var v = await UserVersionAsync(id);

        await using (var dbA = NewDb())
            (await new UpdateProfileCommandHandler(dbA, new UserPermissionResolver(dbA)).Handle(
                new UpdateProfileCommand(id, "A Name", null, null, null, v), default)).IsSuccess.Should().BeTrue();
        await using (var dbB = NewDb())
            (await new UpdateProfileCommandHandler(dbB, new UserPermissionResolver(dbB)).Handle(
                new UpdateProfileCommand(id, "legacy", null, null, null, null), default)).IsSuccess.Should().BeTrue();
    }

    [Fact] // D02 管理员改用户:B 持旧版本应冲突(操作者为另一管理员,避自锁卫兵)
    public async Task AdminUpdateUser_StaleVersion_Throws()
    {
        if (!_available) return;
        var target = await SeedUserAsync("Orig Target");
        var admin = await SeedUserAsync("Admin Op");
        uint v;
        await using (var db = NewDb())
        {
            var r = await new AdminUserQueryHandler(db).Handle(
                new AdminUserQuery("t101", null, null, 1, 50), default);
            v = r.Value.Items.Single(i => i.Id == target).Version;
        }
        v.Should().BeGreaterThan(0u);
        var cu = new StubCurrentUser(admin);

        // 票241:双写 handler 的新依赖(本票用户未映射,双写路径不触发,仅补构造参数)。
        var logto = Substitute.For<ILogtoManagementClient>();
        var logtoOptions = Options.Create(new LogtoManagementOptions());
        await using (var dbA = NewDb())
            (await new AdminUpdateUserCommandHandler(dbA, cu, logto, logtoOptions).Handle(
                new AdminUpdateUserCommand(target, "A Target", true, false, [], v), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb();
        var act = () => new AdminUpdateUserCommandHandler(dbB, cu, logto, logtoOptions).Handle(
            new AdminUpdateUserCommand(target, "B Target", true, false, [], v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var dbR = NewDb();
        (await dbR.Users.AsNoTracking().SingleAsync(u => u.Id == target)).DisplayName.Should().Be("A Target");
    }

    [Fact] // D05 管理员改角色:B 持旧版本应冲突
    public async Task AdminUpsertRole_StaleVersion_Throws()
    {
        if (!_available) return;
        var admin = await SeedUserAsync("Admin Op");
        var cu = new StubCurrentUser(admin);
        Guid roleId;
        await using (var db = NewDb())
        {
            var r = await new AdminUpsertRoleCommandHandler(db, cu).Handle(
                new AdminUpsertRoleCommand(null, Uq("T101Role").Split('@')[0], "orig desc", []), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            roleId = r.Value;
        }
        _roleIds.Add(roleId);
        uint v;
        string roleName;
        await using (var db = NewDb())
        {
            var roles = await new AdminRolesQueryHandler(db).Handle(new AdminRolesQuery(), default);
            var item = roles.Value.Single(x => x.Id == roleId);
            v = item.Version;
            roleName = item.Name;
        }
        v.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb())
            (await new AdminUpsertRoleCommandHandler(dbA, cu).Handle(
                new AdminUpsertRoleCommand(roleId, roleName, "A desc", [], v), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb();
        var act = () => new AdminUpsertRoleCommandHandler(dbB, cu).Handle(
            new AdminUpsertRoleCommand(roleId, roleName, "B desc", [], v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        await using var dbR = NewDb();
        (await dbR.Roles.AsNoTracking().SingleAsync(r => r.Id == roleId)).Description.Should().Be("A desc");
    }

    [Fact] // D05 boundary:新建角色不带版本照常成功(Version 只对既有行生效)
    public async Task AdminUpsertRole_CreateWithoutVersion_Succeeds()
    {
        if (!_available) return;
        var admin = await SeedUserAsync("Admin Op");
        var cu = new StubCurrentUser(admin);
        await using var db = NewDb();
        var r = await new AdminUpsertRoleCommandHandler(db, cu).Handle(
            new AdminUpsertRoleCommand(null, Uq("T101RoleNew").Split('@')[0], "desc", []), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        _roleIds.Add(r.Value);
    }
}
