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
        => Controller(client, store, audit, ConfiguredOptions());

    private static LogtoWebhookController Controller(
        ILogtoManagementClient client, ILogtoMappingStore store, ILogtoSweepAudit audit,
        LogtoManagementOptions options)
    {
        var gate = new LogtoApprovalGate(client, store, audit,
            Options.Create(options), NullLogger<LogtoApprovalGate>.Instance);
        return new LogtoWebhookController(gate, Options.Create(options), new LogtoGateHealth());
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

    private static string SignWith(string key, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static LogtoManagementOptions MultiKeyOptions() => new()
    {
        Endpoint = "http://logto:3001",
        M2mClientId = "test-m2m",
        M2mClientSecret = "test-secret",
        WebhookSigningKeys = ["key-hook-created", "key-hook-deleted"],
    };

    // 票252(B8-A):控制器层 —— 列表中第二个 hook 的 key 签名也能放行(此前单 key 下必 401)
    [Fact]
    public async Task UserCreated_SignedBySecondListKey_ReachesGate_ReturnsOk()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var legacy = Guid.NewGuid();
        client.GetUserAsync("u1", Arg.Any<CancellationToken>()).Returns(
            new LogtoUserDto("u1", false,
                new JsonObject { ["approval"] = "migrated", ["legacyUserId"] = legacy.ToString() },
                DateTimeOffset.UtcNow, "u@test.local", DateTimeOffset.UtcNow));
        var c = Controller(client, store, audit, MultiKeyOptions());
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}""");
        SetRequest(c, body, SignWith("key-hook-deleted", body));

        var result = await c.UserCreated(default);

        result.Should().BeOfType<OkResult>();
        await client.Received(1).GetUserAsync("u1", Arg.Any<CancellationToken>());
    }

    // 票252(B8-A):列表中任一 key 都不匹配 → 401,门不执行
    [Fact]
    public async Task UserCreated_SignedByUnknownKey_Unauthorized_AndGateNeverRuns()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var c = Controller(client, store, audit, MultiKeyOptions());
        var body = Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}""");
        SetRequest(c, body, SignWith("key-unknown", body));

        var result = await c.UserCreated(default);

        result.Should().BeOfType<UnauthorizedObjectResult>();
        await client.DidNotReceiveWithAnyArgs().GetUserAsync(default!, default);
    }

    // 票252(B8-A):列表与单 key 全空 → WebhookConfigured 为假,接收端 503(投递方重发)
    [Fact]
    public async Task UserCreated_NoKeysConfigured_ReturnsServiceUnavailable()
    {
        var c = Controller(Substitute.For<ILogtoManagementClient>(),
            Substitute.For<ILogtoMappingStore>(), Substitute.For<ILogtoSweepAudit>(),
            new LogtoManagementOptions
            {
                Endpoint = "http://logto:3001",
                M2mClientId = "test-m2m",
                M2mClientSecret = "test-secret",
            });
        SetRequest(c, Encoding.UTF8.GetBytes("""{"event":"User.Created","data":{"id":"u1"}}"""), "deadbeef");

        var result = await c.UserCreated(default);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
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
                DateTimeOffset.UtcNow, "u@test.local", DateTimeOffset.UtcNow));
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

    // 票256(NTC-1):审计形态 user-data-updated —— user id 只在 params.userId
    // (data 只有变更字段、无 id)—— 必须 200 并触发翻转,不再 400"事件缺 user id"。
    // Fixture 事件值已非猜测:Logto 1.45.0 源码实证 —— schemas 注册表
    // 'PATCH /users/:userId/custom-data': 'User.Data.Updated'，经
    // context-manager 组装、hook/index buildWebhooks 在载荷首位放 event，
    // 故投递载荷恒含 event='User.Data.Updated'；data = PATCH 响应体即
    // user.customData 全量(无 id 字段)，与 run-3 实测一致。
    [Fact]
    public async Task UserDataUpdated_AuditShape_ReachesGate_FlipsToApproved()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var legacy = Guid.NewGuid();
        client.GetUserAsync("u9", Arg.Any<CancellationToken>()).Returns(
            new LogtoUserDto("u9", false,
                new JsonObject { ["approval"] = "pending", ["legacyUserId"] = legacy.ToString() },
                DateTimeOffset.UtcNow, "u9@test.local", DateTimeOffset.UtcNow));
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes(
            """{"event":"User.Data.Updated","createdAt":"2026-10-27T00:00:00.000Z","ip":"127.0.0.1","path":"/users/u9/custom-data","method":"PATCH","status":200,"params":{"userId":"u9"},"matchedRoute":"/users/:userId/custom-data","data":{"approval":"pending","legacyUserId":"00000000-0000-0000-0000-000000000000"},"hookId":"hook-1"}""");
        SetRequest(c, body, Sign(body));

        var result = await c.UserDataUpdated(default);

        result.Should().BeOfType<OkResult>();
        await client.Received(1).GetUserAsync("u9", Arg.Any<CancellationToken>());
        await audit.Received(1).WriteAsync("logto.approval.data-updated.flip", "u9",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // 票256:审计形态 user-created —— params.userId 同样到达门(置停用 + pending)。
    [Fact]
    public async Task UserCreated_AuditShape_ReachesGate_ReturnsOk()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        client.GetUserAsync("u9", Arg.Any<CancellationToken>()).Returns(
            new LogtoUserDto("u9", false, new JsonObject(),
                DateTimeOffset.UtcNow, "u9@test.local", DateTimeOffset.UtcNow));
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes(
            """{"event":"User.Created","params":{"userId":"u9"},"data":{}}""");
        SetRequest(c, body, Sign(body));

        var result = await c.UserCreated(default);

        result.Should().BeOfType<OkResult>();
        await client.Received(1).SuspendUserAsync("u9", true, Arg.Any<CancellationToken>());
    }

    // 票256:审计形态 suspension-status-updated —— id 门已过,但 data 缺 isSuspended
    // 时仍 400"事件缺 isSuspended"、门不执行:无错误动作,这是 presence 校验在守门,
    // 不是本票要修的范围(审计形态的停用事件是另一问题,见本票说明)。
    [Fact]
    public async Task SuspensionStatusUpdated_AuditShapeWithoutIsSuspended_ReturnsBadRequest_IsSuspended_NotUserId()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes(
            """{"event":"User.SuspensionStatus.Updated","params":{"userId":"u9"},"data":{"approval":"pending"}}""");
        SetRequest(c, body, Sign(body));

        var result = await c.SuspensionStatusUpdated(default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value).Should().Be("事件缺 isSuspended");
        await client.DidNotReceiveWithAnyArgs().GetUserAsync(default!, default);
    }

    // 票256:审计形态 user-deleted —— params.userId 到达门(无镜像行 → NoAction → 200)。
    [Fact]
    public async Task UserDeleted_AuditShape_ReachesGate_ReturnsOk()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes(
            """{"event":"User.Deleted","params":{"userId":"u9"},"data":{}}""");
        SetRequest(c, body, Sign(body));

        var result = await c.UserDeleted(default);

        result.Should().BeOfType<OkResult>();
        await store.Received(1).GetRowByLogtoIdAsync("u9", Arg.Any<CancellationToken>());
    }

    // 票256:回归 —— data.id / user.id / 根 userId 三种旧形态在 data-updated 上仍 200 翻转。
    [Theory]
    [InlineData("""{"event":"User.Data.Updated","data":{"id":"u9"}}""")]
    [InlineData("""{"event":"User.Data.Updated","user":{"id":"u9"}}""")]
    [InlineData("""{"event":"User.Data.Updated","userId":"u9"}""")]
    public async Task UserDataUpdated_LegacyShapes_StillFlip_ReturnsOk(string json)
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var legacy = Guid.NewGuid();
        client.GetUserAsync("u9", Arg.Any<CancellationToken>()).Returns(
            new LogtoUserDto("u9", false,
                new JsonObject { ["approval"] = "pending", ["legacyUserId"] = legacy.ToString() },
                DateTimeOffset.UtcNow, "u9@test.local", DateTimeOffset.UtcNow));
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes(json);
        SetRequest(c, body, Sign(body));

        var result = await c.UserDataUpdated(default);

        result.Should().BeOfType<OkResult>();
        await audit.Received(1).WriteAsync("logto.approval.data-updated.flip", "u9",
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // 票256:params.userId 是 LAST fallback —— data.id 在先时绝不越位。
    [Fact]
    public async Task UserDataUpdated_DataIdWinsOverParamsUserId()
    {
        var client = Substitute.For<ILogtoManagementClient>();
        var store = Substitute.For<ILogtoMappingStore>();
        var audit = Substitute.For<ILogtoSweepAudit>();
        var legacy = Guid.NewGuid();
        client.GetUserAsync("u1", Arg.Any<CancellationToken>()).Returns(
            new LogtoUserDto("u1", false,
                new JsonObject { ["approval"] = "pending", ["legacyUserId"] = legacy.ToString() },
                DateTimeOffset.UtcNow, "u1@test.local", DateTimeOffset.UtcNow));
        var c = Controller(client, store, audit);
        var body = Encoding.UTF8.GetBytes(
            """{"event":"User.Data.Updated","data":{"id":"u1"},"params":{"userId":"u9"}}""");
        SetRequest(c, body, Sign(body));

        var result = await c.UserDataUpdated(default);

        result.Should().BeOfType<OkResult>();
        await client.Received(1).GetUserAsync("u1", Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetUserAsync("u9", Arg.Any<CancellationToken>());
    }

    // 票256:无 id 形态 —— 仍 400"事件缺 user id",畸形载荷信号保留。
    [Theory]
    [InlineData("""{"event":"User.Data.Updated","data":{"approval":"pending"}}""")]
    [InlineData("""{"event":"User.Data.Updated","params":"u9","data":{}}""")]
    [InlineData("""{"event":"User.Data.Updated","params":{"userId":123},"data":{}}""")]
    public async Task UserDataUpdated_NoIdAnywhere_StillBadRequest(string json)
    {
        var c = Controller(Substitute.For<ILogtoManagementClient>(),
            Substitute.For<ILogtoMappingStore>(), Substitute.For<ILogtoSweepAudit>());
        var body = Encoding.UTF8.GetBytes(json);
        SetRequest(c, body, Sign(body));

        var result = await c.UserDataUpdated(default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value).Should().Be("事件缺 user id");
    }

    private static object? Prop(IActionResult result, string name)
    {
        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        return ok.Value!.GetType().GetProperty(name)!.GetValue(ok.Value);
    }
}
