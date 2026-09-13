using MassTransit;
using MediatR;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Jobs.Domain;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

/// <summary>
/// 领域事件 → 集成事件 的转发器。
/// 业务代码只 RaiseDomainEvent(纯领域),不依赖消息总线;
/// 这里把内部领域事件翻译成跨服务集成事件发到 RabbitMQ。
/// </summary>
public sealed class JobStatusChangedPublisher(
    IPublishEndpoint publishEndpoint,
    ILogger<JobStatusChangedPublisher> logger)
    : INotificationHandler<DomainEventNotification<JobApplicationStatusChangedDomainEvent>>
{
    public async Task Handle(
        DomainEventNotification<JobApplicationStatusChangedDomainEvent> notification,
        CancellationToken cancellationToken)
    {
        var e = notification.DomainEvent;
        logger.LogInformation("发布集成事件 ApplicationId={Id} CompanyId={CompanyId} → {Status}",
            e.ApplicationId, e.CompanyId, e.ToStatus);

        await publishEndpoint.Publish(
            new JobApplicationStatusChanged(e.ApplicationId, Guid.Empty, e.CompanyId, "unknown", e.ToStatus),
            cancellationToken);
    }
}
