using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ 查询 ============================

public sealed record ListCompaniesQuery(Guid UserId, string? Search, bool IncludeBlacklisted = false)
    : IRequest<Result<IReadOnlyList<CompanyDto>>>;

/// <summary>
/// 设置 JD 全文与出处(2026-09-18:面试前准备包的输入)。
/// 独立命令而非并入 UpdateApplicationCommand —— 粘贴 JD 是高频动作,
/// 不该要求调用方传齐 Role/Salary 等字段(两端都会互相覆盖)。
/// </summary>
public sealed record SetJdContentCommand(Guid UserId, Guid Id, string? JdText, string? JdSourceUrl,
    uint? Version = null) // 票101:读时 xmin 回传钉版,null=旧调用方不钉
    : IRequest<Result>;

/// <summary>设置公司情报长文本。与 UpdateCompanyCommand 分开,理由同上。</summary>
public sealed record SetCompanyProfileCommand(Guid UserId, Guid Id, string? Profile, string? ProfileSourcesJson,
    uint? Version = null) // 票101:同 SetJdContentCommand
    : IRequest<Result>;

public sealed record GetCompanyQuery(Guid UserId, Guid Id) : IRequest<Result<CompanyDto>>;

/// <summary>
/// 票142 R-142-11:网上抓取公司简介(只取数、不写库)。
/// 返回候选文本 + 来源 URL,或 notFound + 实际尝试过的腿;前端进编辑草稿,用户确认后走现有 PUT 保存。
/// </summary>
public sealed record FetchCompanyProfileQuery(Guid UserId, Guid Id) : IRequest<Result<FetchCompanyProfileResult>>;

public sealed record ListApplicationsQuery(
    Guid UserId, string? Search, string? Status, string? Priority, Guid? CompanyId,
    string? SortBy = "updated", int Page = 1, int PageSize = 50,
    bool? Archived = null)
    : IRequest<Result<PagedResult<ApplicationDto>>>;

public sealed record GetApplicationQuery(Guid UserId, Guid Id) : IRequest<Result<ApplicationDto>>;

public sealed record GetTrackerStatsQuery(Guid UserId) : IRequest<Result<TrackerStats>>;

public sealed record GetUpcomingFollowUpsQuery(Guid UserId, int Days = 7) : IRequest<Result<IReadOnlyList<ApplicationDto>>>;

public sealed record GetDeadlinesQuery(Guid UserId, int Days = 14) : IRequest<Result<IReadOnlyList<ApplicationDto>>>;

/// <summary>
/// 行动中心聚合:一次请求给全仪表盘"今天该做什么"所需的 Jobs 侧数据。
/// 只聚合 Jobs 自有数据(待跟进 / 临近截止 / 状态分布 / 总数),不跨服务,无迁移。
/// </summary>
public sealed record GetActionCenterQuery(Guid UserId, int FollowUpDays = 7, int DeadlineDays = 14)
    : IRequest<Result<ActionCenterDto>>;

// ============================ 命令 ============================

public sealed record CreateCompanyCommand(Guid UserId, string Name, string? Website, string? Industry,
    string? Location, string? LogoUrl, string? Notes, string CompanyType = "Unknown", int? EmployeeCount = null)
    : IRequest<Result<Guid>>;

public sealed record UpdateCompanyCommand(Guid UserId, Guid Id, string Name, string? Website, string? Industry,
    string? Location, string? LogoUrl, string? Notes, string CompanyType, int? EmployeeCount, bool IsBlacklisted,
    uint? Version = null) // 票101:同上
    : IRequest<Result>;

public sealed record DeleteCompanyCommand(Guid UserId, Guid Id, uint? Version = null) : IRequest<Result>; // 票111段3:If-Match 读时 xmin 钉版,null=不钉兼容

