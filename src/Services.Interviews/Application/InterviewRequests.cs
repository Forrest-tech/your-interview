using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Domain.JsonFields;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Interviews.Application;

// ============================ 查询 ============================

public sealed record ListEntriesQuery(
    Guid UserId, int Page = 1, int PageSize = 20, Guid? CompanyId = null, string? Status = null,
    string? Search = null) : IRequest<Result<PagedResult<InterviewEntryListItemDto>>>;

public sealed record GetEntryQuery(Guid UserId, Guid Id, Guid? OwnerUserId = null) : IRequest<Result<InterviewEntryDto>>;

public sealed record GetCompanySummariesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<CompanySummaryDto>>>;

/// <summary>某公司的全部轮次(跨该公司的所有面试条目,按时间排序)。供 Tech Stack 来源选择用。</summary>
public sealed record CompanyRoundDto(Guid RoundId, Guid EntryId, int Order, string Stage, DateOnly? ScheduledDate);
public sealed record GetCompanyRoundsQuery(Guid UserId, Guid CompanyId) : IRequest<Result<IReadOnlyList<CompanyRoundDto>>>;

public sealed record GetInterviewStatsQuery(Guid UserId) : IRequest<Result<InterviewStatsDto>>;

public sealed record ListWeaknessesQuery(Guid UserId, Guid EntryId, string? Category = null)
    : IRequest<Result<IReadOnlyList<WeaknessDto>>>;

/// <summary>任务台账:该条目的分析任务历史(含投递尝试与失败原因)。</summary>
public sealed record ListAnalysisJobsQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<IReadOnlyList<AnalysisJobDto>>>;

/// <summary>任务台账行 DTO —— 给前端"流水线记录"区直出。</summary>
public sealed record AnalysisJobDto(
    Guid Id, string JobType, string Status, int Attempts, int MaxAttempts,
    string? FailureReason, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt, DateTimeOffset? CompletedAt);

// ============================ 命令 ============================

public sealed record CreateEntryCommand(
    Guid UserId, Guid? CompanyId, string CompanyName, string Role, Guid? JobApplicationId = null,
    string? CompanyProfile = null, string? JdText = null, string? JdSummary = null,
    string? InterviewFormat = null, string? Interviewers = null, DateOnly? InterviewDate = null,
    string? Location = null, string? Notes = null)
    : IRequest<Result<Guid>>;

public sealed record UpdateEntryCommand(
    Guid UserId, Guid Id, string CompanyName, string Role, string? CompanyProfile, string? JdText,
    string? JdSummary, int RoundNo, DateOnly? InterviewDate, string? InterviewFormat,
    string? Interviewers, string? Location, string? Result, string? Notes,
    string? QuestionsToAskJson = null, string? DebriefJson = null,
    uint? Version = null) : IRequest<Result>; // 票101 Version:读时 xmin 回传钉版,null=不钉

public sealed record DeleteEntryCommand(Guid UserId, Guid Id, uint? Version = null) : IRequest<Result>; // 票111段3:If-Match 读时 xmin 钉版,null=不钉兼容

/// <summary>回填 Tracker 投递关联:additive —— 条目已有关联时不覆盖,原样返回成功。</summary>
public sealed record LinkJobApplicationCommand(Guid UserId, Guid Id, Guid JobApplicationId) : IRequest<Result>;

public sealed record AttachAssetCommand(
    Guid UserId, Guid EntryId, string Kind, string FileName, string? ContentType, long SizeBytes,
    string? StoragePath, string? BlobUrl, string? TranscriptText, string? TranscriptSegmentsJson,
    double? DurationSeconds, string SourceLanguage = "en", string? Sha256 = null,
    // 2026-10-19:资产按 Round 管理;2026-10-20 v2 起 RoundId 可空 = 条目级材料(沟通记录)。
    Guid? RoundId = null, string? Phase = null,
    // 2026-10-20 详情页 v2:统一入口元数据 JSON(解析字段/摘要/处理状态)。
    string? MetaJson = null) : IRequest<Result<Guid>>;

