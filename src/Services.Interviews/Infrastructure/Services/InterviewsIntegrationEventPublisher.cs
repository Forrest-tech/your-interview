using MassTransit;
using MediatR;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Domain;
using YourInterview.SharedContracts;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Interviews.Infrastructure.Services;

/// <summary>
/// 面试条目领域事件 → 跨服务集成事件 的转发器。
///
/// 为什么要有这一层:领域层不应该知道消息总线的存在(否则就无法单测、也耦合了基础设施)。
/// 领域里只 RaiseDomainEvent(纯 C# 对象),到这里再翻译成 RabbitMQ 上的集成事件,
/// 由 Analysis.Worker 之类的下游去消费。
///
/// M1 修复(2026-09-23):
///   · 转写**开始**(不是完成)才是触发 Worker 的正确时机 —— 之前"开始转写"只改状态
///     不发事件,点完按钮流水线纹丝不动;
///   · 事件带上录音真实 StoragePath 与 UserId —— Worker 不再猜路径(猜错 = 必定失败);
///   · 转写**完成**不再触发分析请求 —— Worker 自己的回写流程已含"推进到分析"那一步,
///     之前两头都发会造成"分析→回写转写稿→又触发分析"的消息风暴(重复烧转写额度)。
/// </summary>
public sealed class InterviewsIntegrationEventPublisher(
    IPublishEndpoint publish,
    ICurrentUser currentUser,
    ILogger<InterviewsIntegrationEventPublisher> logger)
    : INotificationHandler<DomainEventNotification<InterviewTranscriptionStartedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewTranscribedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewAnalysisAppliedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewAnalysisFailedDomainEvent>>
{
    /// <summary>转写开始 → 通知 Analysis.Worker 跑完整管线(STT → 六维诊断 → 回写)。</summary>
    public async Task Handle(DomainEventNotification<InterviewTranscriptionStartedDomainEvent> n,
        CancellationToken ct)
    {
        var e = n.DomainEvent;
        logger.LogInformation(
            "转写开始 EntryId={Id} AssetId={AssetId} → 发布分析请求(路径={Path},用户={User})",
            e.EntryId, e.AssetId, e.StoragePath ?? "(无本地文件)", currentUser.UserId);

        await publish.Publish(new InterviewAnalysisRequested(
            e.EntryId, e.AssetId,
            currentUser.UserId ?? Guid.Empty,
            StoragePath: e.StoragePath,
            TranscriptText: null), ct);
    }

    /// <summary>
    /// 转写完成 —— 只记录,不再发布分析请求(Worker 回写转写稿后自己会推进到分析,
    /// 这里再发一次就是重复消费 + 消息风暴的源头)。
    /// 事件本身保留:后续 Analytics 读模型可以订阅它做"转写吞吐"统计。
    /// </summary>
    public Task Handle(DomainEventNotification<InterviewTranscribedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("转写完成 EntryId={Id} AssetId={AssetId}(Len={Len})",
            n.DomainEvent.EntryId, n.DomainEvent.AssetId, n.DomainEvent.TextLength);
        return Task.CompletedTask;
    }

    /// <summary>分析完成 → 通知技术栈模块,把答不好的题沉淀成知识点。</summary>
    public async Task Handle(DomainEventNotification<InterviewAnalysisAppliedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("分析完成 EntryId={Id} 总分={Score} → 发布知识点抽取请求",
            n.DomainEvent.EntryId, n.DomainEvent.OverallScore);

        await publish.Publish(new InterviewKnowledgeExtractionRequested(
            n.DomainEvent.EntryId, n.DomainEvent.CompanyId, n.DomainEvent.OverallScore), ct);
    }

    /// <summary>分析失败 → 通知前端可重试(前端监听或轮询状态)。</summary>
    public Task Handle(DomainEventNotification<InterviewAnalysisFailedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogWarning("分析失败 EntryId={Id} 原因={Reason}", n.DomainEvent.EntryId, n.DomainEvent.Reason);
        return Task.CompletedTask;
    }
}
