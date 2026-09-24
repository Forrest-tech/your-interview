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

/// <summary>
/// JobTracker:邀请登记(状态进入面试 / 新增面试轮次)→
/// Interviews 收到后自动创建 Playbook 条目草稿(M1.4)。
/// 面试一确认,用户在实战机经里就有现成的条目等着挂录音,不用再手填公司岗位。
/// </summary>
public sealed record InterviewInviteRecorded(
    Guid ApplicationId,
    Guid UserId,
    Guid CompanyId,
    string CompanyName,
    string Role,
    string? JdSummary,
    int RoundNo,
    string? Stage,
    DateOnly? InterviewDate,
    string? Format,
    string? Interviewers
) : IntegrationEvent;

/// <summary>
/// Interviews:用户在条目上登记了面试结果(通过/被拒/…)→
/// Jobs 回写对应轮次的 Outcome;被拒时申请自动转 Rejected(M1.5)。
/// Result 是用户原话(自由文本),关键词→Outcome 的映射在消费端。
/// </summary>
public sealed record InterviewOutcomeRecorded(
    Guid ApplicationId,
    Guid EntryId,
    Guid UserId,
    int RoundNo,
    string Result
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

/// <summary>Interviews 服务:材料转写完成 → 请求 Analysis.Worker 开跑六维分析。</summary>
public sealed record InterviewAnalysisRequested(
    Guid InterviewEntryId,
    Guid AssetId,
    Guid UserId,
    string? StoragePath,
    string? TranscriptText
) : IntegrationEvent;

/// <summary>Interviews 服务:分析完成 → 请求 Knowledge 服务把答不好的题沉淀成技术知识点。</summary>
public sealed record InterviewKnowledgeExtractionRequested(
    Guid InterviewEntryId,
    Guid CompanyId,
    int OverallScore
) : IntegrationEvent;

/// <summary>Knowledge 服务:知识点掌握度变化(int 等级版,给 Analytics 用)。</summary>
public sealed record KnowledgeLevelChanged(
    Guid KnowledgeItemId,
    Guid UserId,
    string Topic,
    string Mastery,
    int Level
) : IntegrationEvent;
