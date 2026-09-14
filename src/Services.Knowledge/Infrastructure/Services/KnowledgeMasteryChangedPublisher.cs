using MassTransit;
using MediatR;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Knowledge.Infrastructure.Services;

/// <summary>
/// 掌握度变化 → 集成事件。
/// Analytics 服务订阅它来刷新"能力雷达图";未来也可通知前端做成就提示。
/// </summary>
public sealed class KnowledgeMasteryChangedPublisher(
    IPublishEndpoint publish,
    ILogger<KnowledgeMasteryChangedPublisher> logger)
    : INotificationHandler<DomainEventNotification<KnowledgeMasteryChangedDomainEvent>>
{
    public async Task Handle(DomainEventNotification<KnowledgeMasteryChangedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("掌握度变化 ItemId={Id} {Prev} → {Now}",
            n.DomainEvent.KnowledgeItemId, n.DomainEvent.PreviousLevel, n.DomainEvent.NewLevel);

        await publish.Publish(new KnowledgeLevelChanged(
            n.DomainEvent.KnowledgeItemId, Guid.Empty, "unknown", "unknown", n.DomainEvent.NewLevel), ct);
    }
}
