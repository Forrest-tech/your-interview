using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// CONF-CAL-2 提醒同步(派生数据对账)。规则(SPEC 设计决定 3/4/5):
///  应有提醒的轮次 = 有排期日期 且 结果仍 Pending:
///   · 缺行 → 新建 Pending;
///   · 已有行 RemindAt 变了,或行处于 Cancelled(取消原因已消失)→ Reschedule 回 Pending;
///   · RemindAt 没变且未取消 → 不动(已确认 Acknowledged 态保住)。
///  不应有提醒的轮次(无日期 / 结果终态 / 轮次或条目已删):其 Pending 行一律 Cancel。
/// 幂等:同一排期重复同步零写入。
/// 归属用显式 UserId 谓词 + IgnoreQueryFilters,不依赖环境全局过滤器
/// (请求上下文与消费者/后台上下文行为一致)。
/// </summary>
public static class ReminderSync
{
    /// <summary>对账某用户的全部条目/轮次(查询侧兜底 + 存量排期回填)。只改跟踪状态,落库由调用方 SaveChanges。</summary>
    public static Task SyncUserAsync(InterviewsDbContext db, Guid userId, CancellationToken ct = default)
        => SyncCoreAsync(db, userId, entryId: null, ct);

    /// <summary>对账某一条目(命令处理器挂钩用,范围更窄)。只改跟踪状态,落库由调用方 SaveChanges。</summary>
    public static Task SyncEntryAsync(InterviewsDbContext db, Guid userId, Guid entryId, CancellationToken ct = default)
        => SyncCoreAsync(db, userId, entryId, ct);

    /// <summary>
    /// 挂钩用安全版:主操作已落库,派生同步失败不连坐主操作 —— 吞异常,
    /// 失败由查询侧 SyncUserAsync 对账自愈(见 SPEC 设计决定 5)。
    /// </summary>
    public static async Task SafeSyncEntryAsync(InterviewsDbContext db, Guid userId, Guid entryId, CancellationToken ct = default)
    {
        try
        {
            await SyncEntryAsync(db, userId, entryId, ct);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // 派生数据:主操作(轮次增改删)已经落库,不许因提醒重算失败而报错;
            // 不一致窗口由下一次提醒查询的 SyncUserAsync 对账补平。
        }
    }

    private static async Task SyncCoreAsync(InterviewsDbContext db, Guid userId, Guid? entryId, CancellationToken ct)
    {
        var entriesQuery = db.Entries.IgnoreQueryFilters()
            .Include(x => x.Rounds)
            .Where(x => x.UserId == userId && !x.IsDeleted);
        if (entryId is { } eid) entriesQuery = entriesQuery.Where(x => x.Id == eid);
        var entries = await entriesQuery.ToListAsync(ct);

        var remindersQuery = db.RoundReminders.IgnoreQueryFilters()
            .Where(x => x.UserId == userId);
        if (entryId is { } eid2) remindersQuery = remindersQuery.Where(x => x.EntryId == eid2);
        var reminders = await remindersQuery.ToListAsync(ct);
        var byKey = reminders.ToDictionary(x => (x.RoundId, x.Kind));

        var desired = new HashSet<(Guid RoundId, RoundReminderKind Kind)>();
        foreach (var entry in entries)
        {
            foreach (var round in entry.Rounds)
            {
                if (round.Outcome != InterviewRoundOutcome.Pending) continue;
                foreach (var (kind, remindAt) in ReminderSchedule.Compute(round.ScheduledDate, round.ScheduledTime))
                {
                    desired.Add((round.Id, kind));
                    if (byKey.TryGetValue((round.Id, kind), out var row))
                    {
                        if (row.RemindAt != remindAt || row.Status == RoundReminderStatus.Cancelled)
                            row.Reschedule(remindAt);
                    }
                    else
                    {
                        var created = new RoundReminder(userId, entry.Id, round.Id, kind, remindAt);
                        db.RoundReminders.Add(created);
                        byKey[(round.Id, kind)] = created;
                    }
                }
            }
        }

        foreach (var row in reminders)
        {
            if (row.Status == RoundReminderStatus.Pending && !desired.Contains((row.RoundId, row.Kind)))
                row.Cancel();
        }
    }
}
