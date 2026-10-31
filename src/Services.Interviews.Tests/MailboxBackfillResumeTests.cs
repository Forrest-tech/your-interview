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
/// 票 294：Gmail 回填断点 + history 稳态 + 配额错面测试。
/// DB 门控同仓内先例（票 101）：连不上沙盒 Postgres 则跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 不用 EF InMemory：共享修复器 NewChildEntityFixer 的 RowExists 走原生 SQL，InMemory 下查库失败恒判“已存在”，
/// 新资产（AttachAsset 加入已跟踪聚合）会被误判 Modified → DbUpdateConcurrencyException；Postgres 下原 SQL 正常工作。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）；provider 级三例（stub HttpClient）不依赖 DB。
/// </summary>
public sealed class MailboxBackfillResumeTests : IAsyncLifetime, IDisposable
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
        // 必须镜像 Program.cs 的 DI 配置：history 表显式落在 interviews schema；
        // 缺了它，MigrateAsync 会把记录写进 public.__EFMigrationsHistory，自定义 history 表读不到，
        // 后续正确配置的迁移会从第一条重放撞表（42P07）——本票初版即因此拖垮并行类，已修复。
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
        public HashSet<string> ExpiredAnchors { get; } = [];

        public List<string?> ListTokensCalled { get; } = [];
        public List<DateTimeOffset> ListSinceCalled { get; } = [];
        public List<string> FullFetchedIds { get; } = [];
        public List<string> RawFetchedIds { get; } = [];
        public List<string> HistoryAnchorsCalled { get; } = [];

        public Dictionary<string, int> FailFullTimes { get; } = [];
        public HashSet<int> FailListCallNumbers { get; } = [];
        private readonly Dictionary<string, int> _fullAttempts = [];
        private int _listCalls;

        public Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListIdsPageAsync(
            string accessToken, string? pageToken, DateTimeOffset sinceUtc, CancellationToken ct)
        {
            ListTokensCalled.Add(pageToken);
            ListSinceCalled.Add(sinceUtc);
            _listCalls++;
            if (FailListCallNumbers.Contains(_listCalls))
                throw new MailboxRateLimitedException("限流", null);
            var key = pageToken ?? "";
            if (!ListPages.TryGetValue(key, out var page))
                return Task.FromResult<(IReadOnlyList<string>, string?)>(([], null));
            return Task.FromResult<(IReadOnlyList<string>, string?)>((page.Ids, page.Next));
        }

        public Task<MailboxMessage?> GetMessageFullAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            FullFetchedIds.Add(providerMessageId);
            _fullAttempts.TryGetValue(providerMessageId, out var n);
            _fullAttempts[providerMessageId] = n + 1;
            if (FailFullTimes.TryGetValue(providerMessageId, out var fail) && n < fail)
                throw new MailboxRateLimitedException("限流", null);
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
            if (ExpiredAnchors.Contains(startHistoryId)) throw new MailboxCursorExpiredException("expired");
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
        public List<(string Url, DateTimeOffset At)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri?.ToString() ?? "", DateTimeOffset.UtcNow));
            return Task.FromResult(Respond(request));
        }
    }

    // ============================ 搭建 ============================

    private static MailboxProtection NewProtection() =>
        new(DataProtectionProvider.Create("test-294"));

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t294-" + Guid.NewGuid());
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

    private static MailboxMessage Msg(string id, string from, string subject, string body,
        string? threadId = null) =>
        new(id, threadId, from, null, ["me@example.com"], [], subject,
            DateTimeOffset.UtcNow, body, null, null, [], null);

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

    // ============================ 回填断点 ============================

    [Fact]
    public async Task Backfill_QuotaMidPage_ResumesWithoutRefetchingCompletedPages()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (_, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = (["m1", "m2"], "p2");
        fake.ListPages["p2"] = (["m3", "m4"], null);
        fake.Fulls["m1"] = Msg("m1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Fulls["m2"] = Msg("m2", "news@other.com", "Newsletter", "hello");
        fake.Fulls["m3"] = Msg("m3", "hr@examplecorp.com", "Interview invitation", "hello");
        fake.Fulls["m4"] = Msg("m4", "news@other.com", "Corpay update", "Corpay is hiring");
        fake.Raws["m1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.Raws["m3"] = RawOf("From: hr@examplecorp.com\n\nhello");
        fake.FailFullTimes["m3"] = 1;
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run1 = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run1.IsSuccess.Should().BeTrue();
        run1.Value.RateLimited.Should().BeTrue();
        run1.Value.Added.Should().Be(1);

        var progress = await db.MailboxBackfillProgress.FirstOrDefaultAsync();
        progress.Should().NotBeNull();
        progress!.CurrentPageIndex.Should().Be(0);
        progress.NextPageToken.Should().BeNull();
        progress.CurrentPageIdsJson.Should().Contain("m3");

        var run2 = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run2.IsSuccess.Should().BeTrue();
        run2.Value.RateLimited.Should().BeFalse();
        run2.Value.Added.Should().Be(1);
        run2.Value.Pending.Should().Be(1);

        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);
        var conn = await db.MailboxConnections.FirstAsync();
        conn.Cursor.Should().Be("h200");
        fake.FullFetchedIds.Where(id => id is "m1" or "m2").Should().HaveCount(2);
        fake.RawFetchedIds.Should().BeEquivalentTo(["m1", "m3"]);
        (await db.MailboxImportRecords.CountAsync()).Should().Be(2);
        (await db.MailboxPendingItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Backfill_QuotaMidList_ResumesAtPageBoundary()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = (["m1"], "p2");
        fake.ListPages["p2"] = (["m2"], null);
        fake.Fulls["m1"] = Msg("m1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Fulls["m2"] = Msg("m2", "hr@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["m1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.Raws["m2"] = RawOf("From: hr@examplecorp.com\n\nhello");
        fake.FailListCallNumbers.Add(2);
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run1 = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run1.Value.RateLimited.Should().BeTrue();

        var progress = await db.MailboxBackfillProgress.FirstOrDefaultAsync();
        progress.Should().NotBeNull();
        progress!.CurrentPageIndex.Should().Be(1);
        progress.NextPageToken.Should().Be("p2");

        var run2 = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run2.Value.RateLimited.Should().BeFalse();
        run2.Value.Added.Should().Be(1);

        fake.ListTokensCalled.Should().BeEquivalentTo([null, "p2", "p2"]);
        fake.ListTokensCalled.Count(t => t is null).Should().Be(1);
    }

    // ============================ 稳态 ============================

    [Fact]
    public async Task Steady_HistoryDeltasOnly_AndAnchorAdvances()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId, cursor: "h100");
        SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.HistoryPages["h100|"] = (["d1"], null, "h105");
        fake.Fulls["d1"] = Msg("d1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["d1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var r = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        r.Value.Added.Should().Be(1);
        r.Value.CursorAdvanced.Should().BeTrue();

        (await db.MailboxConnections.FirstAsync()).Cursor.Should().Be("h105");
        fake.ListTokensCalled.Should().BeEmpty();
        fake.HistoryAnchorsCalled.Should().BeEquivalentTo(["h100"]);
    }

    [Fact]
    public async Task Steady_AnchorExpired404_ClearsAndBackfillsWithFallbackWindow()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId, cursor: "hOld");
        SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ExpiredAnchors.Add("hOld");
        fake.ListPages[""] = (["b1"], null);
        fake.Fulls["b1"] = Msg("b1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["b1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h0|"] = ([], null, "h9");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var r = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        r.Value.Added.Should().Be(1);

        (await db.MailboxConnections.FirstAsync()).Cursor.Should().Be("h9");
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);
        fake.ListSinceCalled.Should().NotBeEmpty();
        fake.ListSinceCalled.Should().AllSatisfy(since =>
            (DateTimeOffset.UtcNow - since).TotalDays.Should().BeInRange(29, 31));
    }

    // ============================ Provider 级：配额映射与节流 ============================

    [Fact]
    public async Task Provider_Quota403_MapsToRateLimited()
    {
        var stub = new StubHandler();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":403,"message":"Quota exceeded","errors":[{"reason":"quotaExceeded"}]}}""",
                Encoding.UTF8, "application/json")
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub), TimeSpan.Zero, TimeSpan.Zero);

        var act = async () => await gmail.GetMessageFullAsync("tok", "m1", CancellationToken.None);
        await act.Should().ThrowAsync<MailboxRateLimitedException>();
    }

    [Fact]
    public async Task Provider_NonQuota403_StaysProviderError()
    {
        var stub = new StubHandler();
        stub.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":403,"message":"Request had insufficient authentication scopes","errors":[{"reason":"forbidden"}]}}""",
                Encoding.UTF8, "application/json")
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub), TimeSpan.Zero, TimeSpan.Zero);

        var act = async () => await gmail.GetMessageFullAsync("tok", "m1", CancellationToken.None);
        await act.Should().ThrowAsync<MailboxProviderException>();
    }

    [Fact]
    public async Task Provider_Pacing_SpacesMeteredCalls()
    {
        var stub = new StubHandler();
        stub.Respond = req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                GmailFullJson("m", "a@b.com", "Hi", "hello"), Encoding.UTF8, "application/json")
        };
        var gmail = new GmailMailboxProvider(new HttpClient(stub),
            TimeSpan.FromMilliseconds(200), TimeSpan.Zero);

        await gmail.GetMessageFullAsync("tok", "m1", CancellationToken.None);
        await gmail.GetMessageFullAsync("tok", "m2", CancellationToken.None);

        stub.Requests.Should().HaveCount(2);
        (stub.Requests[1].At - stub.Requests[0].At).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
    }

    // ============================ 保真度：legacy vs streaming ============================

    private static (InterviewEntry E1, InterviewEntry E2) SeedFidelityEntries(InterviewsDbContext db, Guid userId)
    {
        var (e1, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com", "uniquecorp.com"], [], ["corpay"], ["blocked@spam.com"]);
        var (e2, _) = SeedEntry(db, userId, "OtherCorp", ["examplecorp.com"], [], ["othercorp"], []);
        db.MailContactMaps.Add(new MailContactMap(userId, "known@friend.com", e1.Id));
        return (e1, e2);
    }

    private static List<MailboxMessage> FidelityCorpus() =>
    [
        Msg("s1", "recruiter@uniquecorp.com", "Interview invitation", "hello"),
        Msg("s2", "known@friend.com", "Hello", "just checking in"),
        Msg("s3", "news@other.com", "Corpay hiring", "Corpay is hiring engineers"),
        Msg("s4", "noreply@linkedin.com", "OtherCorp job alert", "OtherCorp has a new role"),
        Msg("s5", "someone@examplecorp.com", "Hello", "nothing here"),
        Msg("s6", "news@other.com", "News", "Corpay and OtherCorp both hiring"),
        Msg("s7", "blocked@spam.com", "Corpay news", "Corpay Corpay Corpay"),
        Msg("s8", "random@other.com", "Hi", "nothing"),
        Msg("s9", "recruiter@uniquecorp.com", "Interview invitation", "hello", threadId: "th1"),
    ];

    private async Task<(int Added, int Pending, int Skipped, int Failed,
        HashSet<string> Imported, HashSet<string> PendingIds)> RunFidelityLegacyAsync(Guid userId)
    {
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedFidelityEntries(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeProviderBase();
        var corpus = FidelityCorpus();
        foreach (var m in corpus)
        {
            fake.Fulls[m.ProviderMessageId] = m;
            fake.Raws[m.ProviderMessageId] = RawOf("From: " + m.FromEmail + "\n\n" + m.BodyText);
            fake.LegacyMessages.Add(m with { RawMimeBase64Url = RawOf("From: " + m.FromEmail + "\n\n" + m.BodyText) });
        }
        fake.ThreadMembers.Add(corpus[8] with { RawMimeBase64Url = RawOf("From: recruiter@uniquecorp.com\n\nhello") });
        fake.ThreadMembers.Add(Msg("t2", "colleague@uniquecorp.com", "Re: Interview invitation", "see you")
            with { RawMimeBase64Url = RawOf("From: colleague@uniquecorp.com\n\nsee you") });

        var root = NewTempRoot();
        var handler = new RefreshMailboxCommandHandler(db, new FakeFactory(fake), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection),
            Options.Create(new MailboxOptions { AttachmentRootDir = root }));
        var r = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        var imported = (await db.MailboxImportRecords.ToListAsync()).Select(x => x.ProviderMessageId).ToHashSet();
        var pending = (await db.MailboxPendingItems.ToListAsync()).Select(x => x.ProviderMessageId).ToHashSet();
        return (r.Value.Added, r.Value.Pending, r.Value.SkippedDuplicates, r.Value.Failed, imported, pending);
    }

    private async Task<(int Added, int Pending, int Skipped, int Failed,
        HashSet<string> Imported, HashSet<string> PendingIds, List<string> RawFetched)> RunFidelityStreamingAsync(Guid userId)
    {
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedFidelityEntries(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        var corpus = FidelityCorpus();
        fake.ListPages[""] = (corpus.Select(m => m.ProviderMessageId).ToList(), null);
        foreach (var m in corpus)
        {
            fake.Fulls[m.ProviderMessageId] = m;
            fake.Raws[m.ProviderMessageId] = RawOf("From: " + m.FromEmail + "\n\n" + m.BodyText);
        }
        fake.ThreadMembers.Add(corpus[8] with { RawMimeBase64Url = RawOf("From: recruiter@uniquecorp.com\n\nhello") });
        fake.ThreadMembers.Add(Msg("t2", "colleague@uniquecorp.com", "Re: Interview invitation", "see you")
            with { RawMimeBase64Url = RawOf("From: colleague@uniquecorp.com\n\nsee you") });
        fake.HistoryPages["h0|"] = ([], null, "h99");

        var root = NewTempRoot();
        var handler = new RefreshMailboxCommandHandler(db, new FakeFactory(fake), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection),
            Options.Create(new MailboxOptions { AttachmentRootDir = root }));
        var r = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        var imported = (await db.MailboxImportRecords.ToListAsync()).Select(x => x.ProviderMessageId).ToHashSet();
        var pending = (await db.MailboxPendingItems.ToListAsync()).Select(x => x.ProviderMessageId).ToHashSet();
        return (r.Value.Added, r.Value.Pending, r.Value.SkippedDuplicates, r.Value.Failed, imported, pending, fake.RawFetchedIds);
    }

    [Fact]
    public async Task MatchingFidelity_LegacyVsStreaming_Identical()
    {
        if (!_available) return;
        var legacyUser = NewUser();
        var streamingUser = NewUser();
        var legacy = await RunFidelityLegacyAsync(legacyUser);
        var streaming = await RunFidelityStreamingAsync(streamingUser);

        streaming.Added.Should().Be(legacy.Added);
        streaming.Pending.Should().Be(legacy.Pending);
        streaming.Skipped.Should().Be(legacy.Skipped);
        streaming.Failed.Should().Be(legacy.Failed);
        streaming.Imported.Should().BeEquivalentTo(legacy.Imported);
        streaming.PendingIds.Should().BeEquivalentTo(legacy.PendingIds);

        // 语料预期：强信号 s1/s2/s9 + 线程成员 t2 导入；弱/歧义 s3/s4/s5/s6 进队列；s7 排除、s8 无信号。
        legacy.Added.Should().Be(4);
        legacy.Pending.Should().Be(4);
        legacy.Imported.Should().BeEquivalentTo(["s1", "s2", "s9", "t2"]);
        legacy.PendingIds.Should().BeEquivalentTo(["s3", "s4", "s5", "s6"]);

        // 流式仅对强信号命中封取原文：s1/s2/s9（线程成员原文走 GetThreadAsync，不计入 RawFetchedIds）。
        streaming.RawFetched.Should().BeEquivalentTo(["s1", "s2", "s9"]);
    }

    // ============================ 作用域、锚点、断开、清标 ============================

    [Fact]
    public async Task ScopeMismatch_DiscardsAndRestarts()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entryA, _) = SeedEntry(db, userId, "A", ["acorp.com"], [], [], []);
        var (entryB, _) = SeedEntry(db, userId, "B", ["uniquecorp.com"], [], [], []);
        await db.SaveChangesAsync();

        db.MailboxBackfillProgress.Add(new MailboxBackfillProgress(userId, MailboxProvider.Gmail,
            "user@example.com", entryA.Id, DateTimeOffset.UtcNow.AddDays(-180), "h0", "[\"m0\"]", 0, null));
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = (["m1", "m2"], null);
        fake.Fulls["m1"] = Msg("m1", "recruiter@uniquecorp.com", "Interview invitation", "hello");
        fake.Fulls["m2"] = Msg("m2", "hr@uniquecorp.com", "Interview invitation", "hello");
        fake.Raws["m1"] = RawOf("From: recruiter@uniquecorp.com\n\nhello");
        fake.FailFullTimes["m2"] = 1;
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run = await handler.Handle(new RefreshMailboxCommand(userId, entryB.Id), CancellationToken.None);
        run.Value.RateLimited.Should().BeTrue();

        var progress = await db.MailboxBackfillProgress.FirstOrDefaultAsync();
        progress.Should().NotBeNull();
        progress!.ScopeEntryId.Should().Be(entryB.Id);
        progress.CurrentPageIdsJson.Should().Contain("m1");
        progress.CurrentPageIdsJson.Should().Contain("m2");
        fake.ListTokensCalled.Any(t => t is null).Should().BeTrue();
    }

    [Fact]
    public async Task Backfill_Completion_SeedsAnchorViaCatchUp()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = (["c1"], null);
        fake.Fulls["c1"] = Msg("c1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["c1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.HistoryPages["h0|"] = (["d1"], null, "h77");
        fake.Fulls["d1"] = Msg("d1", "hr@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["d1"] = RawOf("From: hr@examplecorp.com\n\nhello");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var r = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        r.Value.Added.Should().Be(2);

        (await db.MailboxConnections.FirstAsync()).Cursor.Should().Be("h77");
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Disconnect_ClearsProgress()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        var conn = SeedConnection(db, protection, userId);
        db.MailboxBackfillProgress.Add(new MailboxBackfillProgress(userId, MailboxProvider.Gmail,
            "user@example.com", null, DateTimeOffset.UtcNow.AddDays(-180), "h0", "[\"m1\"]", 0, null));
        await db.SaveChangesAsync();

        var fake = new FakeProviderBase();
        var handler = new DisconnectMailboxCommandHandler(db, new FakeFactory(fake), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection));
        var r = await handler.Handle(new DisconnectMailboxCommand(userId, conn.Id), CancellationToken.None);
        r.IsSuccess.Should().BeTrue();

        (await db.MailboxConnections.CountAsync()).Should().Be(0);
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Resume_DoesNotReclearMailboxNew()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        var (entry, _) = SeedEntry(db, userId, "Corpay", ["examplecorp.com"], [], ["corpay"], []);
        entry.AttachAsset(AssetKind.Text, "old.eml", "message/rfc822", 10,
            null, null, "old body", null, null, "en", "sha", null, null, "{\"mailboxNew\":true}");
        await db.SaveChangesAsync();

        var fake = new FakeStreamingProvider();
        fake.ListPages[""] = (["m1", "m2"], null);
        fake.Fulls["m1"] = Msg("m1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Fulls["m2"] = Msg("m2", "hr@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["m1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        fake.Raws["m2"] = RawOf("From: hr@examplecorp.com\n\nhello");
        fake.FailFullTimes["m2"] = 1;
        fake.HistoryPages["h0|"] = ([], null, "h200");

        var handler = NewHandler(db, fake, protection, NewTempRoot());
        var run1 = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run1.Value.RateLimited.Should().BeTrue();

        // 新起跑清过旧标；m1 在本轮导入、带 new 标记。
        var reloaded1 = await db.Entries.Include(e => e.Assets).FirstAsync(e => e.Id == entry.Id);
        var m1Asset = reloaded1.Assets.First(a => (a.MetaJson ?? "").Contains("m1"));
        m1Asset.MetaJson.Should().Contain("\"mailboxNew\":true");

        var run2 = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);
        run2.Value.RateLimited.Should().BeFalse();

        var reloaded2 = await db.Entries.Include(e => e.Assets).FirstAsync(e => e.Id == entry.Id);
        var m1AssetAfter = reloaded2.Assets.First(a => (a.MetaJson ?? "").Contains("m1"));
        m1AssetAfter.MetaJson.Should().Contain("\"mailboxNew\":true");
    }
}
