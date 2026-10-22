using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Api;
using YourInterview.SharedContracts.Security;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票25 / COD-15:Assessment 写操作不得挂读权限。
///
/// 缺陷:POST recordings/{id}/score(评分落库)与 POST tts(消耗 Azure TTS 额度
/// + 写本地缓存)原挂 MockRead —— Viewer 角色只有 mock.read,却能写与消费
/// 付费合成。修复口径(盘点 00-inventory-and-plan.md §1,协调者已认可):
/// 两端点改挂 MockAnswer —— 与同练习流的上传录音(POST materials/{id}/recordings)
/// 同权;不改 MockManage(User 无此权,改之即回退,违反票面「Admin/User 正常」);
/// 仓库无 MockWrite 且既有常量够用,不新增常量、不动种子、无迁移。
///
/// 本组为纯反射/纯数据断言,不连 DB:
///  · 目标端点 policy == perm:mock.answer(COD-15 本体);
///  · 邻居特征锁定(总纲第八节回归验邻居):读端点仍 MockRead、管理写仍
///    MockManage、上传录音仍 MockAnswer;票面外两端点 AssessPronunciation /
///    TestSpeech 锁定现状 MockRead —— 它们已另立票 86,本票零容忍不扩围,
///    票 86 改挂时此处断言须同步改并注明依据(票 55 先例);
///  · DefaultPermissions 组合:Viewer 有读无答、User/PowerUser/Admin 有答、
///    Service 对 mock 三权全无 —— 这是「改挂 MockAnswer 后只有 Viewer 被挡」
///    的数据依据;
///  · PermissionPolicy 注册:perm:mock.answer 策略存在且 requirement 为
///    perm claim == mock.answer(策略缺失时 [Authorize] 会在运行时抛错,
///    反射断言过、活体必挂,故在单测层先钉住)。
///
/// boundary 类别:空集合(无角色权限集为空)、错权限名(集合内每项必须 ∈
/// Permissions.All,防拼写漂移使策略永不命中)、未知角色名(DefaultPermissions
/// 键必须 ⊆ Roles.All)、策略全集(每个权限恰注册一条策略,不多不少)。
/// </summary>
public sealed class AssessmentEndpointPermissionsTests
{
    private static string PolicyOf(string methodName)
    {
        var method = typeof(AssessmentController).GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"AssessmentController.{methodName} 应存在");
        var authorize = method!.GetCustomAttributes<AuthorizeAttribute>().SingleOrDefault();
        authorize.Should().NotBeNull($"AssessmentController.{methodName} 应挂 [Authorize]");
        return authorize!.Policy!;
    }

    // ---------- COD-15 本体:两目标端点改挂 MockAnswer ----------

    [Fact] // 本体:评分落库是写操作
    public void SaveRecordingScore_RequiresMockAnswer()
        => PolicyOf(nameof(AssessmentController.SaveRecordingScore))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockAnswer);

    [Fact] // 本体:TTS 消耗付费额度 + 写缓存
    public void Synthesize_RequiresMockAnswer()
        => PolicyOf(nameof(AssessmentController.Synthesize))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockAnswer);

    // ---------- 邻居特征锁定:读端点不变(仍 MockRead) ----------

    [Theory] // 邻居回归:读路径逐字不变,Viewer/User 读能力不受本票影响
    [InlineData(nameof(AssessmentController.List))]
    [InlineData(nameof(AssessmentController.GetMaterials))]
    [InlineData(nameof(AssessmentController.GetRecordingScore))]
    public void ReadEndpoints_StillRequireMockRead(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockRead);

    // ---------- 邻居特征锁定:票面外两端点锁现状(票 86 范围,本票不动) ----------

    [Theory] // 不变项:盘点 §2 #14/#34,已立票 86;票 86 落地时改此处并注明依据
    [InlineData(nameof(AssessmentController.AssessPronunciation))]
    [InlineData(nameof(AssessmentController.TestSpeech))]
    public void OutOfScopeEndpoints_LockedAtMockRead(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockRead);

    // ---------- 邻居特征锁定:既有写口径不变 ----------

    [Fact] // 口径先例:上传录音本就挂 MockAnswer,本票两端点与之对齐
    public void SaveRecording_StillRequiresMockAnswer()
        => PolicyOf(nameof(AssessmentController.SaveRecording))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockAnswer);

    [Theory] // 邻居回归:管理级写仍 MockManage(User 本就不可达,非本票造成)
    [InlineData(nameof(AssessmentController.Create))]
    [InlineData(nameof(AssessmentController.SaveMaterials))]
    [InlineData(nameof(AssessmentController.DeleteRecording))]
    public void ManageEndpoints_StillRequireMockManage(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockManage);

    // ---------- DefaultPermissions 组合:改挂后的角色能力依据 ----------

    [Fact] // 验收核心:Viewer 改后被挡的唯一原因 = 有读无答
    public void Viewer_HasMockRead_ButNotMockAnswer()
    {
        var viewer = Roles.DefaultPermissions[Roles.Viewer];
        viewer.Should().Contain(Permissions.MockRead);
        viewer.Should().NotContain(Permissions.MockAnswer);
        viewer.Should().NotContain(Permissions.MockManage);
    }

    [Theory] // 验收核心:Admin/User 正常 —— 三角色均含 MockAnswer,改挂不回退
    [InlineData(Roles.Admin)]
    [InlineData(Roles.PowerUser)]
    [InlineData(Roles.User)]
    public void WritableRoles_ContainMockAnswer(string role)
        => Roles.DefaultPermissions[role].Should().Contain(Permissions.MockAnswer);

    [Fact] // 关键否决项:User 无 MockManage —— 若改挂 MockManage,User 即回退
    public void User_LacksMockManage_SoMockAnswerIsTheOnlyNonRegressingChoice()
        => Roles.DefaultPermissions[Roles.User].Should().NotContain(Permissions.MockManage);

    [Fact] // boundary-空集邻接:Service 对 mock 三权全无,改前改后均 403,不变
    public void Service_HasNoMockPermissionsAtAll()
        => Roles.DefaultPermissions[Roles.Service].Should().NotContain(new[]
        {
            Permissions.MockRead, Permissions.MockAnswer, Permissions.MockManage
        });

    [Fact] // boundary-空集合:任一系统角色的默认权限集都不许为空集
    public void DefaultPermissions_NoRoleHasEmptyPermissionSet()
        => Roles.DefaultPermissions.Values.Should().AllSatisfy(
            perms => perms.Should().NotBeEmpty());

    [Fact] // boundary-错权限名:集合内每项必须 ∈ Permissions.All(拼写漂移 = 策略永不命中)
    public void DefaultPermissions_OnlyKnownPermissionNames()
        => Roles.DefaultPermissions.Values.SelectMany(perms => perms)
            .Should().OnlyContain(perm => Permissions.All.Contains(perm));

    [Fact] // boundary-未知角色名:种子键必须 ⊆ Roles.All
    public void DefaultPermissions_KeysAreKnownRoles()
        => Roles.DefaultPermissions.Keys.Should().OnlyContain(role => Roles.All.Contains(role));

    // ---------- PermissionPolicy 策略注册 ----------

    [Fact] // 策略存在性:perm:mock.answer 必须已注册,否则运行时 [Authorize] 抛错
    public void PermissionPolicies_ContainMockAnswerPolicyRequiringPermClaim()
    {
        var options = new AuthorizationOptions();
        PermissionPolicy.AddPermissionPolicies(options);

        var policy = options.GetPolicy(PermissionPolicy.Prefix + Permissions.MockAnswer);
        policy.Should().NotBeNull("perm:mock.answer 策略应由 AddPermissionPolicies 注册");
        policy!.Requirements.OfType<ClaimsAuthorizationRequirement>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                ClaimType = CustomClaims.Permission,
                AllowedValues = new string?[] { Permissions.MockAnswer }
            });
    }

    [Fact] // boundary-策略全集:Permissions.All 每项都有策略;未知权限名无策略
    public void PermissionPolicies_OnePolicyPerKnownPermission()
    {
        var options = new AuthorizationOptions();
        PermissionPolicy.AddPermissionPolicies(options);

        foreach (var permission in Permissions.All)
            options.GetPolicy(PermissionPolicy.Prefix + permission).Should().NotBeNull(
                $"权限 {permission} 应有对应策略");
        options.GetPolicy(PermissionPolicy.Prefix + "mock.write").Should().BeNull(
            "仓库无 MockWrite 口径,不得注册 perm:mock.write 策略");
    }
}
