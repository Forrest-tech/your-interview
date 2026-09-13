namespace YourInterview.SharedContracts.Messaging;

/// <summary>
/// 消息拓扑常量:队列/交换机名集中定义,避免各服务写错字符串。
/// 面试可讲:集中定义拓扑 + 统一命名规范 = 避免"幽灵队列"和路由错配。
/// </summary>
public static class MessageTopology
{
    public const string ExchangeName = "your-interview.events";
    public const string DeadLetterExchange = "your-interview.events.dlq";

    public static class Queues
    {
        public const string AnalysisRequests = "analysis.requests";
        public const string AnalysisResults = "analysis.results";
        public const string AnalyticsProjections = "analytics.projections";
        public const string IdentityAudit = "identity.audit";
        public const string Notifications = "notifications";
    }

    public static class RoutingKeys
    {
        public const string RecordingUploaded = "interview.recording.uploaded";
        public const string AnalysisCompleted = "interview.analysis.completed";
        public const string AnalysisFailed = "interview.analysis.failed";
        public const string JobStatusChanged = "job.application.status-changed";
        public const string MasteryChanged = "knowledge.mastery.changed";
        public const string UserRegistered = "identity.user.registered";
        public const string AuditRecorded = "identity.audit.recorded";
    }
}
