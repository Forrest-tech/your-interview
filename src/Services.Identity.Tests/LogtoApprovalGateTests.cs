using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票241 审批门:强制 guard(映射表里的迁移用户绝不置停用/打标)、
/// 投递语义(两步都成功才算成功,重发幂等、半完成续做)、扫表 fail-closed。
/// 全部用内存 fake,不连库(Identity 测试项目无 InMemory 包;门本身不依赖
/// DbContext,只依赖 ILogtoManagementClient / ILogtoMappingStore / ILogtoSweepAudit)。
/// </summary>
public class LogtoApprovalGateTests
{
    private sealed class FakeClient : ILogtoManagementClient
    {
        public readonly Dictionary<string, LogtoUserDto> Users = new();
        public int SuspendCalls;
        public int CustomDataCalls;
        public bool FailCustomData;

        public Task<LogtoUserDto?> GetUserAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Users.TryGetValue(userId, out var u) ? u : null);

        public Task SuspendUserAsync(string userId, bool suspended, CancellationToken ct)
        {
            SuspendCalls++;
            Users[userId] = Users[userId] with { IsSuspended = suspended };
            return Task.CompletedTask;
        }

        public Task SetCustomDataAsync(string userId, JsonObject customData, CancellationToken ct)
        {
            if (FailCustomData) throw new HttpRequestException("simulated Management API failure");
            CustomDataCalls++;
            Users[userId] = Users[userId] with { CustomData = customData };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogtoUserDto>> ListUsersAsync(int page, int pageSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LogtoUserDto>>(Users.Values
                .OrderBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }

    private sealed class FakeStore(HashSet<string> migrated) : ILogtoMappingStore
    {
        public Task<bool> IsMigratedAsync(string logtoUserId, CancellationToken ct) =>
            Task.FromResult(migrated.Contains(logtoUserId));
        public Task<HashSet<string>> GetAllMigratedIdsAsync(CancellationToken ct) =>
            Task.FromResult(new HashSet<string>(migrated));
    }

    private sealed class FakeAudit : ILogtoSweepAudit
    {
        public readonly List<(string Action, string UserId, string Detail)> Rows = new();
        public Task WriteAsync(string action, string logtoUserId, string detail, CancellationToken ct)
        {
            Rows.Add((action, logtoUserId, detail));
            return Task.CompletedTask;
        }
    }

    private static LogtoUserDto User(string id, bool suspended, string? approval, DateTimeOffset? created = null) =>
        new(id, suspended,
            approval is null ? new JsonObject() : new JsonObject { ["approval"] = approval },
            created ?? DateTimeOffset.UtcNow, "u@example.com");

    private static LogtoApprovalGate Gate(FakeClient client, FakeStore store, FakeAudit audit,
        DateTimeOffset? launchedAt = null) =>
        new(client, store, audit,
            Options.Create(new LogtoManagementOptions { GateLaunchedAt = launchedAt?.ToString("o") ?? string.Empty }),
            NullLogger<LogtoApprovalGate>.Instance);

    [Fact] // 强制 guard:映射表里的迁移用户 —— 不置停用、不打标、不调 Management 写接口
    public async Task UserCreated_MigratedUser_PassesThroughUntouched()
    {
        var client = new FakeClient();
        client.Users["mig1"] = User("mig1", false, null);
        var gate = Gate(client, new FakeStore(["mig1"]), new FakeAudit());

        (await gate.HandleUserCreatedAsync("mig1", default)).Should().Be(LogtoGateOutcome.PassedThroughMigrated);
        client.SuspendCalls.Should().Be(0);
        client.CustomDataCalls.Should().Be(0);
        client.Users["mig1"].IsSuspended.Should().BeFalse();
    }

    [Fact] // 第一批 admin 导入场景:customData 已打 migrated 同样放行(映射行延迟的第二层)
    public async Task UserCreated_CustomDataMigrated_PassesThrough()
    {
        var client = new FakeClient();
        client.Users["mig2"] = User("mig2", false, "migrated");
        var gate = Gate(client, new FakeStore([]), new FakeAudit());

        (await gate.HandleUserCreatedAsync("mig2", default)).Should().Be(LogtoGateOutcome.PassedThroughMigrated);
        client.SuspendCalls.Should().Be(0);
    }

    [Fact] // 真新注册:置停用 + 打 pending + 分配 legacyUserId
    public async Task UserCreated_NewUser_SuspendsAndMarksPending()
    {
        var client = new FakeClient();
        client.Users["new1"] = User("new1", false, null);
        var gate = Gate(client, new FakeStore([]), new FakeAudit());

        (await gate.HandleUserCreatedAsync("new1", default)).Should().Be(LogtoGateOutcome.Gated);
        client.Users["new1"].IsSuspended.Should().BeTrue();
        client.Users["new1"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        Guid.TryParse(client.Users["new1"].CustomData["legacyUserId"]?.GetValue<string>(), out _)
            .Should().BeTrue();
    }

    [Fact] // 幂等重发:已停用且已打 pending → AlreadyGated,不重复写
    public async Task UserCreated_AlreadyGated_IsIdempotent()
    {
        var client = new FakeClient();
        client.Users["new2"] = User("new2", true, "pending");
        var gate = Gate(client, new FakeStore([]), new FakeAudit());

        (await gate.HandleUserCreatedAsync("new2", default)).Should().Be(LogtoGateOutcome.AlreadyGated);
        client.SuspendCalls.Should().Be(0);
        client.CustomDataCalls.Should().Be(0);
    }

    [Fact] // 半完成续做:已停用但未打标(打标那步失败后重发)—— 只补打标,不重复停用
    public async Task UserCreated_HalfDone_RedeliveryResumesMarkOnly()
    {
        var client = new FakeClient();
        client.Users["new3"] = User("new3", true, null);
        var gate = Gate(client, new FakeStore([]), new FakeAudit());

        (await gate.HandleUserCreatedAsync("new3", default)).Should().Be(LogtoGateOutcome.Gated);
        client.SuspendCalls.Should().Be(0);
        client.CustomDataCalls.Should().Be(1);
        client.Users["new3"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
    }

    [Fact] // 任一步失败 → 抛异常(控制器回 5xx,投递方重发);且停用已做、打标未做
    public async Task UserCreated_MarkFails_ThrowsForRedelivery()
    {
        var client = new FakeClient { FailCustomData = true };
        client.Users["new4"] = User("new4", false, null);
        var gate = Gate(client, new FakeStore([]), new FakeAudit());

        var act = () => gate.HandleUserCreatedAsync("new4", default);
        await act.Should().ThrowAsync<HttpRequestException>();
        client.Users["new4"].IsSuspended.Should().BeTrue();
        client.Users["new4"].CustomData["approval"].Should().BeNull();
    }

    [Fact] // 批准:取消停用 + pending → approved;停用事件本身、非 pending 取消停用均不动
    public async Task SuspensionStatus_Transitions()
    {
        var client = new FakeClient();
        client.Users["p1"] = User("p1", false, "pending");
        client.Users["p2"] = User("p2", false, "approved");
        var gate = Gate(client, new FakeStore([]), new FakeAudit());

        (await gate.HandleSuspensionStatusUpdatedAsync("p1", false, default))
            .Should().Be(LogtoGateOutcome.Approved);
        client.Users["p1"].CustomData["approval"]?.GetValue<string>().Should().Be("approved");
        (await gate.HandleSuspensionStatusUpdatedAsync("p2", false, default))
            .Should().Be(LogtoGateOutcome.NoAction);
        (await gate.HandleSuspensionStatusUpdatedAsync("p1", true, default))
            .Should().Be(LogtoGateOutcome.NoAction);
    }

    [Fact] // 扫表:pending 未停用 → 重置停用+审计;无标未停用非迁移 → 门+审计;
    // 无标已停用(其他原因)不动;approved/migrated 不动;上线前创建的不动
    public async Task Sweep_FailClosedCorrections()
    {
        var client = new FakeClient();
        var now = DateTimeOffset.UtcNow;
        client.Users["s-pending-active"] = User("s-pending-active", false, "pending", now);
        client.Users["s-unmarked-active"] = User("s-unmarked-active", false, null, now);
        client.Users["s-unmarked-suspended"] = User("s-unmarked-suspended", true, null, now);
        client.Users["s-approved"] = User("s-approved", false, "approved", now);
        client.Users["s-old"] = User("s-old", false, null, now.AddDays(-30));
        var audit = new FakeAudit();
        var gate = Gate(client, new FakeStore([]), audit, launchedAt: now.AddDays(-1));

        var actions = await gate.ReconcileAsync(default);
        actions.Should().HaveCount(2);
        client.Users["s-pending-active"].IsSuspended.Should().BeTrue();
        client.Users["s-unmarked-active"].IsSuspended.Should().BeTrue();
        client.Users["s-unmarked-active"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        client.Users["s-unmarked-suspended"].CustomData["approval"].Should().BeNull();
        client.Users["s-old"].IsSuspended.Should().BeFalse();
        audit.Rows.Should().HaveCount(2);
    }

    [Fact] // 验签:HMAC-SHA256 hex;篡改、缺头、错密钥均失败
    public void Signature_Verify()
    {
        const string key = "test-signing-key";
        var body = Encoding.UTF8.GetBytes("{\"event\":\"User.Created\"}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var sig = Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();

        LogtoWebhookSignature.Verify(key, body, sig).Should().BeTrue();
        LogtoWebhookSignature.Verify(key, Encoding.UTF8.GetBytes("{\"event\":\"User.Deleted\"}"), sig)
            .Should().BeFalse();
        LogtoWebhookSignature.Verify(key, body, null).Should().BeFalse();
        LogtoWebhookSignature.Verify("wrong-key", body, sig).Should().BeFalse();
    }
}
