using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票248 R2 驳回通知:User.Deleted webhook 处理扩展 —— 仅 pending 行发信、
/// 非 pending 不发信、重投递不双发、发信失败不阻塞删除回执、无行不发信。
/// 全部用内存 fake,不连库、不发真邮件(门只依赖 ILogtoMappingStore /
/// ILogtoSweepAudit / IRejectionNotifier,删号处理不调 Management 客户端)。
/// </summary>
public class LogtoRejectionEmailTests
{
    private sealed class FakeClient : ILogtoManagementClient
    {
        public Task<LogtoUserDto?> GetUserAsync(string userId, CancellationToken ct) =>
            Task.FromResult<LogtoUserDto?>(null);
        public Task SuspendUserAsync(string userId, bool suspended, CancellationToken ct) =>
            Task.CompletedTask;
        public Task SetCustomDataAsync(string userId, JsonObject customData, CancellationToken ct) =>
            Task.CompletedTask;
        public Task ResetPasswordAsync(string userId, string newPassword, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<LogtoUserDto>> ListUsersAsync(int page, int pageSize, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LogtoUserDto>>(Array.Empty<LogtoUserDto>());
    }

    private sealed class FakeStore : ILogtoMappingStore
    {
        public readonly Dictionary<string, LogtoMirrorRow> Rows = new();
        public readonly Dictionary<Guid, (bool Exists, bool IsActive)> OldUsers = new();

        public Task<LogtoMirrorRow?> GetRowByLogtoIdAsync(string logtoUserId, CancellationToken ct) =>
            Task.FromResult(Rows.TryGetValue(logtoUserId, out var r) ? r : null);
        public Task<LogtoMirrorRow?> GetRowByLegacyIdAsync(Guid legacyUserId, CancellationToken ct) =>
            Task.FromResult(Rows.Values.FirstOrDefault(r => r.LegacyUserId == legacyUserId));
        public Task<IReadOnlyList<LogtoMirrorRow>> GetAllRowsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LogtoMirrorRow>>(Rows.Values.ToList());
        public Task UpsertRowAsync(LogtoMirrorRow row, CancellationToken ct)
        {
            Rows[row.LogtoUserId] = row;
            return Task.CompletedTask;
        }
        public Task<OldUserState?> GetOldUserByIdAsync(Guid legacyUserId, CancellationToken ct) =>
            Task.FromResult(OldUsers.TryGetValue(legacyUserId, out var s)
                ? new OldUserState(s.Exists, s.IsActive) : null);
        public Task<Guid?> GetOldUserByEmailAsync(string email, CancellationToken ct) =>
            Task.FromResult<Guid?>(null);
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

    private sealed class FakeNotifier : IRejectionNotifier
    {
        public readonly List<string> Sent = new();
        public bool Fail;

        public Task SendRejectionAsync(string recipientEmail, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("simulated SMTP failure");
            Sent.Add(recipientEmail);
            return Task.CompletedTask;
        }
    }

    private static LogtoApprovalGate Gate(FakeStore store, FakeAudit audit, FakeNotifier notifier) =>
        new(new FakeClient(), store, audit,
            Options.Create(new LogtoManagementOptions()),
            NullLogger<LogtoApprovalGate>.Instance, notifier);

    private static FakeStore StoreWith(string id, string approval, string email, bool oldActive = true)
    {
        var store = new FakeStore();
        var legacy = Guid.NewGuid();
        store.Rows[id] = new LogtoMirrorRow(id, legacy, email, approval, true, false, null);
        store.OldUsers[legacy] = (true, oldActive);
        return store;
    }

    [Fact] // pending 行被删 → 发一封驳回邮件,镜像翻 deleted,旧侧停用
    public async Task Deleted_PendingRow_SendsRejectionEmail()
    {
        var store = StoreWith("pend1", "pending", "applicant@example.com");
        var audit = new FakeAudit();
        var notifier = new FakeNotifier();
        var gate = Gate(store, audit, notifier);

        (await gate.HandleUserDeletedAsync("pend1", default)).Should().Be(LogtoGateOutcome.MirrorSynced);
        notifier.Sent.Should().Equal("applicant@example.com");
        store.Rows["pend1"].Approval.Should().Be("deleted");
        store.OldUsers.Values.Single().IsActive.Should().BeFalse();
        audit.Rows.Should().Contain(r => r.Action == "logto.approval.rejection-email.sent");
    }

    [Theory] // approved / migrated 删除不是驳回 → 不发信
    [InlineData("approved")]
    [InlineData("migrated")]
    public async Task Deleted_NonPendingRow_SendsNothing(string approval)
    {
        var store = StoreWith("u1", approval, "someone@example.com");
        var audit = new FakeAudit();
        var notifier = new FakeNotifier();
        var gate = Gate(store, audit, notifier);

        (await gate.HandleUserDeletedAsync("u1", default)).Should().Be(LogtoGateOutcome.MirrorSynced);
        notifier.Sent.Should().BeEmpty();
        audit.Rows.Should().NotContain(r => r.Action.StartsWith("logto.approval.rejection-email"));
    }

    [Fact] // 重投递:第二次读到的行已是 deleted → 不双发
    public async Task Deleted_DuplicateDelivery_SendsOnlyOnce()
    {
        var store = StoreWith("pend2", "pending", "dup@example.com");
        var notifier = new FakeNotifier();
        var gate = Gate(store, new FakeAudit(), notifier);

        await gate.HandleUserDeletedAsync("pend2", default);
        (await gate.HandleUserDeletedAsync("pend2", default)).Should().Be(LogtoGateOutcome.MirrorSynced);
        notifier.Sent.Should().HaveCount(1);
    }

    [Fact] // 发信失败 → 不抛、不阻塞删除回执,镜像照常翻 deleted,失败记审计
    public async Task Deleted_SendFailure_DoesNotBlockAck()
    {
        var store = StoreWith("pend3", "pending", "fail@example.com");
        var audit = new FakeAudit();
        var notifier = new FakeNotifier { Fail = true };
        var gate = Gate(store, audit, notifier);

        var act = async () => await gate.HandleUserDeletedAsync("pend3", default);
        (await act.Should().NotThrowAsync()).Which.Should().Be(LogtoGateOutcome.MirrorSynced);
        store.Rows["pend3"].Approval.Should().Be("deleted");
        audit.Rows.Should().Contain(r => r.Action == "logto.approval.rejection-email.failed");
    }

    [Fact] // 无镜像行 → NoAction,不发信
    public async Task Deleted_NoRow_SendsNothing()
    {
        var notifier = new FakeNotifier();
        var gate = Gate(new FakeStore(), new FakeAudit(), notifier);

        (await gate.HandleUserDeletedAsync("ghost", default)).Should().Be(LogtoGateOutcome.NoAction);
        notifier.Sent.Should().BeEmpty();
    }

    [Fact] // pending 行但无邮箱 → 跳过发信,不抛,镜像照常翻 deleted
    public async Task Deleted_PendingRowWithoutEmail_SkipsSend()
    {
        var store = StoreWith("pend4", "pending", string.Empty);
        var notifier = new FakeNotifier();
        var gate = Gate(store, new FakeAudit(), notifier);

        (await gate.HandleUserDeletedAsync("pend4", default)).Should().Be(LogtoGateOutcome.MirrorSynced);
        notifier.Sent.Should().BeEmpty();
        store.Rows["pend4"].Approval.Should().Be("deleted");
    }

    [Fact] // 发信器未配 SMTP → 停用态,不抛不发
    public async Task SmtpNotifier_Unconfigured_IsNoop()
    {
        var sender = new SmtpRejectionNotifier(
            Options.Create(new RejectionEmailOptions()),
            NullLogger<SmtpRejectionNotifier>.Instance);

        var act = async () => await sender.SendRejectionAsync("x@example.com", default);
        await act.Should().NotThrowAsync();
    }

    [Fact] // 模板:中性 outcome-only —— 终局 + 重申,不含内部原因措辞
    public void RejectionBody_IsNeutralOutcomeOnly()
    {
        var body = SmtpRejectionNotifier.BuildBody();
        body.Should().Contain("not approved");
        body.Should().Contain("apply again");
        body.Should().NotContain("reason");
        SmtpRejectionNotifier.Subject.Should().NotBeNullOrWhiteSpace();
    }
}