/// <summary>
/// 真正的录音文件上传(multipart):字节流经 IInterviewAudioStore 原子落盘,
/// 数据库只登记相对路径 + SHA-256 + 实际大小。之前 /assets 只收 JSON 元信息,
/// 文件本体从来没存过 —— Worker 永远找不到文件,这是 M1 修复的第一断点。
/// </summary>
public sealed record UploadAssetFileCommand(
    Guid UserId, Guid EntryId, string FileName, string? ContentType, double? DurationSeconds,
    string? SourceLanguage, Stream Content,
    // 2026-10-19:资产按 Round 管理,新增上传必填 RoundId
    Guid? RoundId = null, string? Phase = null) : IRequest<Result<Guid>>;

/// <summary>回放:取某条材料的音频流(前端 &lt;audio&gt; 直接指向这里)。</summary>
public sealed record GetAssetAudioQuery(Guid UserId, Guid EntryId, Guid AssetId)
    : IRequest<Result<(Stream Stream, string ContentType, string FileName)>>;

public sealed record SaveTranscriptCommand(Guid UserId, Guid EntryId, Guid AssetId, string FullText,
    string? SegmentsJson, Guid? OwnerUserId = null) : IRequest<Result>;

/// <summary>
/// 开始转写(把条目从 AssetsUploaded 推进到 Transcribing)。
/// AssetId 给定时精确转写该录音(COD-07 资产级路由,前端轮次页发起);
/// 为空时条目级旧语义(Worker 经 transcription/start 重跑,不指定材料)。
/// </summary>
public sealed record BeginTranscriptionCommand(Guid UserId, Guid EntryId, Guid? OwnerUserId = null,
    Guid? AssetId = null) : IRequest<Result>;

public sealed class BeginTranscriptionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<BeginTranscriptionCommand, Result>
{
    public async Task<Result> Handle(BeginTranscriptionCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions).Include(x => x.Weaknesses), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.StartTranscription(request.AssetId);   // 幂等:已在 Transcribed/Analyzed 直接返回
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Transcription.InvalidState", ex.Message));
        }
    }
}

public sealed record RequestAnalysisCommand(Guid UserId, Guid EntryId, Guid? OwnerUserId = null) : IRequest<Result>;

public sealed record ApplyAnalysisCommand(
    Guid UserId, Guid EntryId, int Overall, int? Pronunciation, int Fluency, int Structure,
    int TechnicalDepth, int Relevance, string? Summary,
    List<QuestionDraft>? Questions, List<WeaknessDraft>? Weaknesses,
    string? SpeechMetricsJson = null,
    string? CandidateTranscript = null, Guid? OwnerUserId = null) : IRequest<Result>;

public sealed record MarkEntryFailedCommand(Guid UserId, Guid EntryId, string Reason, Guid? OwnerUserId = null) : IRequest<Result>;

public sealed record AddQuestionCommand(
    Guid UserId, Guid EntryId, string QuestionText, string? MyAnswerText, string Category, int Difficulty,
    string? Assessment = null, Guid? RoundId = null, string? AskedBy = null) : IRequest<Result<Guid>>;

public sealed record UpdateQuestionCommand(
    Guid UserId, Guid EntryId, Guid QuestionId, string QuestionText, string? MyAnswerText, string Category,
    int Difficulty, string? Assessment, bool GotStuck, string? StuckReason,
    string? RecommendedAnswer, uint? Version = null) : IRequest<Result>; // 票101 Version:题目行 xmin

public sealed record RemoveQuestionCommand(Guid UserId, Guid EntryId, Guid QuestionId) : IRequest<Result>;

// ============================ Playbook 重做(A1–A7/F3):命令与查询 ============================

