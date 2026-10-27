using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using YourInterview.BuildingBlocks.Security;
using YourInterview.SharedContracts.Security;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// 票241 共享门(LogtoClaimsGate):全服务唯一的审批/停用门,假镜像读,不连库。
/// 身份的 AuthenticationType 模拟 scheme 路由结果("Logto"/"Legacy");
/// 双轨关闭分支(Enabled=false)直接返回原 principal。
/// </summary>
public class LogtoClaimsGateTests
{
    private sealed class FakeReader : ILogtoMirrorReader
    {
        public readonly Dictionary<string, LogtoMirrorEntry> ByLogto = new();
        public readonly Dictionary<Guid, LogtoMirrorEntry> ByLegacy = new();
        public readonly Dictionary<Guid, bool?> OldActive = new();
        public bool ThrowOnRead;

        public Task<LogtoMirrorEntry?> GetByLogtoIdAsync(string logtoUserId, CancellationToken ct) =>
            ThrowOnRead ? throw new InvalidOperationException("db down")
                : Task.FromResult(ByLogto.TryGetValue(logtoUserId, out var r) ? r : null);
        public Task<LogtoMirrorEntry?> GetByLegacyIdAsync(Guid legacyUserId, CancellationToken ct) =>
            ThrowOnRead ? throw new InvalidOperationException("db down")
                : Task.FromResult(ByLegacy.TryGetValue(legacyUserId, out var r) ? r : null);
        public Task<bool?> GetOldUserIsActiveAsync(Guid legacyUserId, CancellationToken ct) =>
            ThrowOnRead ? throw new InvalidOperationException("db down")
                : Task.FromResult(OldActive.TryGetValue(legacyUserId, out var a) ? a : null);
    }

    private static readonly Guid Legacy = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static LogtoMirrorEntry Row(string approval, bool suspended) =>
        new("logto-1", Legacy, "u@example.com", approval, suspended, false, null);

    private static LogtoClaimsGate Gate(FakeReader reader, bool enabled = true) =>
        new(reader, Options.Create(new LogtoOptions
            { Issuer = "http://localhost:3001/oidc", Audience = "api" }),
            NullLogger<LogtoClaimsGate>.Instance);

