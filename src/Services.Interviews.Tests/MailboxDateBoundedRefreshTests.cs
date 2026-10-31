using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 298：条目级日期界定刷新 + 最新邮件日期。
/// DB 门控同仓内先例（票 101/294）：连不上沙盒 Postgres 则跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 不用 EF InMemory（票 294 教训：共享修复器走原生 SQL，InMemory 下新资产误判 Modified）。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）。
/// 假 provider 的列单按 sinceUtc 的 UTC 日期过滤（模拟 Gmail after:yyyy/MM/dd 的日期粒度语义），
/// 以此证明界线值与端到端效果（界线外邮件不被取数、不被导入）。
/// </summary>
public sealed class MailboxDateBoundedRefreshTests : IAsyncLifetime, IDisposable
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
        // 必须镜像 Program.cs 的 DI 配置：history 表显式落在 interviews schema（票 294 教训）。
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

    // ============================ 假 provider ============================

    private sealed class FakeBoundedProvider : IMailboxProvider, IIncrementalMailboxProvider
    {
        public MailboxProvider Kind => MailboxProvider.Gmail;
        public Dictionary<string, MailboxMessage> Fulls { get; } = [];
        public Dictionary<string, string> Raws { get; } = [];
        public List<DateTimeOffset> ListSinceCalled { get; } = [];
        public List<string> FullFetchedIds { get; } = [];
        public List<string> RawFetchedIds { get; } = [];
        public List<string> HistoryAnchorsCalled { get; } = [];
        public Dictionary<string, (List<string> Ids, string? Next, string? NewHistoryId)> HistoryPages { get; } = [];
        public string? ProfileHistoryId { get; set; } = "h0";

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) => "https://example.test/auth";
        public Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access", "refresh", "scope", null));
        public Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access-dummy", "refresh-dummy", "scope", null));
        public Task RevokeAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct) => Task.CompletedTask;
        public Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor, DateTimeOffset sinceUtc, CancellationToken ct)
            => Task.FromResult(new MailboxFetchResult([], null, false));
        public Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            if (!Fulls.TryGetValue(providerMessageId, out var full)) return Task.FromResult<MailboxMessage?>(null);
            Raws.TryGetValue(providerMessageId, out var raw);
            return Task.FromResult<MailboxMessage?>(full with { RawMimeBase64Url = raw });
        }
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
            ListSinceCalled.Add(sinceUtc);
            // 模拟 Gmail after: 的日期粒度：只列出 UTC 日期 >= sinceUtc UTC 日期的邮件。
            var ids = Fulls.Values
                .Where(m => m.ReceivedAt.UtcDateTime.Date >= sinceUtc.UtcDateTime.Date)
                .Select(m => m.ProviderMessageId).ToList();
            return Task.FromResult<(IReadOnlyList<string>, string?)>((ids, null));
        }

        public Task<MailboxMessage?> GetMessageFullAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            FullFetchedIds.Add(providerMessageId);
            Fulls.TryGetValue(providerMessageId, out var full);
            return Task.FromResult(full);
        }

        public Task<string?> GetMessageRawAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            RawFetchedIds.Add(providerMessageId);
            Raws.TryGetValue(providerMessageId, out var raw);
            return Task.FromResult(raw);
        }

        public Task<string?> GetProfileHistoryIdAsync(string accessToken, CancellationToken ct)
            => Task.FromResult(ProfileHistoryId);

        public Task<(IReadOnlyList<string> Ids, string? NextPageToken, string? NewHistoryId)> ListHistoryPageAsync(
            string accessToken, string startHistoryId, string? pageToken, CancellationToken ct)
        {
            HistoryAnchorsCalled.Add(startHistoryId);
            var key = startHistoryId + "|" + (pageToken ?? "");
            if (!HistoryPages.TryGetValue(key, out var page))
                return Task.FromResult<(IReadOnlyList<string>, string?, string?)>(([], null, null));
            return Task.FromResult<(IReadOnlyList<string>, string?, string?)>((page.Ids, page.Next, page.NewHistoryId));
        }
    }

    private sealed class FakeFactory(IMailboxProvider provider) : IMailboxProviderFactory
    {
        public IMailboxProvider Get(MailboxProvider kind) => provider;
    }

    // ============================ 搭建 ============================

    private static MailboxProtection NewProtection() =>
        new(DataProtectionProvider.Create("test-298"));

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t298-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _tempRoots.Add(dir);
        return dir;
    }

    private static IOptions<MailboxOptions> EnvOptions() => Options.Create(new MailboxOptions
    {
        Google = new MailboxOptions.GoogleOptions
            { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/cb" }
    });

    private RefreshMailboxCommandHandler NewHandler(InterviewsDbContext db, IMailboxProvider provider,
        MailboxProtection protection, string root) =>
        new(db, new FakeFactory(provider), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection),
            Options.Create(new MailboxOptions { AttachmentRootDir = root }));

    private static (InterviewEntry Entry, MailMatchRule Rule) SeedEntry(InterviewsDbContext db, Guid userId,
        string company, string[] domains, string[] contacts, string[] keywords, string[] exclusions)
    {
        var entry = new InterviewEntry(Guid.NewGuid(), company, "Developer", userId: userId);
        db.Entries.Add(entry);
        var rule = new MailMatchRule(userId, entry.Id);
        rule.Update(JsonSerializer.Serialize(domains), JsonSerializer.Serialize(contacts),
            JsonSerializer.Serialize(keywords), JsonSerializer.Serialize(exclusions));
        db.MailMatchRules.Add(rule);
        return (entry, rule);
    }

    private static MailboxConnection SeedConnection(InterviewsDbContext db, MailboxProtection protection,
        Guid userId, string? cursor = null)
    {
        var conn = new MailboxConnection(userId, MailboxProvider.Gmail, "user@example.com",
            "https://www.googleapis.com/auth/gmail.readonly openid email profile",
            protection.ProtectRefreshToken("rt"));
        if (cursor is not null) conn.AdvanceCursor(cursor, DateTimeOffset.UtcNow);
        db.MailboxConnections.Add(conn);
        return conn;
    }

    private static MailboxMessage MsgAt(string id, string from, string subject, string body, DateTimeOffset receivedAt,
        string? threadId = null) =>
        new(id, threadId, from, null, ["me@example.com"], [], subject,
            receivedAt, body, null, null, [], null);

    private static string RawOf(string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static void SeedImport(InterviewsDbContext db, Guid userId, Guid entryId, string messageId,
        DateTimeOffset receivedAt)
    {
        db.MailboxImportRecords.Add(new MailboxImportRecord(userId, entryId, null, MailboxProvider.Gmail,
            "user@example.com", messageId, null, "公司域 examplecorp.com", "sha", 10, null, receivedAt));
    }

    // ============================ 界线 ============================

    [Fact]
    public async Task BoundedRefresh_OnlyInWindowImported_AndSinceIsLatestMinusOneDay()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        // 已有导入：最新邮件 2026-10-20（用与夹具不同的 message id，避免去重干扰）。
        SeedImport(db, userId, entry.Id, "seed-msg", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var fake = new FakeBoundedProvider();
        fake.Fulls["old"] = MsgAt("old", "recruiter@examplecorp.com", "Old", "hello",
            new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero));
        fake.Fulls["bound"] = MsgAt("bound", "recruiter@examplecorp.com", "Bound day", "hello",
            new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero));
        fake.Fulls["new"] = MsgAt("new", "recruiter@examplecorp.com", "New", "hello",
            new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        foreach (var id in new[] { "old", "bound", "new" }) fake.Raws[id] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        // 界线起点 = 最新 UTC 日期 10-20 再减 1 天安全垫。
        fake.ListSinceCalled.Should().ContainSingle();
        fake.ListSinceCalled[0].UtcDateTime.Date.Should().Be(new DateTime(2026, 10, 19));
        // 界线外（10-10）不被取数、不被导入；界线日与新邮件导入。
        fake.FullFetchedIds.Should().NotContain("old");
        fake.FullFetchedIds.Should().BeEquivalentTo(["bound", "new"]);
        run.Value.Added.Should().Be(2);
        (await db.MailboxImportRecords.CountAsync()).Should().Be(3); // seed + bound + new
    }

    [Fact]
    public async Task BoundedRefresh_FirstEver_UsesFullWindow()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeBoundedProvider();
        fake.HistoryPages["h0|"] = ([], null, "h200");
        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        fake.ListSinceCalled.Should().ContainSingle();
        fake.ListSinceCalled[0].UtcDateTime.Date.Should().Be(DateTime.UtcNow.AddDays(-180).Date);
    }

    [Fact]
    public async Task BoundedRefresh_FirstEver_WithLiveCursor_UsesBackfillNotSteady()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        // 连接已有锚点，但该条目从未导入过：必须走回填全窗口，否则锚点前的旧邮件永远捞不到。
        SeedConnection(db, protection, userId, cursor: "h-live");
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeBoundedProvider();
        fake.HistoryPages["h0|"] = ([], null, "h200");
        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        // 走了回填全窗口（列单被调用、窗口为 180 天），而非以 live 锚点走稳态；
        // 完成段的 anchor0 兜底会调一次 profile 锚点 h0，那是回填收尾，不是稳态。
        fake.ListSinceCalled.Should().ContainSingle();
        fake.ListSinceCalled[0].UtcDateTime.Date.Should().Be(DateTime.UtcNow.AddDays(-180).Date);
        fake.HistoryAnchorsCalled.Should().NotContain("h-live");
    }

    [Fact]
    public async Task BoundedRefresh_WithLatestAndLiveCursor_UsesSteadyDeltas()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId, cursor: "h-live");
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        SeedImport(db, userId, entry.Id, "seed-msg", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var fake = new FakeBoundedProvider();
        fake.Fulls["new"] = MsgAt("new", "recruiter@examplecorp.com", "New", "hello",
            new DateTimeOffset(2026, 10, 27, 10, 0, 0, TimeSpan.Zero));
        fake.Raws["new"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h-live|"] = (["new"], null, "h300");
        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        fake.HistoryAnchorsCalled.Should().Contain("h-live");
        fake.ListSinceCalled.Should().BeEmpty();
        run.Value.Added.Should().Be(1);
    }

    [Fact]
    public async Task BoundedRefresh_NoNewMail_CompletesCleanly_LatestUnchanged()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        SeedImport(db, userId, entry.Id, "already", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var fake = new FakeBoundedProvider();
        // 界线内只有已导入过的那封 → 去重跳过，零新增、成功收尾。
        fake.Fulls["already"] = MsgAt("already", "recruiter@examplecorp.com", "Again", "hello",
            new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        fake.HistoryPages["h0|"] = ([], null, "h200");
        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        run.Value.Added.Should().Be(0);
        run.Value.Failed.Should().Be(0);
        var latest = await new GetMailboxLatestMailDateQueryHandler(db)
            .Handle(new GetMailboxLatestMailDateQuery(userId, entry.Id), CancellationToken.None);
        latest.Value.Should().Be("2026-10-20");
    }

    [Fact]
    public async Task BoundedRefresh_NoDuplicatesAcrossRuns_AndLatestAdvances()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeBoundedProvider();
        fake.Fulls["m1"] = MsgAt("m1", "recruiter@examplecorp.com", "First", "hello",
            new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        fake.Raws["m1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h0|"] = ([], null, "h200");
        var handler = NewHandler(db, fake, protection, NewTempRoot());

        var run1 = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run1.IsSuccess.Should().BeTrue();
        run1.Value.Added.Should().Be(1);
        var latest1 = await new GetMailboxLatestMailDateQueryHandler(db)
            .Handle(new GetMailboxLatestMailDateQuery(userId, entry.Id), CancellationToken.None);
        latest1.Value.Should().Be("2026-10-25");

        // 第二轮：连接锚已定（首轮回填完成时定为 h200），走稳态 deltas 捞到更新邮件；
        // 旧封去重，最新日期推进到新邮件日。
        fake.Fulls["m2"] = MsgAt("m2", "recruiter@examplecorp.com", "Second", "hello",
            new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
        fake.Raws["m2"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h200|"] = (["m2"], null, "h300");
        var run2 = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run2.IsSuccess.Should().BeTrue();
        run2.Value.Added.Should().Be(1);
        (await db.MailboxImportRecords.CountAsync()).Should().Be(2);
        var latest2 = await new GetMailboxLatestMailDateQueryHandler(db)
            .Handle(new GetMailboxLatestMailDateQuery(userId, entry.Id), CancellationToken.None);
        latest2.Value.Should().Be("2026-10-28");
    }

    [Fact]
    public async Task LatestDate_PendingItemsDoNotCount()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        SeedImport(db, userId, entry.Id, "imp", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        // 待确认项更新（10-28）但尚未归属该条目：不得推高最新日期。
        db.MailboxPendingItems.Add(new MailboxPendingItem(userId, MailboxProvider.Gmail, "user@example.com",
            "pend", null, "news@examplecorp.com", null, "Corpay news",
            new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero),
            "WeakCompanyName", "主题/正文含公司名", entry.Id, null, "snippet"));
        await db.SaveChangesAsync();

        var latest = await new GetMailboxLatestMailDateQueryHandler(db)
            .Handle(new GetMailboxLatestMailDateQuery(userId, entry.Id), CancellationToken.None);
        latest.Value.Should().Be("2026-10-20");
    }

    [Fact]
    public async Task LatestDate_NoImports_ReturnsNull()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var latest = await new GetMailboxLatestMailDateQueryHandler(db)
            .Handle(new GetMailboxLatestMailDateQuery(userId, entry.Id), CancellationToken.None);
        latest.Value.Should().BeNull();
    }
}
