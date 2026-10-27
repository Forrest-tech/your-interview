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
/// 票241 审批门:强制 guard(映射表里的迁移/已批用户绝不置停用/打标)、
/// 投递语义(三步都成功才 2xx,重发幂等、半完成续做、镜像写失败即投递失败)、
/// 悬停事件排序(fresh GET + LastEventAt 水位)、验证翻转两条路、
/// 扫表三道 + fail-closed 对账 + 删号传播。
/// 全部用内存 fake,不连库(门本身不依赖 DbContext,只依赖
/// ILogtoManagementClient / ILogtoMappingStore / ILogtoSweepAudit)。
/// </summary>
public class LogtoApprovalGateTests
{
    private sealed class FakeClient : ILogtoManagementClient
    {
        public readonly Dictionary<string, LogtoUserDto> Users = new();
        public int SuspendCalls;
        public int CustomDataCalls;
        public bool FailCustomData;

        /// <summary>round-14 并发测试钩子:在 SetCustomDataAsync 真正写之前调用(测试用它卡住第一个事件)。</summary>
        public Func<Task>? BeforeSetCustomDataAsync;

        public Task<LogtoUserDto?> GetUserAsync(string userId, CancellationToken ct) =>
            Task.FromResult(Users.TryGetValue(userId, out var u) ? u : null);

        public Task SuspendUserAsync(string userId, bool suspended, CancellationToken ct)
        {
            SuspendCalls++;
            // 真实 Logto 每次 PATCH 都推进 updatedAt;fake 同样推进,否则排序水位测试
            // 会把“同一版本重处理”误当成新事件(见 SuspensionStatus_Transitions 第三段)。
            Users[userId] = Users[userId] with
                { IsSuspended = suspended, UpdatedAt = Users[userId].UpdatedAt.AddSeconds(1) };
            return Task.CompletedTask;
        }

        public Task SetCustomDataAsync(string userId, JsonObject customData, CancellationToken ct)
        {
            // 钩子为空时保持同步完成(不改变其他测试的时序);钩子存在时才真正异步。
            async Task RunAsync()
            {
                if (BeforeSetCustomDataAsync is not null) await BeforeSetCustomDataAsync();
                if (FailCustomData) throw new HttpRequestException("simulated Management API failure");
                CustomDataCalls++;
                Users[userId] = Users[userId] with
                    { CustomData = customData, UpdatedAt = Users[userId].UpdatedAt.AddSeconds(1) };
            }
            return RunAsync();
        }

