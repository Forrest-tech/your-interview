using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Api;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票241 round-15(1) + round-16(1)(4) 回归:
///   · webhook 接收端的唯一鉴权机制是 Logto 签名验签,不是 JWT —— 坏签名/缺签名
///     一律 401,且门根本不执行(调用方伪造事件进不了审批门);
///   · 正向对照:合法签名能到达门并回 200(证明 401 测试不是因接线错误而“恒拒”);
///   · 终局失败告警:扫表连续失败 ≥ 3 次时健康端点 sweepFailing=true(队列路径
///     在本实现中不存在 —— 接收端是同步的,2xx 即三步全成;所谓“最终失败”在
///     本实现里对应“投递持续 5xx + 扫表兜底失败”,由健康端点 + ERROR 日志
///      surfacing,绝不被当成已送达);
///   · 批准标记的服务端独占:唯一能写 customData.approval 的应用内入口是
///     LogtoAdminController(经 Management API 写),且全部带 admin.users.* policy;
///     公开接口里没有任何接受调用方 customData/approval 的端点(本文件 grep
///     级结论:Api 目录下 customData 只出现在 LogtoAdminController 内)。
/// </summary>
public sealed class LogtoWebhookControllerTests
{
    private const string SigningKey = "test-signing-key";

    private static LogtoManagementOptions ConfiguredOptions() => new()
    {
        Endpoint = "http://logto:3001",
        M2mClientId = "test-m2m",
        M2mClientSecret = "test-secret",
        WebhookSigningKey = SigningKey,
    };

    private static LogtoWebhookController Controller(
        ILogtoManagementClient client, ILogtoMappingStore store, ILogtoSweepAudit audit)
    {
        var gate = new LogtoApprovalGate(client, store, audit,
            Options.Create(ConfiguredOptions()), NullLogger<LogtoApprovalGate>.Instance);
        return new LogtoWebhookController(gate, Options.Create(ConfiguredOptions()), new LogtoGateHealth());
    }

    private static void SetRequest(LogtoWebhookController c, byte[] body, string? signature)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);
        if (signature is not null) ctx.Request.Headers[LogtoWebhookSignature.HeaderName] = signature;
        c.ControllerContext = new ControllerContext { HttpContext = ctx };
    }

    private static string Sign(byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningKey));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact] // round-15(1):坏签名 → 401,门不执行
    public async Task UserCreated_BadSignature_Unauthorized_AndGateNeverRuns()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}""");
        SetRequest(c, body, "deadbeef");

        var result = await c.UserCreated(default);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        await client.DidNotReceiveWithAnyArgs().GetUserAsync(default!, default);
        await store.DidNotReceiveWithAnyArgs().GetRowByLogtoIdAsync(default!, default);
    }

    [Fact] // round-15(1):缺签名头 → 同样 401
    public async Task UserCreated_MissingSignature_Unauthorized()
    {
        var c = Controller(Substitute.For<ILogtoManagementClient>(),
            Substitute.For<ILogtoMappingStore>(), Substitute.For<ILogtoSweepAudit>());
        SetRequest(c, Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}"""), null);

        (await c.UserCreated(default)).Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact] // 正向对照:合法签名 → 门执行 → 200(已迁移 customData 直接放行)
    public async Task UserCreated_ValidSignature_ReachesGate_ReturnsOk()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var legacy = Guid.NewGuid();
        client.GetUserAsync("u1", Arg.Any<CancellationToken>()).Returns(
            new LogtoUserDto("u1", false,
                new JsonObject { ["approval"] = "migrated", ["legacyUserId"] = legacy.ToString() },
                DateTimeOffset.UtcNow, "u@test.local", true, DateTimeOffset.UtcNow));
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}""");
        SetRequest(c, body, Sign(body));

        var result = await c.UserCreated(default);

        result.Should().BeOfType<OkResult>();
        await client.Received(1).GetUserAsync("u1", Arg.Any<CancellationToken>());
    }

    [Fact] // round-16(1):扫表连续失败 ≥ 3 次 → 健康端点 sweepFailing=true;一次成功即清零
    public void Health_ConsecutiveSweepFailures_AlertFlag()
    {
        var health = new LogtoGateHealth();
        var controller = new LogtoHealthController(health,
            Options.Create(new LogtoManagementOptions
            {
                Endpoint = "http://logto:3001",
                M2mClientId = "id",
                M2mClientSecret = "secret",
                SweepIntervalMinutes = 15,
            }));

        health.RecordSweepFailure("e1");
        health.RecordSweepFailure("e2");
        Prop(controller.Health(), "sweepFailing").Should().Be(false);
        health.RecordSweepFailure("e3");
        Prop(controller.Health(), "sweepFailing").Should().Be(true);
        Prop(controller.Health(), "consecutiveSweepFailures").Should().Be(3);
        health.RecordSweepSuccess(0);
        Prop(controller.Health(), "sweepFailing").Should().Be(false);
        Prop(controller.Health(), "consecutiveSweepFailures").Should().Be(0);
    }

    [Fact] // round-16(4):批准标记写入口全部带 admin policy,无公开写入口
    public void ApprovalMarkWriters_AreAdminGated()
    {
        var type = typeof(LogtoAdminController);
        type.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        PolicyOf("Approve").Should().Be(PermissionPolicy.Prefix + Permissions.AdminUsersWrite);
        PolicyOf("ResetPassword").Should().Be(PermissionPolicy.Prefix + Permissions.AdminUsersWrite);
        PolicyOf("Unlock").Should().Be(PermissionPolicy.Prefix + Permissions.AdminUsersWrite);
        PolicyOf("Import").Should().Be(PermissionPolicy.Prefix + Permissions.AdminUsersWrite);
        PolicyOf("GetUser").Should().Be(PermissionPolicy.Prefix + Permissions.AdminUsersRead);

        string PolicyOf(string method) =>
            type.GetMethod(method)!.GetCustomAttribute<AuthorizeAttribute>()!.Policy!;
    }

    private static object? Prop(IActionResult result, string name)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value!.GetType().GetProperty(name)!.GetValue(ok.Value);
    }
}
