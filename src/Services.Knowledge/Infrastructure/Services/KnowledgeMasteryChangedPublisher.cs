using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Knowledge.Infrastructure.Services;

/// <summary>
/// 掌握度变化 → 集成事件。
/// Analytics 服务订阅它来刷新"能力雷达图";未来也可通知前端做成就提示。
/// </summary>
public sealed class KnowledgeMasteryChangedPublisher(
    IPublishEndpoint publish,
    KnowledgeDbContext db,
    ILogger<KnowledgeMasteryChangedPublisher> logger)
    : INotificationHandler<DomainEventNotification<KnowledgeMasteryChangedDomainEvent>>
{
    public async Task Handle(DomainEventNotification<KnowledgeMasteryChangedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("掌握度变化 ItemId={Id} {Prev} → {Now}",
            n.DomainEvent.KnowledgeItemId, n.DomainEvent.PreviousLevel, n.DomainEvent.NewLevel);

        // COD-01/裁决 C:旧实现恒发 Guid.Empty + "unknown",Analytics 侧无法归属到人。
        // 改为回查条目本体填真实 UserId / Topic / Mastery —— 事件分发发生在 SaveChanges
        // 的拦截器里(请求作用域尚在,但条目可能来自任意路径),用 IgnoreQueryFilters
        // 按 Id 直查,查不到则记日志跳过,绝不发假归属事件。
        var item = await db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == n.DomainEvent.KnowledgeItemId, ct);
        if (item is null)
        {
            logger.LogWarning("掌握度变化的条目 {Id} 不存在,跳过集成事件", n.DomainEvent.KnowledgeItemId);
            return;
        }

        await publish.Publish(new KnowledgeLevelChanged(
            item.Id, item.UserId, item.Topic, item.Mastery.ToString(),
            n.DomainEvent.NewLevel), ct);
    }
}