/// <summary>A1:设置题目掌握度(0=待练,1=练习中,2=已掌握,3=还卡壳)。</summary>
public sealed record UpdateQuestionMasteryCommand(Guid UserId, Guid EntryId, Guid QuestionId, int Mastery,
    uint? Version = null) // 票101 Version:题目行 xmin
    : IRequest<Result>;

/// <summary>A2:待办列表。</summary>
public sealed record ListActionsQuery(Guid UserId, Guid EntryId) : IRequest<Result<IReadOnlyList<ActionDto>>>;

/// <summary>A2:新增待办。</summary>
public sealed record CreateActionCommand(
    Guid UserId, Guid EntryId, string Title, string? Detail = null, DateTimeOffset? DueDate = null)
    : IRequest<Result<Guid>>;

/// <summary>A2:部分更新待办(null 字段不动)。</summary>
public sealed record UpdateActionCommand(
    Guid UserId, Guid EntryId, Guid ActionId, string? Title = null, bool? IsDone = null,
    DateTimeOffset? DueDate = null) : IRequest<Result>;

/// <summary>A2:删除待办。</summary>
public sealed record DeleteActionCommand(Guid UserId, Guid EntryId, Guid ActionId) : IRequest<Result>;

/// <summary>A3:分析趋势(按时间 asc 的快照列表)。</summary>
public sealed record GetAnalysisTrendQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<IReadOnlyList<AnalysisTrendPointDto>>>;

/// <summary>A3 最新快照:返回最近一条快照全文,没有快照时返回 null(前端显示"首次分析")。</summary>
public sealed record GetLatestSnapshotQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<AnalysisSnapshotDto?>>;

/// <summary>A4:该题在其它面试中的回答版本(归一化题干精确匹配)。</summary>
public sealed record GetQuestionHistoryQuery(Guid UserId, Guid EntryId, Guid QuestionId)
    : IRequest<Result<IReadOnlyList<QuestionHistoryDto>>>;

/// <summary>A5:全局技术覆盖图谱(按 Category 聚合)。</summary>
public sealed record GetCoverageQuery(Guid UserId) : IRequest<Result<IReadOnlyList<CoverageCategoryDto>>>;

/// <summary>A7:整体替换某轮次的邮件时间线。</summary>
public sealed record UpdateRoundEmailsCommand(
    Guid UserId, Guid EntryId, Guid RoundId, List<RoundEmailItem> Emails,
    uint? Version = null) : IRequest<Result>; // 票101 Version:轮次行 xmin

// ============================ 轮次(缺口1) ============================

public sealed record AddRoundCommand(
    Guid UserId, Guid EntryId, string Stage = "Technical", DateOnly? ScheduledDate = null,
    string? Interviewers = null, string? Format = null, string? Location = null,
    string? ScheduledTime = null) : IRequest<Result<Guid>>;

/// <summary>
/// 更新轮次。扩展字段(MeetingLink 起的 7 个)走部分更新协议(COD-04):
/// null/缺省 = 未提供、保持原值;空字符串 = 清空;非空 = 赋值。
/// Controller 的 UpdateRoundBody 与本命令同为可空字段,「未提供」信息逐层不丢失。
/// </summary>
public sealed record UpdateRoundCommand(
    Guid UserId, Guid EntryId, Guid RoundId, string Stage, DateOnly? ScheduledDate, string? Interviewers,
    string? Format, string? Location, string Outcome, string? Notes,
    string? Feedback, string? MeetingLink = null, string? ScheduledTime = null,
    string? PrepQuestionsJson = null, string? EmailsJson = null,
    string? Transcript = null, string? RecordingUrl = null,
    string? ChecklistJson = null,
    uint? Version = null) : IRequest<Result>; // 票101 Version:轮次行 xmin

public sealed record RemoveRoundCommand(Guid UserId, Guid EntryId, Guid RoundId) : IRequest<Result>;

