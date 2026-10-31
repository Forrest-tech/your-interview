using System.Text;
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
/// 票 296：刷新异步任务（202 + 后台任务 + 状态轮询）测试。
/// DB 门控同 294：连不上沙盒 Postgres 则 DB 例跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 任务跟踪本身是纯内存逻辑，守卫/取消/态映射三例不依赖 DB，恒跑。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）。
/// </summary>
public sealed class MailboxAsyncRefreshTests : IAsyncLifetime, IDisposable
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres";
    private bool _available;
    private readonly List<Guid> _users = [];
    private readonly List<string> _tempRoots = [];

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
            var entryIds = await db.Entries.IgnoreQueryFilters()
                .Where(e => _users.Contains(e.UserId)).Select(e => e.Id).ToListAsync();
            if (entryIds.Count > 0)
            {
                var assets = await db.Assets.IgnoreQueryFilters()
                    .Where(a => entryIds.Contains(a.InterviewEntryId)).ToListAsync();
                db.Assets.RemoveRange(assets);
                var entries = await db.Entries.IgnoreQueryFilters()
                    .Where(e => _users.Contains(e.UserId)).ToListAsync();
                db.Entries.RemoveRange(entries);
            }
            foreach (var set in new[]
            {
                (IQueryable<object>)db.MailboxBackfillProgress.IgnoreQueryFilters().Where(p => _users.Contains(p.UserId)),
                db.MailboxConnections.IgnoreQueryFilters().Where(c => _users.Contains(c.UserId)),
                db.MailMatchRules.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)),
                db.MailContactMaps.IgnoreQueryFilters().Where(m => _users.Contains(m.UserId)),
                db.MailboxImportRecords.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)),
                db.MailboxPendingItems.IgnoreQueryFilters().Where(p => _users.Contains(p.UserId)),
            })
            {
                foreach (var row in await set.ToListAsync()) db.Remove(row);
            }
            await db.SaveChangesAsync();
        }
        catch { /* 清理失败不阻塞套件；各例 UserId 随机，不干扰后续例 */ }
    }

    public void Dispose()
    {
        foreach (var dir in _tempRoots)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 忽略 */ }
        }
    }

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
        new(DataProtectionProvider.Create("test-296"));

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t296-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _tempRoots.Add(dir);
        return dir;
    }

    private static IOptions<MailboxOptions> EnvOptions() => Options.Create(new MailboxOptions
    {
        Google = new MailboxOptions.GoogleOptions
            { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/cb" }
    });

    private static void SeedConnection(InterviewsDbContext db, MailboxProtection protection, Guid userId)
    {
        var conn = new MailboxConnection(userId, MailboxProvider.Gmail, "user@example.com",
            "https://www.googleapis.com/auth/gmail.readonly openid email profile",
            protection.ProtectRefreshToken("rt"));
        db.MailboxConnections.Add(conn);
    }

    private static void SeedEntry(InterviewsDbContext db, Guid userId)
    {
        var entry = new InterviewEntry(Guid.NewGuid(), "Corpay", "Developer", userId: userId);
        db.Entries.Add(entry);
        var rule = new MailMatchRule(userId, entry.Id);
        rule.Update(JsonSerializer.Serialize<string[]>(["examplecorp.com"]),
            JsonSerializer.Serialize<string[]>([]),
            JsonSerializer.Serialize<string[]>(["corpay"]),
            JsonSerializer.Serialize<string[]>([]));
        db.MailMatchRules.Add(rule);
    }

    private static MailboxMessage Msg(string id, string from, string subject, string body) =>
        new(id, null, from, null, ["me@example.com"], [], subject,
            DateTimeOffset.UtcNow, body, null, null, [], null);

    private static string RawOf(string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ============================ 假增量 provider ============================

    private sealed class FakeStreaming : IMailboxProvider, IIncrementalMailboxProvider
    {
        public MailboxProvider Kind => MailboxProvider.Gmail;
        public Dictionary<string, (List<string> Ids, string? Next)> ListPages { get; } = [];
        public Dictionary<string, MailboxMessage> Fulls { get; } = [];
        public Dictionary<string, string> Raws { get; } = [];
        public List<string?> ListTokensCalled { get; } = [];
        public List<string> FullFetchedIds { get; } = [];
        public List<string> RawFetchedIds { get; } = [];
        public Func<string, CancellationToken, Task>? BeforeFull { get; set; }
        public Func<string, Exception?>? FailFull { get; set; }

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) => "https://example.test/auth";
        public Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access", "refresh", "scope", null));
        public Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access-dummy", "refresh-dummy", "scope", null));
        public Task RevokeAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct) => Task.CompletedTask;
        public Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor, DateTimeOffset sinceUtc, CancellationToken ct)
            => Task.FromResult(new MailboxFetchResult([], null, false));
        public Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId, CancellationToken ct)
            => Task.FromResult<MailboxMessage?>(null);
        public Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId, string providerAttachmentId, CancellationToken ct)
            => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<MailboxMessage>> GetThreadAsync(string accessToken, string threadId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailboxMessage>>([]);
        public Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct)
            => Task.FromResult<string?>("user@example.com");
        public Task<IReadOnlyList<MailboxAttachment>> ListAttachmentsAsync(string accessToken, string providerMessageId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailboxAttachment>>([]);

        public Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListIdsPageAsync(
            string accessToken, string? pageToken, DateTimeOffset sinceUtc, CancellationToken ct)
        {
            ListTokensCalled.Add(pageToken);
            var key = pageToken ?? "";
            if (!ListPages.TryGetValue(key, out var page))
                return Task.FromResult<(IReadOnlyList<string>, string?)>(([], null));
            return Task.FromResult<(IReadOnlyList<string>, string?)>((page.Ids, page.Next));
        }

        public async Task<MailboxMessage?> GetMessageFullAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            FullFetchedIds.Add(providerMessageId);
            if (BeforeFull is not null) await BeforeFull(providerMessageId, ct);
            if (FailFull?.Invoke(providerMessageId) is { } ex) throw ex;
            Fulls.TryGetValue(providerMessageId, out var full);
            return full;
        }

        public Task<string?> GetMessageRawAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            RawFetchedIds.Add(providerMessageId);
            Raws.TryGetValue(providerMessageId, out var raw);
            return Task.FromResult(raw);
        }

        public Task<string?> GetProfileHistoryIdAsync(string accessToken, CancellationToken ct)
            => Task.FromResult<string?>("h0");

        public Task<(IReadOnlyList<string> Ids, string? NextPageToken, string? NewHistoryId)> ListHistoryPageAsync(
            string accessToken, string startHistoryId, string? pageToken, CancellationToken ct)
            => Task.FromResult<(IReadOnlyList<string>, string?, string?)>(([], null, "h200"));
    }

    private sealed class FakeFactory(IMailboxProvider provider) : IMailboxProviderFactory
    {
        public IMailboxProvider Get(MailboxProvider kind) => provider;
    }

    private Func<CancellationToken, Task<Result<RefreshMailboxResult>>> JobWorker(
        InterviewsDbContext db, FakeStreaming fake, MailboxProtection protection, Guid userId, string root) =>
        ct => new RefreshMailboxCommandHandler(db, new FakeFactory(fake), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection),
            Options.Create(new MailboxOptions { AttachmentRootDir = root }))
            .Handle(new RefreshMailboxCommand(userId, null), ct);

    private static void SeedFourMessages(FakeStreaming fake)
    {
        fake.ListPages[""] = (["m1", "m2"], "p2");
        fake.ListPages["p2"] = (["m3", "m4"], null);
        fake.Fulls["m1"] = Msg("m1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Fulls["m2"] = Msg("m2", "news@other.com", "Newsletter", "hello");
        fake.Fulls["m3"] = Msg("m3", "hr@examplecorp.com", "Interview invitation", "hello");
        fake.Fulls["m4"] = Msg("m4", "news@other.com", "Corpay update", "Corpay is hiring");
        fake.Raws["m1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.Raws["m3"] = RawOf("From: hr@examplecorp.com\n\nhello");
    }

    private static async Task<MailboxRefreshJobSnapshot> WaitTerminalAsync(
        MailboxRefreshJobTracker tracker, Guid userId, int timeoutMs = 60000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snap = tracker.TryGet(userId);
            if (snap is not null && snap.State != MailboxRefreshJobState.Running) return snap;
            await Task.Delay(50);
        }
        throw new TimeoutException($"刷新任务未在 {timeoutMs}ms 内终结，当前态={tracker.TryGet(userId)?.State}");
    }

    // ============================ 纯跟踪逻辑（不依赖 DB） ============================

    [Fact]
    public async Task Tracker_DuplicateStart_ReturnsRunningJob_AndSecondScopeStartCreatesNewJob()
    {
        var tracker = new MailboxRefreshJobTracker();
        var userId = Guid.NewGuid();
        var gate = new TaskCompletionSource<Result<RefreshMailboxResult>>();
        var calls = 0;
        Func<CancellationToken, Task<Result<RefreshMailboxResult>>> run = async ct =>
        {
            Interlocked.Increment(ref calls);
            return await gate.Task.WaitAsync(ct);
        };

        var first = tracker.StartOrAttach(userId, null, run);
        first.State.Should().Be(MailboxRefreshJobState.Running);
        var second = tracker.StartOrAttach(userId, Guid.NewGuid(), run);
        second.JobId.Should().Be(first.JobId);
        second.State.Should().Be(MailboxRefreshJobState.Running);
        // Task.Run 调度异步：等委托真正跑起来再断言只跑了一次。
        var callDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref calls) == 0 && DateTimeOffset.UtcNow < callDeadline)
            await Task.Delay(20);
        calls.Should().Be(1);

        var (cancelled, _) = tracker.TryCancel(userId);
        cancelled.Should().BeTrue();
        var terminal = await WaitTerminalAsync(tracker, userId);
        terminal.State.Should().Be(MailboxRefreshJobState.Cancelled);

        var third = tracker.StartOrAttach(userId, null, _ =>
            Task.FromResult(Result.Success(new RefreshMailboxResult(1, 0, 0, 0, [], false, false, false, false))));
        third.JobId.Should().NotBe(first.JobId);
        (await WaitTerminalAsync(tracker, userId)).State.Should().Be(MailboxRefreshJobState.Done);
    }

    [Fact]
    public async Task Tracker_FailedRun_MapsErrorMessage()
    {
        var tracker = new MailboxRefreshJobTracker();
        var userId = Guid.NewGuid();
        tracker.StartOrAttach(userId, null, _ =>
            Task.FromResult(Result.Failure<RefreshMailboxResult>(Error.Unexpected("boom"))));
        var terminal = await WaitTerminalAsync(tracker, userId);
        terminal.State.Should().Be(MailboxRefreshJobState.Failed);
        terminal.ErrorMessage.Should().Be("boom");
        tracker.TryCancel(userId).Cancelled.Should().BeFalse();
    }

    // ============================ 任务 + 真 handler ============================

    [Fact]
    public async Task Job_StartRunningDone_ImportsViaExistingHandler()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreaming();
        SeedFourMessages(fake);
        fake.BeforeFull = (_, _) => Task.Delay(150);
        var tracker = new MailboxRefreshJobTracker();
        var root = NewTempRoot();

        var start = tracker.StartOrAttach(userId, null, JobWorker(db, fake, protection, userId, root));
        start.State.Should().Be(MailboxRefreshJobState.Running);
        var terminal = await WaitTerminalAsync(tracker, userId);
        terminal.State.Should().Be(MailboxRefreshJobState.Done);
        terminal.Added.Should().Be(2);
        terminal.Pending.Should().Be(1);
        (await db.MailboxImportRecords.CountAsync()).Should().Be(2);
        (await db.MailboxPendingItems.CountAsync()).Should().Be(1);
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);
        (await db.MailboxConnections.FirstAsync()).Cursor.Should().Be("h200");
        fake.RawFetchedIds.Should().BeEquivalentTo(["m1", "m3"]);
    }

    [Fact]
    public async Task Job_DuplicateStart_DoesNotDoubleFetch()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreaming();
        SeedFourMessages(fake);
        fake.BeforeFull = (_, _) => Task.Delay(150);
        var tracker = new MailboxRefreshJobTracker();
        var root = NewTempRoot();
        var worker = JobWorker(db, fake, protection, userId, root);

        var first = tracker.StartOrAttach(userId, null, worker);
        var second = tracker.StartOrAttach(userId, null, worker);
        second.JobId.Should().Be(first.JobId);
        (await WaitTerminalAsync(tracker, userId)).State.Should().Be(MailboxRefreshJobState.Done);
        fake.FullFetchedIds.Should().HaveCount(4);
        (await db.MailboxImportRecords.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Job_Cancel_KeepsSavedImports_StopsBeforeLaterMessages()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreaming();
        SeedFourMessages(fake);
        var blockM3 = new TaskCompletionSource();
        fake.BeforeFull = (id, _) => id == "m3" ? blockM3.Task : Task.CompletedTask;
        var tracker = new MailboxRefreshJobTracker();
        var root = NewTempRoot();
        tracker.StartOrAttach(userId, null, JobWorker(db, fake, protection, userId, root));

        // 等任务走到 m3 并卡住（m1 已落库）。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!fake.FullFetchedIds.Contains("m3") && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);
        fake.FullFetchedIds.Should().Contain("m3");

        tracker.TryCancel(userId).Cancelled.Should().BeTrue();
        blockM3.SetResult();
        var terminal = await WaitTerminalAsync(tracker, userId);
        terminal.State.Should().Be(MailboxRefreshJobState.Cancelled);
        fake.FullFetchedIds.Should().NotContain("m4");
        (await db.MailboxImportRecords.CountAsync()).Should().BeGreaterThanOrEqualTo(1);
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Job_RateLimitedMidRun_ResumesFromCheckpoint_OnNextStart()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreaming();
        SeedFourMessages(fake);
        var m3Attempts = 0;
        fake.FailFull = id => id == "m3" && Interlocked.Increment(ref m3Attempts) == 1
            ? new MailboxRateLimitedException("限流", null) : null;
        var tracker = new MailboxRefreshJobTracker();
        var root = NewTempRoot();
        var worker = JobWorker(db, fake, protection, userId, root);

        tracker.StartOrAttach(userId, null, worker);
        var rateLimited = await WaitTerminalAsync(tracker, userId);
        rateLimited.State.Should().Be(MailboxRefreshJobState.RateLimited);
        rateLimited.Added.Should().Be(1);

        tracker.StartOrAttach(userId, null, worker);
        var done = await WaitTerminalAsync(tracker, userId);
        done.State.Should().Be(MailboxRefreshJobState.Done);
        // 续跑不重列已完成页：空 token（首列）只出现一次。
        fake.ListTokensCalled.Count(t => t is null).Should().Be(1);
        (await db.MailboxImportRecords.CountAsync()).Should().Be(2);
        (await db.MailboxPendingItems.CountAsync()).Should().Be(1);
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Job_RateLimitedAtStart_EndsCleanly()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreaming();
        SeedFourMessages(fake);
        fake.FailFull = _ => new MailboxRateLimitedException("限流", null);
        var tracker = new MailboxRefreshJobTracker();
        var root = NewTempRoot();
        tracker.StartOrAttach(userId, null, JobWorker(db, fake, protection, userId, root));
        var terminal = await WaitTerminalAsync(tracker, userId);
        terminal.State.Should().Be(MailboxRefreshJobState.RateLimited);
        terminal.FailedReasons.Should().Contain(r => r.Contains("限流"));
    }

    [Fact]
    public async Task StatusQuery_NoneRunningDone_StatesAndLiveProgress()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var tracker = new MailboxRefreshJobTracker();
        using var statusDb = NewDb(userId);
        var status = new GetMailboxRefreshStatusQueryHandler(tracker, statusDb);

        var none = await status.Handle(new GetMailboxRefreshStatusQuery(userId), CancellationToken.None);
        none.IsSuccess.Should().BeTrue();
        none.Value.State.Should().Be("None");

        var fake = new FakeStreaming();
        SeedFourMessages(fake);
        var blockM3 = new TaskCompletionSource();
        fake.BeforeFull = (id, _) => id == "m3" ? blockM3.Task : Task.CompletedTask;
        var root = NewTempRoot();
        tracker.StartOrAttach(userId, null, JobWorker(db, fake, protection, userId, root));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!fake.FullFetchedIds.Contains("m3") && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(50);
        fake.FullFetchedIds.Should().Contain("m3");

        var running = await status.Handle(new GetMailboxRefreshStatusQuery(userId), CancellationToken.None);
        running.Value.State.Should().Be("Running");
        running.Value.Added.Should().Be(1);
        running.Value.BackfillActive.Should().BeTrue();
        running.Value.BackfillPageIndex.Should().Be(0);
        running.Value.BackfillPageSize.Should().Be(2);

        blockM3.SetResult();
        var terminal = await WaitTerminalAsync(tracker, userId);
        terminal.State.Should().Be(MailboxRefreshJobState.Done);
        var done = await status.Handle(new GetMailboxRefreshStatusQuery(userId), CancellationToken.None);
        done.Value.State.Should().Be("Done");
        done.Value.Added.Should().Be(2);
        done.Value.Pending.Should().Be(1);
    }
}
