using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票111 段3 §7(Jobs 删除/替换 If-Match 甲案):
/// J03 删公司 / J10 删投递 —— 命令带读时 Version 钉版,陈旧 → DbUpdateConcurrencyException
/// (全局映射 409)且数据完好;新鲜 → 成功;不带 → 兼容成功。
/// J12/J13 简历文件替换 —— ExpectedVersion 写前比对锚行(J12=简历文本行 RowVersion,
/// J13=申请行 Version),不符 → Result 冲突(concurrency_conflict)且文件不落;其余同上。
/// DB 门控同 Ticket101 先例。
/// </summary>
public sealed class Ticket111DeleteReplaceTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb(Guid.NewGuid());
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb(_users[0]);
        var files = await db.ResumeFiles.Where(f => _users.Contains(f.UserId)).ToListAsync();
        db.ResumeFiles.RemoveRange(files);
        var apps = await db.Applications.IgnoreQueryFilters().Where(a => _users.Contains(a.UserId)).ToListAsync();
        db.Applications.RemoveRange(apps);
        var comps = await db.Companies.IgnoreQueryFilters().Where(c => _users.Contains(c.UserId)).ToListAsync();
        db.Companies.RemoveRange(comps);
        var resumes = await db.Resumes.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)).ToListAsync();
        db.Resumes.RemoveRange(resumes);
        await db.SaveChangesAsync();
    }

    private static JobsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<JobsDbContext>().UseNpgsql(Cs).Options;
        return new JobsDbContext(opts, new TestCurrentUser(currentUser));
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }
    private static string Uq(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> CreateCompanyAsync(Guid user, string name)
    {
        await using var db = NewDb(user);
        var r = await new CreateCompanyCommandHandler(db).Handle(
            new CreateCompanyCommand(user, name, "https://orig.example", "Ind", "Loc", null, "notes"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<CompanyDto> GetCompanyAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetCompanyQueryHandler(db).Handle(new GetCompanyQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<Guid> CreateApplicationAsync(Guid user, Guid companyId)
    {
        await using var db = NewDb(user);
        var r = await new CreateApplicationCommandHandler(db).Handle(
            new CreateApplicationCommand(user, companyId, "Role", "Loc", null, "100k",
                "Remote", "LinkedIn", "summary", "Medium"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<ApplicationDto> GetApplicationAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetApplicationQueryHandler(db).Handle(new GetApplicationQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    // ---------- J03 删公司 ----------

    [Fact]
    public async Task DeleteCompany_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateCompanyAsync(user, Uq("T111Co"));
        var v = (await GetCompanyAsync(user, id)).Version;

        // A 先改公司(版本推进)
        await using (var dbA = NewDb(user))
            (await new UpdateCompanyCommandHandler(dbA).Handle(
                new UpdateCompanyCommand(user, id, Uq("T111Co"), "https://a.example", "Ind", "Loc", null, "notes", "Unknown", null, false, v), default))
                .IsSuccess.Should().BeTrue();

        // B 持旧版本删除 → 冲突,公司完好
        await using var dbB = NewDb(user);
        var act = () => new DeleteCompanyCommandHandler(dbB).Handle(new DeleteCompanyCommand(user, id, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetCompanyAsync(user, id)).Version.Should().NotBe(v);

        // 新鲜版本删除 → 成功
        var v2 = (await GetCompanyAsync(user, id)).Version;
        await using (var dbF = NewDb(user))
            (await new DeleteCompanyCommandHandler(dbF).Handle(new DeleteCompanyCommand(user, id, v2), default))
                .IsSuccess.Should().BeTrue();

        // 不带版本 → 兼容成功
        var id2 = await CreateCompanyAsync(user, Uq("T111Co2"));
        await using (var dbN = NewDb(user))
            (await new DeleteCompanyCommandHandler(dbN).Handle(new DeleteCompanyCommand(user, id2), default))
                .IsSuccess.Should().BeTrue();
    }

    // ---------- J10 删投递 ----------

    [Fact]
    public async Task DeleteApplication_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T111CoA"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;

        await using (var dbA = NewDb(user))
            (await new UpdateApplicationCommandHandler(dbA).Handle(
                new UpdateApplicationCommand(user, id, "Role A", "Loc", null, "200k", "Remote", "LinkedIn", "summary", null, "Medium", null, Version: v), default))
                .IsSuccess.Should().BeTrue();

        await using var dbB = NewDb(user);
        var act = () => new DeleteApplicationCommandHandler(dbB).Handle(new DeleteApplicationCommand(user, id, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetApplicationAsync(user, id)).Role.Should().Be("Role A");

        var v2 = (await GetApplicationAsync(user, id)).Version;
        await using (var dbF = NewDb(user))
            (await new DeleteApplicationCommandHandler(dbF).Handle(new DeleteApplicationCommand(user, id, v2), default))
                .IsSuccess.Should().BeTrue();

        var id2 = await CreateApplicationAsync(user, cid);
        await using (var dbN = NewDb(user))
            (await new DeleteApplicationCommandHandler(dbN).Handle(new DeleteApplicationCommand(user, id2), default))
                .IsSuccess.Should().BeTrue();
    }

    // ---------- J12/J13 简历文件替换 ----------

    private static SaveResumeFileCommand FileCmd(Guid user, Guid? appId, string name, uint? expected = null) =>
        new(user, appId, name, "application/pdf", new byte[] { 1, 2, 3 }, "text", expected);

    private async Task<string?> FileNameAsync(Guid user, Guid? appId)
    {
        await using var db = NewDb(user);
        var r = await new GetResumeFileQueryHandler(db).Handle(new GetResumeFileQuery(user, appId), default);
        return r.Value?.FileName;
    }

    [Fact] // J12:锚 = 简历文本行 RowVersion
    public async Task ReplaceBaseResumeFile_StaleRowVersion_Conflict_FileIntact()
    {
        if (!_available) return;
        var user = NewUser();

        uint rv1;
        await using (var db = NewDb(user))
        {
            (await new SaveResumeCommandHandler(db).Handle(new SaveResumeCommand(user, "resume v1"), default))
                .IsSuccess.Should().BeTrue();
            var g = await new GetResumeQueryHandler(db).Handle(new GetResumeQuery(user), default);
            rv1 = g.Value.RowVersion;
        }

        // A 持 rv1 上传成功
        await using (var db = NewDb(user))
            (await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, null, "a.pdf", rv1), default))
                .IsSuccess.Should().BeTrue();

        // A 改文本(锚行版本推进,模拟另一页签更新简历)
        uint rv2;
        await using (var db = NewDb(user))
        {
            (await new SaveResumeCommandHandler(db).Handle(new SaveResumeCommand(user, "resume v2"), default))
                .IsSuccess.Should().BeTrue();
            var g = await new GetResumeQueryHandler(db).Handle(new GetResumeQuery(user), default);
            rv2 = g.Value.RowVersion;
        }
        rv2.Should().NotBe(rv1);

        // B 持旧 rv1 上传 → 冲突,文件仍是 A 的
        await using (var db = NewDb(user))
        {
            var r = await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, null, "b.pdf", rv1), default);
            r.IsSuccess.Should().BeFalse();
            r.Error.Code.Should().Be("concurrency_conflict");
        }
        (await FileNameAsync(user, null)).Should().Be("a.pdf");

        // 新鲜 rv2 → 成功;不带版本 → 兼容成功
        await using (var db = NewDb(user))
            (await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, null, "c.pdf", rv2), default))
                .IsSuccess.Should().BeTrue();
        await using (var db = NewDb(user))
            (await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, null, "d.pdf"), default))
                .IsSuccess.Should().BeTrue();
        (await FileNameAsync(user, null)).Should().Be("d.pdf");
    }

    [Fact] // J13:锚 = 申请行 Version
    public async Task ReplaceApplicationResumeFile_StaleAppVersion_Conflict_FileIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T111CoJ"));
        var appId = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, appId)).Version;

        await using (var db = NewDb(user))
            (await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, appId, "tailored-a.pdf", v), default))
                .IsSuccess.Should().BeTrue();

        // A 改投递(申请行版本推进)
        await using (var dbA = NewDb(user))
            (await new UpdateApplicationCommandHandler(dbA).Handle(
                new UpdateApplicationCommand(user, appId, "Role A", "Loc", null, "200k", "Remote", "LinkedIn", "summary", null, "Medium", null, Version: v), default))
                .IsSuccess.Should().BeTrue();

        await using (var db = NewDb(user))
        {
            var r = await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, appId, "tailored-b.pdf", v), default);
            r.IsSuccess.Should().BeFalse();
            r.Error.Code.Should().Be("concurrency_conflict");
        }
        (await FileNameAsync(user, appId)).Should().Be("tailored-a.pdf");

        var v2 = (await GetApplicationAsync(user, appId)).Version;
        await using (var db = NewDb(user))
            (await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, appId, "tailored-c.pdf", v2), default))
                .IsSuccess.Should().BeTrue();
        await using (var db = NewDb(user))
            (await new SaveResumeFileCommandHandler(db).Handle(FileCmd(user, appId, "tailored-d.pdf"), default))
                .IsSuccess.Should().BeTrue();
    }
}
