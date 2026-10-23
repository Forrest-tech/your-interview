using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// CONF-CAL-2 / CAL2-T3:提醒同步对账规则(SPEC 设计决定 3/4/5)。
/// 直连库(不可达整组跳过,同仓内先例);每例独立用户,互不干扰。
/// 覆盖:生成两条 / 无日期不生成 / 幂等 / 已确认保住 / 改期重算回 Pending /
/// 终态取消 / 删轮次取消 / 删条目取消。
/// </summary>
public sealed class ReminderSyncTests : IAsyncLifetime
{
    private const string Cs = "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";

    private static readonly Guid Owner = Guid.NewGuid();
    private InterviewsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _db = NewDb(Owner);
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
        // DB 可达时迁移失败必须让测试红(不吞)。但多测试类并行 Initialize 时
        // 并发 MigrateAsync 会抢同一条 DDL(23505 pg_type / 42P07),故先拿
        // advisory lock 把「检查+迁移」串行化;已迁移时为 no-op。
        if (_available) await MigrateUnderLockAsync();
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            var rems = await _db.RoundReminders.IgnoreQueryFilters()
                .Where(x => x.UserId == Owner).ToListAsync();
            _db.RoundReminders.RemoveRange(rems);
            var entries = await _db.Entries.IgnoreQueryFilters()
                .Where(x => x.UserId == Owner).ToListAsync();
            _db.Entries.RemoveRange(entries);
            await _db.SaveChangesAsync();
        }
        await _db.DisposeAsync();
    }


    /// <summary>advisory lock 串行化进程内并发迁移(见 InitializeAsync 注)。</summary>
    private async Task MigrateUnderLockAsync()
    {
        await using var lockConn = new Npgsql.NpgsqlConnection(Cs);
        await lockConn.OpenAsync();
        await using (var lk = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(90241023)", lockConn))
            await lk.ExecuteNonQueryAsync();
        try { await _db.Database.MigrateAsync(); }
        finally
        {
            await using var un = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock(90241023)", lockConn);
            await un.ExecuteNonQueryAsync();
        }
    }

    private sealed class ParamUser(Guid id) : ICurrentUser
    {
        public Guid? UserId => id;
        public string? Email => "test@local";
        public bool IsAuthenticated => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
        public IReadOnlyList<string> Roles => [];
    }

    private static InterviewsDbContext NewDb(Guid user)
    {
        // 必须镜像 Program.cs 的 DI 配置:history 表显式落在 interviews schema;
        // 缺了它,进程内 MigrateAsync 读不到已应用迁移,会从第一条重放撞表。
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql(Cs, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", InterviewsDbContext.Schema))
            .Options;
        return new InterviewsDbContext(opts, new ParamUser(user));
    }

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<(Guid EntryId, Guid RoundId)> SeedRoundAsync(
        DateOnly? date, string? time = "14:30", InterviewRoundOutcome outcome = InterviewRoundOutcome.Pending)
    {
        await using var db = NewDb(Owner);
        var e = new InterviewEntry(Guid.NewGuid(), Unique("T"), "Dev", jobApplicationId: null, userId: Owner);
        var r = e.AddRound("Technical");
        db.Entries.Add(e);
        await db.SaveChangesAsync();
        // 排期/时间经公开命令路径落库(UpdateDetails 是 internal,测试不直调)——
        // 该处理器带 CONF-CAL-2 同步挂钩,种子落库后提醒可能已生成,后续 Sync 幂等。
        var res = await new UpdateRoundCommandHandler(db).Handle(
            new UpdateRoundCommand(Owner, e.Id, r.Id, "Technical", date, null, null, null,
                outcome.ToString(), null, null, null, time), default);
        res.IsSuccess.Should().BeTrue(res.Error.Description);
        return (e.Id, r.Id);
    }

    private async Task<List<RoundReminder>> RemindersOfAsync(Guid roundId)
    {
        await using var db = NewDb(Owner);
        return await db.RoundReminders.IgnoreQueryFilters()
            .Where(x => x.RoundId == roundId).OrderBy(x => x.Kind).ToListAsync();
    }

    private async Task SyncAsync()
    {
        await using var db = NewDb(Owner);
        await ReminderSync.SyncUserAsync(db, Owner);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Sync_ScheduledPendingRound_CreatesTwoReminders()
    {
        if (!_available) return;
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);
        var (_, roundId) = await SeedRoundAsync(date);

        await SyncAsync();

        var rows = await RemindersOfAsync(roundId);
        rows.Should().HaveCount(2);
        rows.Select(x => x.Kind).Should().BeEquivalentTo(
            [RoundReminderKind.DayBefore, RoundReminderKind.HourBefore]);
        rows.Should().OnlyContain(x => x.Status == RoundReminderStatus.Pending);
        var expected = ReminderSchedule.Compute(date, "14:30");
        rows.Single(x => x.Kind == RoundReminderKind.DayBefore).RemindAt.Should().Be(expected[0].RemindAt);
        rows.Single(x => x.Kind == RoundReminderKind.HourBefore).RemindAt.Should().Be(expected[1].RemindAt);
    }

    [Fact] // boundary: 无排期日期 —— 不生成(同 .ics 先例)
    public async Task Sync_UnscheduledRound_CreatesNone()
    {
        if (!_available) return;
        var (_, roundId) = await SeedRoundAsync(null);

        await SyncAsync();

        (await RemindersOfAsync(roundId)).Should().BeEmpty();
    }

    [Fact] // 幂等:重复同步不增行、不改行
    public async Task Sync_SecondRun_Idempotent()
    {
        if (!_available) return;
        var (_, roundId) = await SeedRoundAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3));
        await SyncAsync();
        var first = await RemindersOfAsync(roundId);

        await SyncAsync();

        var second = await RemindersOfAsync(roundId);
        second.Should().HaveCount(2);
        second.Select(x => x.Id).Should().BeEquivalentTo(first.Select(x => x.Id));
        second.Select(x => x.RemindAt).Should().BeEquivalentTo(first.Select(x => x.RemindAt));
    }

    [Fact] // 不变项:排期没变时,已确认的提醒不被同步打回 Pending
    public async Task Sync_UnchangedSchedule_KeepsAcknowledged()
    {
        if (!_available) return;
        var (_, roundId) = await SeedRoundAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3));
        await SyncAsync();
        await using (var db = NewDb(Owner))
        {
            var row = await db.RoundReminders.IgnoreQueryFilters()
                .FirstAsync(x => x.RoundId == roundId && x.Kind == RoundReminderKind.DayBefore);
            row.Acknowledge(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await SyncAsync();

        var rows = await RemindersOfAsync(roundId);
        rows.Single(x => x.Kind == RoundReminderKind.DayBefore).Status
            .Should().Be(RoundReminderStatus.Acknowledged);
    }

    [Fact] // 改期:RemindAt 随动,且已确认的提醒回到 Pending(新时刻需重新确认)
    public async Task Sync_Reschedule_UpdatesRemindAt_AndBackToPending()
    {
        if (!_available) return;
        var d1 = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3);
        var (entryId, roundId) = await SeedRoundAsync(d1);
        await SyncAsync();
        await using (var db = NewDb(Owner))
        {
            var row = await db.RoundReminders.IgnoreQueryFilters()
                .FirstAsync(x => x.RoundId == roundId && x.Kind == RoundReminderKind.HourBefore);
            row.Acknowledge(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        var d2 = d1.AddDays(2);
        await using (var db = NewDb(Owner))
        {
            var e = await db.Entries.Include(x => x.Rounds).FirstAsync(x => x.Id == entryId);
            var r = e.Rounds.Single(x => x.Id == roundId);
            e.UpdateRound(r.Id, r.Stage, d2, null, null, null, InterviewRoundOutcome.Pending, null, null);
            await db.SaveChangesAsync();
        }
        await SyncAsync();

        var rows = await RemindersOfAsync(roundId);
        var hour = rows.Single(x => x.Kind == RoundReminderKind.HourBefore);
        hour.RemindAt.Should().Be(ReminderSchedule.Compute(d2, "14:30")[1].RemindAt);
        hour.Status.Should().Be(RoundReminderStatus.Pending);
    }

    [Fact] // 终态:结果已定的轮次,待触发提醒取消
    public async Task Sync_TerminalOutcome_CancelsPending()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedRoundAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3));
        await SyncAsync();
        await using (var db = NewDb(Owner))
        {
            var e = await db.Entries.Include(x => x.Rounds).FirstAsync(x => x.Id == entryId);
            var r = e.Rounds.Single(x => x.Id == roundId);
            e.UpdateRound(r.Id, r.Stage, r.ScheduledDate, null, null, null,
                InterviewRoundOutcome.Passed, null, null);
            await db.SaveChangesAsync();
        }

        await SyncAsync();

        var rows = await RemindersOfAsync(roundId);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(x => x.Status == RoundReminderStatus.Cancelled);
    }

    [Fact] // 删轮次:其提醒取消(行保留作历史,状态 Cancelled)
    public async Task Sync_RoundRemoved_CancelsPending()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedRoundAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3));
        await SyncAsync();
        await using (var db = NewDb(Owner))
        {
            var e = await db.Entries.Include(x => x.Rounds).FirstAsync(x => x.Id == entryId);
            e.RemoveRound(roundId);
            await db.SaveChangesAsync();
        }

        await SyncAsync();

        var rows = await RemindersOfAsync(roundId);
        rows.Should().OnlyContain(x => x.Status == RoundReminderStatus.Cancelled);
    }

    [Fact] // 删条目(软删):其全部提醒取消
    public async Task Sync_EntryDeleted_CancelsPending()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedRoundAsync(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3));
        await SyncAsync();
        await using (var db = NewDb(Owner))
        {
            var e = await db.Entries.FirstAsync(x => x.Id == entryId);
            e.MarkDeleted();
            await db.SaveChangesAsync();
        }

        await SyncAsync();

        var rows = await RemindersOfAsync(roundId);
        rows.Should().OnlyContain(x => x.Status == RoundReminderStatus.Cancelled);
    }
}
