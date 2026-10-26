using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票54(ARC-08b):Jobs 核心写路径 Handler 行为测试 —— 经 Fake ICurrentUser
/// (TestCurrentUser)+ 真实持久化 seam 测公开行为(归属校验/状态机流转/
/// 软删与归档/轮次序号/公司幂等与删除守卫),不测实现细节。
/// DB 门控同仓内先例(TrackerPreferencesTests):连不上库时跳过。
/// 每例注释标注 boundary 类别(总纲第七节);不适用类别在类头说明:
/// - 文件类:本组 Handler 无文件/流输入,不适用;
/// - 超长 ±1 的长度闸门在校验器层(JdTextLimits 200_000 等),由
///   JobValidatorBoundaryTests 覆盖,Handler 直调不重复。
/// </summary>
public sealed class HandlerBehaviorTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private JobsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Owner);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            var apps = await _db.Applications.IgnoreQueryFilters()
                .Where(a => a.UserId == Owner || a.UserId == Other).ToListAsync();
            _db.Applications.RemoveRange(apps);
            var companies = await _db.Companies.IgnoreQueryFilters()
                .Where(c => c.UserId == Owner || c.UserId == Other).ToListAsync();
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

    private static string Unique(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<Guid> CreateCompanyAsync(Guid user, string? name = null)
    {
        await using var db = NewDb(user);
        var r = await new CreateCompanyCommandHandler(db).Handle(
            new CreateCompanyCommand(user, name ?? Unique("Co"), null, null, null, null, null), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<Guid> CreateAppAsync(Guid user, Guid companyId, string role = "Dev",
        string? status = null, string? priority = null, string? jdText = null)
    {
        await using var db = NewDb(user);
        var r = await new CreateApplicationCommandHandler(db).Handle(
            new CreateApplicationCommand(user, companyId, role, null, null, null, null, null, null,
                Priority: priority, Status: status, JdText: jdText), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<ApplicationDto> GetAppAsync(Guid user, Guid id)
    {
        await using var db = NewDb(user);
        var r = await new GetApplicationQueryHandler(db).Handle(new GetApplicationQuery(user, id), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    // ---------- CreateApplication:归属校验 ----------

    [Fact] // boundary: 空/null —— 公司 Id 为空 Guid,视为不存在
    public async Task CreateApplication_EmptyCompanyId_FailsValidation()
    {
        if (!_available) return;
        await using var db = NewDb(Owner);
        var r = await new CreateApplicationCommandHandler(db).Handle(
            new CreateApplicationCommand(Owner, Guid.Empty, "Dev", null, null, null, null, null, null), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Type.Should().Be(ErrorType.Validation);
        r.Error.Code.Should().Be("Company.NotFound");
    }

    [Fact] // boundary: 归属校验 —— 他人的公司建不了投递
    public async Task CreateApplication_OtherUsersCompany_FailsValidation()
    {
        if (!_available) return;
        var foreignCompany = await CreateCompanyAsync(Other);
        await using var db = NewDb(Owner);
        var r = await new CreateApplicationCommandHandler(db).Handle(
            new CreateApplicationCommand(Owner, foreignCompany, "Dev", null, null, null, null, null, null), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Company.NotFound");
    }

    [Fact] // boundary: 特殊字符/Unicode —— 岗位名与 JD 全文逐字落库回读
    public async Task CreateApplication_UnicodeAndSpecialChars_PersistVerbatim()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var role = "高级 .NET 工程师 🚀 <backend> & 'core'";
        var jd = "职责:微服务 «design» — café ☕ \n第二行 <script> 不转义";
        var appId = await CreateAppAsync(Owner, company, role, jdText: jd);
        var dto = await GetAppAsync(Owner, appId);
        dto.Role.Should().Be(role);
        dto.JdText.Should().Be(jd);
        dto.Status.Should().Be("Saved"); // 默认初始状态
        dto.Priority.Should().Be("Medium");
    }

    [Fact] // boundary: 状态流转 —— 建单指定跨级初始状态 Interview,沿状态机 walk 落库且历史完整
    public async Task CreateApplication_InitialStatusInterview_WalksStateMachine()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company, status: "Interview");
        var dto = await GetAppAsync(Owner, appId);
        dto.Status.Should().Be("Interview");
        dto.History.Select(h => h.Status).Should().Contain(["Saved", "Applied", "Interview"]);
    }

    [Fact] // boundary: 特殊值 —— 无法识别的状态/优先级字符串被忽略,不报错不乱改
    public async Task CreateApplication_UnrecognizedStatusAndPriority_Ignored()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company, status: "Banana", priority: "Ultra");
        var dto = await GetAppAsync(Owner, appId);
        dto.Status.Should().Be("Saved");
        dto.Priority.Should().Be("Medium");
    }

    [Fact] // boundary: 日期临界 —— 闰日 AppliedDate 逐字落库
    public async Task CreateApplication_LeapDayAppliedDate_Persists()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        Guid appId;
        await using (var db = NewDb(Owner))
        {
            var r = await new CreateApplicationCommandHandler(db).Handle(
                new CreateApplicationCommand(Owner, company, "Dev", null, null, null, null, null, null,
                    AppliedDate: new DateOnly(2024, 2, 29)), default);
            r.IsSuccess.Should().BeTrue(); appId = r.Value;
        }
        (await GetAppAsync(Owner, appId)).AppliedDate.Should().Be(new DateOnly(2024, 2, 29));
    }

    // ---------- UpdateApplication:JdText 语义 + 归属 ----------

    [Fact] // boundary: 空/null 语义 —— JdText=null 表示未提供,保持原值(FUN-02 口径)
    public async Task UpdateApplication_JdTextNull_KeepsExisting()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company, jdText: "原始 JD 全文");
        await using (var db = NewDb(Owner))
        {
            var r = await new UpdateApplicationCommandHandler(db).Handle(
                new UpdateApplicationCommand(Owner, appId, "Dev v2", null, null, null, null, null, null, null, null,
                    Deadline: new DateOnly(2026, 12, 31), JdText: null), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        var dto = await GetAppAsync(Owner, appId);
        dto.Role.Should().Be("Dev v2");
        dto.JdText.Should().Be("原始 JD 全文");
        dto.Deadline.Should().Be(new DateOnly(2026, 12, 31)); // boundary: 日期临界 —— 年末日逐字落库
    }

    [Fact] // boundary: 空串 —— JdText="" 是明确清空,归一为 null
    public async Task UpdateApplication_JdTextEmpty_Clears()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company, jdText: "待清 JD");
        await using (var db = NewDb(Owner))
        {
            var r = await new UpdateApplicationCommandHandler(db).Handle(
                new UpdateApplicationCommand(Owner, appId, "Dev", null, null, null, null, null, null, null, null,
                    Deadline: null, JdText: ""), default);
            r.IsSuccess.Should().BeTrue();
        }
        (await GetAppAsync(Owner, appId)).JdText.Should().BeNull();
    }

    [Fact] // boundary: 归属校验 —— 跨用户 Update 404 且数据不变;不存在 Id 同样 404
    public async Task UpdateApplication_CrossUserOrMissing_NotFound()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using (var db = NewDb(Other))
        {
            var r = await new UpdateApplicationCommandHandler(db).Handle(
                new UpdateApplicationCommand(Other, appId, "Hacked", null, null, null, null, null, null, null, null, null), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(Owner))
        {
            var r = await new UpdateApplicationCommandHandler(db).Handle(
                new UpdateApplicationCommand(Owner, Guid.NewGuid(), "Ghost", null, null, null, null, null, null, null, null, null), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        (await GetAppAsync(Owner, appId)).Role.Should().Be("Dev");
    }

    // ---------- ChangeApplicationStatus:状态机 ----------

    [Fact] // boundary: 状态流转 —— Saved→Applied 合法,落 AppliedDate 且历史 +1
    public async Task ChangeStatus_LegalTransition_AppliesAndRecordsHistory()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using (var db = NewDb(Owner))
        {
            var r = await new ChangeApplicationStatusCommandHandler(db).Handle(
                new ChangeApplicationStatusCommand(Owner, appId, "Applied", "已投", null), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        var dto = await GetAppAsync(Owner, appId);
        dto.Status.Should().Be("Applied");
        dto.AppliedDate.Should().NotBeNull();
        dto.History.Should().HaveCount(2); // 创建 Saved + Applied
    }

    [Fact] // boundary: 状态流转 —— Saved→Offer 跨级直跳非法 → Conflict,状态不变
    public async Task ChangeStatus_IllegalTransition_FailsConflict()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using (var db = NewDb(Owner))
        {
            var r = await new ChangeApplicationStatusCommandHandler(db).Handle(
                new ChangeApplicationStatusCommand(Owner, appId, "Offer", null, null), default);
            r.IsFailure.Should().BeTrue();
            r.Error.Type.Should().Be(ErrorType.Conflict);
            r.Error.Code.Should().Be("Status.InvalidTransition");
        }
        (await GetAppAsync(Owner, appId)).Status.Should().Be("Saved");
    }

    [Fact] // boundary: 空/无效值 —— 无法识别的状态字符串 → Validation
    public async Task ChangeStatus_UnrecognizedStatus_FailsValidation()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using var db = NewDb(Owner);
        var r = await new ChangeApplicationStatusCommandHandler(db).Handle(
            new ChangeApplicationStatusCommand(Owner, appId, "Banana", null, null), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Status.Invalid");
    }

    [Fact] // boundary: 状态流转 —— 同状态重复提交为 no-op 成功,历史不增
    public async Task ChangeStatus_SameStatus_NoOpSuccess()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using (var db = NewDb(Owner))
        {
            var r = await new ChangeApplicationStatusCommandHandler(db).Handle(
                new ChangeApplicationStatusCommand(Owner, appId, "Saved", null, null), default);
            r.IsSuccess.Should().BeTrue();
        }
        (await GetAppAsync(Owner, appId)).History.Should().HaveCount(1);
    }

    [Fact] // boundary: 归属校验 —— 跨用户改状态 404
    public async Task ChangeStatus_CrossUser_NotFound()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using var db = NewDb(Other);
        var r = await new ChangeApplicationStatusCommandHandler(db).Handle(
            new ChangeApplicationStatusCommand(Other, appId, "Applied", null, null), default);
        r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
    }

    // ---------- Delete / Archive / Unarchive ----------

    [Fact] // boundary: 状态流转 —— 软删后详情 404;跨用户删除 404 且数据完好
    public async Task DeleteApplication_SoftDeletes_AndCrossUserCannot()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var victim = await CreateAppAsync(Owner, company, "Victim");
        var mine = await CreateAppAsync(Owner, company, "Mine");
        await using (var db = NewDb(Other))
        {
            var r = await new DeleteApplicationCommandHandler(db).Handle(
                new DeleteApplicationCommand(Other, victim), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        (await GetAppAsync(Owner, victim)).Role.Should().Be("Victim"); // 未被跨用户删掉
        await using (var db = NewDb(Owner))
        {
            (await new DeleteApplicationCommandHandler(db).Handle(
                new DeleteApplicationCommand(Owner, mine), default)).IsSuccess.Should().BeTrue();
        }
        await using (var db = NewDb(Owner))
        {
            var g = await new GetApplicationQueryHandler(db).Handle(new GetApplicationQuery(Owner, mine), default);
            g.IsFailure.Should().BeTrue(); g.Error.Type.Should().Be(ErrorType.NotFound);
        }
    }

    [Fact] // boundary: 空集合/过滤 —— 归档后默认列表不含、Archived 列表含;取消归档恢复默认列表
    public async Task Archive_Unarchive_ListVisibility()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company, Unique("Arch"));
        await using (var db = NewDb(Owner))
            (await new ArchiveApplicationCommandHandler(db).Handle(
                new ArchiveApplicationCommand(Owner, appId), default)).IsSuccess.Should().BeTrue();

        await using (var db = NewDb(Owner))
        {
            var def = await new ListApplicationsQueryHandler(db).Handle(
                new ListApplicationsQuery(Owner, null, null, null, company), default);
            def.Value.Items.Should().NotContain(i => i.Id == appId);
            var arch = await new ListApplicationsQueryHandler(db).Handle(
                new ListApplicationsQuery(Owner, null, null, null, company, Archived: true), default);
            arch.Value.Items.Should().Contain(i => i.Id == appId);
        }
        await using (var db = NewDb(Owner))
            (await new UnarchiveApplicationCommandHandler(db).Handle(
                new UnarchiveApplicationCommand(Owner, appId), default)).IsSuccess.Should().BeTrue();
        await using (var db = NewDb(Owner))
        {
            var def = await new ListApplicationsQueryHandler(db).Handle(
                new ListApplicationsQuery(Owner, null, null, null, company), default);
            def.Value.Items.Should().Contain(i => i.Id == appId);
        }
    }

    // ---------- 面试轮次 ----------

    [Fact] // boundary: 0/计数 —— 连续加两轮序号 1、2(取 max+1);跨用户加轮 404
    public async Task AddRound_SequentialOrders_AndCrossUserGuard()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using (var db = NewDb(Other))
        {
            var r = await new AddInterviewRoundCommandHandler(db).Handle(
                new AddInterviewRoundCommand(Other, appId, "Technical", null, null, null, null), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(Owner))
        {
            (await new AddInterviewRoundCommandHandler(db).Handle(
                new AddInterviewRoundCommand(Owner, appId, "Screen", new DateOnly(2026, 2, 28), null, null, null), default))
                .IsSuccess.Should().BeTrue();
            (await new AddInterviewRoundCommandHandler(db).Handle(
                new AddInterviewRoundCommand(Owner, appId, "Technical", null, "Ada", null, null), default))
                .IsSuccess.Should().BeTrue();
        }
        var dto = await GetAppAsync(Owner, appId);
        dto.Rounds.Select(r => r.Order).Should().Equal(1, 2);
        dto.Rounds[0].ScheduledDate.Should().Be(new DateOnly(2026, 2, 28)); // boundary: 日期临界 —— 非闰年 2 月末
    }

    [Fact] // boundary: 无效值 —— Outcome 无法识别 → Validation;轮次不存在 → NotFound;合法更新落库
    public async Task UpdateRound_OutcomeValidation_AndUnknownRound()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        Guid roundId;
        await using (var db = NewDb(Owner))
        {
            var r = await new AddInterviewRoundCommandHandler(db).Handle(
                new AddInterviewRoundCommand(Owner, appId, "Technical", null, null, null, null), default);
            roundId = r.Value;
        }
        await using (var db = NewDb(Owner))
        {
            var bad = await new UpdateInterviewRoundCommandHandler(db).Handle(
                new UpdateInterviewRoundCommand(Owner, appId, roundId, "Technical", null, null, null, "Banana", null), default);
            bad.IsFailure.Should().BeTrue(); bad.Error.Code.Should().Be("Outcome.Invalid");
            var ghost = await new UpdateInterviewRoundCommandHandler(db).Handle(
                new UpdateInterviewRoundCommand(Owner, appId, Guid.NewGuid(), "Technical", null, null, null, "Passed", null), default);
            ghost.IsFailure.Should().BeTrue(); ghost.Error.Type.Should().Be(ErrorType.NotFound);
            var ok = await new UpdateInterviewRoundCommandHandler(db).Handle(
                new UpdateInterviewRoundCommand(Owner, appId, roundId, "Technical", null, null, null, "Passed", "顺利"), default);
            ok.IsSuccess.Should().BeTrue(ok.Error.Description);
        }
        (await GetAppAsync(Owner, appId)).Rounds.Single().Outcome.Should().Be("Passed");
    }

    // ---------- SetJdContent / SetCompanyProfile ----------

    [Fact] // boundary: 空串归一 —— SetJdContent 空白清空正文与出处;跨用户 404
    public async Task SetJdContent_SetClear_AndCrossUserGuard()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        var appId = await CreateAppAsync(Owner, company);
        await using (var db = NewDb(Owner))
            (await new SetJdContentCommandHandler(db).Handle(
                new SetJdContentCommand(Owner, appId, "完整 JD", "https://example.com/jd"), default))
                .IsSuccess.Should().BeTrue();
        var dto = await GetAppAsync(Owner, appId);
        dto.JdText.Should().Be("完整 JD"); dto.JdSourceUrl.Should().Be("https://example.com/jd");
        await using (var db = NewDb(Other))
        {
            var r = await new SetJdContentCommandHandler(db).Handle(
                new SetJdContentCommand(Other, appId, "x", null), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(Owner))
            (await new SetJdContentCommandHandler(db).Handle(
                new SetJdContentCommand(Owner, appId, "   ", null), default)).IsSuccess.Should().BeTrue();
        (await GetAppAsync(Owner, appId)).JdText.Should().BeNull();
    }

    // ---------- Company:幂等 / Logo / 更新 / 删除守卫 ----------

    [Fact] // boundary: 去重 —— 同用户同名(大小写不敏感)重复建公司返回同一 Id,不新建第二行
    public async Task CreateCompany_DuplicateName_Idempotent()
    {
        if (!_available) return;
        var name = Unique("Acme");
        var first = await CreateCompanyAsync(Owner, name);
        await using var db = NewDb(Owner);
        var r = await new CreateCompanyCommandHandler(db).Handle(
            new CreateCompanyCommand(Owner, name.ToUpperInvariant(), null, null, null, null, null), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(first);
    }

    [Fact] // boundary: 归属隔离 —— 不同用户同名公司各自一份
    public async Task CreateCompany_SameNameDifferentUsers_BothExist()
    {
        if (!_available) return;
        var name = Unique("Shared");
        var a = await CreateCompanyAsync(Owner, name);
        var b = await CreateCompanyAsync(Other, name);
        b.Should().NotBe(a);
    }

    [Fact] // boundary: 派生值 —— 未给 Logo 且有官网时按 host 推导 favicon;显式 Logo 优先
    public async Task CreateCompany_LogoDerivedFromWebsite()
    {
        if (!_available) return;
        Guid derived, explicitId;
        await using (var db = NewDb(Owner))
        {
            var r = await new CreateCompanyCommandHandler(db).Handle(
                new CreateCompanyCommand(Owner, Unique("Logo"), "https://www.example-corp.com/jobs", null, null, null, null), default);
            derived = r.Value;
            var r2 = await new CreateCompanyCommandHandler(db).Handle(
                new CreateCompanyCommand(Owner, Unique("Logo2"), "https://other.com", null, null, "https://cdn.example.com/l.png", null), default);
            explicitId = r2.Value;
        }
        await using (var db = NewDb(Owner))
        {
            var g1 = await new GetCompanyQueryHandler(db).Handle(new GetCompanyQuery(Owner, derived), default);
            g1.Value.LogoUrl.Should().Be("https://www.google.com/s2/favicons?sz=128&domain=www.example-corp.com");
            var g2 = await new GetCompanyQueryHandler(db).Handle(new GetCompanyQuery(Owner, explicitId), default);
            g2.Value.LogoUrl.Should().Be("https://cdn.example.com/l.png");
        }
    }

    [Fact] // boundary: 归属校验 —— 跨用户改公司 404;本人更新字段与拉黑标记落库
    public async Task UpdateCompany_CrossUserGuard_AndApplies()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        await using (var db = NewDb(Other))
        {
            var r = await new UpdateCompanyCommandHandler(db).Handle(
                new UpdateCompanyCommand(Other, company, "Hacked", null, null, null, null, null, "Unknown", null, false), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(Owner))
        {
            var r = await new UpdateCompanyCommandHandler(db).Handle(
                new UpdateCompanyCommand(Owner, company, "Renamed Co", null, "FinTech", null, null, null, "Agency", 42, true), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        await using (var db = NewDb(Owner))
        {
            var g = await new GetCompanyQueryHandler(db).Handle(new GetCompanyQuery(Owner, company), default);
            g.Value.Name.Should().Be("Renamed Co");
            g.Value.IsBlacklisted.Should().BeTrue();
            g.Value.CompanyType.Should().Be("Agency");
        }
    }

    [Fact] // boundary: 上限/守卫 —— 公司下有投递时删除 Conflict;无投递可删且删后 404
    public async Task DeleteCompany_WithApplications_Conflict_Empty_Succeeds()
    {
        if (!_available) return;
        var busy = await CreateCompanyAsync(Owner);
        var empty = await CreateCompanyAsync(Owner);
        await CreateAppAsync(Owner, busy);
        await using (var db = NewDb(Owner))
        {
            var r = await new DeleteCompanyCommandHandler(db).Handle(new DeleteCompanyCommand(Owner, busy), default);
            r.IsFailure.Should().BeTrue();
            r.Error.Type.Should().Be(ErrorType.Conflict);
            r.Error.Code.Should().Be("Company.HasApplications");
            (await new DeleteCompanyCommandHandler(db).Handle(new DeleteCompanyCommand(Owner, empty), default))
                .IsSuccess.Should().BeTrue();
            var g = await new GetCompanyQueryHandler(db).Handle(new GetCompanyQuery(Owner, empty), default);
            g.IsFailure.Should().BeTrue(); g.Error.Type.Should().Be(ErrorType.NotFound);
        }
    }

    [Fact] // boundary: 归属校验 —— SetCompanyProfile 跨用户 404,本人写入回读一致
    public async Task SetCompanyProfile_OwnershipAndRoundTrip()
    {
        if (!_available) return;
        var company = await CreateCompanyAsync(Owner);
        await using (var db = NewDb(Other))
        {
            var r = await new SetCompanyProfileCommandHandler(db).Handle(
                new SetCompanyProfileCommand(Other, company, "x", null), default);
            r.IsFailure.Should().BeTrue(); r.Error.Type.Should().Be(ErrorType.NotFound);
        }
        await using (var db = NewDb(Owner))
            (await new SetCompanyProfileCommandHandler(db).Handle(
                new SetCompanyProfileCommand(Owner, company, "公司情报正文", "[\"https://a.example\"]"), default))
                .IsSuccess.Should().BeTrue();
        await using (var db = NewDb(Owner))
        {
            var g = await new GetCompanyQueryHandler(db).Handle(new GetCompanyQuery(Owner, company), default);
            g.Value.Profile.Should().Be("公司情报正文");
        }
    }
}

/// <summary>
/// 票54:Jobs 校验器 boundary(纯规则,无 DB)—— 长度上限 ±1、空/null、
/// URL 形态、枚举成员。Handler 直调不经校验管线,长度闸门在此层锁定。
/// </summary>
public sealed class JobValidatorBoundaryTests
{
    private static bool HasError<T>(FluentValidation.AbstractValidator<T> v, T cmd, string property)
        => v.Validate(cmd).Errors.Any(e => e.PropertyName == property);

    private static CreateApplicationCommand AppCmd(string role = "Dev", string? jdText = null, string? priority = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), role, null, null, null, null, null, null,
            Priority: priority, JdText: jdText);

    [Fact] // boundary: 空/null —— Role 空串/纯空白拒绝
    public void CreateApplication_RoleEmpty_Rejected()
    {
        HasError(new CreateApplicationCommandValidator(), AppCmd(role: ""), "Role").Should().BeTrue();
        HasError(new CreateApplicationCommandValidator(), AppCmd(role: "   "), "Role").Should().BeTrue();
    }

    [Fact] // boundary: 超长上限 ±1 —— Role 300 放行 / 301 拒绝
    public void CreateApplication_RoleLength_Boundary()
    {
        HasError(new CreateApplicationCommandValidator(), AppCmd(role: new string('r', 300)), "Role").Should().BeFalse();
        HasError(new CreateApplicationCommandValidator(), AppCmd(role: new string('r', 301)), "Role").Should().BeTrue();
    }

    [Fact] // boundary: 超长上限 ±1 —— JdText 200_000 放行 / 200_001 拒绝(统一闸门 JdTextLimits)
    public void CreateApplication_JdTextLength_Boundary()
    {
        HasError(new CreateApplicationCommandValidator(), AppCmd(jdText: new string('j', 200_000)), "JdText").Should().BeFalse();
        HasError(new CreateApplicationCommandValidator(), AppCmd(jdText: new string('j', 200_001)), "JdText").Should().BeTrue();
    }

    [Fact] // boundary: 空 Guid —— CompanyId 必填
    public void CreateApplication_CompanyIdEmpty_Rejected()
        => HasError(new CreateApplicationCommandValidator(),
            new CreateApplicationCommand(Guid.NewGuid(), Guid.Empty, "Dev", null, null, null, null, null, null), "CompanyId")
            .Should().BeTrue();

    [Fact] // boundary: 枚举成员 —— Priority 合法值(含小写)放行,非法值拒绝,null 放行
    public void CreateApplication_PriorityValues()
    {
        HasError(new CreateApplicationCommandValidator(), AppCmd(priority: "high"), "Priority").Should().BeFalse();
        HasError(new CreateApplicationCommandValidator(), AppCmd(priority: "Ultra"), "Priority").Should().BeTrue();
        HasError(new CreateApplicationCommandValidator(), AppCmd(priority: null), "Priority").Should().BeFalse();
    }

    [Fact] // boundary: 超长上限 ±1 —— 公司名 300 放行 / 301 拒绝;空名拒绝
    public void CreateCompany_NameLength_Boundary()
    {
        var v = new CreateCompanyCommandValidator();
        HasError(v, new CreateCompanyCommand(Guid.NewGuid(), new string('c', 300), null, null, null, null, null), "Name").Should().BeFalse();
        HasError(v, new CreateCompanyCommand(Guid.NewGuid(), new string('c', 301), null, null, null, null, null), "Name").Should().BeTrue();
        HasError(v, new CreateCompanyCommand(Guid.NewGuid(), "", null, null, null, null, null), "Name").Should().BeTrue();
    }

    [Theory] // boundary: 特殊字符/URL 形态 —— 仅 http/https 绝对地址放行
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/x?y=1", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    public void CreateCompany_WebsiteUrl_Schemes(string website, bool valid)
        => HasError(new CreateCompanyCommandValidator(),
            new CreateCompanyCommand(Guid.NewGuid(), "Co", website, null, null, null, null), "Website")
            .Should().Be(!valid);

    [Fact] // boundary: 枚举成员 —— CompanyType 仅四值
    public void CreateCompany_CompanyType_Whitelist()
    {
        var v = new CreateCompanyCommandValidator();
        HasError(v, new CreateCompanyCommand(Guid.NewGuid(), "Co", null, null, null, null, null, CompanyType: "Agency"), "CompanyType").Should().BeFalse();
        HasError(v, new CreateCompanyCommand(Guid.NewGuid(), "Co", null, null, null, null, null, CompanyType: "Partner"), "CompanyType").Should().BeTrue();
    }

    [Fact] // boundary: 超长上限 ±1 —— Update/SetJdContent 的 JdText 同闸门(200_000/200_001),null 放行(未提供语义)
    public void UpdateAndSetJd_JdTextLength_Boundary()
    {
        var upd = new UpdateApplicationCommand(Guid.NewGuid(), Guid.NewGuid(), "Dev", null, null, null, null, null,
            null, null, null, null, JdText: new string('j', 200_001));
        HasError(new UpdateApplicationCommandValidator(), upd, "JdText").Should().BeTrue();
        var updNull = upd with { JdText = null };
        HasError(new UpdateApplicationCommandValidator(), updNull, "JdText").Should().BeFalse();
        var set = new SetJdContentCommand(Guid.NewGuid(), Guid.NewGuid(), new string('j', 200_000), null);
        HasError(new SetJdContentCommandValidator(), set, "JdText").Should().BeFalse();
    }
}
