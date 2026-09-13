namespace YourInterview.BuildingBlocks.Domain;

/// <summary>领域事件基类:统一带 EventId + 发生时间,便于去重和审计。</summary>
public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
