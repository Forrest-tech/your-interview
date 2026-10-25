using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ DTO ============================

public sealed record CompanyDto(Guid Id, string Name, string? Website, string? Industry, string? Location,
    string? LogoUrl, string? Notes, bool IsBlacklisted, string CompanyType, int ApplicationCount,
    // 公司情报(2026-09-18:面试前准备包的输入)。
    // 同 ApplyProfile:同一公司多个岗位共享一份,存 Company 而非 JobApplication。
    string? Profile, string? ProfileSourcesJson,
    DateTimeOffset CreatedAt,
    // 票101:读时 xmin 乐观并发版本(更新回传钉版;0 = 未填充)。
    uint Version = 0,
    // 票 150:员工规模 —— 手动补 logo 经 UpdateCompany 全量回传时须原值带回,
    // 否则一次 logo 保存会把已填的规模清成 null。
    int? EmployeeCount = null);

/// <summary>票142 R-142-11:抓取取数结果。CandidateText 为 null 时 NotFound=true,Tried 列出实际尝试过的腿。</summary>
public sealed record FetchCompanyProfileResult(string? CandidateText, string? SourceUrl, bool NotFound, IReadOnlyList<string> Tried);

public sealed record StatusChangeDto(string Status, string? Note, DateTimeOffset ChangedAt);

public sealed record InterviewRoundDto(Guid Id, int Order, string Stage, DateOnly? ScheduledDate,
    string? Interviewer, string? Format, string Outcome, string? Notes, string? Feedback,
    // 票101:读时 xmin(轮次更新回传钉版;0 = 未填充)。
    uint Version = 0);

public sealed record ApplicationDto(
    Guid Id, Guid CompanyId, string CompanyName, string? CompanyLogoUrl, string Role, string? Location, string? Link,
    string? Salary, string? WorkMode, string? Source, string? JdSummary,
    // JD 全文与出处(2026-09-18:面试前准备包的输入)。
    // 与 JdSummary 并存:摘要是给人看的速览,全文是给 AI 的原料 —— 两者用途不同。
    // ⚠️ 可达 JdTextLimits.MaxLength 字符,列表接口不要带它(见 ListApplicationsQuery:仍用不带全文的映射)。
    string? JdText, string? JdSourceUrl,
    // 公司介绍(2026-10-19):完整存储,不截断。Smart paste 从 "About the company" 提取。
    string? CompanyDescription,
    // 工作身份要求(2026-10-19):归一短文本。Smart paste 从 JD 正文提取。
    string? WorkAuthorization,
    // 岗位性质(2026-10-19):归一短文本(Full-time/Part-time/Permanent/Contract/...)。Smart paste 提取。
    string? JobType,
    // 任职要求(2026-10-19):完整存储。Smart paste 从 Qualifications/Requirements 段提取。
    string? Requirements,
    // 福利待遇(2026-10-19):完整存储。Smart paste 从 Benefits/Advantages 段提取。
    string? Benefits,
    string Status, string Priority, DateOnly? AppliedDate, DateOnly? Deadline,
    DateTimeOffset? NextFollowUpAt, int? ResumeScore, string? PassRateEstimate, string? MatchKeywords,
    string? Notes, string? PosterName, bool NeedsConnectFirst, string? OutreachMessage,
    DateTimeOffset? OutreachSentAt, string? RejectionReason,
    IReadOnlyList<StatusChangeDto> History, IReadOnlyList<InterviewRoundDto> Rounds,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    // 票101:读时 xmin 乐观并发版本(更新回传钉版;0 = 未填充)。
    uint Version = 0);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record PipelineColumn(string Status, int Count);

public sealed record TrackerStats(
    int Total, int Active, int Applied, int InInterview, int Offers, int Rejected, int Ghosted,
    double ResponseRate, double InterviewRate, double OfferRate,
    double AverageDaysToFirstResponse, IReadOnlyList<PipelineColumn> Pipeline,
    IReadOnlyList<SourceBreakdown> BySource, IReadOnlyList<MonthlyCount> ByMonth,
    IReadOnlyDictionary<string, int> ByStatus);

public sealed record SourceBreakdown(string Source, int Count, int Responded);
public sealed record MonthlyCount(string Month, int Applications, int Interviews, int Offers);

