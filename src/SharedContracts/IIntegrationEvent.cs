namespace YourInterview.SharedContracts;

/// <summary>
/// 集成事件(跨服务、经 RabbitMQ 传递)。与领域事件严格区分:
/// 领域事件 = 服务内部同步语义;集成事件 = 跨边界、必须可版本化、必须幂等。
/// 命名用过去式(已发生的事实),一律带 EventId + OccurredOn 以便去重。
/// </summary>
public interface IIntegrationEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOn { get; }
}

public abstract record IntegrationEvent : IIntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
