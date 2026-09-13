namespace YourInterview.BuildingBlocks.Domain;

/// <summary>
/// 领域事件标记接口。聚合根通过 RaiseDomainEvent 发布,由基础设施在 SaveChanges 时派发。
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOn { get; }
}
