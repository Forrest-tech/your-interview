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
/// 票114(REQ-02 残余/CONF-DASH-1):Playbook 轮次排期回写 Tracker 同源。
/// 契约:InterviewRoundScheduled(排期落库/改期/撤销),Jobs 消费者同步投影
/// 轮次排期;定版语义(evidence-114/STATUS §4):Saved/Applied/Screen + 该轮
/// Pending + 日期非空 → 推进 Interview;已决定/已推进状态不覆盖不复活;
/// 结果先到、排期后到只同步日期不回退;撤销(null 日期)只清投影日期不回退
/// 状态;同事件重放幂等;跨用户跳过。
/// 走真实 JobsDbContext + 真实消费者 HandleAsync(无 DB 环境跳过,同票04 先例)。
/// </summary>
public sealed class ScheduleWritebackConsumerTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly DateOnly D1 = new(2026, 10, 28);
    private static readonly DateOnly D2 = new(2026, 11, 2);
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

    private async Task<JobApplication> SeedAppAsync(bool applied = false)
    {
        var company = new Company("Req02Sched-" + Guid.NewGuid().ToString("N")[..8], userId: Owner);
        _seedDb.Companies.Add(company);
        await _seedDb.SaveChangesAsync();

        var app = new JobApplication(company.Id, "Senior .NET Developer", userId: Owner);
        if (applied) app.MarkApplied();
        _seedDb.Applications.Add(app);
        await _seedDb.SaveChangesAsync();
        _seedDb.ChangeTracker.Clear();
        _appIds.Add(app.Id);
        return app;
    }

    private async Task ConsumeScheduleAsync(InterviewRoundScheduled e)
    {
        await using var db = NewDb(Guid.Empty);
        var consumer = new InterviewRoundScheduledConsumer(db,
            NullLogger<InterviewRoundScheduledConsumer>.Instance);
        await consumer.HandleAsync(e, default);
    }

    private async Task ConsumeOutcomeAsync(InterviewOutcomeRecorded e)
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
            .Include(a => a.History)
            .FirstAsync(a => a.Id == appId);
    }

    private static InterviewRoundScheduled SchedEvt(Guid appId, Guid userId, int roundNo,
        string? stage, DateOnly? date)
        => new(appId, Guid.NewGuid(), userId, roundNo, stage, date);

    [Fact]
    public async Task Schedule_SavedNoRound_CreatesProjection_AdvancesToInterview()
    {
        if (!_available) return;
        var app = await SeedAppAsync();

        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", D1));

        var stored = await ReloadAsync(app.Id);
        var round = stored.Rounds.Should().ContainSingle(r => r.Order == 1).Subject;
        round.ScheduledDate.Should().Be(D1);
        round.Stage.Should().Be("Technical");
        round.Outcome.Should().Be(RoundOutcome.Pending);
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    [Fact]
    public async Task Schedule_Applied_AdvancesToInterview()
    {
        if (!_available) return;
        var app = await SeedAppAsync(applied: true);

        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Screen", D1));

        var stored = await ReloadAsync(app.Id);
        stored.Status.Should().Be(ApplicationStatus.Interview);
        stored.Rounds.Single().ScheduledDate.Should().Be(D1);
    }

    [Fact]
    public async Task Reschedule_ExistingProjection_SyncsNewDate_StatusStaysInterview()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", D1));

        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", D2));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().ContainSingle();
        stored.Rounds.Single().ScheduledDate.Should().Be(D2);
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    [Fact]
    public async Task Schedule_DecidedStatusOffer_SyncsDate_DoesNotMoveStatus()
    {
        if (!_available) return;
        // 已推进到 Offer:排期事件只同步投影日期,状态不被拉回 Interview
        var seeded = await SeedAppAsync(applied: true);
        var app = await _seedDb.Applications.IgnoreQueryFilters().Include(a => a.Rounds)
            .FirstAsync(a => a.Id == seeded.Id);
        app.AddRound("Technical", null, null, null, null); // Applied → Interview + 投影轮次
        app.ChangeStatus(ApplicationStatus.Offer);
        await _seedDb.SaveChangesAsync();
        _seedDb.ChangeTracker.Clear();

        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", D1));

        var stored = await ReloadAsync(app.Id);
        stored.Status.Should().Be(ApplicationStatus.Offer);
        stored.Rounds.Single().ScheduledDate.Should().Be(D1);
    }

    [Fact]
    public async Task ResultFirst_ScheduleLater_SyncsDate_DoesNotResurrectRejected()
    {
        if (!_available) return;
        var app = await SeedAppAsync();

        // 结果先到:第 1 轮被拒 → 投递 Rejected、投影 Outcome=Failed
        await ConsumeOutcomeAsync(new InterviewOutcomeRecorded(app.Id, Guid.NewGuid(),
            Owner, 1, "Rejected", InterviewOutcome.Rejected));
        // 排期后到(乱序):只许同步日期,永不把已拒投递复活成 Interview
        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", D1));

        var stored = await ReloadAsync(app.Id);
        stored.Status.Should().Be(ApplicationStatus.Rejected);
        var round = stored.Rounds.Single();
        round.Outcome.Should().Be(RoundOutcome.Failed);
        round.ScheduledDate.Should().Be(D1);
    }

    [Fact]
    public async Task Schedule_Replay_Idempotent_SingleProjectionSingleTransition()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        var evt = SchedEvt(app.Id, Owner, 1, "Technical", D1);

        await ConsumeScheduleAsync(evt);
        await ConsumeScheduleAsync(evt);

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().ContainSingle();
        stored.Status.Should().Be(ApplicationStatus.Interview);
        stored.History.Count(h => h.Status == ApplicationStatus.Interview).Should().Be(1);
    }

    [Fact]
    public async Task ScheduleRevoked_NullDate_ClearsProjectionDate_KeepsStatus()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", D1));

        // 删轮次/清空日期(撤销形态):只清投影日期,状态不回退(定版 §4.6)
        await ConsumeScheduleAsync(SchedEvt(app.Id, Owner, 1, "Technical", null));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single().ScheduledDate.Should().BeNull();
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    [Fact]
    public async Task UserIdMismatch_Skips_NoProjectionNoStatusChange()
    {
        if (!_available) return;
        var app = await SeedAppAsync();

        await ConsumeScheduleAsync(SchedEvt(app.Id, Guid.NewGuid(), 1, "Technical", D1));

        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Should().BeEmpty();
        stored.Status.Should().Be(ApplicationStatus.Saved);
    }
}
