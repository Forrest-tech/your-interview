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
/// 票 245：邮箱 OAuth 凭据卡 —— 解析唯一口径 + 保存/Clear/写-only 语义测试。
/// 纯本地：EF InMemory + 临时 DataProtection provider，不依赖 Postgres 与网络。
/// 夹具全合成（虚构 client id/secret/redirect URI，不含任何真实凭据）。
/// </summary>
public sealed class MailboxOAuthTests
{
    private static InterviewsDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseInMemoryDatabase("t245-" + Guid.NewGuid())
            .Options;
        return new InterviewsDbContext(options);
    }

    private static MailboxProtection NewProtection() =>
        new(DataProtectionProvider.Create("test-245"));

    private static IOptions<MailboxOptions> EnvOptions(
        string googleId = "env-google-id", string googleSecret = "env-google-secret",
        string googleUri = "https://env.example/google-callback",
        string msId = "env-ms-id", string msSecret = "env-ms-secret",
        string msUri = "https://env.example/ms-callback") =>
        Options.Create(new MailboxOptions
        {
            Google = new MailboxOptions.GoogleOptions
                { ClientId = googleId, ClientSecret = googleSecret, RedirectUri = googleUri },
            Microsoft = new MailboxOptions.MicrosoftOptions
                { ClientId = msId, ClientSecret = msSecret, RedirectUri = msUri }
        });

    // ---- 解析：env 兜底 ----

    [Fact]
    public async Task Resolve_NoRow_FallsBackToEnvironment()
    {
        using var db = NewDb();
        var resolver = new MailboxOAuthResolver(db, EnvOptions(), NewProtection());

        var r = await resolver.ResolveAsync(MailboxProvider.Gmail, CancellationToken.None);

        r.IsSuccess.Should().BeTrue();
        r.Value.Source.Should().Be(MailboxOAuthSource.Environment);
        r.Value.ClientId.Should().Be("env-google-id");
        r.Value.ClientSecret.Should().Be("env-google-secret");
        r.Value.RedirectUri.Should().Be("https://env.example/google-callback");
    }

    // ---- 解析：DB 整组优先、绝不混用 ----

    [Fact]
    public async Task Resolve_SavedRowWinsAtomically_NeverMixedWithEnv()
    {
        using var db = NewDb();
        var protection = NewProtection();
        var save = new SaveMailboxOAuthSettingsCommandHandler(db, protection);
        var saved = await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", "db-id", "db-secret", "https://db.example/callback"), CancellationToken.None);
        saved.IsSuccess.Should().BeTrue();

        var resolver = new MailboxOAuthResolver(db, EnvOptions(), protection);
        var r = await resolver.ResolveAsync(MailboxProvider.Gmail, CancellationToken.None);

        r.IsSuccess.Should().BeTrue();
        r.Value.Source.Should().Be(MailboxOAuthSource.Saved);
        r.Value.ClientId.Should().Be("db-id");
        r.Value.ClientSecret.Should().Be("db-secret");
        r.Value.RedirectUri.Should().Be("https://db.example/callback");
    }

    [Fact]
    public async Task Resolve_UndecryptableSavedSecret_FailsWithoutEnvFallback()
    {
        using var db = NewDb();
        db.MailboxOAuthCredentials.Add(new MailboxOAuthCredential(
            MailboxProvider.Graph, "db-id", "garbage-not-a-protected-payload", "https://db.example/cb"));
        await db.SaveChangesAsync();

        var resolver = new MailboxOAuthResolver(db, EnvOptions(), NewProtection());
        var r = await resolver.ResolveAsync(MailboxProvider.Graph, CancellationToken.None);

        r.IsSuccess.Should().BeFalse();
        r.Error.Code.Should().Be("Mailbox.OAuthSecretUnreadable");
    }

    [Fact]
    public async Task Resolve_Imap_Rejected()
    {
        using var db = NewDb();
        var resolver = new MailboxOAuthResolver(db, EnvOptions(), NewProtection());

        var r = await resolver.ResolveAsync(MailboxProvider.Imap, CancellationToken.None);

        r.IsSuccess.Should().BeFalse();
    }

    // ---- 保存：首次必填、留空沿用 ----

    [Fact]
    public async Task Save_FirstSave_RequiresSecret()
    {
        using var db = NewDb();
        var save = new SaveMailboxOAuthSettingsCommandHandler(db, NewProtection());

        var r = await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Graph", "id", null, "https://x.example/cb"), CancellationToken.None);

        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Save_BlankSecret_KeepsExistingCiphertext()
    {
        using var db = NewDb();
        var protection = NewProtection();
        var save = new SaveMailboxOAuthSettingsCommandHandler(db, protection);
        await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", "id-1", "secret-1", "https://a.example/cb"), CancellationToken.None);
        var before = (await db.MailboxOAuthCredentials.FirstAsync()).EncryptedClientSecret;

        var r = await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", "id-2", null, "https://b.example/cb"), CancellationToken.None);

        r.IsSuccess.Should().BeTrue();
        var row = await db.MailboxOAuthCredentials.FirstAsync();
        row.ClientId.Should().Be("id-2");
        row.RedirectUri.Should().Be("https://b.example/cb");
        row.EncryptedClientSecret.Should().Be(before);
        row.EncryptedClientSecret.Should().NotBe("secret-1");
    }

    [Fact]
    public async Task Save_Validation_RejectsBlankIdOrUri_AndBadProvider()
    {
        using var db = NewDb();
        var save = new SaveMailboxOAuthSettingsCommandHandler(db, NewProtection());

        (await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", " ", "s", "https://x.example/cb"), CancellationToken.None))
            .IsSuccess.Should().BeFalse();
        (await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", "id", "s", " "), CancellationToken.None))
            .IsSuccess.Should().BeFalse();
        (await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Nope", "id", "s", "https://x.example/cb"), CancellationToken.None))
            .IsSuccess.Should().BeFalse();
    }

    // ---- Clear：回 env ----

    [Fact]
    public async Task Clear_RemovesRow_ResolverFallsBackToEnv()
    {
        using var db = NewDb();
        var protection = NewProtection();
        var save = new SaveMailboxOAuthSettingsCommandHandler(db, protection);
        await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Graph", "db-id", "db-secret", "https://db.example/cb"), CancellationToken.None);
        var clear = new ClearMailboxOAuthSettingsCommandHandler(db);
        var cleared = await clear.Handle(new ClearMailboxOAuthSettingsCommand("Graph"),
            CancellationToken.None);
        cleared.IsSuccess.Should().BeTrue();

        var resolver = new MailboxOAuthResolver(db, EnvOptions(), protection);
        var r = await resolver.ResolveAsync(MailboxProvider.Graph, CancellationToken.None);

        r.IsSuccess.Should().BeTrue();
        r.Value.Source.Should().Be(MailboxOAuthSource.Environment);
        r.Value.ClientId.Should().Be("env-ms-id");
    }

    // ---- 读：写-only（Secret 永不出现在 DTO 里；DB 存的是密文）----

    [Fact]
    public async Task Get_NeverExposesSecret_ReportsSavedState()
    {
        using var db = NewDb();
        var protection = NewProtection();
        var save = new SaveMailboxOAuthSettingsCommandHandler(db, protection);
        await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", "db-id", "db-secret", "https://db.example/cb"), CancellationToken.None);
        var get = new GetMailboxOAuthSettingsQueryHandler(db,
            new MailboxOAuthResolver(db, EnvOptions(), protection));

        var list = await get.Handle(new GetMailboxOAuthSettingsQuery(), CancellationToken.None);

        list.IsSuccess.Should().BeTrue();
        var gmail = list.Value.First(d => d.Provider == "Gmail");
        gmail.Source.Should().Be("saved");
        gmail.ClientId.Should().Be("db-id");
        gmail.RedirectUri.Should().Be("https://db.example/cb");
        gmail.SecretSaved.Should().BeTrue();
        gmail.SecretUnreadable.Should().BeFalse();
        // DTO 类型本身就没有 Secret 字段：编译期保证 + 密文不等于明文。
        var row = await db.MailboxOAuthCredentials.FirstAsync(c => c.Provider == MailboxProvider.Gmail);
        row.EncryptedClientSecret.Should().NotBe("db-secret");
        var graph = list.Value.First(d => d.Provider == "Graph");
        graph.Source.Should().Be("environment");
        graph.SecretSaved.Should().BeFalse();
    }

    [Fact]
    public async Task Get_UndecryptableRow_ReportsUnreadable_AndClearStillWorks()
    {
        using var db = NewDb();
        db.MailboxOAuthCredentials.Add(new MailboxOAuthCredential(
            MailboxProvider.Gmail, "db-id", "garbage", "https://db.example/cb"));
        await db.SaveChangesAsync();
        var protection = NewProtection();
        var get = new GetMailboxOAuthSettingsQueryHandler(db,
            new MailboxOAuthResolver(db, EnvOptions(), protection));

        var list = await get.Handle(new GetMailboxOAuthSettingsQuery(), CancellationToken.None);

        list.IsSuccess.Should().BeTrue();
        var gmail = list.Value.First(d => d.Provider == "Gmail");
        gmail.SecretUnreadable.Should().BeTrue();
        var clear = new ClearMailboxOAuthSettingsCommandHandler(db);
        (await clear.Handle(new ClearMailboxOAuthSettingsCommand("Gmail"),
            CancellationToken.None)).IsSuccess.Should().BeTrue();
    }

    // ---- Connect：解析口径直通（存了啥就发啥；没配就报 NotConfigured）----

    [Fact]
    public async Task Connect_UsesResolvedValues_AndFailsNotConfiguredWhenEmpty()
    {
        using var db = NewDb();
        var protection = NewProtection();
        var factory = new FakeMailboxProviderFactory();
        var connect = new ConnectMailboxCommandHandler(factory, protection,
            new MailboxOAuthResolver(db, EnvOptions(), protection));

        var emptyEnv = new ConnectMailboxCommandHandler(factory, protection,
            new MailboxOAuthResolver(db,
                Options.Create(new MailboxOptions
                {
                    Google = new MailboxOptions.GoogleOptions(),
                    Microsoft = new MailboxOptions.MicrosoftOptions()
                }), protection));
        var missing = await emptyEnv.Handle(new ConnectMailboxCommand(Guid.NewGuid(), "Gmail"),
            CancellationToken.None);
        missing.IsSuccess.Should().BeFalse();
        missing.Error.Code.Should().Be("Mailbox.NotConfigured");

        var save = new SaveMailboxOAuthSettingsCommandHandler(db, protection);
        await save.Handle(new SaveMailboxOAuthSettingsCommand(
            "Gmail", "saved-id", "saved-secret", "https://saved.example/cb"), CancellationToken.None);
        var ok = await connect.Handle(new ConnectMailboxCommand(Guid.NewGuid(), "Gmail"),
            CancellationToken.None);
        ok.IsSuccess.Should().BeTrue();
        ok.Value.Should().Contain("saved-id");
        ok.Value.Should().Contain("https://saved.example/cb");
        ok.Value.Should().NotContain("saved-secret");
    }

    private sealed class FakeMailboxProviderFactory : IMailboxProviderFactory
    {
        public IMailboxProvider Get(MailboxProvider kind) => new FakeProvider(kind);
    }

    private sealed class FakeProvider(MailboxProvider kind) : IMailboxProvider
    {
        public MailboxProvider Kind => kind;

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) =>
            $"https://auth.example/authorize?client_id={clientId}&redirect_uri={redirectUri}&state={state}";

        public Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code,
            string redirectUri, CancellationToken ct) => throw new NotImplementedException();

        public Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret,
            string refreshToken, CancellationToken ct) => throw new NotImplementedException();

        public Task RevokeAsync(string clientId, string clientSecret, string refreshToken,
            CancellationToken ct) => Task.CompletedTask;

        public Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor,
            DateTimeOffset sinceUtc, CancellationToken ct) => throw new NotImplementedException();

        public Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId,
            CancellationToken ct) => throw new NotImplementedException();

        public Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId,
            string providerAttachmentId, CancellationToken ct) => throw new NotImplementedException();

        public Task<string?> GetAccountEmailAsync(string accessToken, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MailboxAttachment>> ListAttachmentsAsync(string accessToken,
            string providerMessageId, CancellationToken ct) => throw new NotImplementedException();
    }
}
