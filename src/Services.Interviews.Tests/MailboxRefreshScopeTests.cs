using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 306：刷新任务按 (user, entry) 分槽 + 扫描进度 + 心跳/停滞/强制取消。
/// 纯跟踪逻辑不依赖 DB；状态查询例复用 303 同款沙盒 Postgres 门控（连不上则跳过）。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）。
/// </summary>
public sealed class MailboxRefreshScopeTests : IAsyncLifetime, IDisposable
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb(Guid.NewGuid());
        try
        {
            _available = await db.Database.CanConnectAsync();
            if (_available) await db.Database.MigrateAsync();
        }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        try
        {
            await using var db = NewDb(_users[0]);
            var sets = new[]
            {
                (IQueryable<object>)db.MailboxImportRecords.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)),
                db.MailboxPendingItems.IgnoreQueryFilters().Where(p => _users.Contains(p.UserId)),
                db.MailboxConnections.IgnoreQueryFilters().Where(c => _users.Contains(c.UserId)),
                db.Entries.IgnoreQueryFilters().Where(e => _users.Contains(e.UserId)),
            };
            foreach (var set in sets)
                foreach (var row in await set.ToListAsync()) db.Remove(row);
            await db.SaveChangesAsync();
        }
        catch { /* 清理失败不阻塞套件；各例 UserId 随机，不干扰后续例 */ }
    }

    public void Dispose() { }

    private InterviewsDbContext NewDb(Guid userId)
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql(Cs, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", InterviewsDbContext.Schema))
            .Options;
        return new InterviewsDbContext(opts, new FixedCurrentUser(userId));
    }

    private Guid NewUser()
    {
        var userId = Guid.NewGuid();
        _users.Add(userId);
        return userId;
    }

    private static MailboxProtection NewProtection() =>
        new(DataProtectionProvider.Create("test-306"));

    private static Task<Result<RefreshMailboxResult>> BlockedRun(
        TaskCompletionSource<Result<RefreshMailboxResult>> gate, CancellationToken ct) =>
        gate.Task.WaitAsync(ct);

    private static Result<RefreshMailboxResult> DoneResult(int added = 0) =>
        Result.Success(new RefreshMailboxResult(added, 0, 0, 0, [], false, false, false, false));

    // ============================ 纯跟踪逻辑（不依赖 DB） ============================

    [Fact]
    public async Task Scope_EntryJobAndGlobalJob_RunIndependently()
    {
        var tracker = new MailboxRefreshJobTracker();
        var userId = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run = ct => BlockedRun(gate, ct);

        var global = tracker.StartOrAttach(userId, null, run);
        var scoped = tracker.StartOrAttach(userId, entryB, run);
        scoped.JobId.Should().NotBe(global.JobId);
        tracker.TryGet(userId, null)?.JobId.Should().Be(global.JobId);
        tracker.TryGet(userId, entryB)?.JobId.Should().Be(scoped.JobId);
        // 同槽再点仍返回同一运行任务。
        tracker.StartOrAttach(userId, null, run).JobId.Should().Be(global.JobId);
        tracker.StartOrAttach(userId, entryB, run).JobId.Should().Be(scoped.JobId);

        // 取消全局槽不影响条目槽。
        tracker.TryCancel(userId, null).Cancelled.Should().BeTrue();
        await Task.Delay(200);
        tracker.TryGet(userId, null)?.State.Should().Be(MailboxRefreshJobState.Cancelled);
        tracker.TryGet(userId, entryB)?.State.Should().Be(MailboxRefreshJobState.Running);

        tracker.TryCancel(userId, entryB).Cancelled.Should().BeTrue();
        await Task.Delay(200);
        tracker.TryGet(userId, entryB)?.State.Should().Be(MailboxRefreshJobState.Cancelled);
    }

    [Fact]
    public async Task Scope_ForceCancel_DropsEntry_AndLateFinishDoesNotResurrect()
    {
        var tracker = new MailboxRefreshJobTracker();
        var userId = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run = ct => BlockedRun(gate, ct);

        tracker.StartOrAttach(userId, entryB, run);
        tracker.ReportListed(userId, entryB, 5);
        tracker.TryGet(userId, entryB)?.Scanned.Should().Be(5);

        tracker.TryForceCancel(userId, entryB).Should().BeTrue();
        tracker.TryGet(userId, entryB).Should().BeNull();
        // 删槽后上报 no-op，不抛错。
        tracker.Invoking(t => t.ReportListed(userId, entryB, 3)).Should().NotThrow();
        tracker.Invoking(t => t.ReportChecked(userId, entryB)).Should().NotThrow();
        tracker.TryForceCancel(userId, entryB).Should().BeFalse();

        // 迟到的后台收尾（任务完成）不得复活已删条目；同槽可全新启动。
        gate.SetResult(DoneResult());
        await Task.Delay(300);
        tracker.TryGet(userId, entryB).Should().BeNull();
        var restarted = tracker.StartOrAttach(userId, entryB, _ => Task.FromResult(DoneResult(1)));
        restarted.State.Should().Be(MailboxRefreshJobState.Running);
        await Task.Delay(300);
        tracker.TryGet(userId, entryB)?.State.Should().Be(MailboxRefreshJobState.Done);
    }

    [Fact]
    public void Scope_ForceCancel_OtherScopeUnaffected()
    {
        var tracker = new MailboxRefreshJobTracker();
        var userId = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run = ct => BlockedRun(gate, ct);

        tracker.StartOrAttach(userId, null, run);
        tracker.StartOrAttach(userId, entryB, run);
        tracker.TryForceCancel(userId, null).Should().BeTrue();
        tracker.TryGet(userId, null).Should().BeNull();
        tracker.TryGet(userId, entryB)?.State.Should().Be(MailboxRefreshJobState.Running);
    }

    [Fact]
    public void Scope_Progress_Accumulates_AndHeartbeatAdvances()
    {
        var tracker = new MailboxRefreshJobTracker();
        var userId = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        tracker.StartOrAttach(userId, entryB, ct => BlockedRun(gate, ct));

        var before = tracker.TryGet(userId, entryB)!.LastActivityAt;
        Thread.Sleep(60);
        tracker.ReportListed(userId, entryB, 3);
        tracker.ReportListed(userId, entryB, 2);
        var snap = tracker.TryGet(userId, entryB)!;
        snap.Scanned.Should().Be(5);
        snap.Phase.Should().Be("listing");
        snap.LastActivityAt.Should().BeAfter(before);

        tracker.ReportChecked(userId, entryB);
        tracker.ReportChecked(userId, entryB);
        snap = tracker.TryGet(userId, entryB)!;
        snap.Checked.Should().Be(2);
        snap.Phase.Should().Be("fetching");

        // 非正数列取不上报；终态/无任务上报 no-op。
        tracker.ReportListed(userId, entryB, 0);
        tracker.TryGet(userId, entryB)!.Scanned.Should().Be(5);
        tracker.ReportListed(Guid.NewGuid(), entryB, 9);
        tracker.ReportChecked(Guid.NewGuid(), entryB);
    }

    [Fact]
    public void Scope_StaleAfter_DefaultIsFiveMinutes()
    {
        new MailboxRefreshJobTracker().StaleAfter.Should().Be(TimeSpan.FromMinutes(5));
    }

    // ============================ 状态查询（需沙盒 DB） ============================

    [Fact]
    public async Task Status_RunningJob_ReportsScannedChecked_AndStaleFlag()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        // 极小阈值：不睡 5 分钟也能测出 stale 两方向。
        var tracker = new MailboxRefreshJobTracker(TimeSpan.FromMilliseconds(200));
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        tracker.StartOrAttach(userId, null, ct => BlockedRun(gate, ct));
        tracker.ReportListed(userId, null, 4);
        tracker.ReportChecked(userId, null);
        tracker.ReportChecked(userId, null);

        await Task.Delay(300);
        var handler = new GetMailboxRefreshStatusQueryHandler(tracker, db);
        var stale = await handler.Handle(new GetMailboxRefreshStatusQuery(userId), CancellationToken.None);
        stale.IsSuccess.Should().BeTrue();
        stale.Value.State.Should().Be("Running");
        stale.Value.Scanned.Should().Be(4);
        stale.Value.Checked.Should().Be(2);
        stale.Value.Phase.Should().Be("fetching");
        stale.Value.Stale.Should().BeTrue();

        // 一次新上报即刷新心跳，stale 清除。
        tracker.ReportChecked(userId, null);
        var fresh = await handler.Handle(new GetMailboxRefreshStatusQuery(userId), CancellationToken.None);
        fresh.Value.Stale.Should().BeFalse();
        fresh.Value.Checked.Should().Be(3);

        tracker.TryForceCancel(userId, null);
    }

    [Fact]
    public async Task Status_ScopedCounts_OnlyCountThatEntry()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        var entryA = Guid.NewGuid();
        var entryB = Guid.NewGuid();
        db.Entries.Add(new InterviewEntry(entryA, "Corpay", "Developer", userId: userId));
        db.Entries.Add(new InterviewEntry(entryB, "Other", "Developer", userId: userId));
        db.MailboxConnections.Add(new MailboxConnection(userId, MailboxProvider.Gmail, "user@example.com",
            "https://www.googleapis.com/auth/gmail.readonly openid email profile",
            protection.ProtectRefreshToken("rt")));

        // 先起槽内阻塞任务，再落审计行——状态计数只算任务开始后的行。
        var tracker = new MailboxRefreshJobTracker();
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        tracker.StartOrAttach(userId, entryA, ct => BlockedRun(gate, ct));

        db.MailboxImportRecords.Add(new MailboxImportRecord(userId, entryA, null, MailboxProvider.Gmail,
            "user@example.com", "m-a1", null, "公司域", "sha1", 10, null, DateTimeOffset.UtcNow));
        db.MailboxImportRecords.Add(new MailboxImportRecord(userId, entryA, null, MailboxProvider.Gmail,
            "user@example.com", "m-a2", null, "公司域", "sha2", 10, null, DateTimeOffset.UtcNow));
        db.MailboxImportRecords.Add(new MailboxImportRecord(userId, entryB, null, MailboxProvider.Gmail,
            "user@example.com", "m-b1", null, "公司域", "sha3", 10, null, DateTimeOffset.UtcNow));
        db.MailboxPendingItems.Add(new MailboxPendingItem(userId, MailboxProvider.Gmail, "user@example.com",
            "p-a1", null, "x@example.com", null, "subj", DateTimeOffset.UtcNow,
            "WeakCompanyName", "公司名", entryA, null, "snip"));
        db.MailboxPendingItems.Add(new MailboxPendingItem(userId, MailboxProvider.Gmail, "user@example.com",
            "p-amb", null, "y@example.com", null, "subj", DateTimeOffset.UtcNow,
            "Ambiguous", "多条目同域待选", null,
            JsonSerializer.Serialize(new[] { entryA, entryB }), "snip"));
        db.MailboxPendingItems.Add(new MailboxPendingItem(userId, MailboxProvider.Gmail, "user@example.com",
            "p-b1", null, "z@example.com", null, "subj", DateTimeOffset.UtcNow,
            "WeakCompanyName", "公司名", entryB, null, "snip"));
        await db.SaveChangesAsync();

        var handler = new GetMailboxRefreshStatusQueryHandler(tracker, db);
        var scoped = await handler.Handle(new GetMailboxRefreshStatusQuery(userId, entryA), CancellationToken.None);
        scoped.Value.State.Should().Be("Running");
        scoped.Value.Added.Should().Be(2);
        scoped.Value.Pending.Should().Be(1);

        var global = await handler.Handle(new GetMailboxRefreshStatusQuery(userId), CancellationToken.None);
        global.Value.State.Should().Be("None");

        tracker.TryForceCancel(userId, entryA);
    }
}
