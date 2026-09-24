using MassTransit;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Jobs.Application;

/// <summary>
/// 面试结果回写 Tracker(M1.5):用户在实战机经的条目上填了"结果",
/// 这里把关键词映射成轮次 Outcome 落库;被拒时申请自动转 Rejected。
///
/// Result 是用户原话("通过"/"被拒"/"待定"…),映射规则:
///   未通过/不通过/拒/挂 → Failed(同时申请转 Rejected)
///   通过                → Passed(申请保持 Interview,后面还有轮)
///   待定/其它           → 不动(等人确认了再写)
///
/// 轮次对不上(手工建的条目、轮号错位)是正常场景:记日志跳过,不重试 ——
/// 消息重投三遍也变不出那一轮,别让它进死信队列占地方。
/// </summary>
public sealed class InterviewOutcomeRecordedConsumer(
    JobsDbContext db,
    ILogger<InterviewOutcomeRecordedConsumer> logger) : IConsumer<InterviewOutcomeRecorded>
{
    public async Task Consume(ConsumeContext<InterviewOutcomeRecorded> context)
    {
        var e = context.Message;

        var outcome = MapOutcome(e.Result);
        if (outcome is null)
        {
            logger.LogInformation("结果「{Result}」不含可识别关键词,不回写(申请 {AppId} 第 {Round} 轮)",
                e.Result, e.ApplicationId, e.RoundNo);
            return;
        }

        var app = await db.Applications.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == e.ApplicationId, context.CancellationToken);
        if (app is null)
        {
            logger.LogWarning("申请 {AppId} 不存在(可能已删),结果回写跳过", e.ApplicationId);
            return;
        }

        try
        {
            app.RecordRoundOutcome(e.RoundNo, outcome.Value, $"实战机经回写:{e.Result}");
            await db.SaveChangesAsync(context.CancellationToken);
            logger.LogInformation("结果回写完成:申请 {AppId} 第 {Round} 轮 → {Outcome}",
                e.ApplicationId, e.RoundNo, outcome.Value);
        }
        catch (KeyNotFoundException ex)
        {
            // 条目轮号在 Tracker 里没有对应轮次:手工建条目/轮号错位,正常场景
            logger.LogInformation("申请 {AppId} {Msg},结果回写跳过", e.ApplicationId, ex.Message);
        }
    }

    /// <summary>用户原话 → 轮次结果。注意"未通过"要先于"通过"判断。</summary>
    private static RoundOutcome? MapOutcome(string result)
    {
        var r = result.Trim();
        if (r.Contains("未通过") || r.Contains("不通过") || r.Contains("拒") || r.Contains("挂"))
            return RoundOutcome.Failed;
        if (r.Contains("通过"))
            return RoundOutcome.Passed;
        return null;
    }
}
