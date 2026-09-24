using MassTransit;
using MediatR;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Domain;
using YourInterview.SharedContracts;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Services.Interviews.Infrastructure.Services;

/// <summary>
/// 面试条目领域事件 → 跨服务集成事件 的转发器。
///
/// 为什么要有这一层:领域层不应该知道消息总线的存在(否则就无法单测、也耦合了基础设施)。
/// 领域里只 RaiseDomainEvent(纯 C# 对象),到这里再翻译成 RabbitMQ 上的集成事件,
/// 由 Analysis.Worker 之类的下游去消费。
///
/// M1 修复(2026-09-23):「开始转写」触发 Worker(事件带真实 StoragePath 与 UserId)。
/// M1.2(2026-09-24):**转写任务的投递改走台账派发器**(AnalysisJobDispatcher)——
/// 请求内直发的问题是"库提交了、MQ 没发出去"时状态卡死在 Transcribing;
/// 现在任务行与状态变更同事务落库,后台派发器至少一次投递 + 退避重试。
/// 本转发器不再直发 InterviewAnalysisRequested,只保留:
///   · 转写开始/完成:日志(投递交派发器);
///   · 分析完成 → 知识点抽取请求(低价值链路,暂维持直发,M4 一并治理)。
/// </summary>
public sealed class InterviewsIntegrationEventPublisher(
    IPublishEndpoint publish,
    ICurrentUser currentUser,
    ILogger<InterviewsIntegrationEventPublisher> logger)
    : INotificationHandler<DomainEventNotification<InterviewTranscriptionStartedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewTranscribedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewAnalysisAppliedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewAnalysisFailedDomainEvent>>,
      INotificationHandler<DomainEventNotification<InterviewOutcomeRecordedDomainEvent>>
{
    /// <summary>转写开始 —— 任务行已随业务变更同事务落库,由派发器投递,这里只记审计日志。</summary>
    public Task Handle(DomainEventNotification<InterviewTranscriptionStartedDomainEvent> n,
        CancellationToken ct)
    {
        var e = n.DomainEvent;
        logger.LogInformation(
            "转写开始 EntryId={Id} AssetId={AssetId}(路径={Path})→ 任务已登记台账,等待派发器投递",
            e.EntryId, e.AssetId, e.StoragePath ?? "(无本地文件)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 转写完成 —— 只记录。Worker 回写转写稿后自己会推进到分析,
    /// 这里再发分析请求就是重复消费的源头(M1 已修)。
    /// </summary>
    public Task Handle(DomainEventNotification<InterviewTranscribedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("转写完成 EntryId={Id} AssetId={AssetId}(Len={Len})",
            n.DomainEvent.EntryId, n.DomainEvent.AssetId, n.DomainEvent.TextLength);
        return Task.CompletedTask;
    }

    /// <summary>分析完成 → 通知技术栈模块,把答不好的题沉淀成知识点。</summary>
    public async Task Handle(DomainEventNotification<InterviewAnalysisAppliedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogInformation("分析完成 EntryId={Id} 总分={Score} → 发布知识点抽取请求",
            n.DomainEvent.EntryId, n.DomainEvent.OverallScore);

        await publish.Publish(new InterviewKnowledgeExtractionRequested(
            n.DomainEvent.EntryId, n.DomainEvent.CompanyId, n.DomainEvent.OverallScore), ct);
    }

    /// <summary>分析失败 —— 台账已由失败上报端点关单留痕,这里只记日志。</summary>
    public Task Handle(DomainEventNotification<InterviewAnalysisFailedDomainEvent> n,
        CancellationToken ct)
    {
        logger.LogWarning("分析失败 EntryId={Id} 原因={Reason}", n.DomainEvent.EntryId, n.DomainEvent.Reason);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 面试结果登记(M1.5)→ 回写 Tracker:对应轮次 Outcome 更新,被拒时申请转 Rejected。
    /// UserId 取当前 JWT(转发在请求管线内,上下文还在)。
    /// </summary>
    public async Task Handle(DomainEventNotification<InterviewOutcomeRecordedDomainEvent> n,
        CancellationToken ct)
    {
        var e = n.DomainEvent;
        logger.LogInformation("面试结果登记 EntryId={Id} 第 {Round} 轮 = {Result} → 回写 Tracker(申请 {AppId})",
            e.EntryId, e.RoundNo, e.Result, e.ApplicationId);

        await publish.Publish(new SharedContracts.Events.InterviewOutcomeRecorded(
            e.ApplicationId, e.EntryId, currentUser.UserId ?? Guid.Empty, e.RoundNo, e.Result), ct);
    }
}
