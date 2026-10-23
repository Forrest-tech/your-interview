using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Knowledge.Infrastructure.Services;

/// <summary>
/// 条目生命周期(新建/恢复/删除)→ 集成事件(票91,补票34 申报的发布侧缺口)。
///
/// 为什么单独一个发布器而不动 KnowledgeMasteryChangedPublisher:
/// 掌握度变化走 PromoteMastery 既有路径(不变项),生命周期是另一条语义线 ——
/// 新建/恢复发既有 KnowledgeLevelChanged(消费侧台账对「没见过的条目」即 Total+1,
/// 与票34 消费者口径天然对齐,不需要新契约);删除必须让台账把条目移出分布,
/// KnowledgeLevelChanged 没有「移除」语义,故发新契约 KnowledgeItemDeleted,
/// 带足条目 id/用户/原主题/原等级(字符串 + 枚举 int,同 MasteryLevelMapping 口径)。
///
/// 与既有发布器同规矩:事件分发在 SaveChanges 之后(拦截器),回查条目本体填真实值,
/// 用 IgnoreQueryFilters 按 Id 直查(删除事件到达时条目已软删,普通查询查不到);
/// 查不到只记日志跳过,绝不发假归属事件。
/// </summary>
public sealed class KnowledgeItemLifecyclePublisher(
    IPublishEndpoint publish,
    KnowledgeDbContext db,
    ILogger<KnowledgeItemLifecyclePublisher> logger)
    : INotificationHandler<DomainEventNotification<KnowledgeItemCreatedDomainEvent>>,
      INotificationHandler<DomainEventNotification<KnowledgeItemDeletedDomainEvent>>
{
    public async Task Handle(DomainEventNotification<KnowledgeItemCreatedDomainEvent> n,
        CancellationToken ct)
    {
        var item = await db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == n.DomainEvent.KnowledgeItemId, ct);
        if (item is null)
        {
            logger.LogWarning("新建/恢复的条目 {Id} 不存在,跳过集成事件", n.DomainEvent.KnowledgeItemId);
            return;
        }

        // 以保存后的最终状态为准:导入路径在构造后还会 PromoteMastery,
        // 那条事件随后到达消费侧时同桶 no-op,不会重复计 Total。
        logger.LogInformation("条目进入集合 ItemId={Id} Topic={Topic} Mastery={Mastery}",
            item.Id, item.Topic, item.Mastery);
        await publish.Publish(new KnowledgeLevelChanged(
            item.Id, item.UserId, item.Topic, item.Mastery.ToString(), (int)item.Mastery), ct);
    }

    public async Task Handle(DomainEventNotification<KnowledgeItemDeletedDomainEvent> n,
        CancellationToken ct)
    {
        var item = await db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == n.DomainEvent.KnowledgeItemId, ct);
        if (item is null)
        {
            logger.LogWarning("被删除的条目 {Id} 不存在,跳过集成事件", n.DomainEvent.KnowledgeItemId);
            return;
        }

        // 原等级以领域事件捕获的删除前值为准(int),字符串取本体 Mastery ——
        // 删除不改 Mastery,两者一致;双载是为与既有契约口径对齐(字符串优先/int 回落)。
        logger.LogInformation("条目删除 ItemId={Id} Topic={Topic} Mastery={Mastery}",
            item.Id, item.Topic, item.Mastery);
        await publish.Publish(new KnowledgeItemDeleted(
            item.Id, item.UserId, item.Topic, item.Mastery.ToString(),
            n.DomainEvent.PreviousLevel), ct);
    }
}
