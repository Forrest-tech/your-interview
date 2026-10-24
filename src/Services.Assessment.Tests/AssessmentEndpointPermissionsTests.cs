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
/// 票86 追补(同根 COD-15):票 25 盘点票面外发现 #14/#34 在此落地 ——
///  · #14 AssessPronunciation(POST pronunciation/assess,练习页跟读评分、
///    消费 Azure 付费评估)改挂 MockAnswer —— 与本类既有两目标端点同为
///    练习参与流,口径一致;
///  · #34 TestSpeech(POST speech/test,真连 Azure 测「已保存的 key」)
///    改挂 MockManage —— 定责依据(读码,evidence-86/STATUS.md §8):
///    前端 PracticeApi.testSpeech() 零组件调用方,练习页从不调用;
///    设置页实际凭据流是 test-credential(已 MockManage);凭据验证同族
///    (PUT speech/settings、POST speech/test-credential、AiGateway
///    test-credential)全挂 MockManage,speech/test 为唯一例外,故对齐
///    MockManage。User 无 MockManage 改后 403,但 User 无 UI 路径可达
///    本端点,不构成可用功能回退;#14 的 User 不回退由 MockAnswer 保证。
/// 原 OutOfScopeEndpoints_LockedAtMockRead 锁定据此移除,两端点移入
/// 下方目标断言(票 55 先例:改挂时同步改断言并注明依据)。
///
/// 票 F-XT-R2-1(WB R1-C1 回执新发现 S2):默认 User 空态无 CTA ——
/// 空态文案承诺“开一场主题专项”,但建会话按钮被 mock.manage 门控,
/// User 无 MockManage 故按钮不渲染;后端 POST sessions 亦挂 MockManage。
/// 修复口径:User 练习旅程 7 端点(创建 Create、生成 Generate、作答
/// Answer、跳过 Skip、AI 评分 ScoreAi、完成 Complete、放弃 Abandon)
/// 改挂 MockAnswer —— 旅程全程按 UserId 隔离,放行只影响本人会话;
/// 角色集不动(User 仍无 MockManage,关键否决断言原样保留)。
/// 不改挂:手动 Score(人工评分,策展/管理类)、AddQuestions(人工加题)、
/// Delete(删会话)、materials/categories/speech 全族 —— 下方以
/// JourneyExcluded_StillRequireMockManage 钉住,防改挂扩散。
/// Create 自邻居回归理论移入下方目标断言(票 55 先例:改挂时同步改
/// 断言并注明依据,不得只改实现不改断言、不得删断言蒙混)。
/// Skip/Abandon 现行前端无调用方(仅状态文案),仍按旅程枚举改挂,
/// 使旅程权限模型不分裂(User 能答却不能跳过/放弃本人会话)。
///
/// 本组为纯反射/纯数据断言,不连 DB:
///  · 目标端点 policy == perm:mock.answer(COD-15 本体);
///  · 邻居特征锁定(总纲第八节回归验邻居):读端点仍 MockRead、管理写仍
///    MockManage、上传录音仍 MockAnswer;
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

    // ---------- 票86 本体:盘点 #14/#34 ----------

    [Fact] // 票86 #14:跟读发音评估是练习参与流且消费 Azure 付费评估
    public void AssessPronunciation_RequiresMockAnswer()
        => PolicyOf(nameof(AssessmentController.AssessPronunciation))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockAnswer);

    [Fact] // 票86 #34:测已保存凭据属设置/凭据验证流,与 test-credential 同族同权
    public void TestSpeech_RequiresMockManage()
        => PolicyOf(nameof(AssessmentController.TestSpeech))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockManage);

    // ---------- 邻居特征锁定:读端点不变(仍 MockRead) ----------

    [Theory] // 邻居回归:读路径逐字不变,Viewer/User 读能力不受本票影响
    [InlineData(nameof(AssessmentController.List))]
    [InlineData(nameof(AssessmentController.GetMaterials))]
    [InlineData(nameof(AssessmentController.GetRecordingScore))]
    [InlineData(nameof(AssessmentController.GetMaterialTrash))] // 票124:回收站清单是读端点
    public void ReadEndpoints_StillRequireMockRead(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockRead);

    // ---------- 票124:回收站三端点的策略钉测 ----------

    [Fact] // 票124:GET materials/trash 是读端点 → MockRead
    public void TrashList_RequiresMockRead()
        => PolicyOf(nameof(AssessmentController.GetMaterialTrash))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockRead);

    [Theory] // 票124:两个恢复端点是管理级写 → MockManage
    [InlineData(nameof(AssessmentController.RestoreMaterial))]
    [InlineData(nameof(AssessmentController.RestoreAllMaterials))]
    public void TrashRestore_RequiresMockManage(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockManage);

    // ---------- 邻居特征锁定:既有写口径不变 ----------

    [Fact] // 口径先例:上传录音本就挂 MockAnswer,本票两端点与之对齐
    public void SaveRecording_StillRequiresMockAnswer()
        => PolicyOf(nameof(AssessmentController.SaveRecording))
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockAnswer);

    // ---------- 票 F-XT-R2-1 本体:旅程 7 端点改挂 MockAnswer ----------

    [Theory] // 本体:User 练习旅程(创建→生成→作答→跳过→AI 评分→完成→放弃)改挂 MockAnswer;
    // 依据:User 持 MockAnswer 且旅程按 UserId 隔离,放行只影响本人会话;
    // Create 自邻居回归理论移入此处(票 55 先例);手动 Score 不在此列(见下方锁定)
    [InlineData(nameof(AssessmentController.Create))]
    [InlineData(nameof(AssessmentController.Generate))]
    [InlineData(nameof(AssessmentController.Answer))]
    [InlineData(nameof(AssessmentController.Skip))]
    [InlineData(nameof(AssessmentController.ScoreAi))]
    [InlineData(nameof(AssessmentController.Complete))]
    [InlineData(nameof(AssessmentController.Abandon))]
    public void PracticeJourney_RequiresMockAnswer(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockAnswer);

    [Theory] // 本体边界锁定:旅程外三端点继续 MockManage,防改挂扩散
    [InlineData(nameof(AssessmentController.AddQuestions))] // 人工加题:策展类,非旅程参与
    [InlineData(nameof(AssessmentController.Delete))] // 删会话:管理级写
    [InlineData(nameof(AssessmentController.Score))] // 手动评分:人工评分,非旅程 AI 评分
    public void JourneyExcluded_StillRequireMockManage(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockManage);

    [Theory] // 邻居回归:管理级写仍 MockManage(User 本就不可达,非本票造成)
    [InlineData(nameof(AssessmentController.SaveMaterials))]
    [InlineData(nameof(AssessmentController.DeleteRecording))]
    [InlineData(nameof(AssessmentController.TestSpeechCredential))] // 票86 定责同族锚点:候选凭据测试本就 MockManage
    [InlineData(nameof(AssessmentController.SaveSpeechSettings))]
    public void ManageEndpoints_StillRequireMockManage(string methodName)
        => PolicyOf(methodName)
            .Should().Be(PermissionPolicy.Prefix + Permissions.MockManage);

    [Theory] // 票86 #34 角色依据:Admin/PowerUser 持 MockManage,改挂后仍可达(不变)
    [InlineData(Roles.Admin)]
    [InlineData(Roles.PowerUser)]
    public void ManageRoles_ContainMockManage(string role)
        => Roles.DefaultPermissions[role].Should().Contain(Permissions.MockManage);

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

    [Fact] // 票86 #34 策略存在性:perm:mock.manage 必须已注册(同 MockAnswer 钉法)
    public void PermissionPolicies_ContainMockManagePolicyRequiringPermClaim()
    {
        var options = new AuthorizationOptions();
        PermissionPolicy.AddPermissionPolicies(options);

        var policy = options.GetPolicy(PermissionPolicy.Prefix + Permissions.MockManage);
        policy.Should().NotBeNull("perm:mock.manage 策略应由 AddPermissionPolicies 注册");
        policy!.Requirements.OfType<ClaimsAuthorizationRequirement>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new
            {
                ClaimType = CustomClaims.Permission,
                AllowedValues = new string?[] { Permissions.MockManage }
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
