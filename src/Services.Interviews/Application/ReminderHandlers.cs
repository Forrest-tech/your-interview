using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 提醒读模型(CONF-CAL-2):联表带出公司/岗位/阶段/排期原文,前端不需二次拼装。
/// StartAt = 开场时刻(ReminderSchedule 口径);IsDue = 已到触发时刻且未确认。
/// </summary>
public sealed record RoundReminderDto(
    Guid Id, Guid EntryId, Guid RoundId,
    string CompanyName, string Role, string Stage, int RoundOrder,
    DateOnly? ScheduledDate, string? ScheduledTime, DateTimeOffset StartAt,
    string Kind, DateTimeOffset RemindAt, bool IsDue, string Status);

public sealed record GetRemindersQuery(Guid UserId, bool DueOnly = false)
    : IRequest<Result<List<RoundReminderDto>>>;

public sealed record AcknowledgeReminderCommand(Guid UserId, Guid ReminderId) : IRequest<Result>;

/// <summary>
/// 提醒列表:先做一次用户级对账(SyncUserAsync —— 存量排期回填 + 旁路变更自愈,
/// 见 SPEC 设计决定 5),再读 Pending 行。已确认/已取消不出现在列表里:
/// 确认即消失是 ack 的语义,取消是系统内部状态。
/// </summary>
public sealed class GetRemindersQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetRemindersQuery, Result<List<RoundReminderDto>>>
{
    public async Task<Result<List<RoundReminderDto>>> Handle(GetRemindersQuery request, CancellationToken ct)
    {
        await ReminderSync.SyncUserAsync(db, request.UserId, ct);
        await db.SaveChangesAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var rows = await db.RoundReminders.IgnoreQueryFilters()
            .Where(x => x.UserId == request.UserId && x.Status == RoundReminderStatus.Pending)
            .OrderBy(x => x.RemindAt)
            .ToListAsync(ct);
        if (request.DueOnly)
            rows = rows.Where(x => x.RemindAt <= now).ToList();
        if (rows.Count == 0)
            return Result.Success(new List<RoundReminderDto>());

        var entryIds = rows.Select(x => x.EntryId).Distinct().ToList();
        var entries = await db.Entries.IgnoreQueryFilters()
            .Include(x => x.Rounds)
            .Where(x => entryIds.Contains(x.Id) && x.UserId == request.UserId && !x.IsDeleted)
            .ToListAsync(ct);
        var entryById = entries.ToDictionary(x => x.Id);
        var roundById = entries.SelectMany(e => e.Rounds).ToDictionary(r => r.Id);

        var result = new List<RoundReminderDto>();
        foreach (var row in rows)
        {
            // 条目/轮次已被删而提醒尚未及取消的极端时序:跳过该行(下轮对账会取消),
            // 不让脏行挡住整表读取。
            if (!entryById.TryGetValue(row.EntryId, out var entry)) continue;
            if (!roundById.TryGetValue(row.RoundId, out var round)) continue;
            if (round.ScheduledDate is null) continue;   // 防御:无日期轮次本不应有 Pending 提醒
            result.Add(new RoundReminderDto(
                row.Id, row.EntryId, row.RoundId,
                entry.CompanyName, entry.Role, round.Stage, round.Order,
                round.ScheduledDate, round.ScheduledTime,
                ReminderSchedule.ComputeStart(round.ScheduledDate!.Value, round.ScheduledTime),
                row.Kind.ToString(), row.RemindAt, row.RemindAt <= now, row.Status.ToString()));
        }
        return Result.Success(result);
    }
}

/// <summary>确认提醒:仅本人可确认,他人的提醒按不存在处理(票03 隔离口径,回 404)。</summary>
public sealed class AcknowledgeReminderCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AcknowledgeReminderCommand, Result>
{
    public async Task<Result> Handle(AcknowledgeReminderCommand request, CancellationToken ct)
    {
        var row = await db.RoundReminders.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == request.ReminderId && x.UserId == request.UserId, ct);
        if (row is null) return Result.Failure(Error.NotFound("提醒"));
        row.Acknowledge(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
