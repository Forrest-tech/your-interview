using FluentAssertions;
using YourInterview.Services.Analytics.Application;
using YourInterview.SharedContracts.Security;
using Xunit;

namespace YourInterview.Services.Analytics.Tests;

/// <summary>
/// 票56(解法A):AbilityOwnership 判定矩阵。
/// 归属红线:只有 JWT 带 perm claim analytics.writeback 的调用方(服务账号)能让
/// ownerUserId honoured;其余一律落 Me(票06 口径)。用例名后缀标注 boundary 类别。
/// </summary>
public sealed class AbilityOwnershipTests
{
    private static readonly Guid Me = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // 常规:无 writeback claim 的普通用户带 ownerUserId=他人 → 忽略,落 Me(票06 攻击用例)
    [Fact]
    public void NoWritebackClaim_OwnerIgnored_FallsBackToMe()
    {
        var perms = new[] { Permissions.AnalyticsRead, Permissions.AnalyticsWrite };
        AbilityOwnership.Resolve(Me, perms, Other).Should().Be(Me);
    }

    // 关键分界(解法A):PowerUser 持有 analytics.write 但无 writeback → 不 honoured
    [Fact]
    public void PowerUserPermissions_WithoutWriteback_NotHonoured()
    {
        AbilityOwnership.Resolve(Me, Roles.DefaultPermissions[Roles.PowerUser], Other)
            .Should().Be(Me);
    }

    // 常规:服务账号(writeback)+ ownerUserId 有效 → honoured,落事件用户名下
    [Fact]
    public void WritebackClaim_ValidOwner_Honoured()
    {
        var perms = new[] { Permissions.AnalyticsWrite, Permissions.AnalyticsWriteback };
        AbilityOwnership.Resolve(Me, perms, Other).Should().Be(Other);
    }

    // boundary(空/null):writeback + ownerUserId 缺失(null)→ 落 Me(服务账号自写,AC③ 口径)
    [Fact]
    public void WritebackClaim_NullOwner_FallsBackToMe_BoundaryNull()
    {
        var perms = new[] { Permissions.AnalyticsWriteback };
        AbilityOwnership.Resolve(Me, perms, null).Should().Be(Me);
    }

    // boundary(空值):writeback + ownerUserId = Guid.Empty → 落 Me,不写空 Guid 归属行
    [Fact]
    public void WritebackClaim_EmptyGuidOwner_FallsBackToMe_BoundaryEmptyGuid()
    {
        var perms = new[] { Permissions.AnalyticsWriteback };
        AbilityOwnership.Resolve(Me, perms, Guid.Empty).Should().Be(Me);
    }

    // 常规:writeback + ownerUserId = 自己 → 落自己(与 honoured 结果一致)
    [Fact]
    public void WritebackClaim_OwnerIsSelf_ReturnsSelf()
    {
        var perms = new[] { Permissions.AnalyticsWriteback };
        AbilityOwnership.Resolve(Me, perms, Me).Should().Be(Me);
    }

    // boundary(空/null):权限集合为 null → 视为无 writeback,忽略 ownerUserId
    [Fact]
    public void NullPermissions_OwnerIgnored_BoundaryNull()
    {
        AbilityOwnership.Resolve(Me, null, Other).Should().Be(Me);
    }

    // boundary(空集合):权限集合为空 → 忽略 ownerUserId
    [Fact]
    public void EmptyPermissions_OwnerIgnored_BoundaryEmptyCollection()
    {
        AbilityOwnership.Resolve(Me, [], Other).Should().Be(Me);
    }

    // 契约:writeback 已注册进 Permissions.All(Identity 授权校验/PermissionPolicy 依赖),
    // 且默认权限里只有 Service(与全集 Admin)持有,人类角色 PowerUser/User/Viewer 均不持有
    [Fact]
    public void WritebackPermission_Registered_AndOnlyServiceRoleHoldsItByDefault()
    {
        Permissions.All.Should().Contain(Permissions.AnalyticsWriteback);
        Roles.DefaultPermissions[Roles.Service].Should().Contain(Permissions.AnalyticsWriteback);
        Roles.DefaultPermissions[Roles.PowerUser].Should().NotContain(Permissions.AnalyticsWriteback);
        Roles.DefaultPermissions[Roles.User].Should().NotContain(Permissions.AnalyticsWriteback);
        Roles.DefaultPermissions[Roles.Viewer].Should().NotContain(Permissions.AnalyticsWriteback);
    }

    // 契约:Service 角色最小集边界 —— 不含任何 admin.* 与删除权限(不给服务账号全量豁免)
    [Fact]
    public void ServiceRoleDefaults_NoAdminOrDeletePermissions_BoundaryLeastPrivilege()
    {
        var perms = Roles.DefaultPermissions[Roles.Service];
        perms.Should().NotContainMatch("admin.*");
        perms.Should().NotContainMatch("*.delete");
    }
}
