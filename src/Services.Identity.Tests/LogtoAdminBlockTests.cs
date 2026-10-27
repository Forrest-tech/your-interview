using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using YourInterview.Services.Identity.Application.Admin;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using YourInterview.Services.Identity.Infrastructure.Security;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票241 旧管理端封口(round-9 映射块 + round-10 灰度期建号块 + round-11 回归 +
/// round-15 停用双写):
///   · 灰度期 POST /api/admin/users 建号被拒(与 POST /api/auth/register 的 410
///     是两回事:一个是管理员代建通道,一个是公开注册通道,不要混淆);
///     建号块在 handler 第一行,任何 DB 查询之前 —— 本测试不需要活库。
///   · 已映射用户的旧侧解锁被拒(与删除/重置同规则)。
///   · 例外:已映射用户的启用/停用不是拒绝,而是双写(round-3/4/5 决议;
///     round-9 的拒绝从未覆盖停用,本文件先前的“停用也拒绝”是超范围延伸,
///     round-15 起纠正为双写 + 失败注入测试)。
/// 需要活库的测试沿用票101 的 _available 跳过模式。
/// </summary>
public class LogtoAdminBlockTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _userIds = [];

    private sealed class StubHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string encodedHash) => false;
        public bool NeedsRehash(string encodedHash) => false;
    }

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        // 清理只求尽力:表缺失(迁移未应用的旧库)时不让清理失败掩盖测试结果。
        try
        {
            await using var db = NewDb();
            var maps = await db.LogtoUserMappings.Where(m => _userIds.Contains(m.LegacyUserId)).ToListAsync();
            db.LogtoUserMappings.RemoveRange(maps);
            var users = await db.Users.IgnoreQueryFilters().Where(u => _userIds.Contains(u.Id)).ToListAsync();
            db.Users.RemoveRange(users);
            await db.SaveChangesAsync();
        }
        catch { }
    }

    private static IdentityDbContext NewDb() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(Cs).Options);

    [Fact] // round-11(1):灰度期管理员代建被拒,且不产生任何用户行(无需活库)
    public async Task CreateUser_GreyPeriod_Rejected_BeforeAnyDbCall()
    {
        await using var db = NewDb();
        var handler = new AdminCreateUserCommandHandler(db, new StubHasher(),
            new StubCurrentUser(null),
            Options.Create(new LegacyAuthOptions { LegacyRegistrationEnabled = false }));

        var result = await handler.Handle(
            new AdminCreateUserCommand("grey-block@test.local", "Grey Block", "pw", ["User"]), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Admin.LegacyCreationClosed");
    }

    [Theory] // round-13(5) 回滚映射:只有 approved/migrated 且未停用才导为启用
    [InlineData("approved", false, true)]
    [InlineData("migrated", false, true)]
    [InlineData("pending", false, false)]
    [InlineData("approved", true, false)]
    [InlineData("migrated", true, false)]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    public void ImportMapIsActive_Mapping(string? approval, bool suspended, bool expected)
    {
        AdminImportLogtoUserCommand.MapIsActive(approval, suspended).Should().Be(expected);
    }

    [Fact] // round-14(3) 回滚重导集:pending 与 suspended 用户都在重导集里,
    // 且导后旧侧行一律 IsActive=false;approved + 未停用作对照(导为启用)
    public async Task Import_ReimportSet_PendingAndSuspended_AllInactive()
    {
        if (!_available) return;
        var cases = new[]
        {
            ("pending", false, false),
            ("approved", true, false),
            ("migrated", true, false),
            ("approved", false, true),
        };
        var ids = new List<Guid>();
        await using (var db = NewDb())
        {
            foreach (var (approval, suspended, _) in cases)
            {
                var id = Guid.NewGuid();
                var handler = new AdminImportLogtoUserCommandHandler(db, new StubHasher(), new StubCurrentUser(null));
                var r = await handler.Handle(new AdminImportLogtoUserCommand(
                    id, "logto-reimport-" + id.ToString("N")[..8], "reimport-" + id.ToString("N")[..8] + "@test.local",
                    "Reimport", suspended, approval), default);
                r.IsSuccess.Should().BeTrue(r.Error.Description);
                ids.Add(id);
                _userIds.Add(id);
            }
            await db.SaveChangesAsync();
        }
        await using var dbR = NewDb();
        var rows = await dbR.Users.IgnoreQueryFilters()
            .Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id);
        for (var i = 0; i < cases.Length; i++)
        {
            rows[ids[i]].IsActive.Should().Be(cases[i].Item3);
            rows[ids[i]].MustChangePassword.Should().BeTrue();
        }
    }

    private static IOptions<LogtoManagementOptions> ConfiguredLogtoOptions() => Options.Create(
        new LogtoManagementOptions
        {
            Endpoint = "http://logto:3001",
            M2mClientId = "test-m2m",
            M2mClientSecret = "test-secret",
        });

    private async Task<Guid> SeedMappedUser(string email, string logtoId, bool active, bool mirrorSuspended)
    {
        var userId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            var user = new AppUser(userId, email, "Dual", "x-hash");
            if (!active) user.Deactivate();
            db.Users.Add(user);
            db.LogtoUserMappings.Add(new LogtoUserMapping(logtoId, userId, email, "migrated", mirrorSuspended));
            await db.SaveChangesAsync();
            _userIds.Add(userId);
        }
        return userId;
    }

    [Fact] // round-15(3) 停用双写成功:Logto 停用 + 旧侧停用 + 镜像同步,一次操作完成
    public async Task Update_MappedUser_Disable_DualWritesLogto()
    {
        if (!_available) return;
        var userId = await SeedMappedUser("dualwrite@test.local", "logto-dual-1", true, false);
        var client = Substitute.For<ILogtoManagementClient>();
        await using var db = NewDb();
        var handler = new AdminUpdateUserCommandHandler(db, new StubCurrentUser(Guid.NewGuid()),
            client, ConfiguredLogtoOptions());
        var result = await handler.Handle(
            new AdminUpdateUserCommand(userId, "Dual", false, false, []), default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        await client.Received(1).SuspendUserAsync("logto-dual-1", true, Arg.Any<CancellationToken>());

        await using var dbR = NewDb();
        (await dbR.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).IsActive.Should().BeFalse();
        var map = await dbR.LogtoUserMappings.SingleAsync(m => m.LegacyUserId == userId);
        map.IsSuspended.Should().BeTrue();
        map.SuspendedByGate.Should().BeFalse(); // 管理员动作,不是门/扫表动作
        map.Approval.Should().Be("migrated"); // 停用不改批准标记
    }

    [Fact] // round-15(3) 停用双写失败注入:Logto 侧调失败 → 旧侧不提交 + 明示错误 + 失败审计行
    public async Task Update_MappedUser_LogtoFailure_RollsBackOld_AndAudits()
    {
        if (!_available) return;
        var userId = await SeedMappedUser("dualwrite-fail@test.local", "logto-dual-fail", true, false);
        var client = Substitute.For<ILogtoManagementClient>();
        client.SuspendUserAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("simulated Logto down")));
        await using var db = NewDb();
        var handler = new AdminUpdateUserCommandHandler(db, new StubCurrentUser(Guid.NewGuid()),
            client, ConfiguredLogtoOptions());
        var result = await handler.Handle(
            new AdminUpdateUserCommand(userId, "DualFail", false, false, []), default);
        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Admin.LogtoSyncFailed");

        await using var dbR = NewDb();
        (await dbR.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).IsActive.Should().BeTrue();
        (await dbR.LogtoUserMappings.SingleAsync(m => m.LegacyUserId == userId)).IsSuspended.Should().BeFalse();
        (await dbR.AuditLogs.AnyAsync(a => a.Action == "admin.user.logto-sync-failed"
            && a.ResourceId == userId.ToString())).Should().BeTrue();
    }

    [Fact] // round-15(3) 对照:未映射用户停用不触 Logto,旧行为不变
    public async Task Update_UnmappedUser_Disable_NoLogtoCall()
    {
        if (!_available) return;
        var userId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Users.Add(new AppUser(userId, "dualwrite-unmapped@test.local", "Unmapped", "x-hash"));
            await db.SaveChangesAsync();
            _userIds.Add(userId);
        }
        var client = Substitute.For<ILogtoManagementClient>();
        await using var db2 = NewDb();
        var handler = new AdminUpdateUserCommandHandler(db2, new StubCurrentUser(Guid.NewGuid()),
            client, ConfiguredLogtoOptions());
        var result = await handler.Handle(
            new AdminUpdateUserCommand(userId, "Unmapped", false, false, []), default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        await client.DidNotReceiveWithAnyArgs().SuspendUserAsync(default!, default, default);
        await using var dbR = NewDb();
        (await dbR.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).IsActive.Should().BeFalse();
    }

    [Fact] // round-15(3) 对照:已映射用户启用 → Logto 取消停用 + 旧侧启用 + 镜像同步
    public async Task Update_MappedUser_Enable_DualWritesUnsuspend()
    {
        if (!_available) return;
        var userId = await SeedMappedUser("dualwrite-enable@test.local", "logto-dual-enable", false, true);
        var client = Substitute.For<ILogtoManagementClient>();
        await using var db = NewDb();
        var handler = new AdminUpdateUserCommandHandler(db, new StubCurrentUser(Guid.NewGuid()),
            client, ConfiguredLogtoOptions());
        var result = await handler.Handle(
            new AdminUpdateUserCommand(userId, "Dual", true, false, []), default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        await client.Received(1).SuspendUserAsync("logto-dual-enable", false, Arg.Any<CancellationToken>());
        await using var dbR = NewDb();
        (await dbR.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).IsActive.Should().BeTrue();
        (await dbR.LogtoUserMappings.SingleAsync(m => m.LegacyUserId == userId)).IsSuspended.Should().BeFalse();
    }

    [Fact] // round-10(3):已映射用户的旧侧解锁被拒
    public async Task Unlock_MappedUser_Rejected()
    {
        if (!_available) return;
        var userId = Guid.NewGuid();
        await using (var db = NewDb())
        {
            db.Users.Add(new AppUser(userId, "unlock-block@test.local", "Block", "x-hash"));
            db.LogtoUserMappings.Add(new LogtoUserMapping("logto-unlock-1", userId,
                "unlock-block@test.local", "migrated", false));
            await db.SaveChangesAsync();
            _userIds.Add(userId);
        }

        await using var db2 = NewDb();
        var handler = new AdminUnlockUserCommandHandler(db2, new StubCurrentUser(null));
        var result = await handler.Handle(new AdminUnlockUserCommand(userId), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be("Admin.MappedUser");
    }
}
