using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Infrastructure.Persistence;

/// <summary>
/// 台账拦截器:领域事件 → 分析任务行,与业务变更**同一次 SaveChanges** 落库。
///
/// 为什么在 SavingChanges(保存前)而不是保存后:
///   EF 官方支持在 SavingChanges 里修改正在保存的状态(加实体是文档化用法);
///   在 SavedChanges 里再补插行需要嵌套 SaveChanges —— 并发检测器会拒绝,
///   而且拆成两段就不再原子。这里加的 AnalysisJob 行和状态流转
///   (AssetsUploaded → Transcribing)要么一起提交、要么一起回滚。
///
/// 派发(真正发 MQ)由 AnalysisJobDispatcher 后台服务负责 ——
/// 行落库 ≠ 消息发出,中间隔一次投递重试,这正是发件箱模式的"至少一次"。
/// </summary>
public sealed class AnalysisJobOutboxInterceptor(ICurrentUser currentUser)
    : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await StageJobsAsync(eventData.Context, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        StageJobsAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    private async Task StageJobsAsync(DbContext? context, CancellationToken ct)
    {
        if (context is not InterviewsDbContext db) return;

        // 从被跟踪的聚合上收集"转写开始"事件(此时事件还在实体上,
        // DomainEventDispatchInterceptor 要到 SavedChanges 才取走它们)
        var started = context.ChangeTracker.Entries<Entity>()
            .SelectMany(e => e.Entity.DomainEvents
                .OfType<InterviewTranscriptionStartedDomainEvent>())
            .ToList();
        if (started.Count == 0) return;

        foreach (var evt in started)
        {
            var key = BuildKey(evt.EntryId, AnalysisJobType.Transcription);

            // 单飞检查:该条目已有未关单的任务就不重复登记
            // (正常路径靠聚合的幂等守卫挡在前面,这里挡并发双击)
            // ⚠️ IsOpen 是计算属性进不了查询 —— 必须用 IsOpenFilter(可翻译表达式)
            var exists = await db.AnalysisJobs
                .Where(AnalysisJob.IsOpenFilter)
                .AnyAsync(j => j.IdempotencyKey == key, ct);
            if (exists) continue;

            var payload = JsonSerializer.Serialize(new JobPayload(
                evt.AssetId, evt.StoragePath, currentUser.UserId));
            db.AnalysisJobs.Add(new AnalysisJob(evt.EntryId,
                AnalysisJobType.Transcription, key, payload));
        }
    }

    internal static string BuildKey(Guid entryId, AnalysisJobType type)
        => $"{entryId}:{type.ToString().ToLowerInvariant()}";

    /// <summary>负载契约:派发器按它重建集成事件。字段与 InterviewAnalysisRequested 对齐。</summary>
    internal sealed record JobPayload(Guid AssetId, string? StoragePath, Guid? UserId);
}
