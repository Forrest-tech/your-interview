using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票26(REQ-05):GetEffectiveResume 文本简历回落。
/// 契约:tailored 文件 &gt; 基础文件 &gt; 文本简历 &gt; none;文件优先口径不变;
/// 文本为空/空白视为无简历;回落只查当前用户自己的文本简历(票03 归属口径,他人的不回落)。
/// 走真实 JobsDbContext + 真实 Handler(无 DB 环境跳过,同 TrackerPreferencesTests 先例)。
/// </summary>
public sealed class EffectiveResumeFallbackTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    // 每例独立用户:文本简历一人一份,共用用户会让种子互相串扰。
    private readonly List<Guid> _users = [];
    private readonly List<Guid> _appIds = [];
    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }
    private JobsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Guid.Empty);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            var files = await _db.ResumeFiles.IgnoreQueryFilters()
                .Where(f => _users.Contains(f.UserId)).ToListAsync();
            _db.ResumeFiles.RemoveRange(files);
            var texts = await _db.Resumes.IgnoreQueryFilters()
                .Where(r => _users.Contains(r.UserId)).ToListAsync();
            _db.Resumes.RemoveRange(texts);
            var apps = await _db.Applications.IgnoreQueryFilters()
                .Where(a => _appIds.Contains(a.Id)).ToListAsync();
            _db.Applications.RemoveRange(apps);
            var companies = await _db.Companies.IgnoreQueryFilters()
                .Where(c => _users.Contains(c.UserId)).ToListAsync();
            _db.Companies.RemoveRange(companies);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }

    private static JobsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<JobsDbContext>().UseNpgsql(Cs).Options;
        return new JobsDbContext(opts, new TestCurrentUser(currentUser));
    }

    private async Task<JobApplication> SeedAppAsync(Guid owner)
    {
        await using var db = NewDb(owner);
        var company = new Company("T26-" + Guid.NewGuid().ToString("N")[..8], userId: owner);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var app = new JobApplication(company.Id, "Senior .NET Developer", userId: owner);
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        _appIds.Add(app.Id);
        return app;
    }

    private async Task SeedTextAsync(Guid owner, string content)
    {
        await using var db = NewDb(owner);
        db.Resumes.Add(new UserResume(owner, content));
        await db.SaveChangesAsync();
    }

    private async Task SeedFileAsync(Guid owner, Guid? applicationId, string textContent)
    {
        await using var db = NewDb(owner);
        db.ResumeFiles.Add(new ResumeFile(owner, "resume.pdf", "application/pdf",
            "%PDF-t26"u8.ToArray(), textContent, applicationId));
        await db.SaveChangesAsync();
    }

    private static async Task<EffectiveResumeDto> EffectiveAsync(Guid user, Guid appId)
    {
        await using var db = NewDb(user);
        var r = await new GetEffectiveResumeQueryHandler(db)
            .Handle(new GetEffectiveResumeQuery(user, appId), default);
        r.IsSuccess.Should().BeTrue();
        return r.Value;
    }

    // boundary: 仅文本简历(无任何文件)—— REQ-05 本体
    [Fact]
    public async Task TextOnly_FallsBackToTextKind_WithContent()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        await SeedTextAsync(owner, "TEXT RESUME BODY");

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("text");
        v.File.Should().BeNull();
        v.TextContent.Should().Be("TEXT RESUME BODY");
    }

    // boundary: 仅基础文件 —— 既有行为不变
    [Fact]
    public async Task BaseFileOnly_ReturnsBase()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        await SeedFileAsync(owner, null, "BASE FILE TEXT");

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("base");
        v.File!.TextContent.Should().Be("BASE FILE TEXT");
        v.TextContent.Should().BeNull();
    }

    // boundary: 文件 + 文本两者都有 —— 文件优先,口径不变
    [Fact]
    public async Task BaseFileAndText_FileWins()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        await SeedTextAsync(owner, "TEXT RESUME BODY");
        await SeedFileAsync(owner, null, "BASE FILE TEXT");

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("base");
        v.File!.TextContent.Should().Be("BASE FILE TEXT");
    }

    // boundary: tailored + 基础文件 + 文本三者都有 —— tailored 优先,口径不变
    [Fact]
    public async Task TailoredWinsOverBaseAndText()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        await SeedTextAsync(owner, "TEXT RESUME BODY");
        await SeedFileAsync(owner, null, "BASE FILE TEXT");
        await SeedFileAsync(owner, app.Id, "TAILORED TEXT");

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("tailored");
        v.File!.TextContent.Should().Be("TAILORED TEXT");
    }

    // boundary: 两者都无 —— none
    [Fact]
    public async Task Nothing_ReturnsNone()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("none");
        v.File.Should().BeNull();
        v.TextContent.Should().BeNull();
    }

    // boundary: 文本为空白字符串 —— 视为无简历(SaveResume 拒绝空白,但存量直写行须防)
    [Fact]
    public async Task WhitespaceText_ReturnsNone()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        await SeedTextAsync(owner, "   \n\t ");

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("none");
        v.TextContent.Should().BeNull();
    }

    // boundary: 文本为空字符串 —— 视为无简历
    [Fact]
    public async Task EmptyText_ReturnsNone()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        await SeedTextAsync(owner, string.Empty);

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("none");
    }

    // boundary: 他人简历不回落 —— 别人有文本简历,当前用户查自己的投递仍 none(票03 归属口径)
    [Fact]
    public async Task OtherUsersText_DoesNotFallBack()
    {
        if (!_available) return;
        var owner = NewUser(); var other = NewUser();
        var otherApp = await SeedAppAsync(other);
        await SeedTextAsync(owner, "OWNER TEXT RESUME");

        var v = await EffectiveAsync(other, otherApp.Id);
        v.Kind.Should().Be("none");
        v.TextContent.Should().BeNull();
    }

    // boundary: Unicode/特殊字符文本原样回落(不截断不转义)
    [Fact]
    public async Task TextWithUnicodeAndSymbols_ReturnedVerbatim()
    {
        if (!_available) return;
        var owner = NewUser();
        var app = await SeedAppAsync(owner);
        const string body = "简历 — C#/.NET « senior » \n 第二行\t<>&\"'";
        await SeedTextAsync(owner, body);

        var v = await EffectiveAsync(owner, app.Id);
        v.Kind.Should().Be("text");
        v.TextContent.Should().Be(body);
    }
}
