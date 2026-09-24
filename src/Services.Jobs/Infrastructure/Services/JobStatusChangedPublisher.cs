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
/// UserId 从 ICurrentUser 取(转发发生在请求管线内,JWT 上下文还在)——
/// 之前发 Guid.Empty,下游 Analytics 存下来的事件全都没有主人。
/// </summary>
public sealed class JobStatusChangedPublisher(
    IPublishEndpoint publishEndpoint,
    ICurrentUser currentUser,
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
            new JobApplicationStatusChanged(e.ApplicationId, currentUser.UserId ?? Guid.Empty,
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
    ICurrentUser currentUser,
    ILogger<InterviewInvitationPublisher> logger)
    : INotificationHandler<DomainEventNotification<InterviewInvitationRecordedDomainEvent>>
{
    public async Task Handle(
        DomainEventNotification<InterviewInvitationRecordedDomainEvent> notification,
        CancellationToken cancellationToken)
    {
        var e = notification.DomainEvent;

        var companyName = await db.Companies.AsNoTracking()
            .Where(c => c.Id == e.CompanyId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(cancellationToken);
        var jdSummary = await db.Applications.AsNoTracking()
            .Where(a => a.Id == e.ApplicationId)
            .Select(a => a.JdSummary)
            .FirstOrDefaultAsync(cancellationToken);

        await publishEndpoint.Publish(new InterviewInviteRecorded(
            e.ApplicationId, currentUser.UserId ?? Guid.Empty, e.CompanyId,
            companyName ?? "未知公司", e.Role, jdSummary, e.RoundNo, e.Stage,
            e.ScheduledDate, e.Format, e.Interviewer), cancellationToken);

        logger.LogInformation("发布邀请登记事件:{Company} · {Role} 第 {Round} 轮(申请 {AppId},用户 {UserId})",
            companyName, e.Role, e.RoundNo, e.ApplicationId, currentUser.UserId);
    }
}