public sealed record CreateApplicationCommand(Guid UserId, Guid CompanyId, string Role, string? Location, string? Link,
    string? Salary, string? WorkMode, string? Source, string? JdSummary, string? Priority = null,
    DateOnly? AppliedDate = null, string? Status = null, string? CompanyDescription = null,
    string? WorkAuthorization = null, string? JobType = null,
    string? Requirements = null, string? Benefits = null,
    // FUN-02:手动建单填的 JD 全文与备注此前无处可放 (命令里根本没有这两个字段),
    // 前端发不发都会丢。尾部可选参数补齐,既有调用方不受影响。
    string? JdText = null, string? Notes = null)
    : IRequest<Result<Guid>>;

public sealed record UpdateApplicationCommand(Guid UserId, Guid Id, string Role, string? Location, string? Link,
    string? Salary, string? WorkMode, string? Source, string? JdSummary, string? Notes, string? Priority,
    DateOnly? Deadline, DateOnly? AppliedDate = null, string? CompanyDescription = null,
    string? WorkAuthorization = null, string? JobType = null,
    string? Requirements = null, string? Benefits = null,
    // FUN-02:JD 全文纳入全量更新。语义: null = 调用方未提供该字段,保持原值
    // (详情页局部保存不带 jdText,不能误清); 非 null (含空串) = 明确的新值,空串归一为清空。
    string? JdText = null,
    uint? Version = null, // 票101:读时 xmin 回传钉版,null=旧调用方不钉
    // 票 154:小节列表 JSON。语义与 JdText 同口径:null = 未提供,保持原值;
    // 非 null = 明确的新值(重解析合并后的列表),后端据此重算 requirements / benefits 投影。
    string? SectionsJson = null)
    : IRequest<Result>;

public sealed record ChangeApplicationStatusCommand(Guid UserId, Guid Id, string Status, string? Note, string? RejectionReason)
    : IRequest<Result>;

public sealed record DeleteApplicationCommand(Guid UserId, Guid Id, uint? Version = null) : IRequest<Result>; // 票111段3:同 DeleteCompanyCommand
public sealed record ArchiveApplicationCommand(Guid UserId, Guid Id) : IRequest<Result>;
public sealed record UnarchiveApplicationCommand(Guid UserId, Guid Id) : IRequest<Result>;

public sealed record SetApplicationAnalysisCommand(Guid UserId, Guid Id, int? ResumeScore, string? PassRateEstimate,
    string? MatchKeywords, uint? Version = null) : IRequest<Result>; // 票101 Version:同 UpdateApplicationCommand

public sealed record SetOutreachCommand(Guid UserId, Guid Id, string? PosterName, bool NeedsConnectFirst, string? Message,
    uint? Version = null) : IRequest<Result>; // 票101 Version:同上

public sealed record MarkOutreachSentCommand(Guid UserId, Guid Id) : IRequest<Result>;

public sealed record ScheduleFollowUpCommand(Guid UserId, Guid Id, DateTimeOffset? At,
    uint? Version = null) : IRequest<Result>; // 票101 Version:同上

public sealed record AddInterviewRoundCommand(Guid UserId, Guid ApplicationId, string Stage, DateOnly? ScheduledDate,
    string? Interviewer, string? Format, string? Notes) : IRequest<Result<Guid>>;

public sealed record UpdateInterviewRoundCommand(Guid UserId, Guid ApplicationId, Guid RoundId, string? Stage,
    DateOnly? ScheduledDate, string? Interviewer, string? Format, string Outcome, string? Notes,
    uint? Version = null) // 票101:轮次行 xmin 回传钉版
    : IRequest<Result>;

public sealed record BulkImportApplicationsCommand(Guid UserId, IReadOnlyList<BulkImportRow> Rows) : IRequest<Result<BulkImportResult>>;

public sealed record BulkImportRow(string Company, string Role, string? Location, string? Link, string? Salary,
    string? Status, string? Source, string? Notes, DateOnly? AppliedDate);

public sealed record BulkImportResult(int Created, int Skipped, IReadOnlyList<string> Errors);

