using MassTransit;
using MediatR;
using YourInterview.BuildingBlocks.Persistence;
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
/// </summary>
public sealed class InterviewsIntegrationEventPublisher(
    IPublishEndpoint publish,
    ILogger<InterviewsIntegrationEventPublisher> logger)
    : INotificationHandler<DomainEventNotification<InterviewTranscribedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewAnalysisAppliedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewAnalysisFailedDomainEvent>>
{
    /// <summary>转写完成 → 通知分析流水线开始六维分析。</summary>
    public async Task Handle(DomainEventNotification<InterviewTranscribedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("转写完成 EntryId={Id} → 发布分析请求(Len={Len})",
            n.DomainEvent.EntryId, n.DomainEvent.TextLength);

        await publish.Publish(new InterviewAnalysisRequested(
            n.DomainEvent.EntryId, n.DomainEvent.AssetId, Guid.Empty,
            StoragePath: null, TranscriptText: null), ct);
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