    private static ClaimsPrincipal Principal(string authType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authType));

    private static string[] Perms(ClaimsPrincipal p) =>
        p.FindAll(CustomClaims.Permission).Select(c => c.Value).ToArray();

    private static string? NameId(ClaimsPrincipal p) =>
        p.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    [Fact] // 双轨关闭:原样放行(旧口径一字不变)
    public async Task Disabled_PassesThrough()
    {
        // Enabled=false 靠 Issuer 为空实现,直接构造关闭配置的门
        var gate = new LogtoClaimsGate(new FakeReader(),
            Options.Create(new LogtoOptions()), NullLogger<LogtoClaimsGate>.Instance);
        var p = Principal("Bearer", new Claim(CustomClaims.Permission, Permissions.JobsRead));
        (await gate.TransformAsync(p)).Should().BeSameAs(p);
    }

    [Fact] // Logto 已批 + scope → perm;未知 scope 忽略
    public async Task Logto_Approved_ScopeMapsToPerms_UnknownIgnored()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("approved", false);
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", $"{Permissions.JobsRead} nope.unknown {Permissions.JobsWrite}"));
        var result = await Gate(reader).TransformAsync(p);
        Perms(result).Should().BeEquivalentTo(Permissions.JobsRead, Permissions.JobsWrite);
        NameId(result).Should().Be(Legacy.ToString());
    }

    [Fact] // Logto 已迁移同样放行
    public async Task Logto_Migrated_Passes()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("migrated", false);
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", Permissions.JobsRead));
        var result = await Gate(reader).TransformAsync(p);
        Perms(result).Should().Contain(Permissions.JobsRead);
    }

    [Fact] // Logto 待审批:零权限,但 NameIdentifier 已翻译(/me 可返回待审批态)
    public async Task Logto_Pending_ZeroPerms_ButTranslated()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("pending", false);
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", Permissions.JobsRead));
        var result = await Gate(reader).TransformAsync(p);
        Perms(result).Should().BeEmpty();
        NameId(result).Should().Be(Legacy.ToString());
    }

    [Fact] // Logto 已停用:零权限(停用门,round-3)
    public async Task Logto_Suspended_ZeroPerms()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("approved", true);
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();
    }

    [Fact] // Logto 无镜像行:零权限(default-deny),NameIdentifier 不翻译
    public async Task Logto_NoRow_ZeroPerms_Untranslated()
    {
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", Permissions.JobsRead));
        var result = await Gate(new FakeReader()).TransformAsync(p);
        Perms(result).Should().BeEmpty();
        NameId(result).Should().Be("logto-1");
    }

    [Fact] // Logto token 自带 perm 声明一律先剥掉(权威是镜像 + scope,不是 token)
    public async Task Logto_IncomingPermClaims_Stripped()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("approved", false);
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim(CustomClaims.Permission, Permissions.AdminUsersWrite));
        var result = await Gate(reader).TransformAsync(p);
        Perms(result).Should().BeEmpty();
    }

    [Fact] // Logto token 里的 Service 角色声明丢弃
    public async Task Logto_ServiceRoleClaim_Dropped()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("approved", false);
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim(ClaimTypes.Role, Roles.Service),
            new Claim("scope", Permissions.JobsRead));
        var result = await Gate(reader).TransformAsync(p);
        result.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().NotContain(Roles.Service);
    }

    [Fact] // Logto 查库失败:fail-closed 零权限
    public async Task Logto_ReaderThrows_FailClosed()
    {
        var reader = new FakeReader { ThrowOnRead = true };
        var p = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();
    }

    [Fact] // 旧 token 有镜像行 + 已批未停用:原样放行
    public async Task Old_WithRow_Approved_Passes()
    {
        var reader = new FakeReader();
        reader.ByLegacy[Legacy] = Row("approved", false);
        var p = Principal("Legacy",
            new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
            new Claim(CustomClaims.Permission, Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().ContainSingle();
    }

    [Fact] // 旧 token 有镜像行 + 已停用:剥夺
    public async Task Old_WithRow_Suspended_Stripped()
    {
        var reader = new FakeReader();
        reader.ByLegacy[Legacy] = Row("approved", true);
        var p = Principal("Legacy",
            new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
            new Claim(CustomClaims.Permission, Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();
    }

    [Fact] // 旧 token 有镜像行 + pending:剥夺(严格 allowlist,空标记同样剥夺)
    public async Task Old_WithRow_PendingOrBlank_Stripped()
    {
        foreach (var approval in new[] { "pending", "" })
        {
            var reader = new FakeReader();
            reader.ByLegacy[Legacy] = Row(approval, false);
            var p = Principal("Legacy",
                new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
                new Claim(CustomClaims.Permission, Permissions.JobsRead));
            (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();
        }
    }

    [Fact] // 旧 token 有镜像行 + deleted:剥夺(删号传播)
    public async Task Old_WithRow_Deleted_Stripped()
    {
        var reader = new FakeReader();
        reader.ByLegacy[Legacy] = Row("deleted", true);
        var p = Principal("Legacy",
            new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
            new Claim(CustomClaims.Permission, Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();
    }

    [Fact] // round-7(a) 未迁移旧 token 三态:启用放行 / 停用剥夺 / 建镜像行后镜像门接管
    public async Task Old_NoRow_Fallback_ThreeCases()
    {
        // 启用 → 放行
        var reader = new FakeReader();
        reader.OldActive[Legacy] = true;
        var p = Principal("Legacy",
            new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
            new Claim(CustomClaims.Permission, Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().ContainSingle();

        // 停用 → 剥夺(未迁移用户的管理员停用在旧侧生效)
        reader = new FakeReader();
        reader.OldActive[Legacy] = false;
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();

        // 用户不存在 → 剥夺
        reader = new FakeReader();
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();

        // 镜像行出现 → 镜像门接管(行 pending 时即使旧侧启用也剥夺)
        reader = new FakeReader();
        reader.ByLegacy[Legacy] = Row("pending", false);
        reader.OldActive[Legacy] = true;
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().BeEmpty();
    }

    [Fact] // round-11(2) 角色/权限残留窗口:门只认 token 里的 scope,从不查实时角色 ——
    // 降级后,已发 token 在有效期内仍带旧 scope(门照常展开);新发 token 才带新 scope。
    // 残留窗口 = 剩余 access token 有效期,到刷新/过期为止,这是机制本身,不是缺陷。
    public async Task ResidualWindow_OldTokenKeepsOldScope_NewTokenReflectsDowngrade()
    {
        var reader = new FakeReader();
        reader.ByLogto["logto-1"] = Row("approved", false);
        var gate = Gate(reader);

        // 降级前发的 token:scope 含 jobs.write → 门展开 jobs.write(残留窗口内有效)。
        var oldToken = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", $"{Permissions.JobsRead} {Permissions.JobsWrite}"));
        Perms(await gate.TransformAsync(oldToken)).Should()
            .BeEquivalentTo(Permissions.JobsRead, Permissions.JobsWrite);

        // 降级后新发的 token:scope 只剩 jobs.read → 门只展开 jobs.read。
        var newToken = Principal("Logto",
            new Claim(ClaimTypes.NameIdentifier, "logto-1"),
            new Claim("scope", Permissions.JobsRead));
        Perms(await gate.TransformAsync(newToken)).Should().BeEquivalentTo(Permissions.JobsRead);
    }

    [Fact] // round-16(2) 服务连续性:收敛后 Service 角色旧 token 仍走 Legacy scheme
    // (选择器在 RejectOldIssuer 下仍把 Service 旧 token 路由到 Legacy,见
    // LogtoDualSchemeTests),且服务账号不经过 Logto、无镜像行 —— 门按旧侧
    // IsActive 回退放行,服务间调用不断。用户 token 的收敛拒绝不受影响
    // (用户旧 token 在收敛后路由到 Logto scheme 验签失败)。
    public async Task Old_ServiceToken_NoMirrorRow_ActiveOldUser_Passes()
    {
        var reader = new FakeReader();
        reader.OldActive[Legacy] = true;
        var p = Principal("Legacy",
            new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
            new Claim(ClaimTypes.Role, "Service"),
            new Claim(CustomClaims.Permission, Permissions.JobsRead));
        var result = await Gate(reader).TransformAsync(p);
        Perms(result).Should().ContainSingle().Which.Should().Be(Permissions.JobsRead);
    }

    [Fact] // 旧 token 查库失败:可用性规则 → 原样放行
    public async Task Old_ReaderThrows_PassesThrough()
    {
        var reader = new FakeReader { ThrowOnRead = true };
        var p = Principal("Legacy",
            new Claim(ClaimTypes.NameIdentifier, Legacy.ToString()),
            new Claim(CustomClaims.Permission, Permissions.JobsRead));
        (await Gate(reader).TransformAsync(p)).FindAll(CustomClaims.Permission).Should().ContainSingle();
    }
}
