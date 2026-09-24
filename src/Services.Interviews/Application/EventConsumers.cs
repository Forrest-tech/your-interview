using MassTransit;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 邀请登记 → 自动建 Playbook 条目草稿(M1.4)。
///
/// Tracker 里"要面试了"的那一刻,实战机经就该有一个等着装录音的条目:
/// 公司、岗位、轮次、时间、形式都从 Tracker 带过来 ——
/// 用户面试完直接传录音开转写,不用再手建条目、手填信息。
///
/// 幂等键 = (申请 Id, 轮次号):消息至少一次投递、状态来回切换、
/// 先切状态后补轮次 —— 同一轮永远只留一个条目;
/// 已有草稿时只补空字段(用户填过的内容不覆盖);
/// 条目已推进(转写中/已完成)则重放事件不动它。
/// </summary>
public sealed class InterviewInviteRecordedConsumer(
    InterviewsDbContext db,
    ILogger<InterviewInviteRecordedConsumer> logger) : IConsumer<InterviewInviteRecorded>
{
    public async Task Consume(ConsumeContext<InterviewInviteRecorded> context)
    {
        var e = context.Message;

        var entry = await db.Entries.FirstOrDefaultAsync(x =>
            x.JobApplicationId == e.ApplicationId && x.RoundNo == e.RoundNo,
            context.CancellationToken);

        if (entry is null)
        {
            entry = new InterviewEntry(e.CompanyId, e.CompanyName, e.Role, e.ApplicationId);
            entry.UpdateBasicInfo(e.CompanyName, e.Role, null, null, e.JdSummary,
                e.RoundNo, e.InterviewDate, e.Format, e.Interviewers, null, null, null);
            db.Entries.Add(entry);
            logger.LogInformation("邀请登记自动建稿:{Company} · {Role} 第 {Round} 轮(申请 {AppId})",
                e.CompanyName, e.Role, e.RoundNo, e.ApplicationId);
        }
        else if (entry.Status == InterviewStatus.Draft)
        {
            // 草稿期的重复邀请(比如先切状态建的裸草稿、后补的轮次细节):补空不覆盖
            entry.UpdateBasicInfo(entry.CompanyName, entry.Role, entry.CompanyProfile,
                entry.JdText,
                entry.JdSummary ?? e.JdSummary,
                entry.RoundNo,
                entry.InterviewDate ?? e.InterviewDate,
                entry.InterviewFormat ?? e.Format,
                entry.Interviewers ?? e.Interviewers,
                entry.Location, entry.Result, entry.Notes);
            logger.LogInformation("邀请登记补全草稿:{Company} 第 {Round} 轮(申请 {AppId})",
                entry.CompanyName, entry.RoundNo, e.ApplicationId);
        }
        else
        {
            // 已推进的条目(转写/分析中或已完成):邀请重放不影响
            logger.LogInformation("申请 {AppId} 第 {Round} 轮条目已推进到 {Status},跳过邀请处理",
                e.ApplicationId, e.RoundNo, entry.Status);
            return;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
