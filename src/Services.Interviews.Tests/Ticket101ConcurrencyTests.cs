using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票101 §7:Interviews 详情编辑的乐观并发(读时 xmin 版本回传,口径同票70)。
/// 定性背景(第一段活体):条目全量 PUT 与题目/轮次/材料子行编辑均不回传版本,
/// Handler 现查现改总拿最新 xmin,串行双页签必然静默覆盖;子行(题目/轮次/
/// 材料)原本连 xmin 令牌都没有,本票一并纳入(系统列,零迁移)。
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
        var entries = await db.Entries.IgnoreQueryFilters().Where(e => _users.Contains(e.UserId)).ToListAsync();
        db.Entries.RemoveRange(entries);
        await db.SaveChangesAsync();
    }

    private static InterviewsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        return new InterviewsDbContext(opts, new FixedCurrentUser(currentUser));
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }
    private static string Uq(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> CreateEntryAsync(Guid user, string company)
    {
        await using var db = NewDb(user);
        var r = await new CreateEntryCommandHandler(db).Handle(
            new CreateEntryCommand(user, null, company, "Orig Role", null,
                "orig profile", "orig jd", "orig sum", null, "OrigInt", null, null, "orig notes"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<InterviewEntryDto> GetAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetEntryQueryHandler(db).Handle(new GetEntryQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private static UpdateEntryCommand Upd(Guid user, Guid id, uint? version, string role = "Orig Role", string notes = "orig notes")
        => new(user, id, "placeholder-co", role, "orig profile", "orig jd", "orig sum", 1, null,
               null, "OrigInt", null, null, notes, Version: version);

    [Fact] // 核心:条目全量更新 —— 两页签同读一版本,A 先存,B 持旧版本应冲突,A 数据完好
    public async Task Entry_DualTab_StaleVersion_Throws_FirstSaveIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var company = Uq("T101Co");
        var id = await CreateEntryAsync(user, company);
        var tabA = await GetAsync(user, id);
        var tabB = await GetAsync(user, id);
        tabA.Version.Should().BeGreaterThan(0u);
        tabB.Version.Should().Be(tabA.Version);

        await using (var dbA = NewDb(user))
            (await new UpdateEntryCommandHandler(dbA).Handle(
                Upd(user, id, tabA.Version, role: "A Role") with { CompanyName = company }, default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateEntryCommandHandler(dbB).Handle(
            Upd(user, id, tabB.Version, notes: "B notes") with { CompanyName = company }, default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        var final = await GetAsync(user, id);
        final.Role.Should().Be("A Role");
        final.Notes.Should().Be("orig notes");
    }

    [Fact] // boundary: Version=null 旧调用方不钉版 —— 后存仍成功(兼容)
    public async Task Entry_NullVersion_SkipsCheck()
    {
        if (!_available) return;
        var user = NewUser();
        var company = Uq("T101CoN");
        var id = await CreateEntryAsync(user, company);
        var v = (await GetAsync(user, id)).Version;

        await using (var dbA = NewDb(user))
            (await new UpdateEntryCommandHandler(dbA).Handle(
                Upd(user, id, v, role: "A Role") with { CompanyName = company }, default)).IsSuccess.Should().BeTrue();
        await using (var dbB = NewDb(user))
            (await new UpdateEntryCommandHandler(dbB).Handle(
                Upd(user, id, null, role: "legacy") with { CompanyName = company }, default)).IsSuccess.Should().BeTrue();
        (await GetAsync(user, id)).Role.Should().Be("legacy");
    }

    [Fact] // 不变项:单页签拿新鲜版本连续编辑全成功
    public async Task Entry_SingleTab_FreshVersion_SequentialEditsSucceed()
    {
        if (!_available) return;
        var user = NewUser();
        var company = Uq("T101CoS");
        var id = await CreateEntryAsync(user, company);

        var v1 = (await GetAsync(user, id)).Version;
        await using (var db = NewDb(user))
            (await new UpdateEntryCommandHandler(db).Handle(
                Upd(user, id, v1, role: "edit 1") with { CompanyName = company }, default)).IsSuccess.Should().BeTrue();
        var v2 = (await GetAsync(user, id)).Version;
        v2.Should().NotBe(v1);
        await using (var db = NewDb(user))
            (await new UpdateEntryCommandHandler(db).Handle(
                Upd(user, id, v2, role: "edit 2") with { CompanyName = company }, default)).IsSuccess.Should().BeTrue();
        (await GetAsync(user, id)).Role.Should().Be("edit 2");
    }

    [Fact] // boundary:软删条目持版本更新走 NotFound,不抛并发异常
    public async Task Entry_SoftDeleted_Update_ReturnsNotFound()
    {
        if (!_available) return;
        var user = NewUser();
        var company = Uq("T101CoD");
        var id = await CreateEntryAsync(user, company);
        var v = (await GetAsync(user, id)).Version;

        await using (var db = NewDb(user))
            (await new DeleteEntryCommandHandler(db).Handle(new DeleteEntryCommand(user, id), default))
                .IsSuccess.Should().BeTrue();
        await using var db2 = NewDb(user);
        var r = await new UpdateEntryCommandHandler(db2).Handle(
            Upd(user, id, v, role: "zombie") with { CompanyName = company }, default);
        r.IsSuccess.Should().BeFalse();
    }

    private async Task<Guid> AddQuestionAsync(Guid user, Guid entryId)
    {
        await using var db = NewDb(user);
        var r = await new AddQuestionCommandHandler(db).Handle(
            new AddQuestionCommand(user, entryId, "Orig Q?", "orig ans", "Technical", 3), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private static UpdateQuestionCommand UpdQ(Guid user, Guid entryId, Guid qid, uint? version,
        string text = "Orig Q?", string answer = "orig ans")
        => new(user, entryId, qid, text, answer, "Technical", 3, null, false, null, null, version);

    [Fact] // 题目(子行):DTO 带题目 Version,B 持旧版本更新应冲突
    public async Task Question_DualTab_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T101CoQ"));
        var qid = await AddQuestionAsync(user, id);
        var dto = await GetAsync(user, id);
        var qv = dto.Questions.Single(q => q.Id == qid).Version;
        qv.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new UpdateQuestionCommandHandler(dbA).Handle(UpdQ(user, id, qid, qv, text: "A Q?"), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateQuestionCommandHandler(dbB).Handle(
            UpdQ(user, id, qid, qv, answer: "B ans"), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).Questions.Single(q => q.Id == qid).QuestionText.Should().Be("A Q?");
    }

    [Fact] // 题目 mastery(单字段子行写):B 持旧题目版本应冲突
    public async Task QuestionMastery_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T101CoM"));
        var qid = await AddQuestionAsync(user, id);
        var qv = (await GetAsync(user, id)).Questions.Single(q => q.Id == qid).Version;

        await using (var dbA = NewDb(user))
            (await new UpdateQuestionMasteryCommandHandler(dbA).Handle(
                new UpdateQuestionMasteryCommand(user, id, qid, 2, qv), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateQuestionMasteryCommandHandler(dbB).Handle(
            new UpdateQuestionMasteryCommand(user, id, qid, 1, qv), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).Questions.Single(q => q.Id == qid).Mastery.Should().Be(2);
    }

    [Fact] // 题目复盘 JSON:B 持旧题目版本应冲突
    public async Task QuestionReview_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T101CoQR"));
        var qid = await AddQuestionAsync(user, id);
        var qv = (await GetAsync(user, id)).Questions.Single(q => q.Id == qid).Version;

        await using (var dbA = NewDb(user))
            (await new SetQuestionReviewCommandHandler(dbA).Handle(
                new SetQuestionReviewCommand(user, id, qid, "{\"v\":\"A\"}", qv), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new SetQuestionReviewCommandHandler(dbB).Handle(
            new SetQuestionReviewCommand(user, id, qid, "{\"v\":\"B-stale\"}", qv), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private async Task<Guid> AddRoundAsync(Guid user, Guid entryId)
    {
        await using var db = NewDb(user);
        var r = await new AddRoundCommandHandler(db).Handle(
            new AddRoundCommand(user, entryId, "Technical", null, "OrigInt"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact] // 轮次(子行):B 持旧轮次版本更新应冲突
    public async Task Round_DualTab_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T101CoR"));
        var rid = await AddRoundAsync(user, id);
        var dto = await GetAsync(user, id);
        var rv = dto.Rounds.Single(r => r.Id == rid).Version;
        rv.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new UpdateRoundCommandHandler(dbA).Handle(
                new UpdateRoundCommand(user, id, rid, "Technical", null, "A-Int", null, null, "Pending", null, null, Version: rv), default))
                .IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateRoundCommandHandler(dbB).Handle(
            new UpdateRoundCommand(user, id, rid, "Technical", null, "OrigInt", null, null, "Pending", "B notes", null, Version: rv), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetAsync(user, id)).Rounds.Single(r => r.Id == rid).Interviewers.Should().Be("A-Int");
    }

    [Fact] // 轮次邮件(单字段子行写):B 持旧轮次版本应冲突
    public async Task RoundEmails_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T101CoRE"));
        var rid = await AddRoundAsync(user, id);
        var rv = (await GetAsync(user, id)).Rounds.Single(r => r.Id == rid).Version;

        await using (var dbA = NewDb(user))
            (await new UpdateRoundEmailsCommandHandler(dbA).Handle(
                new UpdateRoundEmailsCommand(user, id, rid,
                    [new RoundEmailItem("a@x", "A subj", "2026-10-01", "s")], rv), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateRoundEmailsCommandHandler(dbB).Handle(
            new UpdateRoundEmailsCommand(user, id, rid,
                [new RoundEmailItem("b@x", "B subj", "2026-10-02", "s")], rv), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // 材料 meta(单字段子行写):B 持旧材料版本应冲突
    public async Task AssetMeta_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var id = await CreateEntryAsync(user, Uq("T101CoAs"));
        Guid assetId;
        await using (var db = NewDb(user))
        {
            var r = await new AttachAssetCommandHandler(db).Handle(
                new AttachAssetCommand(user, id, "Text", "note.txt", "text/plain", 5, null, null,
                    "hello", null, null, "en"), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
            assetId = r.Value;
        }
        var av = (await GetAsync(user, id)).Assets.Single(a => a.Id == assetId).Version;
        av.Should().BeGreaterThan(0u);

        await using (var dbA = NewDb(user))
            (await new UpdateAssetMetaCommandHandler(dbA).Handle(
                new UpdateAssetMetaCommand(user, id, assetId, "{\"v\":\"A\"}", av), default)).IsSuccess.Should().BeTrue();
        await using var dbB = NewDb(user);
        var act = () => new UpdateAssetMetaCommandHandler(dbB).Handle(
            new UpdateAssetMetaCommand(user, id, assetId, "{\"v\":\"B-stale\"}", av), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
