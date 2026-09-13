namespace YourInterview.SharedContracts.Events;

/// <summary>Interview 服务:录音/文本上传完成 → 触发异步分析管线。</summary>
public sealed record InterviewRecordingUploaded(
    Guid InterviewSessionId,
    Guid CompanyId,
    Guid UserId,
    Guid AssetId,
    string FileName,
    string StoragePath,
    string ContentType,
    long SizeBytes,
    string? Language = "en-US"
) : IntegrationEvent;

/// <summary>Analysis.Worker:分析管线完成 → 回写结果 + 通知前端。</summary>
public sealed record InterviewAnalysisCompleted(
    Guid InterviewSessionId,
    Guid AssetId,
    Guid UserId,
    string ReportPath,
    int PronunciationScore,
    int StructureScore,
    int FluencyScore
) : IntegrationEvent;

/// <summary>Analysis.Worker:分析失败 → 记录原因,允许重试。</summary>
public sealed record InterviewAnalysisFailed(
    Guid InterviewSessionId,
    Guid AssetId,
    Guid UserId,
    string Reason
) : IntegrationEvent;

/// <summary>Assessment 服务:一次 AI 模拟答题完成 → 触发多维分析。</summary>
public sealed record MockAnswerSubmitted(
    Guid MockSessionId,
    Guid TurnId,
    Guid UserId,
    Guid QuestionId,
    string Transcript,
    string? AudioPath,
    string? ReferenceAnswer
) : IntegrationEvent;

/// <summary>JobTracker:投递状态流转 → 通知 Analytics 刷新读模型。</summary>
public sealed record JobApplicationStatusChanged(
    Guid ApplicationId,
    Guid UserId,
    Guid CompanyId,
    string FromStatus,
    string ToStatus
) : IntegrationEvent;

/// <summary>Knowledge:某技术条目掌握度变化 → Analytics 刷新能力雷达。</summary>
public sealed record KnowledgeMasteryChanged(
    Guid KnowledgeItemId,
    Guid UserId,
    int PreviousLevel,
    int NewLevel
) : IntegrationEvent;

/// <summary>Identity:新用户注册 → 初始化该用户的默认工作区(默认知识库、默认管道列)。</summary>
public sealed record UserRegistered(Guid UserId, string Email, string DisplayName) : IntegrationEvent;

/// <summary>审计事件:任何敏感写操作 → Identity 审计流水(合规/PIPEDA 需要)。</summary>
public sealed record AuditEventRecorded(
    Guid UserId,
    string Action,
    string Resource,
    string? ResourceId,
    string? Detail,
    string? IpAddress
) : IntegrationEvent;
