using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Identity.Application.Admin;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票111 段3 §7(Identity 删除 If-Match 甲案):
/// D03 删用户 / D06 删角色 —— 陈旧 Version → DbUpdateConcurrencyException(409)且数据完好;
/// 新鲜 → 成功;不带 → 兼容成功。操作者为另一管理员(避自删/末位管理员卫兵)。
/// DB 门控同 Ticket101 先例。
/// </summary>
public sealed class Ticket111DeleteTests : IAsyncLifetime
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
        var urs = await db.UserRoles.Where(ur => _userIds.Contains(ur.UserId) || _roleIds.Contains(ur.RoleId)).ToListAsync();
        db.UserRoles.RemoveRange(urs);
        var users = await db.Users.IgnoreQueryFilters().Where(u => _userIds.Contains(u.Id)).ToListAsync();
        db.Users.RemoveRange(users);
        var roles = await db.Roles.IgnoreQueryFilters().Where(r => _roleIds.Contains(r.Id)).ToListAsync();
        db.Roles.RemoveRange(roles);
        var audits = await db.AuditLogs.Where(a => _userIds.Contains(a.UserId ?? Guid.Empty)).ToListAsync();
        db.AuditLogs.RemoveRange(audits);
        await db.SaveChangesAsync();
    }

    private static IdentityDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(Cs).Options;
        return new IdentityDbContext(opts);
    }

    private static string Uq(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> SeedUserAsync(string displayName)
    {
        await using var db = NewDb();
        var u = new AppUser(Uq("t111") + "@test.local", displayName, "x-hash");
        db.Users.Add(u);
        await db.SaveChangesAsync();
        _userIds.Add(u.Id);
        return u.Id;
    }

    private async Task<uint> UserVersionAsync(Guid id)
    {
        await using var db = NewDb();
        return await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => EF.Property<uint>(u, "xmin")).FirstAsync();
    }

    private async Task<uint> RoleVersionAsync(Guid id)
    {
        await using var db = NewDb();
        var r = await new AdminRolesQueryHandler(db).Handle(new AdminRolesQuery(), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value.Single(x => x.Id == id).Version;
    }

    // ---------- D03 删用户 ----------

    [Fact]
    public async Task DeleteUser_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var admin = await SeedUserAsync("Admin Op");
        var target = await SeedUserAsync("Target");
        var cu = new StubCurrentUser(admin);
        var v = await UserVersionAsync(target);

        // A 先改目标用户资料(版本推进)
        await using (var dbA = NewDb())
            (await new UpdateProfileCommandHandler(dbA, new UserPermissionResolver(dbA)).Handle(
                new UpdateProfileCommand(target, "A Name", null, null, null, v), default)).IsSuccess.Should().BeTrue();

        // B(管理员)持旧版本删除 → 冲突,用户完好
        await using var dbB = NewDb();
        var act = () => new AdminDeleteUserCommandHandler(dbB, cu).Handle(new AdminDeleteUserCommand(target, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await using (var dbR = NewDb())
            (await dbR.Users.AsNoTracking().SingleAsync(u => u.Id == target)).DisplayName.Should().Be("A Name");

        var v2 = await UserVersionAsync(target);
        await using (var dbF = NewDb())
            (await new AdminDeleteUserCommandHandler(dbF, cu).Handle(new AdminDeleteUserCommand(target, v2), default))
                .IsSuccess.Should().BeTrue();

        var target2 = await SeedUserAsync("Target 2");
        await using (var dbN = NewDb())
            (await new AdminDeleteUserCommandHandler(dbN, cu).Handle(new AdminDeleteUserCommand(target2), default))
                .IsSuccess.Should().BeTrue();
    }

    // ---------- D06 删角色 ----------

    [Fact]
    public async Task DeleteRole_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var admin = await SeedUserAsync("Admin Op");
        var cu = new StubCurrentUser(admin);

        Guid roleId;
        await using (var db = NewDb())
        {
            var r = await new AdminUpsertRoleCommandHandler(db, cu).Handle(
                new AdminUpsertRoleCommand(null, Uq("T111Role"), "orig desc", []), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            roleId = r.Value;
        }
        _roleIds.Add(roleId);
        var v = await RoleVersionAsync(roleId);

        // A 先改角色(版本推进)
        await using (var dbA = NewDb())
            (await new AdminUpsertRoleCommandHandler(dbA, cu).Handle(
                new AdminUpsertRoleCommand(roleId, Uq("T111Role"), "A desc", [], v), default)).IsSuccess.Should().BeTrue();

        await using var dbB = NewDb();
        var act = () => new AdminDeleteRoleCommandHandler(dbB, cu).Handle(new AdminDeleteRoleCommand(roleId, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await RoleVersionAsync(roleId)).Should().NotBe(v);

        var v2 = await RoleVersionAsync(roleId);
        await using (var dbF = NewDb())
            (await new AdminDeleteRoleCommandHandler(dbF, cu).Handle(new AdminDeleteRoleCommand(roleId, v2), default))
                .IsSuccess.Should().BeTrue();

        Guid roleId2;
        await using (var db = NewDb())
        {
            var r = await new AdminUpsertRoleCommandHandler(db, cu).Handle(
                new AdminUpsertRoleCommand(null, Uq("T111Role2"), "desc", []), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            roleId2 = r.Value;
        }
        _roleIds.Add(roleId2);
        await using (var dbN = NewDb())
            (await new AdminDeleteRoleCommandHandler(dbN, cu).Handle(new AdminDeleteRoleCommand(roleId2), default))
                .IsSuccess.Should().BeTrue();
    }
}
