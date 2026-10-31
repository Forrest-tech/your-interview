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
/// 票 300：单封保存失败不中断整轮。
/// DB 门控同 294/296：连不上沙盒 Postgres 则 DB 例跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 夹具全合成（虚构域名、邮箱、主题，不含任何真实邮件）。
/// 回归手段：弱信号邮件用超长主题（2001 字 &gt; pending 表 subject 列 varchar(2000)）
/// 在逐封 SaveChanges 处触发真实 22001 约束错；断言整轮继续、坏封计 failed 且原因含内层异常文本、
/// 后续健康邮件正常导入、坏封无残留行、第二轮不重复处理。
/// </summary>
public sealed class MailboxRefreshSaveFailureTests : IAsyncLifetime, IDisposable
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
        new(DataProtectionProvider.Create("test-300"));

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t300-" + Guid.NewGuid());
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

    private async Task<Result<RefreshMailboxResult>> RunRefreshAsync(
        InterviewsDbContext db, FakeStreaming fake, MailboxProtection protection, Guid userId, string root) =>
        await new RefreshMailboxCommandHandler(db, new FakeFactory(fake), protection,
                new MailboxOAuthResolver(db, EnvOptions(), protection),
                Options.Create(new MailboxOptions { AttachmentRootDir = root }))
            .Handle(new RefreshMailboxCommand(userId, null), CancellationToken.None);

    [Fact]
    public async Task SaveFailure_OnOneMessage_ContinuesWithNext_AndCountsFailedWithInnerReason()
    {
        if (!_available) return;
        var userId = NewUser();
        using var db = NewDb(userId);
        var protection = NewProtection();
        SeedConnection(db, protection, userId);
        SeedEntry(db, userId);
        await db.SaveChangesAsync();

        // 坏封在前、健康封在后：坏封弱信号进待确认队列，超长主题在逐封保存时触发 22001；
        // 健康封强信号（域名命中）应照常导入。
        var fake = new FakeStreaming();
        fake.ListPages[""] = (["bad", "good"], null);
        fake.Fulls["bad"] = Msg("bad", "news@other.com", new string('x', 2001), "Corpay newsletter");
        fake.Fulls["good"] = Msg("good", "recruiter@examplecorp.com", "Interview invitation", "hello");
        fake.Raws["good"] = RawOf("From: recruiter@examplecorp.com\n\nhello");
        var root = NewTempRoot();

        var result = await RunRefreshAsync(db, fake, protection, userId, root);

        // 整轮未死：成功返回，坏封计 failed 且原因含内层 22001 文本，健康封已导入。
        result.IsSuccess.Should().BeTrue();
        result.Value.Added.Should().Be(1);
        result.Value.Failed.Should().Be(1);
        result.Value.Pending.Should().Be(0);
        result.Value.FailedReasons.Should().HaveCount(1);
        result.Value.FailedReasons[0].Should().Contain("varying(2000)");
        (await db.MailboxImportRecords.CountAsync()).Should().Be(1);
        (await db.MailboxImportRecords.FirstAsync()).ProviderMessageId.Should().Be("good");
        (await db.MailboxPendingItems.CountAsync()).Should().Be(0);
        (await db.MailboxBackfillProgress.CountAsync()).Should().Be(0);

        // 第二轮：坏封不被重复处理、不重复计 failed（断点已越过坏封，稳态无新信）。
        var second = await RunRefreshAsync(db, fake, protection, userId, root);
        second.IsSuccess.Should().BeTrue();
        second.Value.Added.Should().Be(0);
        second.Value.Failed.Should().Be(0);
        second.Value.Pending.Should().Be(0);
    }
}
