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
/// 票 303：后台任务无 HTTP 用户时刷新仍应看见连接（296 回归）。
/// 296 的异步任务在 Task.Run + 自带 scope 内跑 handler，HttpCurrentUser 为 null，
/// DbContext 全局过滤器（UserId == CurrentUserId）fail-closed 匹配零行 → 误报 needsReconnect。
/// 本类刻意用「无 ICurrentUser」的 DbContext（CurrentUserId = null，恰为任务 scope 的真实形态）
/// 跑真实 handler；旧测试注入 FixedCurrentUser，因而永远测不出此回归。
/// DB 门控同 294/296：连不上沙盒 Postgres 则 DB 例跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）。
/// </summary>
public sealed class MailboxRefreshNullAmbientUserTests : IAsyncLifetime, IDisposable
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres";
    private bool _available;
    private readonly List<Guid> _users = [];
    private readonly List<string> _tempRoots = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDbNoUser();
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
            await using var db = NewDbNoUser();
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

    /// <summary>恰为 296 任务 scope 的形态：不传 ICurrentUser，CurrentUserId = null。</summary>
    private static InterviewsDbContext NewDbNoUser()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql(Cs, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", InterviewsDbContext.Schema))
            .Options;
        return new InterviewsDbContext(opts);
    }

    private Guid NewUser()
    {
        var userId = Guid.NewGuid();
        _users.Add(userId);
        return userId;
    }

    private static MailboxProtection NewProtection() =>
        new(DataProtectionProvider.Create("test-303"));

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t303-" + Guid.NewGuid());
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

    // ============================ 假增量 provider（仅回填路径） ============================

    private sealed class FakeStreaming : IMailboxProvider, IIncrementalMailboxProvider
    {
        public MailboxProvider Kind => MailboxProvider.Gmail;
        public Dictionary<string, (List<string> Ids, string? Next)> ListPages { get; } = [];
        public Dictionary<string, MailboxMessage> Fulls { get; } = [];
        public Dictionary<string, string> Raws { get; } = [];

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
            var key = pageToken ?? "";
            if (!ListPages.TryGetValue(key, out var page))
                return Task.FromResult<(IReadOnlyList<string>, string?)>(([], null));
            return Task.FromResult<(IReadOnlyList<string>, string?)>((page.Ids, page.Next));
        }

        public Task<MailboxMessage?> GetMessageFullAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
            Fulls.TryGetValue(providerMessageId, out var full);
            return Task.FromResult(full);
        }

        public Task<string?> GetMessageRawAsync(string accessToken, string providerMessageId, CancellationToken ct)
        {
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

    [Fact]
    public async Task JobScope_NullAmbientUser_RefreshSeesExistingConnection_AndImports()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDbNoUser();
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        var fake = new FakeStreaming();
        fake.ListPages[""] = (["m1"], null);
        fake.Fulls["m1"] = Msg("m1", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["m1"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        var root = NewTempRoot();

        // 校验查询本身也要 IgnoreQueryFilters：断言侧的 null-user 上下文同样受过滤器影响。
        await using var verifyDb = NewDbNoUser();
        (await verifyDb.MailboxConnections.IgnoreQueryFilters()
            .CountAsync(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active))
            .Should().Be(1);

        var handler = new RefreshMailboxCommandHandler(db, new FakeFactory(fake), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection),
            Options.Create(new MailboxOptions { AttachmentRootDir = root }));
        var result = await handler.Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.NeedsReconnect.Should().BeFalse();
        result.Value.Added.Should().Be(1);
        result.Value.Failed.Should().Be(0);
        (await verifyDb.MailboxImportRecords.IgnoreQueryFilters()
            .CountAsync(r => r.UserId == userId)).Should().Be(1);
        (await verifyDb.MailboxBackfillProgress.IgnoreQueryFilters()
            .CountAsync(p => p.UserId == userId)).Should().Be(0);
        (await verifyDb.MailboxConnections.IgnoreQueryFilters()
            .FirstAsync(c => c.UserId == userId)).Cursor.Should().Be("h200");
    }

    [Fact]
    public async Task Callback_NullAmbientUser_ReconnectReplacesInsteadOfAccumulatingActiveRows()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDbNoUser();
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();

        await using var verifyDb = NewDbNoUser();
        (await verifyDb.MailboxConnections.IgnoreQueryFilters()
            .CountAsync(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active))
            .Should().Be(2);

        var factory = new CallbackFakeFactory();
        var handler = new MailboxCallbackCommandHandler(db, factory, protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection));
        var state = protection.CreateState(userId, MailboxProvider.Gmail);
        var result = await handler.Handle(new MailboxCallbackCommand("code", state), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("user@example.com");
        (await verifyDb.MailboxConnections.IgnoreQueryFilters()
            .CountAsync(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active))
            .Should().Be(1);
    }

    private sealed class CallbackFakeFactory : IMailboxProviderFactory
    {
        public IMailboxProvider Get(MailboxProvider kind) => new CallbackFakeProvider(kind);
    }

    private sealed class CallbackFakeProvider(MailboxProvider kind) : IMailboxProvider
    {
        public MailboxProvider Kind => kind;

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) => "https://example.test/auth";
        public Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access-new", "refresh-new", "scope", null));
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
    }
}
