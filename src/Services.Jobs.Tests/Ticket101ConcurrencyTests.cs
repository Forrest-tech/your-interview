using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票101 §7:Jobs 详情编辑的乐观并发(读时 xmin 版本回传,口径同票70)。
/// 定性背景(第一段活体):公司/投递/问答/沟通等详情 DTO 均不回传版本,
/// Handler 现查现改总拿最新 xmin,串行双页签 B 的旧表单必然静默覆盖 A。
/// 修复:DTO 暴露 Version → 更新命令回传 → Handler 钉 xmin OriginalValue,
/// 过期即 DbUpdateConcurrencyException(全局映射 409)。Version=null 不钉(兼容)。
/// 两「页签」= 两次独立 Get 同读一版本 + 各自新 DbContext Handler,与活体同构。
/// DB 门控同仓内先例:连不上库跳过;每例独立随机 UserId,Dispose 清本例数据。
/// </summary>
public sealed class Ticket101ConcurrencyTests : IAsyncLifetime
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
        var apps = await db.Applications.IgnoreQueryFilters().Where(a => _users.Contains(a.UserId)).ToListAsync();
        db.Applications.RemoveRange(apps);
        var comps = await db.Companies.IgnoreQueryFilters().Where(c => _users.Contains(c.UserId)).ToListAsync();
        db.Companies.RemoveRange(comps);
        var tpls = await db.AnswerTemplates.IgnoreQueryFilters().Where(t => _users.Contains(t.UserId)).ToListAsync();
        db.AnswerTemplates.RemoveRange(tpls);
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
            new CreateCompanyCommand(user, name, "https://orig.example", "OrigInd", "OrigLoc", null, "orig notes"), default);
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
            new CreateApplicationCommand(user, companyId, "Orig Role", "OrigLoc", null, "100k",
                "Remote", "LinkedIn", "orig summary", "Medium"), default);
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

    private static UpdateCompanyCommand UpdCompany(Guid user, Guid id, uint? version, string website = "https://orig.example", string industry = "OrigInd")
        => new(user, id, "placeholder", website, industry, "OrigLoc", null, "orig notes", "Unknown", null, false, version);

    [Fact] // 核心:公司全量更新 —— 两页签同读一版本,A 先存,B 持旧版本应冲突,A 数据完好
    public async Task Company_DualTab_StaleVersion_Throws_FirstSaveIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var name = Uq("T101Co");
        var id = await CreateCompanyAsync(user, name);
        var tabA = await GetCompanyAsync(user, id);
        var tabB = await GetCompanyAsync(user, id);
        tabA.Version.Should().BeGreaterThan(0u);
        tabB.Version.Should().Be(tabA.Version);

        await using (var dbA = NewDb(user))
        {
            var r = await new UpdateCompanyCommandHandler(dbA).Handle(
                UpdCompany(user, id, tabA.Version, website: "https://a.example") with { Name = name }, default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }

        await using var dbB = NewDb(user);
        var act = () => new UpdateCompanyCommandHandler(dbB).Handle(
            UpdCompany(user, id, tabB.Version, industry: "B-Industry") with { Name = name }, default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var final = await GetCompanyAsync(user, id);
        final.Website.Should().Be("https://a.example");
        final.Industry.Should().Be("OrigInd");
    }

    [Fact] // 公司情报(独立端点同行):A 写 profile,B 持旧版本写 sources 应冲突
    public async Task CompanyProfile_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateCompanyAsync(user, Uq("T101CoP"));
        var v = (await GetCompanyAsync(user, id)).Version;

        await using (var dbA = NewDb(user))
        {
            var r = await new SetCompanyProfileCommandHandler(dbA).Handle(
                new SetCompanyProfileCommand(user, id, "PROFILE-A", "src-orig", v), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using var dbB = NewDb(user);
        var act = () => new SetCompanyProfileCommandHandler(dbB).Handle(
            new SetCompanyProfileCommand(user, id, null, "src-B", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetCompanyAsync(user, id)).Profile.Should().Be("PROFILE-A");
    }

    private static UpdateApplicationCommand UpdApp(Guid user, Guid id, uint? version, string salary = "100k", string notes = "orig notes")
        => new(user, id, "Orig Role", "OrigLoc", null, salary, "Remote", "LinkedIn",
               "orig summary", notes, "Medium", null, Version: version);

    [Fact] // 核心:投递全量更新 —— B 持旧版本(旧 salary)保存应冲突,A 的 salary 完好
    public async Task Application_DualTab_StaleVersion_Throws_FirstSaveIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoA"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;
        v.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
        {
            var r = await new UpdateApplicationCommandHandler(dbA).Handle(UpdApp(user, id, v, salary: "200k-A"), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using var dbB = NewDb(user);
        var act = () => new UpdateApplicationCommandHandler(dbB).Handle(
            UpdApp(user, id, v, notes: "B notes"), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var final = await GetApplicationAsync(user, id);
        final.Salary.Should().Be("200k-A");
        final.Version.Should().NotBe(v);
    }

    [Fact] // 子字段端点同行:JD —— A 写全文,B 持旧版本只改出处应冲突(行级令牌,不做字段合并)
    public async Task ApplicationJd_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoJ"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;

        await using (var dbA = NewDb(user))
        {
            var r = await new SetJdContentCommandHandler(dbA).Handle(
                new SetJdContentCommand(user, id, "JD-A-TEXT", "http://orig-src", v), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using var dbB = NewDb(user);
        var act = () => new SetJdContentCommandHandler(dbB).Handle(
            new SetJdContentCommand(user, id, null, "http://b-src", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetApplicationAsync(user, id)).JdText.Should().Be("JD-A-TEXT");
    }

    [Fact] // 子字段端点同行:分析 —— B 持旧版本写关键词应冲突,A 的分数完好
    public async Task ApplicationAnalysis_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoAn"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;

        await using (var dbA = NewDb(user))
        {
            var r = await new SetApplicationAnalysisCommandHandler(dbA).Handle(
                new SetApplicationAnalysisCommand(user, id, 88, "orig-rate", "orig-kw", v), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using var dbB = NewDb(user);
        var act = () => new SetApplicationAnalysisCommandHandler(dbB).Handle(
            new SetApplicationAnalysisCommand(user, id, null, "orig-rate", "B-kw", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetApplicationAsync(user, id)).ResumeScore.Should().Be(88);
    }

    [Fact] // boundary: Version=null 旧调用方不钉版 —— 后存仍成功(兼容,行为同票101前)
    public async Task NullVersion_SkipsCheck_SecondSaveSucceeds()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoN"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;

        await using (var dbA = NewDb(user))
            (await new UpdateApplicationCommandHandler(dbA).Handle(UpdApp(user, id, v, salary: "200k-A"), default))
                .IsSuccess.Should().BeTrue();
        await using (var dbB = NewDb(user))
            (await new UpdateApplicationCommandHandler(dbB).Handle(UpdApp(user, id, null, salary: "legacy"), default))
                .IsSuccess.Should().BeTrue();
        (await GetApplicationAsync(user, id)).Salary.Should().Be("legacy");
    }

    [Fact] // 不变项:单页签拿新鲜版本连续编辑全成功,版本逐次推进
    public async Task SingleTab_FreshVersion_SequentialEditsSucceed()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoS"));
        var id = await CreateApplicationAsync(user, cid);

        var v1 = (await GetApplicationAsync(user, id)).Version;
        await using (var db = NewDb(user))
            (await new UpdateApplicationCommandHandler(db).Handle(UpdApp(user, id, v1, salary: "s1"), default))
                .IsSuccess.Should().BeTrue();
        var v2 = (await GetApplicationAsync(user, id)).Version;
        v2.Should().NotBe(v1);
        await using (var db = NewDb(user))
            (await new UpdateApplicationCommandHandler(db).Handle(UpdApp(user, id, v2, salary: "s2"), default))
                .IsSuccess.Should().BeTrue();
        (await GetApplicationAsync(user, id)).Salary.Should().Be("s2");
    }

    [Fact] // boundary:伪造版本同样冲突
    public async Task BogusVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoB"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;

        await using var db = NewDb(user);
        var act = () => new UpdateApplicationCommandHandler(db).Handle(
            UpdApp(user, id, v + 999_999u, salary: "bogus"), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // boundary:软删投递持版本更新走 NotFound,不抛并发异常
    public async Task SoftDeletedApplication_Update_ReturnsNotFound()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoD"));
        var id = await CreateApplicationAsync(user, cid);
        var v = (await GetApplicationAsync(user, id)).Version;

        await using (var db = NewDb(user))
            (await new DeleteApplicationCommandHandler(db).Handle(new DeleteApplicationCommand(user, id), default))
                .IsSuccess.Should().BeTrue();

        await using var db2 = NewDb(user);
        var r = await new UpdateApplicationCommandHandler(db2).Handle(UpdApp(user, id, v, salary: "zombie"), default);
        r.IsSuccess.Should().BeFalse();
        r.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact] // 轮次(子实体,票101 新增 xmin):DTO 带轮次 Version,B 持旧轮次版本更新应冲突
    public async Task Round_DualTab_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoR"));
        var id = await CreateApplicationAsync(user, cid);

        Guid roundId;
        await using (var db = NewDb(user))
        {
            var r = await new AddInterviewRoundCommandHandler(db).Handle(
                new AddInterviewRoundCommand(user, id, "Phone", null, "OrigInt", "Phone", "orig round notes"), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            roundId = r.Value;
        }

        var dto = await GetApplicationAsync(user, id);
        var round = dto.Rounds.Single(x => x.Id == roundId);
        round.Version.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
        {
            var r = await new UpdateInterviewRoundCommandHandler(dbA).Handle(
                new UpdateInterviewRoundCommand(user, id, roundId, "Phone", null, "A-Interviewer", "Phone", "Pending", "orig round notes", round.Version), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using var dbB = NewDb(user);
        var act = () => new UpdateInterviewRoundCommandHandler(dbB).Handle(
            new UpdateInterviewRoundCommand(user, id, roundId, "Phone", null, "OrigInt", "Phone", "Pending", "B notes", round.Version), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var final = await GetApplicationAsync(user, id);
        final.Rounds.Single(x => x.Id == roundId).Interviewer.Should().Be("A-Interviewer");
    }

    [Fact] // 问答模板:B 持旧版本应冲突
    public async Task AnswerTemplate_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        Guid tid;
        await using (var db = NewDb(user))
        {
            var r = await new CreateAnswerTemplateCommandHandler(db).Handle(
                new CreateAnswerTemplateCommand(user, "General", "Orig Q?", "orig answer"), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            tid = r.Value;
        }
        uint v;
        await using (var db = NewDb(user))
        {
            var list = await new ListAnswerTemplatesQueryHandler(db).Handle(new ListAnswerTemplatesQuery(user), default);
            v = list.Value.Single(t => t.Id == tid).Version;
        }
        v.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new UpdateAnswerTemplateCommandHandler(dbA).Handle(
                new UpdateAnswerTemplateCommand(tid, user, "General", "A Q?", "orig answer", v), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateAnswerTemplateCommandHandler(dbB).Handle(
            new UpdateAnswerTemplateCommand(tid, user, "General", "Orig Q?", "B answer", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // 沟通记录:B 持旧版本应冲突,A 的 subject 完好
    public async Task Communication_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoCm"));
        var appId = await CreateApplicationAsync(user, cid);
        Guid cmid;
        var occurred = DateTimeOffset.UtcNow;
        await using (var db = NewDb(user))
        {
            var r = await new CreateCommunicationCommandHandler(db).Handle(
                new CreateCommunicationCommand(user, appId, "Email", "OrigSubj", "orig content", "OrigContact", null, occurred), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            cmid = r.Value;
        }
        uint v;
        await using (var db = NewDb(user))
        {
            var list = await new ListCommunicationsQueryHandler(db).Handle(new ListCommunicationsQuery(user, appId), default);
            v = list.Value.Single(c => c.Id == cmid).Version;
        }
        v.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new UpdateCommunicationCommandHandler(dbA).Handle(
                new UpdateCommunicationCommand(user, cmid, appId, "Email", "A-Subj", "orig content", "OrigContact", null, occurred, v), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateCommunicationCommandHandler(dbB).Handle(
            new UpdateCommunicationCommand(user, cmid, appId, "Email", "OrigSubj", "B content", "OrigContact", null, occurred, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // 求职信:B 持旧版本保存应冲突
    public async Task CoverLetter_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var cid = await CreateCompanyAsync(user, Uq("T101CoCl"));
        var appId = await CreateApplicationAsync(user, cid);

        uint v;
        await using (var db = NewDb(user))
        {
            var r = await new SaveCoverLetterCommandHandler(db).Handle(
                new SaveCoverLetterCommand(appId, user, "COVER-orig"), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            v = r.Value.Version;
        }
        // 注:新建路径返回 DTO 的 Version 未回填(0);以 GET 为准再读一次
        await using (var db = NewDb(user))
        {
            var g = await new GetCoverLetterQueryHandler(db).Handle(new GetCoverLetterQuery(appId, user), default);
            v = g.Value!.Version;
        }
        v.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new SaveCoverLetterCommandHandler(dbA).Handle(
                new SaveCoverLetterCommand(appId, user, "COVER-A", v), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new SaveCoverLetterCommandHandler(dbB).Handle(
            new SaveCoverLetterCommand(appId, user, "COVER-B-stale", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // 简历正文:RowVersion(xmin)与域内计数 Version 分开 —— B 持旧 RowVersion 保存应冲突
    public async Task ResumeText_StaleRowVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        uint rv;
        await using (var db = NewDb(user))
        {
            var r = await new SaveResumeCommandHandler(db).Handle(new SaveResumeCommand(user, "RESUME-orig"), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using (var db = NewDb(user))
        {
            var g = await new GetResumeQueryHandler(db).Handle(new GetResumeQuery(user), default);
            rv = g.Value.RowVersion;
        }
        rv.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new SaveResumeCommandHandler(dbA).Handle(new SaveResumeCommand(user, "RESUME-A", rv), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new SaveResumeCommandHandler(dbB).Handle(new SaveResumeCommand(user, "RESUME-B-stale", rv), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
