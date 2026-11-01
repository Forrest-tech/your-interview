using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Api;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票 309 §11 后端 DB 测试（1 的导入路径、5–12、8b、12b + 端点头断言）。
/// 门控同仓内先例：连不上沙盒 Postgres 则跳过；每例独立随机 UserId，Dispose 清本例数据。
/// 夹具全合成（example.test 域名、虚构主题，不含任何真实邮件）。
/// </summary>
public sealed class MailHtmlBackfillTests : IAsyncLifetime, IDisposable
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
                (IQueryable<object>)db.MailboxConnections.IgnoreQueryFilters()
                    .Where(c => _users.Contains(c.UserId)),
                db.MailboxImportRecords.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)),
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

    private string NewTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "t309-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        _tempRoots.Add(dir);
        return dir;
    }

    private static MailboxProtection NewProtection() =>
        new(Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("test-309"));

    private static IOptions<MailboxOptions> EnvOptions() => Options.Create(new MailboxOptions
    {
        Google = new MailboxOptions.GoogleOptions
            { ClientId = "id", ClientSecret = "secret", RedirectUri = "https://example.test/cb" },
    });

    private sealed class FakeFactory(IMailboxProvider provider) : IMailboxProviderFactory
    {
        public IMailboxProvider Get(MailboxProvider kind) => provider;
    }

    /// <summary>票 309 回填/导入 stub：Gmail 增量口径（GetMessageFullAsync 为回填取信口）。</summary>
    private sealed class StubMailProvider : IIncrementalMailboxProvider
    {
        public MailboxProvider Kind => MailboxProvider.Gmail;
        public Dictionary<string, MailboxMessage?> Fulls { get; } = [];
        public Dictionary<string, byte[]> BytesByAttachmentId { get; } = [];
        public List<MailboxAttachment>? ListResult { get; set; }
        public Exception? FullException { get; set; }
        public int FullCalls { get; private set; }
        public int BytesCalls { get; private set; }

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state)
            => "https://example.test/auth";
        public Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret,
            string code, string redirectUri, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access", "refresh", "scope", null));
        public Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret,
            string refreshToken, CancellationToken ct)
            => Task.FromResult(new MailboxTokens("access-dummy", "refresh-dummy", "scope", null));
        public Task RevokeAsync(string clientId, string clientSecret, string refreshToken,
            CancellationToken ct) => Task.CompletedTask;
        public Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor,
            DateTimeOffset sinceUtc, CancellationToken ct)
            => Task.FromResult(new MailboxFetchResult([], null, false));
        public Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId,
            CancellationToken ct)
        {
            FullCalls++;
            if (FullException is not null) throw FullException;
            Fulls.TryGetValue(providerMessageId, out var full);
            return Task.FromResult(full);
        }
        public Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId,
            string providerAttachmentId, CancellationToken ct)
        {
            BytesCalls++;
            BytesByAttachmentId.TryGetValue(providerAttachmentId, out var bytes);
            return Task.FromResult(bytes);
        }
        public Task<IReadOnlyList<MailboxMessage>> GetThreadAsync(string accessToken, string threadId,
            CancellationToken ct) => Task.FromResult<IReadOnlyList<MailboxMessage>>([]);
        public Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct)
            => Task.FromResult<string?>("user@example.com");
        public Task<IReadOnlyList<MailboxAttachment>> ListAttachmentsAsync(string accessToken,
            string providerMessageId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MailboxAttachment>>(ListResult ?? []);
        public Task<(IReadOnlyList<string> Ids, string? NextPageToken)> ListIdsPageAsync(
            string accessToken, string? pageToken, DateTimeOffset sinceUtc, CancellationToken ct)
            => Task.FromResult<(IReadOnlyList<string>, string?)>(([], null));
        public Task<MailboxMessage?> GetMessageFullAsync(string accessToken, string providerMessageId,
            CancellationToken ct)
        {
            FullCalls++;
            if (FullException is not null) throw FullException;
            Fulls.TryGetValue(providerMessageId, out var full);
            return Task.FromResult(full);
        }
        public Task<string?> GetMessageRawAsync(string accessToken, string providerMessageId,
            CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> GetProfileHistoryIdAsync(string accessToken, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task<(IReadOnlyList<string> Ids, string? NextPageToken, string? NewHistoryId)>
            ListHistoryPageAsync(string accessToken, string startHistoryId, string? pageToken,
                CancellationToken ct)
            => Task.FromResult<(IReadOnlyList<string>, string?, string?)>(([], null, null));
    }

    private GetMailHtmlQueryHandler NewHtmlHandler(InterviewsDbContext db, IMailboxProvider provider,
        MailboxProtection protection, string root) =>
        new(db, new FakeFactory(provider), protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection),
            Options.Create(new MailboxOptions { AttachmentRootDir = root }));

    private GetMailInlineImageQueryHandler NewInlineHandler(InterviewsDbContext db, string root) =>
        new(db, Options.Create(new MailboxOptions { AttachmentRootDir = root }));

    private static InterviewEntry SeedEntry(InterviewsDbContext db, Guid userId)
    {
        var entry = new InterviewEntry(Guid.NewGuid(), "ExampleCo", "Developer", userId: userId);
        db.Entries.Add(entry);
        return entry;
    }

    private static void SeedConnection(InterviewsDbContext db, MailboxProtection protection,
        Guid userId)
    {
        db.MailboxConnections.Add(new MailboxConnection(userId, MailboxProvider.Gmail,
            "user@example.com",
            "https://www.googleapis.com/auth/gmail.readonly openid email profile",
            protection.ProtectRefreshToken("rt")));
    }

    // 注意：SetHtmlBody/SetMeta 是 internal（程序集无 InternalsVisibleTo），测试内
    // 不直接调；HtmlBody 由导入/回填路径写，cidMap 由导入写或随 AttachAsset 的
    // metaJson 一次性带入（esc 类手写 file 值不需要真实 asset id，见 T12/T12b）。
    private static InterviewAsset SeedMailAsset(InterviewsDbContext db, InterviewEntry entry,
        string metaJson)
    {
        return entry.AttachAsset(AssetKind.Text, "seed-mail.eml", "message/rfc822",
            10, null, null, "plain body text", null, null, "en", null, null, null, metaJson);
    }

    private static string MetaWith(string providerMessageId, string? extraJson = null)
    {
        var meta = new JsonObject
        {
            ["channel"] = "email",
            ["mailboxProvider"] = "Gmail",
            ["mailboxMessageId"] = providerMessageId,
        };
        if (extraJson is not null)
        {
            var extra = JsonNode.Parse(extraJson) as JsonObject;
            if (extra is not null)
                foreach (var kv in extra) meta[kv.Key] = kv.Value?.DeepClone();
        }
        return meta.ToJsonString();
    }

    private static MailboxMessage BackfillMsg(string id, string bodyHtml,
        IReadOnlyList<MailboxAttachment> attachments) =>
        new(id, null, "sender@example.test", null, ["me@example.test"], [], "Subject",
            DateTimeOffset.UtcNow, "plain body text", bodyHtml, null, attachments, null);

    private static string RawOf(string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        return b64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    // ============================ 票 309 测试 ============================

    /// <summary>测试 1：导入存消毒后 HTML（script/form/on*/javascript:/svg 进不去；合法标记留存）。</summary>
    [Fact]
    public async Task Import_StoresSanitizedHtml()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        await db.SaveChangesAsync();
        var bodyHtml = "<script>alert(1)</script><form action=\"https://evil.example\"><input></form>" +
            "<p onclick=\"steal()\">hi <b>bold</b> " +
            "<a href=\"javascript:alert(1)\">bad</a> " +
            "<a href=\"https://example.test\">good</a> " +
            "<a href=\"mailto:a@example.test\">mail</a></p>" +
            "<svg><script>alert(2)</script></svg>" +
            "<table><tr><td style=\"color:red\">cell</td></tr></table>";
        var msg = new MailboxMessage("m-import", null, "sender@example.test", null,
            ["me@example.test"], [], "Subject", DateTimeOffset.UtcNow,
            "plain body", bodyHtml, RawOf("fake raw"), [], null);
        var assetId = await MailboxImportHelper.ImportMessageAsync(
            db, provider, "access", Options.Create(new MailboxOptions { AttachmentRootDir = root }).Value,
            userId, entry, msg, "rule", CancellationToken.None);
        assetId.Should().NotBeNull();
        await db.SaveChangesAsync();
        var asset = await db.Assets.IgnoreQueryFilters()
            .FirstAsync(a => a.Id == assetId!.Value);
        asset.HtmlBody.Should().NotBeNull();
        asset.HtmlBody.Should().NotContain("<script");
        asset.HtmlBody.Should().NotContain("<form");
        asset.HtmlBody.Should().NotContain("<input");
        asset.HtmlBody.Should().NotContain("onclick");
        asset.HtmlBody.Should().NotContain("javascript:");
        asset.HtmlBody.Should().NotContain("<svg");
        asset.HtmlBody.Should().Contain("<b>bold</b>");
        asset.HtmlBody.Should().Contain("https://example.test");
        asset.HtmlBody.Should().Contain("mailto:a@example.test");
        asset.HtmlBody.Should().Contain("<table");
        asset.HtmlBody.Should().Contain("color");
        asset.TranscriptText.Should().Be("plain body");
    }

    /// <summary>测试 5：导入捕获 Content-ID 进 cidMap（归一化键 + 文件 + 类型 + 落盘字节）。</summary>
    [Fact]
    public async Task Import_CapturesContentIdIntoCidMap()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        await db.SaveChangesAsync();
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var msg = new MailboxMessage("m-cid", null, "sender@example.test", null,
            ["me@example.test"], [], "Subject", DateTimeOffset.UtcNow,
            "plain body", "<p><img src=\"cid:img1@example\"></p>", RawOf("fake raw"),
            [new MailboxAttachment("pic.png", "image/png", png.Length, "att1", png, "<img1@example>")],
            null);
        var assetId = await MailboxImportHelper.ImportMessageAsync(
            db, provider, "access", Options.Create(new MailboxOptions { AttachmentRootDir = root }).Value,
            userId, entry, msg, "rule", CancellationToken.None);
        assetId.Should().NotBeNull();
        await db.SaveChangesAsync();
        var asset = await db.Assets.IgnoreQueryFilters()
            .FirstAsync(a => a.Id == assetId!.Value);
        var map = MailHtmlSanitizer.ParseCidMap(asset.MetaJson);
        map.Should().ContainKey("img1@example");
        map["img1@example"].File.Should()
            .Be($"mail-attachments/{asset.Id}/attachments/pic.png");
        map["img1@example"].ContentType.Should().Be("image/png");
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(root, asset.Id.ToString(), "attachments", "pic.png"));
        bytes.Should().Equal(png);
    }

    /// <summary>测试 6：消毒后超 512KB → HtmlBody NULL（无截断入库），纯文本完好。</summary>
    [Fact]
    public async Task Import_OverCapHtml_StoresNull_KeepsText()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        await db.SaveChangesAsync();
        var big = "<p>" + new string('x', 600 * 1024) + "</p>";
        var msg = new MailboxMessage("m-big", null, "sender@example.test", null,
            ["me@example.test"], [], "Subject", DateTimeOffset.UtcNow,
            "plain body", big, RawOf("fake raw"), [], null);
        var assetId = await MailboxImportHelper.ImportMessageAsync(
            db, provider, "access", Options.Create(new MailboxOptions { AttachmentRootDir = root }).Value,
            userId, entry, msg, "rule", CancellationToken.None);
        await db.SaveChangesAsync();
        var asset = await db.Assets.IgnoreQueryFilters()
            .FirstAsync(a => a.Id == assetId!.Value);
        asset.HtmlBody.Should().BeNull();
        asset.TranscriptText.Should().Be("plain body");
    }

    /// <summary>测试 7：纯 script 正文 → HtmlBody NULL → /html 404（不取信、不留空 iframe）。</summary>
    [Fact]
    public async Task Html_NullBody_NoProviderId_Returns404_NoFetch()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var asset = SeedMailAsset(db, entry, """{"channel":"email"}""");
        await db.SaveChangesAsync();
        var r = await NewHtmlHandler(db, provider, protection, root)
            .Handle(new GetMailHtmlQuery(userId, asset.Id, false), CancellationToken.None);
        r.IsSuccess.Should().BeFalse();
        r.Error.Type.Should().Be(ErrorType.NotFound);
        provider.FullCalls.Should().Be(0);
    }

    /// <summary>测试 8：懒回填取一次即存盘、合并 CID（既有键不动）；二次展开不再取信。</summary>
    [Fact]
    public async Task Backfill_OnceThenStored_MergesCids_AddMissingOnly()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var existingMap = """{"old@example":{"file":"mail-attachments/OLD/attachments/old.png","contentType":"image/png"}}""";
        var asset = SeedMailAsset(db, entry,
            MetaWith("m1", "{\"cidMap\":" + existingMap + "}"));
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();
        var png = new byte[] { 9, 8, 7 };
        provider.BytesByAttachmentId["att2"] = png;
        provider.Fulls["m1"] = BackfillMsg("m1", "<p>hello <b>world</b></p>",
            [new MailboxAttachment("pic.png", "image/png", png.Length, "att2", null, "<new@example>")]);
        var handler = NewHtmlHandler(db, provider, protection, root);
        var first = await handler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        first.Value.Html.Should().Contain("hello");
        provider.FullCalls.Should().Be(1);
        await using var db2 = NewDb(userId);
        var stored = await db2.Assets.IgnoreQueryFilters().FirstAsync(a => a.Id == asset.Id);
        stored.HtmlBody.Should().NotBeNull();
        var map = MailHtmlSanitizer.ParseCidMap(stored.MetaJson);
        map.Should().ContainKey("old@example");
        map["old@example"].File.Should().Contain("old.png");
        map.Should().ContainKey("new@example");
        map["new@example"].File.Should()
            .Be($"mail-attachments/{asset.Id}/attachments/pic.png");
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(root, asset.Id.ToString(), "attachments", "pic.png"));
        bytes.Should().Equal(png);
        var second = await handler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        provider.FullCalls.Should().Be(1);
    }

    /// <summary>测试 8b：回填合并的 CID 真能出字节（含同名碰撞各归各字节）+ served 含 /inline/ URL。</summary>
    [Fact]
    public async Task Backfill_CidServesExactBytes_IncludingFilenameCollision()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var asset = SeedMailAsset(db, entry, MetaWith("m2"));
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();
        var bytesA = new byte[] { 1, 1, 1 };
        var bytesB = new byte[] { 2, 2, 2, 2 };
        provider.BytesByAttachmentId["attA"] = bytesA;
        provider.BytesByAttachmentId["attB"] = bytesB;
        // 注：夹具带正文——§2 判空规则下纯图 HTML 存 NULL（同 4a 注释），
        // 8b 测的是 CID 出字节，故带正文使 HTML 可存盘。
        provider.Fulls["m2"] = BackfillMsg("m2",
            "<p>see the images <img src=\"cid:a@example\"><img src=\"cid:b@example\"></p>",
            [
                new MailboxAttachment("pic.png", "image/png", bytesA.Length, "attA", null, "<a@example>"),
                new MailboxAttachment("pic.png", "image/png", bytesB.Length, "attB", null, "<b@example>"),
            ]);
        var htmlHandler = NewHtmlHandler(db, provider, protection, root);
        var r = await htmlHandler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        r.IsSuccess.Should().BeTrue();
        r.Value.Html.Should().Contain($"/api/interviews/mailbox/assets/{asset.Id}/inline/a%40example");
        r.Value.Html.Should().Contain($"/api/interviews/mailbox/assets/{asset.Id}/inline/b%40example");
        r.Value.Html.Should().NotContain("cid:");
        await using var db2 = NewDb(userId);
        var stored = await db2.Assets.IgnoreQueryFilters().FirstAsync(a => a.Id == asset.Id);
        var map = MailHtmlSanitizer.ParseCidMap(stored.MetaJson);
        map["a@example"].File.Should().Be($"mail-attachments/{asset.Id}/attachments/pic.png");
        map["b@example"].File.Should().Be($"mail-attachments/{asset.Id}/attachments/pic-2.png");
        var inline = NewInlineHandler(db2, root);
        var ra = await inline.Handle(new GetMailInlineImageQuery(userId, asset.Id, "a@example"),
            CancellationToken.None);
        ra.IsSuccess.Should().BeTrue();
        ra.Value.Bytes.Should().Equal(bytesA);
        ra.Value.ContentType.Should().Be("image/png");
        var rb = await inline.Handle(new GetMailInlineImageQuery(userId, asset.Id, "b@example"),
            CancellationToken.None);
        rb.IsSuccess.Should().BeTrue();
        rb.Value.Bytes.Should().Equal(bytesB);
    }

    /// <summary>测试 9：回填取信 404 → 墓碑 + 纯文本保留；二次展开不再取信。</summary>
    [Fact]
    public async Task Backfill_Provider404_Tombstone_NoRefetch()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var asset = SeedMailAsset(db, entry, MetaWith("m3"));
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();
        var handler = NewHtmlHandler(db, provider, protection, root);
        var first = await handler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        first.IsSuccess.Should().BeFalse();
        provider.FullCalls.Should().Be(1);
        await using var db2 = NewDb(userId);
        var stored = await db2.Assets.IgnoreQueryFilters().FirstAsync(a => a.Id == asset.Id);
        stored.HtmlBody.Should().BeNull();
        stored.TranscriptText.Should().Be("plain body text");
        var meta = JsonNode.Parse(stored.MetaJson!) as JsonObject;
        meta!["htmlBackfillUnavailable"]!.GetValue<bool>().Should().BeTrue();
        var second = await handler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        second.IsSuccess.Should().BeFalse();
        provider.FullCalls.Should().Be(1);
    }

    /// <summary>测试 10a：限流是瞬时失败——不写墓碑、不存东西，下次展开重试成功。</summary>
    [Fact]
    public async Task Backfill_RateLimited_Transient_NoTombstone_Retries()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var asset = SeedMailAsset(db, entry, MetaWith("m4"));
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();
        provider.FullException = new MailboxRateLimitedException("限流", null);
        var handler = NewHtmlHandler(db, provider, protection, root);
        var first = await handler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        first.IsSuccess.Should().BeFalse();
        await using var db2 = NewDb(userId);
        var stored = await db2.Assets.IgnoreQueryFilters().FirstAsync(a => a.Id == asset.Id);
        stored.HtmlBody.Should().BeNull();
        stored.MetaJson.Should().NotContain("htmlBackfillUnavailable");
        provider.FullException = null;
        provider.Fulls["m4"] = BackfillMsg("m4", "<p>recovered</p>", []);
        var second = await handler.Handle(new GetMailHtmlQuery(userId, asset.Id, false),
            CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        second.Value.Html.Should().Contain("recovered");
    }

    /// <summary>测试 10b：网络错误是瞬时失败——不写墓碑、不存东西。</summary>
    [Fact]
    public async Task Backfill_NetworkError_Transient_NoTombstone_NothingStored()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var asset = SeedMailAsset(db, entry, MetaWith("m5"));
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();
        provider.FullException = new HttpRequestException("network down");
        var r = await NewHtmlHandler(db, provider, protection, root)
            .Handle(new GetMailHtmlQuery(userId, asset.Id, false), CancellationToken.None);
        r.IsSuccess.Should().BeFalse();
        await using var db2 = NewDb(userId);
        var stored = await db2.Assets.IgnoreQueryFilters().FirstAsync(a => a.Id == asset.Id);
        stored.HtmlBody.Should().BeNull();
        stored.MetaJson.Should().NotContain("htmlBackfillUnavailable");
    }

    /// <summary>测试 11：无 provider id → /html 404，不尝试取信。</summary>
    [Fact]
    public async Task Html_NoProviderId_Returns404_NoFetchAttempted()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        var asset = SeedMailAsset(db, entry, """{"channel":"email"}""");
        SeedConnection(db, protection, userId);
        await db.SaveChangesAsync();
        var r = await NewHtmlHandler(db, provider, protection, root)
            .Handle(new GetMailHtmlQuery(userId, asset.Id, false), CancellationToken.None);
        r.IsSuccess.Should().BeFalse();
        r.Error.Type.Should().Be(ErrorType.NotFound);
        provider.FullCalls.Should().Be(0);
    }

    /// <summary>测试 12：归属/映射/类型/路径四道 404（他人的资产两端点皆 404 非 403；正向对照通过）。</summary>
    [Fact]
    public async Task Inline_ScopeChecks_404Not403()
    {
        if (!_available) return;
        var userA = NewUser();
        var userB = NewUser();
        var root = NewTempRoot();
        var provider = new StubMailProvider();
        var protection = NewProtection();
        await using var db = NewDb(userA);
        var entry = SeedEntry(db, userA);
        await db.SaveChangesAsync();
        var png = new byte[] { 5, 6, 7 };
        var pdf = new byte[] { 8, 9 };
        // img/doc 经真实导入路径建（cidMap + 落盘字节与真实布局一致）；
        // esc 的 file 值是手写逃逸路径，不含 asset id，随 AttachAsset 的 meta 一次性带入。
        var opts = Options.Create(new MailboxOptions { AttachmentRootDir = root }).Value;
        var importMsg = new MailboxMessage("m-scope", null, "sender@example.test", null,
            ["me@example.test"], [], "Subject", DateTimeOffset.UtcNow,
            "plain body", "<p>hi</p>", RawOf("fake raw"),
            [
                new MailboxAttachment("pic.png", "image/png", png.Length, "att-pic", png, "<img@example>"),
                new MailboxAttachment("doc.pdf", "application/pdf", pdf.Length, "att-doc", pdf, "<doc@example>"),
            ],
            null);
        var assetId = await MailboxImportHelper.ImportMessageAsync(
            db, provider, "access", opts, userA, entry, importMsg, "rule", CancellationToken.None);
        assetId.Should().NotBeNull();
        var escAsset = SeedMailAsset(db, entry,
            """{"channel":"email","cidMap":{"esc@example":{"file":"../../escape.png","contentType":"image/png"}}}""");
        await db.SaveChangesAsync();
        var htmlHandler = NewHtmlHandler(db, provider, protection, root);
        var inline = NewInlineHandler(db, root);
        var otherHtml = await htmlHandler.Handle(new GetMailHtmlQuery(userB, assetId!.Value, false),
            CancellationToken.None);
        otherHtml.IsSuccess.Should().BeFalse();
        otherHtml.Error.Type.Should().Be(ErrorType.NotFound);
        var otherInline = await inline.Handle(new GetMailInlineImageQuery(userB, assetId.Value, "img@example"),
            CancellationToken.None);
        otherInline.IsSuccess.Should().BeFalse();
        otherInline.Error.Type.Should().Be(ErrorType.NotFound);
        var unknown = await inline.Handle(new GetMailInlineImageQuery(userA, assetId.Value, "nope@example"),
            CancellationToken.None);
        unknown.IsSuccess.Should().BeFalse();
        var nonImage = await inline.Handle(new GetMailInlineImageQuery(userA, assetId.Value, "doc@example"),
            CancellationToken.None);
        nonImage.IsSuccess.Should().BeFalse();
        nonImage.Error.Type.Should().Be(ErrorType.NotFound);
        var escaping = await inline.Handle(new GetMailInlineImageQuery(userA, escAsset.Id, "esc@example"),
            CancellationToken.None);
        escaping.IsSuccess.Should().BeFalse();
        escaping.Error.Type.Should().Be(ErrorType.NotFound);
        var ok = await inline.Handle(new GetMailInlineImageQuery(userA, assetId.Value, "img@example"),
            CancellationToken.None);
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Bytes.Should().Equal(png);
        ok.Value.ContentType.Should().Be("image/png");
    }

    /// <summary>测试 12b：缺失文件回占位图 200（a），且 check-3/check-4 顺序在回落之前（b/c）。</summary>
    [Fact]
    public async Task Inline_MissingFile_Placeholder200_OrderChecksFirst()
    {
        if (!_available) return;
        var userId = NewUser();
        var root = NewTempRoot();
        await using var db = NewDb(userId);
        var entry = SeedEntry(db, userId);
        // 缺失文件三例都不落盘：file 值指向不存在的固定路径即可（解析只验 containment，
        // 不验路径里的 id 是否等于资产 id；esc 例的逃逸值与 id 无关）。
        var asset = SeedMailAsset(db, entry,
            """{"channel":"email","cidMap":{"gone@example":{"file":"mail-attachments/00000000-0000-0000-0000-000000000000/attachments/gone.png","contentType":"image/png"},"docgone@example":{"file":"mail-attachments/00000000-0000-0000-0000-000000000000/attachments/gone.pdf","contentType":"application/pdf"},"escgone@example":{"file":"../../gone.png","contentType":"image/png"}}}""");
        await db.SaveChangesAsync();
        var inline = NewInlineHandler(db, root);
        var gone = await inline.Handle(new GetMailInlineImageQuery(userId, asset.Id, "gone@example"),
            CancellationToken.None);
        gone.IsSuccess.Should().BeTrue();
        gone.Value.Bytes.Should().Equal(MailHtmlSanitizer.PlaceholderBytes);
        gone.Value.ContentType.Should().Be("image/png");
        var docGone = await inline.Handle(
            new GetMailInlineImageQuery(userId, asset.Id, "docgone@example"), CancellationToken.None);
        docGone.IsSuccess.Should().BeFalse();
        docGone.Error.Type.Should().Be(ErrorType.NotFound);
        var escGone = await inline.Handle(
            new GetMailInlineImageQuery(userId, asset.Id, "escgone@example"), CancellationToken.None);
        escGone.IsSuccess.Should().BeFalse();
        escGone.Error.Type.Should().Be(ErrorType.NotFound);
    }

    /// <summary>测试 12c（端点头补充，计划 §6 头字段）：控制器为 /html 置 CSP sandbox 与
    /// X-Images-Blocked，为 /inline/ 置 inline/nosniff/private,no-store。</summary>
    [Fact]
    public async Task Controller_SetsSecurityHeaders()
    {
        var userId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var sender = new FakeSender(
            Result.Success(new MailHtmlContent("<p>hi</p>", ImagesBlocked: true)),
            Result.Success(new MailInlineImage([1, 2, 3], "image/png")));
        var controller = NewController(userId, sender);

        var htmlResult = await controller.GetMailHtml(assetId, null, CancellationToken.None);
        htmlResult.Should().NotBeNull();
        controller.HttpContext.Response.Headers["Content-Security-Policy"].ToString()
            .Should().Be("sandbox");
        controller.HttpContext.Response.Headers["X-Images-Blocked"].ToString()
            .Should().Be("1");

        var controller2 = NewController(userId, sender);
        var inlineResult = await controller2.GetMailInlineImage(
            assetId, "img@example", CancellationToken.None);
        inlineResult.Should().NotBeNull();
        var headers = controller2.HttpContext.Response.Headers;
        headers["Content-Disposition"].ToString().Should().Be("inline");
        headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        headers["Cache-Control"].ToString().Should().Be("private, no-store");
    }

    private sealed class FakeSender(
        Result<MailHtmlContent> html, Result<MailInlineImage> image) : ISender
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            object value = request switch
            {
                GetMailHtmlQuery => html,
                GetMailInlineImageQuery => image,
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)value);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken ct = default)
            where TRequest : IRequest => Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken ct = default)
            => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(
            IStreamRequest<TResponse> request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope();

        private sealed class StubScope : IServiceScope
        {
            public IServiceProvider ServiceProvider =>
                new ServiceCollection().BuildServiceProvider();
            public void Dispose() { }
        }
    }

    private static MailboxController NewController(Guid userId, ISender sender)
    {
        var currentUser = new FixedCurrentUser(userId);
        var controller = new MailboxController(sender, currentUser,
            Options.Create(new MailboxOptions()), new MailboxRefreshJobTracker(),
            new StubScopeFactory())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return controller;
    }
}
