using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Jobs.Infrastructure.Services;

/// <summary>
/// 领域事件 → 集成事件 的转发器。
/// 业务代码只 RaiseDomainEvent(纯领域),不依赖消息总线;
/// 这里把内部领域事件翻译成跨服务集成事件发到 RabbitMQ。
///
/// UserId 从领域事件自带(聚合产生事件时带入聚合的 UserId)——
/// 不再依赖 ICurrentUser:消费者作用域内转发时 HttpContext 为空,
/// 旧写法会发出 UserId=Guid.Empty 的事件污染 Analytics 投影(COD-01 §5.3)。
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
        logger.LogInformation("发布集成事件 ApplicationId={Id} CompanyId={CompanyId} {From} → {Status}",
            e.ApplicationId, e.CompanyId, e.FromStatus, e.ToStatus);

        await publishEndpoint.Publish(
            new JobApplicationStatusChanged(e.ApplicationId, e.UserId,
                e.CompanyId, e.FromStatus, e.ToStatus),
            cancellationToken);
    }
}

/// <summary>
/// 邀请登记 → InterviewInviteRecorded(M1.4):Interviews 收到后自动建 Playbook 草稿。
///
/// 聚合只认得 CompanyId;草稿要能看,得有公司名与 JD 摘要 ——
/// 这里查库补全(转发器在请求管线内、事务已提交,查得到)。
/// </summary>
public sealed class InterviewInvitationPublisher(
    JobsDbContext db,
    IPublishEndpoint publishEndpoint,
    ILogger<InterviewInvitationPublisher> logger)
    : INotificationHandler<DomainEventNotification<InterviewInvitationRecordedDomainEvent>>
{
    public async Task Handle(
        DomainEventNotification<InterviewInvitationRecordedDomainEvent> notification,
        CancellationToken cancellationToken)
    {
        var e = notification.DomainEvent;

        // 按确切 Id 补全:发布器可能在消费者作用域运行(无当前用户,过滤器 fail-closed),
        // 故 IgnoreQueryFilters 按 Id 直查 —— 归属由事件自带的 UserId 决定,不靠过滤器。
        var companyName = await db.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.Id == e.CompanyId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken);
        var jdSummary = await db.Applications.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Id == e.ApplicationId)
            .Select(a => a.JdSummary)
            .FirstOrDefaultAsync(cancellationToken);

        await publishEndpoint.Publish(new InterviewInviteRecorded(
            e.ApplicationId, e.UserId, e.CompanyId,
            companyName ?? "未知公司", e.Role, jdSummary, e.RoundNo, e.Stage,
            e.ScheduledDate, e.Format, e.Interviewer), cancellationToken);

        logger.LogInformation("发布邀请登记事件:{Company} · {Role} 第 {Round} 轮(申请 {AppId},用户 {UserId})",
            companyName, e.Role, e.RoundNo, e.ApplicationId, e.UserId);
    }
}
