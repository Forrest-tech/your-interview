using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票04(REQ-02+ARC-07):Playbook 轮次结果回写 Tracker 同源。
/// 契约:InterviewOutcomeRecorded 带结构化 Outcome 枚举(SharedContracts),
/// Jobs 消费者以枚举为准;无枚举的旧事件走 Jobs 侧单一 adapter 的关键词回落;
/// Jobs 侧无对应轮次时按枚举语义建投影,不得静默跳过(修复前:KeyNotFound 被吞,
/// 投递停在 Saved、Tracker interviewed=0,与 Playbook/Dashboard 同屏矛盾)。
/// 走真实 JobsDbContext + 真实消费者 HandleAsync(无 DB 环境跳过,同 Interviews.Tests 先例)。
/// </summary>
public sealed class OutcomeWritebackConsumerTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";

    private static readonly Guid Owner = Guid.NewGuid();
    private readonly List<Guid> _appIds = [];
    private JobsDbContext _seedDb = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _seedDb = NewDb(Owner);
        try { _available = await _seedDb.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            // 清理本测试种子(IgnoreQueryFilters:含软删/他人名下都不漏)
            var apps = await _seedDb.Applications.IgnoreQueryFilters()
                .Where(a => _appIds.Contains(a.Id)).ToListAsync();
            _seedDb.Applications.RemoveRange(apps);
            var companies = await _seedDb.Companies.IgnoreQueryFilters()
                .Where(c => c.UserId == Owner).ToListAsync();
            _seedDb.Companies.RemoveRange(companies);
            await _seedDb.SaveChangesAsync();
        }
        await _seedDb.DisposeAsync();
    }

    private static JobsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<JobsDbContext>().UseNpgsql(Cs).Options;
        return new JobsDbContext(opts, new TestCurrentUser(currentUser));
    }

    /// <summary>种子:一家公司 + 一条投递。withRound=true 时经 MarkApplied+AddRound 到 Interview 且有第 1 轮。</summary>
    private async Task<JobApplication> SeedAppAsync(bool withRound, bool applied = false)
    {
        var company = new Company("Req02-" + Guid.NewGuid().ToString("N")[..8], userId: Owner);
        _seedDb.Companies.Add(company);
        await _seedDb.SaveChangesAsync();

        var app = new JobApplication(company.Id, "Senior .NET Developer", userId: Owner);
        if (applied || withRound) app.MarkApplied();
        if (withRound) app.AddRound("Technical", null, null, null, null);
        _seedDb.Applications.Add(app);
        await _seedDb.SaveChangesAsync();
        _seedDb.ChangeTracker.Clear();
        _appIds.Add(app.Id);
        return app;
    }

    /// <summary>以「无 HTTP 上下文」的消费者上下文(Guid.Empty)直调消费者核心处理。</summary>
    private async Task ConsumeAsync(InterviewOutcomeRecorded e)
    {
        await using var db = NewDb(Guid.Empty);
        var consumer = new InterviewOutcomeRecordedConsumer(db,
            NullLogger<InterviewOutcomeRecordedConsumer>.Instance);
        await consumer.HandleAsync(e, default);
    }

    private async Task<JobApplication> ReloadAsync(Guid appId)
    {
        await using var db = NewDb(Owner);
        return await db.Applications.IgnoreQueryFilters().Include(a => a.Rounds)
            .FirstAsync(a => a.Id == appId);
    }

    private static InterviewOutcomeRecorded Evt(Guid appId, Guid userId, int roundNo,
        string result, InterviewOutcome? outcome)
        => new(appId, Guid.NewGuid(), userId, roundNo, result, outcome);

    [Fact]
    public async Task EnumPassed_NoJobsRound_CreatesProjection_AndMovesSavedToInterview()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: false);

        await ConsumeAsync(Evt(app.Id, Owner, 1, "Passed", InterviewOutcome.Passed));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().ContainSingle(r => r.Order == 1);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Passed);
        // 状态随之变化:Tracker interviewed 口径(状态 Interview 或 Rounds>0)与 Playbook 不再矛盾
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    [Fact]
    public async Task EnumRejected_NoJobsRound_CreatesProjection_AndRejectsFromSaved()
    {
        if (!_available) return;
        // 修复前复现点:Jobs 侧无对应轮次 → RecordRoundOutcome 抛 KeyNotFound 被吞,
        // 投递停在 Saved、计数为 0,尽管 Playbook 已填「被拒」。
        var app = await SeedAppAsync(withRound: false);

        await ConsumeAsync(Evt(app.Id, Owner, 1, "Rejected", InterviewOutcome.Rejected));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().ContainSingle(r => r.Order == 1);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Failed);
        stored.Status.Should().Be(ApplicationStatus.Rejected);
    }

    [Fact]
    public async Task EnumRejected_ExistingRound_SetsFailed_AndRejects()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: true);
        app.Status.Should().Be(ApplicationStatus.Interview);

        await ConsumeAsync(Evt(app.Id, Owner, 1, "Rejected", InterviewOutcome.Rejected));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Failed);
        stored.Status.Should().Be(ApplicationStatus.Rejected);
    }

    [Fact]
    public async Task EnumWinsOverFreeText_PassedEnum_WithRejectedLookingText_Passes()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: true);

        // 自由文本原文只是展示:哪怕文本看着像未通过,结构化枚举才是准绳
        await ConsumeAsync(Evt(app.Id, Owner, 1, "未通过", InterviewOutcome.Passed));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Passed);
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    [Fact]
    public async Task EnumGhosted_NoJobsRound_CreatesProjection_AndGhostsApplication()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: false, applied: true);

        await ConsumeAsync(Evt(app.Id, Owner, 1, "Ghosted", InterviewOutcome.Ghosted));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().ContainSingle(r => r.Order == 1);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Ghosted);
        stored.Status.Should().Be(ApplicationStatus.Ghosted);
    }

    [Fact]
    public async Task LegacyEvent_NoEnum_KeywordFallback_StillWorks()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: true);

        // 旧事件:无枚举字段(契约升级前已发出的消息),靠 Jobs 侧 adapter 关键词回落
        await ConsumeAsync(Evt(app.Id, Owner, 1, "被拒", null));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Failed);
        stored.Status.Should().Be(ApplicationStatus.Rejected);
    }

    [Fact]
    public async Task LegacyEvent_NoEnum_UnrecognizedText_NoChange()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: true);

        await ConsumeAsync(Evt(app.Id, Owner, 1, "等回复", null));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single().Outcome.Should().Be(RoundOutcome.Pending);
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    [Fact]
    public async Task UserIdMismatch_Skips_NoProjectionNoStatusChange()
    {
        if (!_available) return;
        var app = await SeedAppAsync(withRound: false);

        // 票03 语义不回退:事件 UserId 与投递归属不一致 → 跳过,不建投影、不动状态
        await ConsumeAsync(Evt(app.Id, Guid.NewGuid(), 1, "Rejected", InterviewOutcome.Rejected));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().BeEmpty();
        stored.Status.Should().Be(ApplicationStatus.Saved);
    }
}