        public Task ResetPasswordAsync(string userId, string newPassword, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<LogtoUserDto>> ListUsersAsync(int page, int pageSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LogtoUserDto>>(Users.Values
                .OrderBy(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }

    private sealed class FakeStore : ILogtoMappingStore
    {
        public readonly Dictionary<string, LogtoMirrorRow> Rows = new();
        public readonly Dictionary<Guid, (bool Exists, bool IsActive)> OldUsers = new();
        public readonly Dictionary<string, Guid> OldByEmail = new(StringComparer.OrdinalIgnoreCase);
        public bool FailUpsert;

        public Task<LogtoMirrorRow?> GetRowByLogtoIdAsync(string logtoUserId, CancellationToken ct) =>
            Task.FromResult(Rows.TryGetValue(logtoUserId, out var r) ? r : null);
        public Task<LogtoMirrorRow?> GetRowByLegacyIdAsync(Guid legacyUserId, CancellationToken ct) =>
            Task.FromResult(Rows.Values.FirstOrDefault(r => r.LegacyUserId == legacyUserId));
        public Task<IReadOnlyList<LogtoMirrorRow>> GetAllRowsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LogtoMirrorRow>>(Rows.Values.ToList());
        public Task UpsertRowAsync(LogtoMirrorRow row, CancellationToken ct)
        {
            if (FailUpsert) throw new InvalidOperationException("simulated mirror write failure");
            Rows[row.LogtoUserId] = row;
            return Task.CompletedTask;
        }
        public Task<OldUserState?> GetOldUserByIdAsync(Guid legacyUserId, CancellationToken ct) =>
            Task.FromResult(OldUsers.TryGetValue(legacyUserId, out var s)
                ? new OldUserState(s.Exists, s.IsActive) : null);
        public Task<Guid?> GetOldUserByEmailAsync(string email, CancellationToken ct) =>
            Task.FromResult(OldByEmail.TryGetValue(email, out var g) ? g : (Guid?)null);
        public Task SetOldUserActiveAsync(Guid legacyUserId, bool active, CancellationToken ct)
        {
            if (OldUsers.TryGetValue(legacyUserId, out var s)) OldUsers[legacyUserId] = (s.Exists, active);
            return Task.CompletedTask;
        }
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

    // 票251 B4:User() 不再带 verified 参数 —— 1.45.0 用户对象无邮箱验证字段。
    private static LogtoUserDto User(string id, bool suspended, string? approval,
        DateTimeOffset? created = null, DateTimeOffset? updated = null,
        string? legacyId = null, string email = "u@example.com")
    {
        var cd = new JsonObject();
        if (approval is not null) cd["approval"] = approval;
        if (legacyId is not null) cd["legacyUserId"] = legacyId;
        return new LogtoUserDto(id, suspended, cd, created ?? DateTimeOffset.UtcNow, email,
            updated ?? DateTimeOffset.UtcNow);
    }

    private static LogtoApprovalGate Gate(FakeClient client, FakeStore store, FakeAudit audit,
        DateTimeOffset? launchedAt = null) =>
        new(client, store, audit,
            Options.Create(new LogtoManagementOptions { GateLaunchedAt = launchedAt?.ToString("o") ?? string.Empty }),
            NullLogger<LogtoApprovalGate>.Instance);

    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddHours(1);
    private static readonly DateTimeOffset T2 = T0.AddHours(2);

    [Fact] // 强制 guard:映射表里的迁移用户 —— 不置停用、不打标、不调 Management 写接口
    public async Task UserCreated_MigratedUser_PassesThroughUntouched()
    {
        var client = new FakeClient();
        client.Users["mig1"] = User("mig1", false, null);
        var store = new FakeStore();
        store.Rows["mig1"] = new LogtoMirrorRow("mig1", Guid.NewGuid(), "u@example.com", "migrated", false, false, null);
        var gate = Gate(client, store, new FakeAudit());

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
        var gate = Gate(client, new FakeStore(), new FakeAudit());

        (await gate.HandleUserCreatedAsync("mig2", default)).Should().Be(LogtoGateOutcome.PassedThroughMigrated);
        client.SuspendCalls.Should().Be(0);
    }

    [Fact] // 真新注册:置停用 + 打 pending + 分配 legacyUserId + 镜像行(最后一步)
    public async Task UserCreated_NewUser_SuspendsMarksAndMirrors()
    {
        var client = new FakeClient();
        client.Users["new1"] = User("new1", false, null);
        var store = new FakeStore();
        var gate = Gate(client, store, new FakeAudit());

        (await gate.HandleUserCreatedAsync("new1", default)).Should().Be(LogtoGateOutcome.Gated);
        client.Users["new1"].IsSuspended.Should().BeTrue();
        client.Users["new1"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        Guid.TryParse(client.Users["new1"].CustomData["legacyUserId"]?.GetValue<string>(), out _)
            .Should().BeTrue();
        store.Rows.Should().ContainKey("new1");
        store.Rows["new1"].Approval.Should().Be("pending");
        store.Rows["new1"].IsSuspended.Should().BeTrue();
        store.Rows["new1"].SuspendedByGate.Should().BeTrue();
    }

    [Fact] // 幂等重发:已停用且已打 pending → AlreadyGated,不重复写 Management,但补写镜像
    public async Task UserCreated_AlreadyGated_IsIdempotent_ButEnsuresMirror()
    {
        var client = new FakeClient();
        client.Users["new2"] = User("new2", true, "pending");
        var store = new FakeStore();
        var gate = Gate(client, store, new FakeAudit());

        (await gate.HandleUserCreatedAsync("new2", default)).Should().Be(LogtoGateOutcome.AlreadyGated);
        client.SuspendCalls.Should().Be(0);
        client.CustomDataCalls.Should().Be(0);
        store.Rows.Should().ContainKey("new2");
    }

    [Fact] // 半完成续做:已停用但未打标(打标那步失败后重发)—— 只补打标,不重复停用
    public async Task UserCreated_HalfDone_RedeliveryResumesMarkOnly()
    {
        var client = new FakeClient();
        client.Users["new3"] = User("new3", true, null);
        var store = new FakeStore();
        var gate = Gate(client, store, new FakeAudit());

        (await gate.HandleUserCreatedAsync("new3", default)).Should().Be(LogtoGateOutcome.Gated);
        client.SuspendCalls.Should().Be(0);
        client.CustomDataCalls.Should().Be(1);
        client.Users["new3"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        store.Rows.Should().ContainKey("new3");
    }

    [Fact] // 任一步失败 → 抛异常(控制器回 5xx,投递方重发);且停用已做、打标未做
    public async Task UserCreated_MarkFails_ThrowsForRedelivery()
    {
        var client = new FakeClient { FailCustomData = true };
        client.Users["new4"] = User("new4", false, null);
        var gate = Gate(client, new FakeStore(), new FakeAudit());

        var act = () => gate.HandleUserCreatedAsync("new4", default);
        await act.Should().ThrowAsync<HttpRequestException>();
        client.Users["new4"].IsSuspended.Should().BeTrue();
        client.Users["new4"].CustomData["approval"].Should().BeNull();
    }

    [Fact] // 镜像写失败 = 投递失败(2xx 条件含镜像写):三步前两步已做,仍抛异常等重发
    public async Task UserCreated_MirrorWriteFails_ThrowsForRedelivery()
    {
        var client = new FakeClient();
        client.Users["new5"] = User("new5", false, null);
        var store = new FakeStore { FailUpsert = true };
        var gate = Gate(client, store, new FakeAudit());

        var act = () => gate.HandleUserCreatedAsync("new5", default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        client.Users["new5"].IsSuspended.Should().BeTrue();
        client.Users["new5"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        store.Rows.Should().NotContainKey("new5");
    }

    [Fact] // 碰撞可见性:新注册邮箱撞上未迁移旧账号 → 审计行,但仍走标准门流程
    public async Task UserCreated_EmailCollision_WritesAudit_StillGates()
    {
        var client = new FakeClient();
        client.Users["col1"] = User("col1", false, null, email: "old@example.com");
        var store = new FakeStore();
        var oldId = Guid.NewGuid();
        store.OldByEmail["old@example.com"] = oldId;
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit);

        (await gate.HandleUserCreatedAsync("col1", default)).Should().Be(LogtoGateOutcome.Gated);
        audit.Rows.Should().ContainSingle(r => r.Action == "logto.approval.collision");
    }

    [Fact] // 批准:取消停用 + pending 已验证 → approved;停用事件本身、非 pending 取消停用均不动
    public async Task SuspensionStatus_Transitions()
    {
        var client = new FakeClient();
        client.Users["p1"] = User("p1", false, "pending", updated: T1);
        client.Users["p2"] = User("p2", false, "approved", updated: T1);
        var gate = Gate(client, new FakeStore(), new FakeAudit());

        (await gate.HandleSuspensionStatusUpdatedAsync("p1", false, default))
            .Should().Be(LogtoGateOutcome.Approved);
        client.Users["p1"].CustomData["approval"]?.GetValue<string>().Should().Be("approved");
        (await gate.HandleSuspensionStatusUpdatedAsync("p2", false, default))
            .Should().Be(LogtoGateOutcome.MirrorSynced);
        (await gate.HandleSuspensionStatusUpdatedAsync("p1", true, default))
            .Should().Be(LogtoGateOutcome.MirrorSynced);
    }

    [Fact] // 票251 B4:取消停用 pending 用户 = 管理员批准,直接翻 approved ——
    // 旧 UnverifiedHeld 分支已删除(1.45.0 用户对象无邮箱验证字段,旧分支恒成立)。
    public async Task SuspensionStatus_PendingUnsuspend_ApprovesDirectly()
    {
        var client = new FakeClient();
        client.Users["u1"] = User("u1", false, "pending", updated: T1);
        var store = new FakeStore();
        var gate = Gate(client, store, new FakeAudit());

        (await gate.HandleSuspensionStatusUpdatedAsync("u1", false, default))
            .Should().Be(LogtoGateOutcome.Approved);
        client.Users["u1"].CustomData["approval"]?.GetValue<string>().Should().Be("approved");
        store.Rows["u1"].Approval.Should().Be("approved");
        store.Rows["u1"].IsSuspended.Should().BeFalse();
    }

    [Fact] // 排序水位:镜像 LastEventAt 更新 → 延迟/重发的旧取消停用事件被忽略
    public async Task SuspensionStatus_StaleEvent_Ignored()
    {
        var client = new FakeClient();
        // fresh 现态是“已停用”(更新的停用),但 payload 说取消停用 —— fresh 赢。
        client.Users["s1"] = User("s1", true, "pending", updated: T1);
        var store = new FakeStore();
        store.Rows["s1"] = new LogtoMirrorRow("s1", Guid.NewGuid(), "u@example.com", "pending", true, false, T2);
        var gate = Gate(client, store, new FakeAudit());

        (await gate.HandleSuspensionStatusUpdatedAsync("s1", false, default))
            .Should().Be(LogtoGateOutcome.Ignored);
        client.SuspendCalls.Should().Be(0);
        client.CustomDataCalls.Should().Be(0);
    }

    [Fact] // fresh 赢过 payload:payload 说停用、fresh 说未停用 → 仍批准
    public async Task SuspensionStatus_FreshStateBeatsPayload()
    {
        var client = new FakeClient();
        client.Users["f1"] = User("f1", false, "pending", updated: T1);
        var gate = Gate(client, new FakeStore(), new FakeAudit());

        (await gate.HandleSuspensionStatusUpdatedAsync("f1", true, default))
            .Should().Be(LogtoGateOutcome.Approved);
    }

    [Fact] // round-8 8a 解锁双写:Console 取消停用 → 旧侧 IsActive 拉回启用,
    // 随后扫表看不到漂移(无对账动作);只测登录不够,必须断言对账无动作
    public async Task SuspensionStatus_UnlockDualWrite_ThenSweepSeesNoDivergence()
    {
        var legacy = Guid.NewGuid();
        var client = new FakeClient();
        client.Users["u2"] = User("u2", false, "approved", updated: T1, legacyId: legacy.ToString());
        var store = new FakeStore();
        store.Rows["u2"] = new LogtoMirrorRow("u2", legacy, "u@example.com", "approved", true, false, T0);
        store.OldUsers[legacy] = (true, false); // 旧侧停用中(此前 Console 停用双侧一致)
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit, launchedAt: T0.AddDays(-1));

        (await gate.HandleSuspensionStatusUpdatedAsync("u2", false, default))
            .Should().Be(LogtoGateOutcome.MirrorSynced);
        store.OldUsers[legacy].IsActive.Should().BeTrue();
        audit.Rows.Should().ContainSingle(r => r.Action == "logto.approval.unlock.dualwrite");

        var actions = await gate.ReconcileAsync(default);
        actions.Should().NotContain(a => a.Contains("u2"));
    }

    [Fact] // 票251 B4 改写 round-9(b):取消停用事件丢失、镜像 pending 未停用,
    // 数据更新事件到达 → 直接翻 approved,无需第二次管理员操作
    public async Task DataUpdated_OutOfOrderFlip_NoSecondAdminAction()
    {
        var legacy = Guid.NewGuid();
        var client = new FakeClient();
        client.Users["v1"] = User("v1", false, "pending", updated: T2, legacyId: legacy.ToString());
        var store = new FakeStore();
        store.Rows["v1"] = new LogtoMirrorRow("v1", legacy, "u@example.com", "pending", false, false, T1);
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit);

        (await gate.HandleUserDataUpdatedAsync("v1", default))
            .Should().Be(LogtoGateOutcome.DataUpdatedFlip);
        client.Users["v1"].CustomData["approval"]?.GetValue<string>().Should().Be("approved");
        store.Rows["v1"].Approval.Should().Be("approved");
        audit.Rows.Should().ContainSingle(r => r.Action == "logto.approval.data-updated.flip");
    }

    [Fact] // 数据更新事件不翻转的情形:仍停用 / 非 pending 一律 NoAction
    // (票251 B4:pending + 未停用现在翻转,见 OutOfOrderFlip 测试,不再列入 NoFlip)
    public async Task DataUpdated_NoFlip_WhenSuspendedOrNotPending()
    {
        var client = new FakeClient();
        client.Users["d1"] = User("d1", true, "pending");
        client.Users["d3"] = User("d3", false, "approved");
        var gate = Gate(client, new FakeStore(), new FakeAudit());

        (await gate.HandleUserDataUpdatedAsync("d1", default)).Should().Be(LogtoGateOutcome.NoAction);
        (await gate.HandleUserDataUpdatedAsync("d3", default)).Should().Be(LogtoGateOutcome.NoAction);
    }

    [Fact] // round-9(a) + round-11(3) 删号快传:镜像置 deleted + 旧侧停用 + 审计,
    // 且全部由 User.Deleted 处理器同步完成 —— 本测试全程不调 ReconcileAsync,
    // 证明三项在删除后数分钟内(远早于 15 分钟扫表周期)即完成,扫表只是兜底。
    // 门拒 deleted 行另见 BuildingBlocks 的 Old_WithRow_Deleted_Stripped。
    public async Task UserDeleted_MarksDeleted_AndDisablesOld()
    {
        var legacy = Guid.NewGuid();
        var client = new FakeClient();
        var store = new FakeStore();
        store.Rows["del1"] = new LogtoMirrorRow("del1", legacy, "u@example.com", "approved", false, false, null);
        store.OldUsers[legacy] = (true, true);
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit);

        await gate.HandleUserDeletedAsync("del1", default);
        store.Rows["del1"].Approval.Should().Be("deleted");
        store.Rows["del1"].IsSuspended.Should().BeTrue();
        store.OldUsers[legacy].IsActive.Should().BeFalse();
        audit.Rows.Should().ContainSingle(r => r.Action == "logto.approval.deleted");
    }

    [Fact] // round-13(1a) 镜像写失败重发补行:首次投递在置停用 + 打标成功后写行失败
    // (抛异常 → 控制器 5xx);重发时已停用 + 已打 pending,快分支必须先补行再回 2xx,
    // 绝不能跳过缺失的镜像行(否则残态卡住:门以为已处理,claims 门却无行可查)
    public async Task UserCreated_MirrorWriteFailure_ThenRedeliveryBackfills()
    {
        var client = new FakeClient();
        client.Users["rb1"] = User("rb1", false, null);
        var store = new FakeStore { FailUpsert = true };
        var gate = Gate(client, store, new FakeAudit());

        var first = () => gate.HandleUserCreatedAsync("rb1", default);
        await first.Should().ThrowAsync<InvalidOperationException>();
        store.Rows.Should().NotContainKey("rb1");

        store.FailUpsert = false;
        (await gate.HandleUserCreatedAsync("rb1", default)).Should().Be(LogtoGateOutcome.AlreadyGated);
        store.Rows.Should().ContainKey("rb1");
        store.Rows["rb1"].Approval.Should().Be("pending");
        store.Rows["rb1"].IsSuspended.Should().BeTrue();
        store.Rows["rb1"].SuspendedByGate.Should().BeTrue();
    }

    [Fact] // round-13(1b) + round-12(3) 扫表补行:Logto 侧有标记但无镜像行 → 按现态补行。
    // 补行不限上线时间(迁移用户的导入残态可能早于上线时间);pending + 未停用无行
    // 不在此列(归因规则:走 fail-closed 重置停用,那条路自己写行,见 Sweep_FailClosedCorrections)
    public async Task Sweep_MarkWithoutRow_BackfillsMirror_Unscoped()
    {
        var client = new FakeClient();
        client.Users["bf-approved"] = User("bf-approved", false, "approved", T0.AddDays(1));
        client.Users["bf-migrated-old"] = User("bf-migrated-old", true, "migrated", T0.AddDays(-30));
        client.Users["bf-pending-susp"] = User("bf-pending-susp", true, "pending", T0.AddDays(1));
        var store = new FakeStore();
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit, launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().Contain("mirror-backfill:bf-approved");
        actions.Should().Contain("mirror-backfill:bf-migrated-old");
        actions.Should().Contain("mirror-backfill:bf-pending-susp");
        store.Rows["bf-approved"].Approval.Should().Be("approved");
        store.Rows["bf-approved"].IsSuspended.Should().BeFalse();
        store.Rows["bf-migrated-old"].Approval.Should().Be("migrated");
        store.Rows["bf-migrated-old"].IsSuspended.Should().BeTrue();
        store.Rows["bf-pending-susp"].Approval.Should().Be("pending");
        store.Rows["bf-pending-susp"].IsSuspended.Should().BeTrue();
        audit.Rows.Should().Contain(r => r.Action == "logto.approval.sweep.mirror.backfill");
    }

    [Fact] // round-14(1) 并发悬停事件:同一用户的取消停用事件(A)与停用事件(B)并发,
    // 镜像终态必须与 Logto 现态一致,绝不能 last-writer-wins 到旧态。
    // 剧本:A 先读到未停用现态、卡在写 customData 处;管理员此时重新停用(Logto 现态
    // 已是停用);B 随后处理。若无按用户串行化,B 会先写停用、A 随后用旧快照
    // 覆盖成未停用 = 已停用用户拿到放行态。本测试用短暂延时让 B 先到达锁
    // (无锁实现下 B 会在此窗口内跑完,随后被 A 覆盖 → 断言失败;有锁时 B
    //  parked 在锁上,A 完成后 B 再读 fresh 现态纠正 → 终态停用)。
    public async Task SuspensionStatus_ConcurrentSuspendUnsuspend_MirrorMatchesLogto()
    {
        var legacy = Guid.NewGuid();
        var client = new FakeClient();
        client.Users["c1"] = User("c1", false, "pending", updated: T1, legacyId: legacy.ToString());
        var store = new FakeStore();
        store.Rows["c1"] = new LogtoMirrorRow("c1", legacy, "u@example.com", "pending", true, false, T0);
        var gate = Gate(client, store, new FakeAudit());

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        client.BeforeSetCustomDataAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var taskA = gate.HandleSuspensionStatusUpdatedAsync("c1", false, default);
        await entered.Task; // A 已读到未停用现态、卡在写 customData 处
        // 管理员重新停用:Logto 现态翻成停用(U3)
        client.Users["c1"] = User("c1", true, "pending", updated: T2, legacyId: legacy.ToString());
        var taskB = gate.HandleSuspensionStatusUpdatedAsync("c1", true, default);
        await Task.Delay(200); // 让 B 到达用户锁(无锁实现下 B 在此窗口内跑完)
        release.TrySetResult();
        await Task.WhenAll(taskA, taskB);

        // 终态必须与 Logto 一致:停用。approval 仍是 approved(A 的批准写已生效),
        // 但停用态为真 → 门拒绝,绝不是“批准 + 未停用”的放行态。
        store.Rows["c1"].IsSuspended.Should().BeTrue();
        client.Users["c1"].IsSuspended.Should().BeTrue();
        client.Users["c1"].CustomData["approval"]?.GetValue<string>().Should().Be("approved");
    }

    [Fact] // round-14(2) 对账范围再证:Logto-only 用户未停用时,对账绝不能因“旧侧查无此人”
    // 就把它当成旧侧停用而去停 Logto(缺失旧行 ≠ IsActive=false,缺失就是缺失)。
    public async Task Sweep_LogtoOnlyUser_Unsuspended_NotTouchedByOldSide()
    {
        var client = new FakeClient();
        client.Users["lo2"] = User("lo2", false, "approved", T0.AddDays(1));
        var store = new FakeStore();
        store.Rows["lo2"] = new LogtoMirrorRow("lo2", Guid.NewGuid(), "u@example.com", "approved", false, false, null);
        var gate = Gate(client, store, new FakeAudit(), launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().NotContain(a => a.Contains("lo2"));
        client.Users["lo2"].IsSuspended.Should().BeFalse();
    }

    [Fact] // round-13(3) 对账范围:Logto-only 用户(无旧侧行)只走镜像对账 ——
    // 停用态同步照做,但绝不产生旧侧对账动作(没有旧侧可写,也不许凭空建旧行)
    public async Task Sweep_LogtoOnlyUser_MirrorSyncOnly_NoOldSideReconcile()
    {
        var client = new FakeClient();
        client.Users["lo1"] = User("lo1", true, "approved", T0.AddDays(1));
        var store = new FakeStore();
        store.Rows["lo1"] = new LogtoMirrorRow("lo1", Guid.NewGuid(), "u@example.com", "approved", false, false, null);
        // 注意:OldUsers 里没有该 Guid —— Logto-only 用户
        var gate = Gate(client, store, new FakeAudit(), launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().ContainSingle(a => a == "suspension-sync:lo1");
        store.Rows["lo1"].IsSuspended.Should().BeTrue();
    }

    [Fact] // 扫表:pending 未停用且无法归因 → 重置停用+审计;无标未停用非迁移 → 门+审计;
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
        var store = new FakeStore();
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit, launchedAt: now.AddDays(-1));

        var actions = await gate.ReconcileAsync(default);
        // s-pending-active:无镜像行 → 无法归因 → 重置停用;s-unmarked-active → 门;
        // s-approved:有标记无行 → round-12 补行(补行不做审批决定,纯粹记账)。
        actions.Should().HaveCount(3);
        actions.Should().Contain("mirror-backfill:s-approved");
        actions.Should().Contain("resuspend-pending:s-pending-active");
        actions.Should().Contain("gate-unmarked:s-unmarked-active");
        client.Users["s-pending-active"].IsSuspended.Should().BeTrue();
        client.Users["s-unmarked-active"].IsSuspended.Should().BeTrue();
        client.Users["s-unmarked-active"].CustomData["approval"]?.GetValue<string>().Should().Be("pending");
        client.Users["s-unmarked-suspended"].CustomData["approval"].Should().BeNull();
        client.Users["s-old"].IsSuspended.Should().BeFalse();
        store.Rows["s-approved"].Approval.Should().Be("approved");
        audit.Rows.Should().HaveCount(3);
    }

    [Fact] // 扫表路验证翻转:镜像已记录解封 + 现态已验证 → approved(无需管理员再操作)
    public async Task Sweep_VerifiedFlip_WhenMirrorRecordedUnsuspend()
    {
        var legacy = Guid.NewGuid();
        var client = new FakeClient();
        client.Users["vf1"] = User("vf1", false, "pending", T0.AddDays(1), legacyId: legacy.ToString());
        var store = new FakeStore();
        store.Rows["vf1"] = new LogtoMirrorRow("vf1", legacy, "u@example.com", "pending", false, false, T1);
        var gate = Gate(client, store, new FakeAudit(), launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().ContainSingle(a => a == "approved-flip:vf1");
        client.Users["vf1"].CustomData["approval"]?.GetValue<string>().Should().Be("approved");
        store.Rows["vf1"].Approval.Should().Be("approved");
    }

    [Fact] // Pass 2:已映射 pending 行纠为 migrated;门误停(SuspendedByGate)解封,管理员停用不动
    public async Task Sweep_MappedPending_Corrected_ByProvenance()
    {
        var legacyGate = Guid.NewGuid();
        var legacyAdmin = Guid.NewGuid();
        var client = new FakeClient();
        client.Users["mp-gate"] = User("mp-gate", true, "pending", T0.AddDays(1), legacyId: legacyGate.ToString());
        client.Users["mp-admin"] = User("mp-admin", true, "pending", T0.AddDays(1), legacyId: legacyAdmin.ToString());
        var store = new FakeStore();
        store.Rows["mp-gate"] = new LogtoMirrorRow("mp-gate", legacyGate, "u@example.com", "pending", true, true, null);
        store.Rows["mp-admin"] = new LogtoMirrorRow("mp-admin", legacyAdmin, "u@example.com", "pending", true, false, null);
        store.OldUsers[legacyGate] = (true, true);
        store.OldUsers[legacyAdmin] = (true, true);
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit, launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().Contain("mapped-pending:mp-gate").And.Contain("mapped-pending:mp-admin");
        client.Users["mp-gate"].IsSuspended.Should().BeFalse(); // 导入窗口误停 → 解封
        client.Users["mp-admin"].IsSuspended.Should().BeTrue(); // 管理员停用 → 不动
        store.Rows["mp-gate"].Approval.Should().Be("migrated");
        store.Rows["mp-admin"].Approval.Should().Be("migrated");
        audit.Rows.Should().HaveCount(2);
    }

    [Fact] // round-7(b) fail-closed 对账演练:旧停用/Logto 启用 → 停 Logto;
    // 旧启用/Logto 停用 → 停旧侧;两方向都写审计,绝不自动启用任一侧
    public async Task Sweep_FailClosedReconciliation_BothDirections()
    {
        var legacyD2 = Guid.NewGuid();
        var legacyD1 = Guid.NewGuid();
        var client = new FakeClient();
        client.Users["rc-d2"] = User("rc-d2", false, "approved", T0.AddDays(1), legacyId: legacyD2.ToString());
        client.Users["rc-d1"] = User("rc-d1", true, "approved", T0.AddDays(1), legacyId: legacyD1.ToString());
        var store = new FakeStore();
        store.Rows["rc-d2"] = new LogtoMirrorRow("rc-d2", legacyD2, "u@example.com", "approved", false, false, null);
        store.Rows["rc-d1"] = new LogtoMirrorRow("rc-d1", legacyD1, "u@example.com", "approved", true, false, null);
        store.OldUsers[legacyD2] = (true, false); // 旧停用 + Logto 启用 → 停 Logto
        store.OldUsers[legacyD1] = (true, true);  // 旧启用 + Logto 停用 → 停旧侧
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit, launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().Contain("reconcile-suspend-logto:rc-d2");
        actions.Should().Contain("reconcile-disable-old:rc-d1");
        client.Users["rc-d2"].IsSuspended.Should().BeTrue();
        store.OldUsers[legacyD1].IsActive.Should().BeFalse();
        // 绝不自动启用:两用户最终都是两侧停用
        store.OldUsers[legacyD2].IsActive.Should().BeFalse();
        client.Users["rc-d1"].IsSuspended.Should().BeTrue();
        audit.Rows.Should().Contain(r => r.Action == "logto.approval.sweep.reconcile.suspend-logto");
        audit.Rows.Should().Contain(r => r.Action == "logto.approval.sweep.reconcile.disable-old");
    }

    [Fact] // 扫表存在检查:镜像行对应的 Logto 用户已不存在 → 置 deleted + 旧侧停用
    public async Task Sweep_DeletedUserDetected_ByExistenceCheck()
    {
        var legacy = Guid.NewGuid();
        var client = new FakeClient(); // 注意:del2 不在 Logto 侧 = 已删
        var store = new FakeStore();
        store.Rows["del2"] = new LogtoMirrorRow("del2", legacy, "u@example.com", "approved", false, false, null);
        store.OldUsers[legacy] = (true, true);
        var audit = new FakeAudit();
        var gate = Gate(client, store, audit, launchedAt: T0);

        var actions = await gate.ReconcileAsync(default);
        actions.Should().Contain("deleted:del2");
        store.Rows["del2"].Approval.Should().Be("deleted");
        store.OldUsers[legacy].IsActive.Should().BeFalse();
        audit.Rows.Should().ContainSingle(r => r.Action == "logto.approval.sweep.deleted");
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
