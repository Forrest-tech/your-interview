using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MediatR;
using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.BuildingBlocks.Persistence;

/// <summary>
/// EF Core 拦截器:在 SaveChanges 成功后,把聚合根的领域事件转成 MediatR 通知发布出去。
/// 领域事件 → 应用内处理器(同步语义),集成事件 → 消息总线(MassTransit,异步跨服务)。
/// </summary>
public sealed class DomainEventDispatchInterceptor(IPublisher publisher) : SaveChangesInterceptor
{
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await DispatchAsync(eventData.Context, cancellationToken);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        DispatchAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return result;
    }

    private async Task DispatchAsync(DbContext? context, CancellationToken ct)
    {
        if (context is null) return;

        var aggregates = context.ChangeTracker.Entries<Entity>()
            .Where(e => e.Entity.DomainEvents.Count > 0)
            .Select(e => e.Entity)
            .ToList();

        var events = aggregates.SelectMany(a => a.DomainEvents).ToList();
        aggregates.ForEach(a => a.ClearDomainEvents());

        foreach (var domainEvent in events)
        {
            var notificationType = typeof(DomainEventNotification<>).MakeGenericType(domainEvent.GetType());
            var notification = (INotification)Activator.CreateInstance(notificationType, domainEvent)!;
            await publisher.Publish(notification, ct);
        }
    }
}

/// <summary>领域事件的 MediatR 包装,处理器写 INotificationHandler&lt;DomainEventNotification&lt;TEvent&gt;&gt;。</summary>
public sealed class DomainEventNotification<TDomainEvent>(TDomainEvent domainEvent) : INotification
    where TDomainEvent : IDomainEvent
{
    public TDomainEvent DomainEvent { get; } = domainEvent;
}
