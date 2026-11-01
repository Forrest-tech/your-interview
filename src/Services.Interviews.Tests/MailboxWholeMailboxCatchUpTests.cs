using System.Net;
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
/// 票 308：整邮箱检索——列单含垃圾邮件/已删除（includeSpamTrash），稳态 + anchor0 兜底
/// 合并 labelsAdded，合并后 id 去重，升级后一次性全窗口追补（作用域级标记，服从切换丢弃）。
/// DB 门控同仓内先例（票 101/294）：连不上沙盒 Postgres 则跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 不用 EF InMemory：共享修复器 NewChildEntityFixer 的 RowExists 走原生 SQL（见票 294 夹具说明）。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）；provider 级四例（stub HttpClient）不依赖 DB。
/// </summary>
public sealed class MailboxWholeMailboxCatchUpTests : IAsyncLifetime, IDisposable
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
                db.MailboxCatchUpMarkers.IgnoreQueryFilters().Where(m => _users.Contains(m.UserId)),
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

    // ============================ 假 provider ============================

    private class FakeProviderBase : IMailboxProvider
    {
        public MailboxProvider Kind => MailboxProvider.Gmail;
        public List<MailboxMessage> LegacyMessages { get; } = [];
        public List<MailboxMessage> ThreadMembers { get; } = [];
        public Dictionary<string, MailboxMessage> Fulls { get; } = [];
        public Dictionary<string, string> Raws { get; } = [];

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) => "https://example.test/auth";
        public Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access", "refresh", "scope", null));
        public Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access-dummy", "refresh-dummy", "scope", null));
        public Task RevokeAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct) => Task.CompletedTask;
        public Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor, DateTimeOffset sinceUtc, CancellationToken ct)
            => Task.FromResult(new MailboxFetchResult(LegacyMessages, null, false));
        public Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            if (!Fulls.TryGetValue(providerMessageId, out var full)) return Task.FromResult<MailboxMessage?>(null);
            Raws.TryGetValue(providerMessageId, out var raw);
            return Task.FromResult<MailboxMessage?>(full with { RawMimeBase64Url = raw });
        }
        public Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId, string providerAttachmentId, CancellationToken ct)
            => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<MailboxMessage>> GetThreadAsync(string accessToken, string threadId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailboxMessage>>(ThreadMembers);
        public Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct)
            => Task.FromResult<string?>("user@example.com");
        public Task<IReadOnlyList<MailboxAttachment>> ListAttachmentsAsync(string accessToken, string providerMessageId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailboxAttachment>>([]);
    }

    private sealed class FakeStreamingProvider : FakeProviderBase, IIncrementalMailboxProvider
    {
        public Dictionary<string, (List<string> Ids, string? Next)> ListPages { get; } = [];
        public Dictionary<string, (List<string> Ids, string? Next, string? NewHistoryId)> HistoryPages { get; } = [];
        public string? ProfileHistoryId { get; set; } = "h0";

        public List<string?> ListTokensCalled { get; } = [];
        public List<DateTimeOffset> ListSinceCalled { get; } = [];
        public List<string> FullFetchedIds { get; } = [];
        public List<string> RawFetchedIds { get; } = [];
        public List<string> HistoryAnchorsCalled { get; } = [];

        public Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListIdsPageAsync(
            string accessToken, string? pageToken, DateTimeOffset sinceUtc, CancellationToken ct)
        {
            ListTokensCalled.Add(pageToken);
            ListSinceCalled.Add(sinceUtc);
            var key = pageToken ?? "";
            if (!ListPages.TryGetValue(key, out var page))
                return Task.FromResult<(IReadOnlyList<string>, string?)>(([], null));
            return Task.FromResult<(IReadOnlyList<string>, string?)>((page.Ids, page.Next));
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

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.OK);
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(Respond(request));
        }
    }

    // ============================ 搭建 ============================

    private static MailboxProtection NewProtection() =>
        new(DataProtectionProvider.Create("test-308"));

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t308-" + Guid.NewGuid());
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
            Options.Create(new MailboxOptions { AttachmentRootDir = root }),
            new MailboxRefreshJobTracker());

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

    private static void SeedImport(InterviewsDbContext db, Guid userId, Guid entryId, string messageId,
        DateTimeOffset receivedAt)
    {
        db.MailboxImportRecords.Add(new MailboxImportRecord(userId, entryId, null, MailboxProvider.Gmail,
            "user@example.com", messageId, null, "公司域 examplecorp.com", "sha", 10, null, receivedAt));
    }

    private static void SeedCatchUpMarker(InterviewsDbContext db, Guid userId, Guid? scopeEntryId)
    {
        db.MailboxCatchUpMarkers.Add(new MailboxCatchUpMarker(userId, MailboxProvider.Gmail,
            "user@example.com", scopeEntryId, DateTimeOffset.UtcNow));
    }

    private static MailboxMessage Msg(string id, string from, string subject, string body,
        DateTimeOffset receivedAt, string? threadId = null) =>
        new(id, threadId, from, null, ["me@example.com"], [], subject,
            receivedAt, body, null, null, [], null);

    private static string RawOf(string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string GmailFullJson(string id, string from, string subject, string bodyText)
    {
        var bodyB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(bodyText))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $$"""
            {
              "id": "{{id}}", "threadId": "th-{{id}}", "internalDate": "1760000000000",
              "payload": {
                "mimeType": "text/plain",
                "headers": [
                  { "name": "From", "value": "{{from}}" },
                  { "name": "To", "value": "me@example.com" },
                  { "name": "Subject", "value": "{{subject}}" },
                  { "name": "Date", "value": "Mon, 06 Oct 2026 10:00:00 +0000" }
                ],
                "body": { "data": "{{bodyB64}}", "size": 10 }
              }
            }
            """;
    }

    // ============================ Provider 级 ============================

    [Fact]
    public async Task Provider_ListIdsPage_IncludesSpamTrash()
    {
        var stub = new StubHandler();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"messages":[{"id":"m1"},{"id":"m2"}],"nextPageToken":null}""",
                Encoding.UTF8, "application/json")
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub), TimeSpan.Zero, TimeSpan.Zero);

        var page = await gmail.ListIdsPageAsync("tok", null, DateTimeOffset.UtcNow, CancellationToken.None);

        page.Ids.Should().BeEquivalentTo(["m1", "m2"]);
        stub.Urls.Should().HaveCount(1);
        stub.Urls[0].Should().Contain("includeSpamTrash=true");
    }

    [Fact]
    public async Task Provider_ListHistory_MergesLabelsAdded()
    {
        var stub = new StubHandler();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"history":[{"messagesAdded":[{"message":{"id":"a"}}],"labelsAdded":[{"message":{"id":"b"}}]}],"historyId":"h9"}""",
                Encoding.UTF8, "application/json")
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub), TimeSpan.Zero, TimeSpan.Zero);

        var page = await gmail.ListHistoryPageAsync("tok", "h1", null, CancellationToken.None);

        page.Ids.Should().BeEquivalentTo(["a", "b"]);
        stub.Urls.Should().HaveCount(1);
        stub.Urls[0].Should().Contain("historyTypes=messageAdded&historyTypes=labelAdded");
    }

    [Fact]
    public async Task Provider_LegacyHistory_MergesLabelsAdded()
    {
        var stub = new StubHandler();
        stub.Respond = req =>
        {
            var url = req.RequestUri?.ToString() ?? "";
            if (url.Contains("/history"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"history":[{"messagesAdded":[{"message":{"id":"a"}}],"labelsAdded":[{"message":{"id":"b"}}]}],"historyId":"h9"}""",
                        Encoding.UTF8, "application/json")
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GmailFullJson("x", "a@b.com", "Hi", "hello"),
                    Encoding.UTF8, "application/json")
            };
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub), TimeSpan.Zero, TimeSpan.Zero);

        await gmail.ListNewAsync("tok", "h1", DateTimeOffset.UtcNow, CancellationToken.None);

        stub.Urls.Should().ContainSingle(u => u.Contains("/history"));
        stub.Urls.First(u => u.Contains("/history")).Should().Contain("historyTypes=labelAdded");
    }

    [Fact]
    public async Task Provider_LegacyWindow_IncludesSpamTrash()
    {
        var stub = new StubHandler();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"messages":[{"id":"m1"}],"nextPageToken":null}""",
                Encoding.UTF8, "application/json")
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub), TimeSpan.Zero, TimeSpan.Zero);

        await gmail.ListNewAsync("tok", null, DateTimeOffset.UtcNow, CancellationToken.None);

        stub.Urls.Should().ContainSingle(u => u.Contains("/messages?"));
        stub.Urls.First(u => u.Contains("/messages?")).Should().Contain("includeSpamTrash=true");
    }

    // ============================ 追补窗口：一次全窗口，之后常规窗口 ============================

    [Fact]
    public async Task Handler_FirstRefresh_UsesFullWindow_ThenSecondRefresh_UsesBoundedWindow()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        // 条目已有最新邮件 2026-10-20：常规窗口应为 2026-10-19；追补窗口为 now − 180 天。
        SeedImport(db, userId, entry.Id, "seed-msg", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        // ProfileHistoryId = null：回填完成不定锚，游标保持空，次轮仍走回填（便于断言窗口收窄）。
        fake.ProfileHistoryId = null;
        // 窗口外旧信（2026-06-01）：全窗口能捞到，常规窗口（2026-10-19 起）捞不到。
        fake.ListPages[""] = (["old1"], null);
        fake.Fulls["old1"] = Msg("old1", "recruiter@examplecorp.com", "Interview invitation", "hello",
            new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero));
        fake.Raws["old1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run1 = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run1.IsSuccess.Should().BeTrue();
        run1.Value.Added.Should().Be(1);
        fake.ListSinceCalled.Should().HaveCount(1);
        fake.ListSinceCalled[0].Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-180), TimeSpan.FromDays(1));
        fake.ListSinceCalled[0].Date.Should().BeBefore(new DateTime(2026, 10, 19));
        (await db.MailboxCatchUpMarkers.IgnoreQueryFilters().Where(m => m.UserId == userId).CountAsync()).Should().Be(1);
        var marker = await db.MailboxCatchUpMarkers.IgnoreQueryFilters().Where(m => m.UserId == userId).FirstAsync();
        marker.ScopeEntryId.Should().Be(entry.Id);
        (await db.MailboxBackfillProgress.IgnoreQueryFilters().Where(p => p.UserId == userId).CountAsync()).Should().Be(0);

        var run2 = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run2.IsSuccess.Should().BeTrue();
        run2.Value.Added.Should().Be(0);
        fake.ListSinceCalled.Should().HaveCount(2);
        // 常规窗口：最新 2026-10-20 → 界线 2026-10-19 00:00 UTC（票 298）。
        fake.ListSinceCalled[1].Should().Be(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        // 窗口外旧信不再重取（导入记录前置去重），标记不重复写。
        fake.FullFetchedIds.Where(id => id == "old1").Should().HaveCount(1);
        (await db.MailboxCatchUpMarkers.IgnoreQueryFilters().Where(m => m.UserId == userId).CountAsync()).Should().Be(1);
    }

    // ============================ 稳态门控：追补未完成不走稳态 ============================

    [Fact]
    public async Task Handler_SteadySkipped_UntilCatchUpDone_ThenSteadyResumes()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId, cursor: "h100");
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        SeedImport(db, userId, entry.Id, "seed-msg", new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = (["d1"], null);
        fake.Fulls["d1"] = Msg("d1", "recruiter@examplecorp.com", "Interview invitation", "hello",
            DateTimeOffset.UtcNow);
        fake.Raws["d1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run1 = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run1.IsSuccess.Should().BeTrue();
        run1.Value.Added.Should().Be(1);
        // 首轮未走稳态：未以旧游标 h100 拉 history，而是走回填全窗口 + anchor0（h0）。
        fake.HistoryAnchorsCalled.Should().NotContain("h100");
        fake.HistoryAnchorsCalled.Should().Contain("h0");
        fake.ListSinceCalled.Should().HaveCount(1);
        fake.ListSinceCalled[0].Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-180), TimeSpan.FromDays(1));
        (await db.MailboxCatchUpMarkers.IgnoreQueryFilters().Where(m => m.UserId == userId).CountAsync()).Should().Be(1);

        fake.HistoryPages["h200|"] = (["d2"], null, "h210");
        fake.Fulls["d2"] = Msg("d2", "hr@examplecorp.com", "Interview invitation", "hello",
            DateTimeOffset.UtcNow);
        fake.Raws["d2"] = RawOf("From: hr@examplecorp.com\n\nhello");
        var run2 = await handler.Handle(new RefreshMailboxCommand(userId, entry.Id), CancellationToken.None);
        run2.IsSuccess.Should().BeTrue();
        run2.Value.Added.Should().Be(1);
        // 次轮走稳态：不再列单，只拉 history。
        fake.ListSinceCalled.Should().HaveCount(1);
        fake.HistoryAnchorsCalled.Should().Contain("h200");
    }

    // ============================ 去重 ============================

    [Fact]
    public async Task Handler_Steady_DuplicateIds_Deduped()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId, cursor: "h100");
        SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        SeedCatchUpMarker(db, userId, null);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        // 同一 id 同时出现在 messagesAdded 与 labelsAdded 中（合并后重复）。
        fake.HistoryPages["h100|"] = (["d1", "d1"], null, "h105");
        fake.Fulls["d1"] = Msg("d1", "recruiter@examplecorp.com", "Interview invitation", "hello",
            DateTimeOffset.UtcNow);
        fake.Raws["d1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        run.Value.Added.Should().Be(1);
        run.Value.SkippedDuplicates.Should().Be(0);
        fake.FullFetchedIds.Where(id => id == "d1").Should().HaveCount(1);
    }

    [Fact]
    public async Task Handler_Anchor0_DuplicateIds_Deduped()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        SeedCatchUpMarker(db, userId, null);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = ([], null);
        fake.HistoryPages["h0|"] = (["d1", "d1"], null, "h200");
        fake.Fulls["d1"] = Msg("d1", "recruiter@examplecorp.com", "Interview invitation", "hello",
            DateTimeOffset.UtcNow);
        fake.Raws["d1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        run.Value.Added.Should().Be(1);
        run.Value.SkippedDuplicates.Should().Be(0);
        fake.FullFetchedIds.Where(id => id == "d1").Should().HaveCount(1);
    }

    // ============================ 作用域切换丢弃 ============================

    [Fact]
    public async Task Handler_ScopeSwitch_DiscardsCatchUpProgress_WithoutMarking()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entryA, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        var (entryB, _) = SeedEntry(db, userId, "OtherCorp", ["othercorp.com"], [], ["othercorp"], []);
        // 作用域 A 的追补进度行（窗口 2026-01-01，与全窗口可区分）：切换到 B 时应丢弃，不写 A 的标记。
        db.MailboxBackfillProgress.Add(new MailboxBackfillProgress(userId, MailboxProvider.Gmail,
            "user@example.com", entryA.Id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "h0", "[\"x\"]", 0, null, true));
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = ([], null);
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entryB.Id), CancellationToken.None);
        run.IsSuccess.Should().BeTrue();
        // 未续跑 A 的旧行（否则 ListSince = 2026-01-01）；B 新起跑用全窗口。
        fake.ListSinceCalled.Should().HaveCount(1);
        fake.ListSinceCalled[0].Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-180), TimeSpan.FromDays(1));
        fake.ListSinceCalled[0].Should().NotBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var markers = await db.MailboxCatchUpMarkers.IgnoreQueryFilters().Where(m => m.UserId == userId).ToListAsync();
        markers.Should().HaveCount(1);
        markers[0].ScopeEntryId.Should().Be(entryB.Id);
        (await db.MailboxBackfillProgress.IgnoreQueryFilters().Where(p => p.UserId == userId).CountAsync()).Should().Be(0);
    }
}
