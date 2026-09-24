using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Interviews.Infrastructure.Services;

/// <summary>
/// 分析任务派发器:台账(Pending)→ RabbitMQ,至少一次投递。
///
/// 三件事:
///   1. 常规投递:Pending 且到期的任务 → 发 InterviewAnalysisRequested → Dispatched;
///   2. 失败退避:投递异常(MQ 不可达)→ 指数退避,耗尽判 Dead(不静默丢);
///   3. 卡单补发:Dispatched 超过 15 分钟且条目仍在 Transcribing(消息在
///      传输中丢了 / Worker 全挂了)→ 重投,给系统一条自愈路径。
///
/// 与直接在请求里 Publish 的差别:那边"库提交成功 + 发消息失败"= 状态停在
/// Transcribing 却没人干活,只能人工救;这边消息发不出去时任务行还在,
/// 派发器会一直试到成功或明确 Dead —— 失败可见、可重试、可追溯。
/// </summary>
public sealed class AnalysisJobDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<AnalysisJobDispatcher> logger) : BackgroundService
{
    /// <summary>轮询间隔。派发是后台动作,10 秒粒度足够(上传→分析本就是异步流水线)。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>卡单判定:Dispatched 超过这个时长且条目仍在转写中 → 疑似消息丢失。</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(15);

    /// <summary>单轮最多处理多少个任务 —— 避免一个积压批次把循环占满一分钟以上。</summary>
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("分析任务派发器已启动(轮询 {Interval}s,卡单阈值 {Stuck}m)",
            PollInterval.TotalSeconds, StuckThreshold.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchPendingBatchAsync(stoppingToken);
                await ReapStuckDispatchedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 单轮失败不退出:派发器是长驻的自愈组件,崩了就没人投递了
                logger.LogWarning(ex, "任务派发循环异常,下一轮重试");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task DispatchPendingBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewsDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var now = DateTimeOffset.UtcNow;

        // 到期 = 没排过退避(NextAttemptAt 为空)或退避时间已过
        var due = await db.AnalysisJobs
            .Where(j => j.Status == AnalysisJobStatus.Pending
                && (j.NextAttemptAt == null || j.NextAttemptAt <= now))
            .OrderBy(j => j.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var job in due)
            await TryPublishAsync(db, publish, job, ct);

        if (due.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task ReapStuckDispatchedAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewsDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        var threshold = DateTimeOffset.UtcNow - StuckThreshold;

        // 条目仍在 Transcribing = 派发出去的消息没有产生任何回写
        var stuck = await (from j in db.AnalysisJobs
                           join e in db.Entries on j.InterviewEntryId equals e.Id
                           where j.Status == AnalysisJobStatus.Dispatched
                                 && j.DispatchedAt < threshold
                                 && e.Status == InterviewStatus.Transcribing
                                 && j.Attempts < j.MaxAttempts
                           select j)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var job in stuck)
        {
            logger.LogWarning("任务 {JobId}(条目 {EntryId})派发后 {Minutes:F0} 分钟无回写,补发一次",
                job.Id, job.InterviewEntryId, StuckThreshold.TotalMinutes);
            await TryPublishAsync(db, publish, job, ct);
        }

        if (stuck.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private async Task TryPublishAsync(InterviewsDbContext db, IPublishEndpoint publish,
        AnalysisJob job, CancellationToken ct)
    {
        try
        {
            var evt = BuildEvent(job);
            await publish.Publish(evt, ct);
            job.MarkDispatched();
            logger.LogInformation("任务 {JobId} 已投递(第 {Attempts} 次)", job.Id, job.Attempts + 1);
        }
        catch (Exception ex)
        {
            // 消息总线不可达等传输层失败:留痕 + 退避,绝不丢任务
            job.MarkAttemptFailed(ex.Message);
            logger.LogError(ex, "任务 {JobId} 投递失败(第 {Attempts}/{Max} 次)",
                job.Id, job.Attempts, job.MaxAttempts);
        }
    }

    /// <summary>台账行 → 集成事件。负载字段与 Worker 消费端约定一一对应。</summary>
    private static InterviewAnalysisRequested BuildEvent(AnalysisJob job)
    {
        var payload = JsonSerializer
            .Deserialize<AnalysisJobOutboxInterceptor.JobPayload>(job.PayloadJson);
        return new InterviewAnalysisRequested(
            job.InterviewEntryId,
            payload?.AssetId ?? Guid.Empty,
            payload?.UserId ?? Guid.Empty,
            payload?.StoragePath,
            TranscriptText: null);
    }
}
