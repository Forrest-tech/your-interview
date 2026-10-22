using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;

namespace YourInterview.Services.Interviews.Application;

// ============================ DTO ============================
// DTO 一律用 record + 精确定义,不用匿名对象 —— 前端 TypeScript 类型可直接照抄。

public sealed record AssetDto(
    Guid Id, string Kind, string FileName, string? ContentType, long SizeBytes,
    string? BlobUrl, double? DurationSeconds, string SourceLanguage, bool HasTranscript,
    int TranscriptLength, DateTimeOffset UploadedAt,
    string? StoragePath = null, string? Sha256 = null, bool FileExists = true,
    string? IntegrityStatus = null, DateTimeOffset? LastVerifiedAt = null,
    // 2026-10-19:资产按 Round 管理。RoundId/Phase 为 null = 历史遗留未归类材料。
    Guid? RoundId = null, string? Phase = null,
    // 2026-10-20 详情页 v2:统一入口解析字段/摘要/处理状态等元数据 JSON。
    string? MetaJson = null);

public sealed record QuestionDto(
    Guid Id, int Sequence, string QuestionText, string? MyAnswerText, string Category,
    int Difficulty, string? Assessment, bool GotStuck, string? StuckReason,
    string? RecommendedAnswer, double? AskedAtSeconds, string? WeaknessTagsJson,
    string? FollowUpQuestionsJson, string? MissedPointsJson, int Mastery = 0,
    // 2026-10-20 详情页 v2:归轮 / 提问人 / 逐题复盘 JSON。
    Guid? RoundId = null, string? AskedBy = null, string? ReviewJson = null);

public sealed record WeaknessDto(
    Guid Id, string Category, string Title, string? Detail, string? Evidence,
    int Severity, string? Suggestion, int OccurrenceCount, string SourceType,
    DateTimeOffset CreatedAt);

public sealed record RoundDto(
    Guid Id, int Order, string Stage, DateOnly? ScheduledDate, string? Interviewers,
    string? Format, string? Location, string Outcome, string? Notes, string? Feedback,
    string? MeetingLink, string? ScheduledTime, string? EmailsJson, string? Transcript,
    string? PrepQuestionsJson = null, string? RecordingUrl = null, string? ChecklistJson = null,
    // 2026-10-20 详情页 v2:整轮复盘汇总 / 结构化备考报告 JSON。
    string? ReviewJson = null, string? PrepReportJson = null);

/// <summary>全局 upcoming:跨所有条目的未来轮次,带公司/职位上下文,供日历视图用。</summary>
public sealed record UpcomingRoundDto(
    Guid RoundId, Guid EntryId, string CompanyName, string Role,
    int Order, string Stage, DateOnly? ScheduledDate, string? ScheduledTime,
    string? Interviewers, string? Format, string? Location,
    string? MeetingLink, string Outcome);

public sealed record GetUpcomingRoundsQuery(Guid UserId, int Days = 30) : IRequest<Result<List<UpcomingRoundDto>>>;

public sealed record GuidanceMaterialDto(
    Guid Id, int Version, string ContentMarkdown, string Model,
    DateTimeOffset GeneratedAt, int? PromptTokens, int? CompletionTokens);

public sealed record GuidanceVersionDto(
    Guid Id, int Version, DateTimeOffset GeneratedAt, string Model);

/// <summary>问答候选(缺口3):把条目的 InterviewQuestion 转成可勾选导入 TechStack 的候选。</summary>
public sealed record QuestionCandidateDto(
    Guid QuestionId, string QuestionText, string? MyAnswerText, string? RecommendedAnswer,
    string? Category, int Difficulty, bool GotStuck, string? Assessment);

/// <summary>
/// 客观声学指标 DTO(缺口4)。字段与 Analysis.Worker 的 SpeechMetrics 一一对应,
/// 由存储的 SpeechMetricsJson 反序列化得到,缺字段时容错(可空/默认值)。
/// </summary>
public sealed record SpeechMetricsDto(
    double DurationSeconds, int WordCount, double WordsPerMinute,
    double AverageSentenceLength, int ShortSentenceCount, int FillerWordCount,
    Dictionary<string, int>? FillerWordBreakdown, int SelfRepetitionCount,
    int LongPauseCount, double TotalSilenceSeconds, double PronunciationAccuracy,
    double LowScoreWordRatio, List<ProblemWordDto>? ProblemWords,
    Dictionary<string, int>? StructureMarkers = null);

public sealed record ProblemWordDto(
    string Word, double AccuracyScore, double? FluencyScore,
    double StartSeconds, double EndSeconds);

public sealed record InterviewEntryDto(
    Guid Id, Guid CompanyId, string CompanyName, Guid? JobApplicationId, string Role,
    string? CompanyProfile, string? JdText, string? JdSummary,
    int RoundNo, DateOnly? InterviewDate, string? InterviewFormat, string? Interviewers,
    string? Location, string? Result, string? Notes,
    string Status, string? FailureReason, DateTimeOffset? TranscribedAt, DateTimeOffset? AnalyzedAt,
    int? OverallScore, int? PronunciationScore, int? FluencyScore, int? StructureScore,
    int? TechnicalDepthScore, int? RelevanceScore, string? AnalysisSummary,
    SpeechMetricsDto? SpeechMetrics, string? CandidateTranscript,
    int AssetCount, int QuestionCount, int WeaknessCount, int RoundCount,
    List<AssetDto> Assets, List<QuestionDto> Questions, List<WeaknessDto> Weaknesses,
    List<RoundDto> Rounds,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    string? QuestionsToAskJson = null, int ActionCount = 0,
    List<ActionDto>? Actions = null, string? DebriefJson = null,
    // 票05(ARC-14):结构化结果 —— 后端按 DeriveOutcome 单一口径派生(值域与
    // SharedContracts InterviewOutcome / 轮次 InterviewRoundOutcome 同名同值),
    // 前端只消费此字段分组/展示,不再本地判读 Result 自由文本。
    string? Outcome = null);

public sealed record InterviewEntryListItemDto(
    Guid Id, Guid CompanyId, string CompanyName, string Role, int RoundNo,
    DateOnly? InterviewDate, string? InterviewFormat, string Status, string? Result,
    int? OverallScore, int? PronunciationScore, int? FluencyScore, int? StructureScore,
    int? TechnicalDepthScore, int? RelevanceScore,
    int AssetCount, int QuestionCount, int WeaknessCount, bool HasTranscript,
    DateTimeOffset CreatedAt, Guid? JobApplicationId = null,
    // 票05(ARC-14):结构化结果,口径同 InterviewEntryDto.Outcome(见 DeriveOutcome)。
    string? Outcome = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    public bool HasNext => Page < TotalPages;
    public bool HasPrevious => Page > 1;
}

public sealed record CompanySummaryDto(
    Guid CompanyId, string CompanyName, int EntryCount, int AnalyzedCount,
    double? AverageScore, DateOnly? LatestInterviewDate, string? LatestResult);

public sealed record InterviewStatsDto(
    int TotalEntries, int Draft, int Transcribing, int Transcribed, int Analyzing,
    int Analyzed, int Failed,
    int TotalQuestions, int GotStuckQuestions, int TotalWeaknesses,
    double? AverageOverallScore, double? AveragePronunciation, double? AverageFluency,
    double? AverageStructure, double? AverageTechnicalDepth, double? AverageRelevance,
    List<CategoryCountDto> WeaknessByCategory,
    List<CategoryCountDto> QuestionsByCategory,
    List<TrendPointDto> ScoreTrend,
    int PendingAnalysis);

public sealed record CategoryCountDto(string Category, int Count, double? AverageSeverity = null);

public sealed record TrendPointDto(DateOnly Date, int Score, string CompanyName);

// ============================ Playbook 重做(A1–A7/F3)DTO ============================

/// <summary>A2 待办事项。</summary>
public sealed record ActionDto(
    Guid Id, string Title, string? Detail, DateTimeOffset? DueDate, bool IsDone,
    DateTimeOffset CreatedAt);

/// <summary>A3 分析趋势点:trend 端点只返回这三个字段(按时间 asc)。</summary>
public sealed record AnalysisTrendPointDto(Guid Id, DateTimeOffset CreatedAt, int? OverallScore);

/// <summary>A3 最新快照全文:latest 端点返回最近一次分析的完整快照(结论/维度)。</summary>
public sealed record AnalysisSnapshotDto(
    Guid Id, DateTimeOffset CreatedAt, int? OverallScore,
    string? DimensionsJson, string? SummaryBulletsJson);

/// <summary>A4 某题在其它面试中的回答版本。</summary>
public sealed record QuestionHistoryDto(
    Guid InterviewId, string CompanyName, string Role, DateOnly? InterviewDate,
    string? MyAnswerText, int Mastery);

/// <summary>A5 全局技术覆盖:按 Category 聚合。</summary>
public sealed record CoverageCategoryDto(
    string Category, int AskedCount, int StuckCount, int MasteredCount, List<string> Interviews);

/// <summary>A7 轮次邮件时间线条目 —— JSON 键与前端契约一致:{from,subject,date,snippet}。</summary>
public sealed record RoundEmailItem(
    string? From, string? Subject, string? Date, string? Snippet);

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
    string? QuestionsToAskJson = null, string? DebriefJson = null) : IRequest<Result>;

public sealed record DeleteEntryCommand(Guid UserId, Guid Id) : IRequest<Result>;

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
    string? RecommendedAnswer) : IRequest<Result>;

public sealed record RemoveQuestionCommand(Guid UserId, Guid EntryId, Guid QuestionId) : IRequest<Result>;

// ============================ Playbook 重做(A1–A7/F3):命令与查询 ============================

/// <summary>A1:设置题目掌握度(0=待练,1=练习中,2=已掌握,3=还卡壳)。</summary>
public sealed record UpdateQuestionMasteryCommand(Guid UserId, Guid EntryId, Guid QuestionId, int Mastery)
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
    Guid UserId, Guid EntryId, Guid RoundId, List<RoundEmailItem> Emails) : IRequest<Result>;

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
    string? ChecklistJson = null) : IRequest<Result>;

public sealed record RemoveRoundCommand(Guid UserId, Guid EntryId, Guid RoundId) : IRequest<Result>;

public sealed record AddWeaknessCommand(
    Guid UserId, Guid EntryId, string Category, string Title, string? Detail, string? Evidence,
    int Severity, string? Suggestion) : IRequest<Result<Guid>>;

public sealed record RemoveWeaknessCommand(Guid UserId, Guid EntryId, Guid WeaknessId) : IRequest<Result>;

// ============================ 校验器 ============================
// 注意:校验器由 ServiceDefaults 的反射扫描自动注册(踩过"忘了注册导致校验静默失效"的坑)。

public sealed class CreateEntryCommandValidator : AbstractValidator<CreateEntryCommand>
{
    public CreateEntryCommandValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(300)
            .WithMessage("公司名不能为空且不超过 300 字");
        RuleFor(x => x.Role).NotEmpty().MaximumLength(300).WithMessage("岗位名不能为空");
        RuleFor(x => x.JdText).MaximumLength(60000);
    }
}

public sealed class UpdateEntryCommandValidator : AbstractValidator<UpdateEntryCommand>
{
    public UpdateEntryCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Role).NotEmpty().MaximumLength(300);
        RuleFor(x => x.RoundNo).InclusiveBetween(1, 20).WithMessage("轮次应在 1-20 之间");
    }
}

public sealed class AttachAssetCommandValidator : AbstractValidator<AttachAssetCommand>
{
    private static readonly string[] AllowedKinds =
        ["Audio", "Transcript", "Notes", "Text", "PrepDoc", "Email", "Feedback", "Other"];
    private static readonly string[] AllowedPhases = ["Pre", "Post"];

    public AttachAssetCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Kind).Must(k => AllowedKinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            .WithMessage("材料类型无效");
        // 文本类材料必须有内容 —— 这条规则挡掉了"传了个空的 Transcript"
        RuleFor(x => x.TranscriptText)
            .NotEmpty()
            .When(x => !string.Equals(x.Kind, "Audio", StringComparison.OrdinalIgnoreCase))
            .WithMessage("文本类材料必须提供内容");
        // 2026-10-20 详情页 v2:统一入口允许条目级材料(RoundId=null → 沟通记录),
        // 不再强制 RoundId;给了 RoundId 时由领域层校验轮次存在。
        RuleFor(x => x.Phase)
            .Must(p => p is null || AllowedPhases.Contains(p, StringComparer.OrdinalIgnoreCase))
            .WithMessage("阶段只能是 Pre(面试前) / Post(面试后)");
    }
}

public sealed class UploadAssetFileCommandValidator : AbstractValidator<UploadAssetFileCommand>
{
    public UploadAssetFileCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500)
            .WithMessage("请选择要上传的录音文件");
        // 大小上限在 Handler 里对着真实流校验(信 Form 长度不如信实读字节数),
        // 这里只挡明显非法的值,避免双重报错口径。
        RuleFor(x => x.DurationSeconds).InclusiveBetween(0, 24 * 3600)
            .When(x => x.DurationSeconds.HasValue)
            .WithMessage("录音时长不能超过 24 小时");
        // 2026-10-20 详情页 v2:录音也可归条目级(沟通电话录音),RoundId 可空。
        RuleFor(x => x.Phase)
            .Must(p => p is null || p.Equals("Pre", StringComparison.OrdinalIgnoreCase)
                                || p.Equals("Post", StringComparison.OrdinalIgnoreCase))
            .WithMessage("阶段只能是 Pre(面试前) / Post(面试后)");
    }
}

public sealed class AddQuestionCommandValidator : AbstractValidator<AddQuestionCommand>
{
    public AddQuestionCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.QuestionText).NotEmpty().MaximumLength(8000);
        RuleFor(x => x.Difficulty).InclusiveBetween(1, 5);
    }
}

public sealed class AddRoundCommandValidator : AbstractValidator<AddRoundCommand>
{
    public AddRoundCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Stage).NotEmpty().MaximumLength(50);
    }
}

public sealed class UpdateRoundCommandValidator : AbstractValidator<UpdateRoundCommand>
{
    public UpdateRoundCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
        RuleFor(x => x.Stage).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Outcome).NotEmpty();
        RuleFor(x => x.Interviewers).MaximumLength(1000);
        RuleFor(x => x.Location).MaximumLength(300);
    }
}

public sealed class AddWeaknessCommandValidator : AbstractValidator<AddWeaknessCommand>
{
    public AddWeaknessCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Severity).InclusiveBetween(1, 5);
    }
}

// ============================ Playbook 重做(A1–A7)校验器 ============================

public sealed class UpdateQuestionMasteryCommandValidator
    : AbstractValidator<UpdateQuestionMasteryCommand>
{
    public UpdateQuestionMasteryCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.QuestionId).NotEmpty();
        RuleFor(x => x.Mastery).InclusiveBetween(0, 3)
            .WithMessage("掌握度只能是 0-3(0=待练,1=练习中,2=已掌握,3=还卡壳)");
    }
}

public sealed class CreateActionCommandValidator : AbstractValidator<CreateActionCommand>
{
    public CreateActionCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500)
            .WithMessage("待办标题不能为空且不超过 500 字");
        RuleFor(x => x.Detail).MaximumLength(8000);
    }
}

public sealed class UpdateActionCommandValidator : AbstractValidator<UpdateActionCommand>
{
    public UpdateActionCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.ActionId).NotEmpty();
        RuleFor(x => x.Title).MaximumLength(500).When(x => x.Title is not null);
    }
}

public sealed class UpdateRoundEmailsCommandValidator : AbstractValidator<UpdateRoundEmailsCommand>
{
    public UpdateRoundEmailsCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
        RuleFor(x => x.Emails).NotNull().WithMessage("emails 不能为空");
        RuleForEach(x => x.Emails).ChildRules(email =>
        {
            email.RuleFor(e => e.From).MaximumLength(500);
            email.RuleFor(e => e.Subject).MaximumLength(1000);
            email.RuleFor(e => e.Date).MaximumLength(100);
            email.RuleFor(e => e.Snippet).MaximumLength(4000);
        });
    }
}

// ============================ 映射(扩展方法,和 Jobs 服务保持一致风格) ============================

public static class InterviewMappingExtensions
{
    public static AssetDto ToDto(this InterviewAsset a) => new(
        a.Id, a.Kind.ToString(), a.FileName, a.ContentType, a.SizeBytes, a.BlobUrl,
        a.DurationSeconds, a.SourceLanguage, a.HasTranscript,
        a.TranscriptText?.Length ?? 0, a.UploadedAt,
        a.StoragePath, a.Sha256, a.FileExists,
        a.IntegrityStatus?.ToString(), a.LastVerifiedAt,
        a.RoundId, a.Phase?.ToString(), a.MetaJson);

    public static QuestionDto ToDto(this InterviewQuestion q) => new(
        q.Id, q.Sequence, q.QuestionText, q.MyAnswerText, q.Category.ToString(), q.Difficulty,
        q.Assessment, q.GotStuck, q.StuckReason, q.RecommendedAnswer, q.AskedAtSeconds,
        q.WeaknessTagsJson, q.FollowUpQuestionsJson, q.MissedPointsJson, q.Mastery,
        q.RoundId, q.AskedBy, q.ReviewJson);

    public static ActionDto ToDto(this InterviewAction a) => new(
        a.Id, a.Title, a.Detail, a.DueDate, a.IsDone, a.CreatedAt);

    public static WeaknessDto ToDto(this InterviewWeakness w) => new(
        w.Id, w.Category.ToString(), w.Title, w.Detail, w.Evidence, w.Severity, w.Suggestion,
        w.OccurrenceCount, w.SourceType.ToString(), w.CreatedAt);

    public static RoundDto ToDto(this InterviewRound r) => new(
        r.Id, r.Order, r.Stage, r.ScheduledDate, r.Interviewers, r.Format, r.Location,
        r.Outcome.ToString(), r.Notes, r.Feedback,
        r.MeetingLink, r.ScheduledTime, r.EmailsJson, r.Transcript,
        r.PrepQuestionsJson, r.RecordingUrl, r.ChecklistJson,
        r.ReviewJson, r.PrepReportJson);

    public static GuidanceMaterialDto ToDto(this GeneratedMaterial m) => new(
        m.Id, m.Version, m.ContentMarkdown, m.Model, m.GeneratedAt,
        m.PromptTokens, m.CompletionTokens);

    public static QuestionCandidateDto ToCandidateDto(this InterviewQuestion q) => new(
        q.Id, q.QuestionText, q.MyAnswerText, q.RecommendedAnswer,
        q.Category.ToString(), q.Difficulty, q.GotStuck, q.Assessment);

    public static InterviewEntryDto ToDto(this InterviewEntry e) => new(
        e.Id, e.CompanyId, e.CompanyName, e.JobApplicationId, e.Role,
        e.CompanyProfile, e.JdText, e.JdSummary,
        e.RoundNo, e.InterviewDate, e.InterviewFormat, e.Interviewers, e.Location, e.Result, e.Notes,
        e.Status.ToString(), e.FailureReason, e.TranscribedAt, e.AnalyzedAt,
        e.OverallScore, e.PronunciationScore, e.FluencyScore, e.StructureScore,
        e.TechnicalDepthScore, e.RelevanceScore, e.AnalysisSummary,
        ParseSpeechMetrics(e.SpeechMetricsJson), e.CandidateTranscript,
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count, e.Rounds.Count,
        e.Assets.OrderBy(x => x.UploadedAt).Select(x => x.ToDto()).ToList(),
        e.Questions.OrderBy(x => x.Sequence).Select(x => x.ToDto()).ToList(),
        e.Weaknesses.OrderByDescending(x => x.Severity).Select(x => x.ToDto()).ToList(),
        e.Rounds.OrderBy(x => x.Order).Select(x => x.ToDto()).ToList(),
        e.CreatedAt, e.UpdatedAt,
        e.QuestionsToAskJson, e.Actions.Count,
        e.Actions.OrderBy(x => x.IsDone).ThenBy(x => x.CreatedAt)
            .Select(x => x.ToDto()).ToList(), e.DebriefJson,
        DeriveOutcome(e)?.ToString());

    /// <summary>指标 JSON 反序列化:坏数据/缺字段时返回 null,前端按"无指标"处理。</summary>
    private static SpeechMetricsDto? ParseSpeechMetrics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SpeechMetricsDto>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>列表项只带聚合的标量字段(不带子集合),避免列表页把大文本拖出来。</summary>
    public static InterviewEntryListItemDto ToListItemDto(this InterviewEntry e) => new(
        e.Id, e.CompanyId, e.CompanyName, e.Role, e.RoundNo, e.InterviewDate, e.InterviewFormat,
        e.Status.ToString(), e.Result,
        e.OverallScore, e.PronunciationScore, e.FluencyScore, e.StructureScore,
        e.TechnicalDepthScore, e.RelevanceScore,
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count,
        e.Assets.Any(a => a.TranscriptText != null),
        e.CreatedAt,
        // Tracker 联动(M1.4):邀请登记自动建的草稿带申请 Id,前端可回链
        e.JobApplicationId,
        DeriveOutcome(e)?.ToString());

    // ===================== 条目结果派生(票05 / ARC-14 单一事实源) =====================
    //
    // 背景:结果判读此前 TS/C# 各一份且口径不同(前端 v2.helpers 用中英关键词判
    // 分组,后端 Jobs 消费者用另一组关键词回写 Tracker)。票04 已把跨服务回写
    // 收归结构化枚举(轮次 InterviewRoundOutcome → SharedContracts
    // InterviewOutcome,枚举为准、文本只作展示);本方法把「条目展示用结果」
    // 也收归同一事实源:列表/详情 DTO 的 Outcome 只许由这里派生,前端只消费
    // 枚举做分组(In Progress/Completed)与徽章映射,不再判读 Result 原文。
    //
    // 派生规则(顺序即优先级,上者胜):
    //  1. 结构化轮次优先(与票04 UpdateBasicInfo 发事件的口径完全一致):
    //     条目当前轮(RoundNo 对应的轮次)已有定论(Outcome 非 Pending)时
    //     直接取轮次枚举 —— 哪怕 Result 原文与之矛盾也以枚举为准
    //     (同 Jobs 消费者 EnumWinsOverFreeText 的语义)。只看当前轮:
    //     早先轮次通过不等于整场结束,后续轮次未定论时条目仍在进行中。
    //  2. Result 原文恰为受控词表(域约定 Passed|Rejected|Pending|Ghosted|
    //     Cancelled|NoShow)时按枚举名精确解析 —— 这是结构化值,不是关键词猜测。
    //  3. 其余自由文本才走关键词回落,且与票04 Jobs 侧
    //     LegacyOutcomeTextAdapter 的口径逐项对照(见下),保证同一原文在
    //     列表分组与 Tracker 回写的结论同向:
    //       · 未落定优先:含 pending/waiting/awaiting/in progress/等回复/等待/
    //         进行中 → Pending(留 In Progress;Legacy 对「等回复」同样不回写)。
    //       · 拒向:Legacy 的 未通过/不通过/拒/挂 → Failed 与前端旧判读的
    //         reject/denied/declin/fail/没过/淘汰/黄了 并集 → Rejected。
    //         拒向必须先于通过向判定(Legacy 明注「未通过先于通过」:
    //         未通过/不通过都含「通过」二字,顺序反了结论就反了)。
    //       · 通过向:Legacy 的 通过 → Passed 与旧判读的 offer/hired/accepted/
    //         录用/录取/成功/\bpass 并集 → Passed。
    //       · 中性终局(旧判读的 closed 组):ghost/失联/无回复 → Ghosted;
    //         cancel/withdraw/closed/ended/done/放弃/取消/结束/完结 → Cancelled
    //         (枚举内无通用 Closed 值,取中性终局 Cancelled 承接;方向同为已结束)。
    //  4. 都不命中 → null(无结构化结论,留 In Progress —— 宁可漏分,不可错分,
    //     与 Legacy「识别不了就不回写」同向)。
    //
    // 与改前前端判读的已知差异(均为向票04口径靠拢,非回归):
    //  · 含「不通过」的原文:旧判读误入 positive(含「通过」),现按 Legacy 改判 Rejected;
    //  · 含「挂」的原文(如「挂了」):旧判读不命中留 In Progress,现按 Legacy 判 Rejected;
    //  · 当前轮已有定论而 Result 原文写未落定词(如「等后续轮次」):旧判读留
    //    In Progress,现以轮次枚举为准判终局 —— 与 Tracker 回写(票04 事件带枚举)一致。
    /// <summary>条目结构化结果派生(票05 单一事实源;规则见上方注释块)。</summary>
    public static InterviewRoundOutcome? DeriveOutcome(InterviewEntry e)
    {
        // 1. 当前轮的结构化定论优先(票04 口径)。
        var round = e.Rounds.FirstOrDefault(r => r.Order == e.RoundNo);
        if (round is not null && round.Outcome != InterviewRoundOutcome.Pending)
            return round.Outcome;

        var raw = (e.Result ?? string.Empty).Trim();
        if (raw.Length == 0) return null;

        // 2. 受控词表精确解析(名字对上才算,防 "1" 之类数字串被 Enum.TryParse 误收)。
        if (Enum.TryParse<InterviewRoundOutcome>(raw, ignoreCase: true, out var parsed)
            && string.Equals(parsed.ToString(), raw, StringComparison.OrdinalIgnoreCase))
            return parsed;

        // 3. 自由文本关键词回落(与 LegacyOutcomeTextAdapter 对照,见注释块)。
        var s = raw.ToLowerInvariant();
        if (ContainsAny(s, "pending", "waiting", "awaiting", "in progress", "等回复", "等待", "进行中"))
            return InterviewRoundOutcome.Pending;
        if (ContainsAny(s, "未通过", "不通过", "拒", "挂",
                "reject", "denied", "declin", "fail", "没过", "淘汰", "黄了"))
            return InterviewRoundOutcome.Rejected;
        if (ContainsAny(s, "通过", "offer", "hired", "accepted", "录用", "录取", "成功")
            || Regex.IsMatch(s, @"\bpass"))
            return InterviewRoundOutcome.Passed;
        if (ContainsAny(s, "ghost", "失联", "无回复"))
            return InterviewRoundOutcome.Ghosted;
        if (ContainsAny(s, "cancel", "withdraw", "closed", "ended", "done", "放弃", "取消", "结束", "完结"))
            return InterviewRoundOutcome.Cancelled;
        return null;
    }

    private static bool ContainsAny(string s, params string[] keywords)
        => keywords.Any(k => s.Contains(k, StringComparison.Ordinal));
}

// ============================ 归属加载(COD-01/票03) ============================

/// <summary>
/// 条目归属加载的统一入口(双保险第二层):
/// · 普通请求路径:按 (Id, UserId) 谓词加载(全局过滤器之外再显式一道);
/// · Worker 回写路径(OwnerUserId 非空,裁决 B):IgnoreQueryFilters 按 Id 加载后
///   校验 entry.UserId == OwnerUserId —— 不给服务账号全量豁免,主人不对一律视为不存在(404)。
/// </summary>
internal static class OwnedEntry
{
    public static async Task<InterviewEntry?> LoadAsync(
        InterviewsDbContext db, Guid entryId, Guid userId, Guid? ownerUserId,
        Func<IQueryable<InterviewEntry>, IQueryable<InterviewEntry>>? include = null,
        CancellationToken ct = default)
    {
        IQueryable<InterviewEntry> q = ownerUserId.HasValue
            ? db.Entries.IgnoreQueryFilters()
            : db.Entries;
        if (include is not null) q = include(q);
        var e = await q.FirstOrDefaultAsync(x => x.Id == entryId, ct);
        if (e is null) return null;
        var expected = ownerUserId ?? userId;
        return e.UserId == expected ? e : null;
    }

    public static async Task<InterviewEntry?> LoadNoTrackingAsync(
        InterviewsDbContext db, Guid entryId, Guid userId, Guid? ownerUserId,
        Func<IQueryable<InterviewEntry>, IQueryable<InterviewEntry>>? include = null,
        CancellationToken ct = default)
    {
        IQueryable<InterviewEntry> q = ownerUserId.HasValue
            ? db.Entries.IgnoreQueryFilters().AsNoTracking()
            : db.Entries.AsNoTracking();
        if (include is not null) q = include(q);
        var e = await q.FirstOrDefaultAsync(x => x.Id == entryId, ct);
        if (e is null) return null;
        var expected = ownerUserId ?? userId;
        return e.UserId == expected ? e : null;
    }

    /// <summary>子表直查前的归属存在性校验:条目不属于当前用户 → 按不存在处理(404)。</summary>
    public static Task<bool> ExistsAsync(
        InterviewsDbContext db, Guid entryId, Guid userId, CancellationToken ct = default)
        => db.Entries.AnyAsync(x => x.Id == entryId && x.UserId == userId, ct);
}

// ============================ Handler:查询 ============================

public sealed class ListEntriesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListEntriesQuery, Result<PagedResult<InterviewEntryListItemDto>>>
{
    public async Task<Result<PagedResult<InterviewEntryListItemDto>>> Handle(
        ListEntriesQuery request, CancellationToken ct)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var size = request.PageSize is < 1 or > 200 ? 20 : request.PageSize;

        var q = db.Entries.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .Include(x => x.Assets)
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            // 票05:列表项 Outcome 由当前轮轮次枚举派生(DeriveOutcome),必须带 Rounds。
            .Include(x => x.Rounds)
            .AsQueryable();

        if (request.CompanyId is { } cid) q = q.Where(x => x.CompanyId == cid);

        if (!string.IsNullOrWhiteSpace(request.Status)
            && Enum.TryParse<InterviewStatus>(request.Status, true, out var st))
            q = q.Where(x => x.Status == st);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(x => x.CompanyName.ToLower().Contains(s)
                          || x.Role.ToLower().Contains(s)
                          || (x.Interviewers != null && x.Interviewers.ToLower().Contains(s)));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.InterviewDate ?? DateOnly.MinValue)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return Result.Success(new PagedResult<InterviewEntryListItemDto>(
            items.Select(x => x.ToListItemDto()).ToList(), total, page, size));
    }
}

public sealed class GetEntryQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetEntryQuery, Result<InterviewEntryDto>>
{
    public async Task<Result<InterviewEntryDto>> Handle(GetEntryQuery request, CancellationToken ct)
    {
        // 详情必须 Include 子集合:它们是字段访问模式,不显式加载会得到空集合(踩过的坑)
        var e = await OwnedEntry.LoadNoTrackingAsync(db, request.Id, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets)
                .Include(x => x.Questions)
                .Include(x => x.Weaknesses)
                .Include(x => x.Actions)
                .Include(x => x.Rounds), ct);

        return e is null
            ? Result.Failure<InterviewEntryDto>(Error.NotFound("面试条目"))
            : Result.Success(e.ToDto());
    }
}

public sealed class GetCompanySummariesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetCompanySummariesQuery, Result<IReadOnlyList<CompanySummaryDto>>>
{
    public async Task<Result<IReadOnlyList<CompanySummaryDto>>> Handle(
        GetCompanySummariesQuery request, CancellationToken ct)
    {
        // 前端左侧"公司树"用:按公司聚合出场次、已分析数、平均分、最近一场
        var rows = await db.Entries.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .GroupBy(x => new { x.CompanyId, x.CompanyName })
            .Select(g => new CompanySummaryDto(
                g.Key.CompanyId,
                g.Key.CompanyName,
                g.Count(),
                g.Count(x => x.Status == InterviewStatus.Analyzed),
                g.Where(x => x.OverallScore != null).Average(x => (double?)x.OverallScore),
                g.Max(x => x.InterviewDate),
                g.OrderByDescending(x => x.InterviewDate).Select(x => x.Result).FirstOrDefault()))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<CompanySummaryDto>>(
            rows.OrderByDescending(r => r.LatestInterviewDate ?? DateOnly.MinValue).ToList());
    }
}

public sealed class GetCompanyRoundsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetCompanyRoundsQuery, Result<IReadOnlyList<CompanyRoundDto>>>
{
    public async Task<Result<IReadOnlyList<CompanyRoundDto>>> Handle(
        GetCompanyRoundsQuery request, CancellationToken ct)
    {
        var rounds = await db.Entries.AsNoTracking()
            .Where(e => e.CompanyId == request.CompanyId && e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { EntryId = e.Id, Round = r })
            .OrderBy(x => x.Round.ScheduledDate ?? DateOnly.MaxValue)
            .ThenBy(x => x.Round.Order)
            .Select(x => new CompanyRoundDto(x.Round.Id, x.EntryId, x.Round.Order, x.Round.Stage, x.Round.ScheduledDate))
            .ToListAsync(ct);

        // 老数据兜底:有条目但无 Round 实体时,按 entry 的 RoundNo/日期合成一条
        if (rounds.Count == 0)
        {
            var fallback = await db.Entries.AsNoTracking()
                .Where(e => e.CompanyId == request.CompanyId && e.UserId == request.UserId)
                .OrderByDescending(e => e.InterviewDate ?? DateOnly.MinValue)
                .Select(e => new { e.Id, e.RoundNo, e.InterviewDate })
                .FirstOrDefaultAsync(ct);
            if (fallback is not null)
            {
                rounds.Add(new CompanyRoundDto(
                    Guid.Empty, fallback.Id, fallback.RoundNo, string.Empty, fallback.InterviewDate));
            }
        }

        return Result.Success<IReadOnlyList<CompanyRoundDto>>(rounds);
    }
}

public sealed class ListWeaknessesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListWeaknessesQuery, Result<IReadOnlyList<WeaknessDto>>>
{
    public async Task<Result<IReadOnlyList<WeaknessDto>>> Handle(
        ListWeaknessesQuery request, CancellationToken ct)
    {
        // 子表直查:先校验父条目归属,他人的条目按不存在处理(404)
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<IReadOnlyList<WeaknessDto>>(Error.NotFound("面试条目"));

        var q = db.Weaknesses.AsNoTracking().Where(w => w.InterviewEntryId == request.EntryId);

        if (!string.IsNullOrWhiteSpace(request.Category)
            && Enum.TryParse<WeaknessCategory>(request.Category, true, out var cat))
            q = q.Where(w => w.Category == cat);

        var list = await q.OrderByDescending(w => w.Severity).ThenBy(w => w.Category)
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<WeaknessDto>>(list.Select(w => w.ToDto()).ToList());
    }
}

public sealed class ListAnalysisJobsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListAnalysisJobsQuery, Result<IReadOnlyList<AnalysisJobDto>>>
{
    public async Task<Result<IReadOnlyList<AnalysisJobDto>>> Handle(
        ListAnalysisJobsQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<IReadOnlyList<AnalysisJobDto>>(Error.NotFound("面试条目"));

        var jobs = await db.AnalysisJobs.AsNoTracking()
            .Where(j => j.InterviewEntryId == request.EntryId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<AnalysisJobDto>>(jobs.Select(j => new AnalysisJobDto(
            j.Id, j.JobType.ToString(), j.Status.ToString(), j.Attempts, j.MaxAttempts,
            j.FailureReason, j.LastError, j.CreatedAt, j.DispatchedAt, j.CompletedAt)).ToList());
    }
}

public sealed class GetInterviewStatsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetInterviewStatsQuery, Result<InterviewStatsDto>>
{
    public async Task<Result<InterviewStatsDto>> Handle(GetInterviewStatsQuery request,
        CancellationToken ct)
    {
        var entries = await db.Entries.AsNoTracking()
            .Where(x => x.UserId == request.UserId).ToListAsync(ct);
        // 子表无 UserId:按当前用户的条目 Id 集合收口,不得把他人问答/短板计进统计
        var ownedIds = db.Entries.Where(x => x.UserId == request.UserId).Select(x => x.Id);
        var weaknesses = await db.Weaknesses.AsNoTracking()
            .Where(w => ownedIds.Contains(w.InterviewEntryId)).ToListAsync(ct);
        var questions = await db.Questions.AsNoTracking()
            .Where(q => ownedIds.Contains(q.InterviewEntryId)).ToListAsync(ct);

        var analyzed = entries.Where(e => e.Status == InterviewStatus.Analyzed).ToList();

        double? Avg(Func<Domain.InterviewEntry, int?> sel)
        {
            var vals = analyzed.Select(sel).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            return vals.Count == 0 ? null : Math.Round(vals.Average(), 1);
        }

        var weaknessByCat = weaknesses
            .GroupBy(w => w.Category.ToString())
            .Select(g => new CategoryCountDto(g.Key, g.Count(),
                Math.Round(g.Average(w => (double)w.Severity), 1)))
            .OrderByDescending(x => x.Count)
            .ToList();

        var questionByCat = questions
            .GroupBy(q => q.Category.ToString())
            .Select(g => new CategoryCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        // 进步曲线:按面试日期排序的总分序列(前端折线图)
        var trend = analyzed
            .Where(e => e.OverallScore.HasValue && e.InterviewDate.HasValue)
            .OrderBy(e => e.InterviewDate)
            .Select(e => new TrendPointDto(e.InterviewDate!.Value, e.OverallScore!.Value, e.CompanyName))
            .ToList();

        return Result.Success(new InterviewStatsDto(
            entries.Count,
            entries.Count(e => e.Status == InterviewStatus.Draft),
            entries.Count(e => e.Status == InterviewStatus.Transcribing),
            entries.Count(e => e.Status == InterviewStatus.Transcribed),
            entries.Count(e => e.Status == InterviewStatus.Analyzing),
            entries.Count(e => e.Status == InterviewStatus.Analyzed),
            entries.Count(e => e.Status == InterviewStatus.Failed),
            questions.Count,
            questions.Count(q => q.GotStuck),
            weaknesses.Count,
            Avg(e => e.OverallScore),
            Avg(e => e.PronunciationScore),
            Avg(e => e.FluencyScore),
            Avg(e => e.StructureScore),
            Avg(e => e.TechnicalDepthScore),
            Avg(e => e.RelevanceScore),
            weaknessByCat,
            questionByCat,
            trend,
            entries.Count(e => e.Status is InterviewStatus.Transcribed or InterviewStatus.AssetsUploaded
                or InterviewStatus.Transcribing)));
    }
}

// ============================ Handler:命令 ============================

public sealed class CreateEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<CreateEntryCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateEntryCommand request, CancellationToken ct)
    {
        // FUN-03(票10):JobApplicationId 幂等 —— Tracker 点 Prep 重复点击/重试时,
        // 当前用户名下已有同 JobApplicationId 的条目就直接返回既有条目 id、不新建,
        // 否则同一投递散落两条备考记录,轮次/材料各记一半。口径与票03 用户隔离
        // 一致:只查当前用户自己的条目(全局过滤器之外再显式带 UserId 谓词);
        // 软删条目被全局过滤器排除,删除后重新 Prep 仍可正常新建。
        // 无 JobApplicationId(或空 Guid)的建单不参与去重,行为不变。
        // 存量若已有多条重复,返回最早建的那条(原始条目)。
        if (request.JobApplicationId is { } jobApplicationId && jobApplicationId != Guid.Empty)
        {
            var existingId = await db.Entries.AsNoTracking()
                .Where(x => x.UserId == request.UserId && x.JobApplicationId == jobApplicationId)
                .OrderBy(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (existingId is not null)
                return Result.Success(existingId.Value);
        }

        // CompanyId 可空:Tracker 一键创建时只知道公司名,按名复用已有公司的 Id,
        // 找不到则生成新的 —— 避免 Guid.Empty 导致公司树聚合错乱。
        var companyId = request.CompanyId;
        if (companyId is null || companyId == Guid.Empty)
        {
            var existing = await db.Entries.AsNoTracking()
                .Where(x => x.CompanyName == request.CompanyName && x.UserId == request.UserId)
                .Select(x => x.CompanyId)
                .FirstOrDefaultAsync(ct);
            companyId = existing == Guid.Empty ? Guid.NewGuid() : existing;
        }

        var entry = new InterviewEntry(companyId.Value, request.CompanyName, request.Role,
            request.JobApplicationId, userId: request.UserId);

        if (request.CompanyProfile is not null || request.JdText is not null
            || request.JdSummary is not null || request.Interviewers is not null
            || request.InterviewDate is not null || request.Location is not null
            || request.Notes is not null)
        {
            entry.UpdateBasicInfo(request.CompanyName, request.Role, request.CompanyProfile,
                request.JdText, request.JdSummary, 1, request.InterviewDate,
                request.InterviewFormat, request.Interviewers, request.Location, null,
                request.Notes);
        }

        // 从 Tracker 建条目必须至少有一个 Round 实体,否则 Rounds tab 与 Basic info 不一致
        var round = entry.AddRound();
        if (request.InterviewDate is not null || request.InterviewFormat is not null
            || request.Interviewers is not null || request.Location is not null)
        {
            entry.UpdateRound(round.Id, round.Stage, request.InterviewDate,
                request.Interviewers, request.InterviewFormat, request.Location,
                round.Outcome, null, null);
        }

        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct);
        return Result.Success(entry.Id);
    }
}

public sealed class UpdateEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateEntryCommand, Result>
{
    public async Task<Result> Handle(UpdateEntryCommand request, CancellationToken ct)
    {
        // 票04:UpdateBasicInfo 要按 RoundNo 找对应轮次的定论枚举填结构化 outcome,
        // 必须带 Rounds 加载,否则 _rounds 为空、事件永远无枚举。
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        e.UpdateBasicInfo(request.CompanyName, request.Role, request.CompanyProfile,
            request.JdText, request.JdSummary, request.RoundNo, request.InterviewDate,
            request.InterviewFormat, request.Interviewers, request.Location, request.Result,
            request.Notes, request.QuestionsToAskJson);
        if (request.DebriefJson is not null)
            e.SetDebrief(request.DebriefJson);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class LinkJobApplicationCommandHandler(InterviewsDbContext db)
    : IRequestHandler<LinkJobApplicationCommand, Result>
{
    public async Task<Result> Handle(LinkJobApplicationCommand request, CancellationToken ct)
    {
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        if (e.LinkJobApplication(request.JobApplicationId))
            await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteEntryCommand, Result>
{
    public async Task<Result> Handle(DeleteEntryCommand request, CancellationToken ct)
    {
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        e.MarkDeleted();   // 软删除:全局查询过滤器会自动隐藏
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AttachAssetCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AttachAssetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AttachAssetCommand request, CancellationToken ct)
    {
        // 带子集合加载 —— 聚合方法要往集合里加东西(Rounds 用于校验 RoundId)
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<AssetKind>(request.Kind, true, out var kind))
            return Result.Failure<Guid>(Error.Validation("Asset.InvalidKind", "材料类型无效"));

        // 2026-10-19:阶段枚举(Pre=面试前 / Post=面试后),大小写不敏感
        AssetPhase? phase = null;
        if (!string.IsNullOrWhiteSpace(request.Phase))
        {
            if (!Enum.TryParse<AssetPhase>(request.Phase, true, out var p))
                return Result.Failure<Guid>(Error.Validation("Asset.InvalidPhase", "阶段只能是 Pre / Post"));
            phase = p;
        }

        try
        {
            var asset = e.AttachAsset(kind, request.FileName, request.ContentType, request.SizeBytes,
                request.StoragePath, request.BlobUrl, request.TranscriptText,
                request.TranscriptSegmentsJson, request.DurationSeconds, request.SourceLanguage,
                request.Sha256, request.RoundId, phase, request.MetaJson);
            await db.SaveChangesAsync(ct);
            return Result.Success(asset.Id);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Conflict("Asset.InvalidState", ex.Message));
        }
    }
}

public sealed class UploadAssetFileCommandHandler(
    InterviewsDbContext db,
    IInterviewAudioStore store,
    ILogger<UploadAssetFileCommandHandler> logger)
    : IRequestHandler<UploadAssetFileCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UploadAssetFileCommand request, CancellationToken ct)
    {
        if (request.Content is null || request.Content.CanRead == false)
            return Result.Failure<Guid>(Error.Validation("Asset.EmptyFile", "没有收到音频内容"));

        if (request.Content.Length > LocalInterviewAudioStore.MaxFileBytes)
            return Result.Failure<Guid>(Error.Validation("Asset.TooLarge",
                $"录音超过上限 {LocalInterviewAudioStore.MaxFileBytes / 1024 / 1024}MB"));

        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        // 1) 先落盘(文件名由存储层生成,天然唯一;tmp+原子改名,崩溃不留半截文件)
        StoredAudioFile stored;
        try
        {
            stored = await store.SaveAsync(request.EntryId, request.FileName, request.Content, ct);
        }
        catch (InvalidOperationException ex)   // 扩展名白名单拒绝
        {
            return Result.Failure<Guid>(Error.Validation("Asset.BadFormat", ex.Message));
        }

        // 2) 再登记元数据。落盘成功但入库失败时,尽力清掉孤儿文件 ——
        //    留着它,巡检永远报"数据库里没有这条资产"。
        AssetPhase? phase = null;
        if (!string.IsNullOrWhiteSpace(request.Phase))
        {
            if (!Enum.TryParse<AssetPhase>(request.Phase, true, out var p2))
                return Result.Failure<Guid>(Error.Validation("Asset.InvalidPhase", "阶段只能是 Pre / Post"));
            phase = p2;
        }
        try
        {
            var asset = e.AttachAsset(AssetKind.Audio, request.FileName, request.ContentType,
                stored.SizeBytes, stored.RelativePath, null, null, null,
                request.DurationSeconds, request.SourceLanguage ?? "en", stored.Sha256,
                request.RoundId, phase);
            await db.SaveChangesAsync(ct);
            return Result.Success(asset.Id);
        }
        catch (InvalidOperationException ex)
        {
            await TryDeleteQuietly(stored.RelativePath, ct);
            return Result.Failure<Guid>(Error.Conflict("Asset.InvalidState", ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "登记录音元数据失败,清理已落盘文件 {Path}", stored.RelativePath);
            await TryDeleteQuietly(stored.RelativePath, ct);
            throw;
        }
    }

    private async Task TryDeleteQuietly(string relativePath, CancellationToken ct)
    {
        try { await store.DeleteAsync(relativePath, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "清理孤儿录音文件失败 {Path}", relativePath); }
    }
}

public sealed class GetAssetAudioQueryHandler(
    InterviewsDbContext db,
    IInterviewAudioStore store)
    : IRequestHandler<GetAssetAudioQuery, Result<(Stream, string, string)>>
{
    public async Task<Result<(Stream, string, string)>> Handle(GetAssetAudioQuery request,
        CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<(Stream, string, string)>(Error.NotFound("材料"));

        var a = await db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.AssetId
                && x.InterviewEntryId == request.EntryId, ct);
        if (a is null) return Result.Failure<(Stream, string, string)>(Error.NotFound("材料"));

        if (string.IsNullOrWhiteSpace(a.StoragePath))
            return Result.Failure<(Stream, string, string)>(new Error("AssetAudio.NotFound",
                "该材料没有本地文件(可能是外部链接或纯文本)", ErrorType.NotFound));

        var stream = await store.OpenReadAsync(a.StoragePath, ct);
        if (stream is null)
            return Result.Failure<(Stream, string, string)>(new Error("AssetAudio.NotFound",
                "录音文件已丢失,可重新上传;系统巡检会标记此类资产", ErrorType.NotFound));

        return Result.Success((stream,
            a.ContentType ?? "application/octet-stream", a.FileName));
    }
}

/// <summary>读取转写稿(供 Analysis.Worker 文本模式取粘贴文本)。</summary>
public sealed record GetTranscriptQuery(Guid UserId, Guid EntryId, Guid AssetId, Guid? OwnerUserId = null)
    : IRequest<Result<TranscriptPayload>>;

public sealed record TranscriptPayload(string? FullText, string? SegmentsJson);

public sealed class GetTranscriptQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetTranscriptQuery, Result<TranscriptPayload>>
{
    public async Task<Result<TranscriptPayload>> Handle(GetTranscriptQuery request,
        CancellationToken ct)
    {
        var e = await OwnedEntry.LoadNoTrackingAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets), ct);
        if (e is null) return Result.Failure<TranscriptPayload>(Error.NotFound("面试条目"));
        var a = e.Assets.FirstOrDefault(x => x.Id == request.AssetId);
        if (a is null) return Result.Failure<TranscriptPayload>(Error.NotFound("材料"));
        return Result.Success(new TranscriptPayload(a.TranscriptText, a.TranscriptSegmentsJson));
    }
}

public sealed class SaveTranscriptCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SaveTranscriptCommand, Result>
{
    public async Task<Result> Handle(SaveTranscriptCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions).Include(x => x.Weaknesses), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.CompleteTranscription(request.AssetId, request.FullText, request.SegmentsJson);
            // 转写稿落库 = 管线走完了前半程:同事务关单(Succeeded)
            await db.StageCloseOpenJobsAsync(request.EntryId, AnalysisJobStatus.Succeeded, null, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Transcript.InvalidState", ex.Message));
        }
    }
}

public sealed class RequestAnalysisCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RequestAnalysisCommand, Result>
{
    public async Task<Result> Handle(RequestAnalysisCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions).Include(x => x.Weaknesses), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.StartAnalysis();
            await db.SaveChangesAsync(ct);   // 领域事件在保存后由拦截器发布 → 触发 Worker
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Analysis.InvalidState", ex.Message));
        }
    }
}

public sealed class ApplyAnalysisCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ApplyAnalysisCommand, Result>
{
    public async Task<Result> Handle(ApplyAnalysisCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions)
                .Include(x => x.Weaknesses).Include(x => x.Snapshots), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.ApplyAnalysis(request.Overall, request.Pronunciation, request.Fluency,
                request.Structure, request.TechnicalDepth, request.Relevance, request.Summary,
                request.Questions, request.Weaknesses, request.SpeechMetricsJson,
                request.CandidateTranscript);
            // A3:每次分析成功回写记一条快照 —— 趋势曲线与"较上次"对比的数据源。
            // DimensionsJson 存 6 维可空分数;Summary 按句切成 3~5 条一句话结论。
            e.AddAnalysisSnapshot(request.Overall,
                PlaybookText.BuildDimensionsJson(request.Overall, request.Pronunciation,
                    request.Fluency, request.Structure, request.TechnicalDepth,
                    request.Relevance),
                PlaybookText.SummaryToBulletsJson(request.Summary));
            // 分析结果回写 = 流水线终点:同事务关单
            await db.StageCloseOpenJobsAsync(request.EntryId, AnalysisJobStatus.Succeeded, null, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Analysis.InvalidState", ex.Message));
        }
    }
}

public sealed class MarkEntryFailedCommandHandler(InterviewsDbContext db)
    : IRequestHandler<MarkEntryFailedCommand, Result>
{
    public async Task<Result> Handle(MarkEntryFailedCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId, null, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try
        {
            e.MarkFailed(request.Reason);
            // 失败上报:同事务把任务关成 Failed,原因留档可查
            await db.StageCloseOpenJobsAsync(request.EntryId, AnalysisJobStatus.Failed,
                request.Reason, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Entry.InvalidState", ex.Message));
        }
    }
}

public sealed class AddQuestionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AddQuestionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddQuestionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<QuestionCategory>(request.Category, true, out var cat))
            return Result.Failure<Guid>(Error.Validation("Question.InvalidCategory", "问题类型无效"));

        InterviewQuestion q;
        try
        {
            q = e.AddQuestion(request.QuestionText, request.MyAnswerText, cat, request.Difficulty,
                request.RoundId, request.AskedBy);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Conflict("Question.InvalidState", ex.Message));
        }
        if (request.Assessment is not null)
            q.UpdateFromAnalysis(request.QuestionText, request.MyAnswerText, request.Assessment,
                cat, request.Difficulty, false, null, null, null);

        await db.SaveChangesAsync(ct);
        return Result.Success(q.Id);
    }
}

public sealed class UpdateQuestionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateQuestionCommand, Result>
{
    public async Task<Result> Handle(UpdateQuestionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        if (!Enum.TryParse<QuestionCategory>(request.Category, true, out var cat))
            return Result.Failure(Error.Validation("Question.InvalidCategory", "问题类型无效"));

        try
        {
            e.UpdateQuestion(request.QuestionId, request.QuestionText, request.MyAnswerText, cat,
                request.Difficulty, request.Assessment, request.GotStuck, request.StuckReason,
                request.RecommendedAnswer);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class RemoveQuestionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RemoveQuestionCommand, Result>
{
    public async Task<Result> Handle(RemoveQuestionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try
        {
            e.RemoveQuestion(request.QuestionId);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class AddRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AddRoundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        var r = e.AddRound(request.Stage);
        // ★ 一次性设置日期/面试官/形式/地点,避免建完再改两步走
        if (request.ScheduledDate.HasValue || !string.IsNullOrWhiteSpace(request.Interviewers) ||
            !string.IsNullOrWhiteSpace(request.Format) || !string.IsNullOrWhiteSpace(request.Location))
        {
            e.UpdateRound(r.Id, request.Stage, request.ScheduledDate, request.Interviewers,
                request.Format, request.Location, InterviewRoundOutcome.Pending, null, null);
        }
        // 2026-10-20 v2 修复:开始时间此前被丢弃 —— 统一入口/沟通建轮次都带时间,必须落库。
        if (!string.IsNullOrWhiteSpace(request.ScheduledTime))
        {
            r.UpdateDetails(null, request.ScheduledTime, null, null, null, null);
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(r.Id);
    }
}

/// <summary>全局 upcoming:未来 N 天内所有条目的轮次,按日期排序,供日历视图用。</summary>
public sealed class GetUpcomingRoundsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetUpcomingRoundsQuery, Result<List<UpcomingRoundDto>>>
{
    public async Task<Result<List<UpcomingRoundDto>>> Handle(
        GetUpcomingRoundsQuery request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var limit = today.AddDays(request.Days);

        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { e, r })
            .Where(x => x.r.ScheduledDate != null
                && x.r.ScheduledDate >= today
                && x.r.ScheduledDate <= limit)
            .OrderBy(x => x.r.ScheduledDate).ThenBy(x => x.r.ScheduledTime)
            .Select(x => new UpcomingRoundDto(
                x.r.Id, x.e.Id, x.e.CompanyName, x.e.Role,
                x.r.Order, x.r.Stage, x.r.ScheduledDate, x.r.ScheduledTime,
                x.r.Interviewers, x.r.Format, x.r.Location,
                x.r.MeetingLink, x.r.Outcome.ToString()))
            .ToListAsync(ct);

        return Result.Success(rows);
    }
}

/// <summary>轮次通过概率:基于用户历史同 stage 轮次的通过率 + 本轮准备度信号。</summary>
public sealed record PassProbabilityDto(
    int Probability,           // 0-100
    int SampleSize,            // 历史样本数
    int PassedCount,
    string Stage,
    List<OfferFactorDto> Factors);     // 影响因素说明(Key 由前端按 locale 格式化)

public sealed record GetRoundPassProbabilityQuery(Guid UserId, Guid RoundId) : IRequest<Result<PassProbabilityDto>>;

public sealed class GetRoundPassProbabilityQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetRoundPassProbabilityQuery, Result<PassProbabilityDto>>
{
    public async Task<Result<PassProbabilityDto>> Handle(
        GetRoundPassProbabilityQuery request, CancellationToken ct)
    {
        // 找到本轮,取其 stage
        var round = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { e, r })
            .Where(x => x.r.Id == request.RoundId)
            .Select(x => new { x.r.Stage, RoundId = x.r.Id })
            .FirstOrDefaultAsync(ct);
        if (round is null) return Result.Failure<PassProbabilityDto>(Error.NotFound("Round"));

        // 历史同 stage 轮次:只算有定论的 (Passed / Rejected)
        var history = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds)
            .Where(r => r.Stage == round.Stage
                && r.Id != round.RoundId
                && (r.Outcome == InterviewRoundOutcome.Passed
                    || r.Outcome == InterviewRoundOutcome.Rejected))
            .ToListAsync(ct);

        var factors = new List<OfferFactorDto>();
        int baseProb;
        int historyCount = history.Count;
        if (historyCount == 0)
        {
            // 无历史数据:中性 50%,提示多攒数据
            baseProb = 50;
            factors.Add(new OfferFactorDto("pp_no_history"));
        }
        else
        {
            var passed = history.Count(r => r.Outcome == InterviewRoundOutcome.Passed);
            baseProb = (int)Math.Round(100.0 * passed / historyCount);
            factors.Add(new OfferFactorDto("pp_history_rate", passed, historyCount));
        }

        // 准备度信号:本轮的准备越充分,微调概率
        var target = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { e, r })
            .Where(x => x.r.Id == request.RoundId)
            .Select(x => new
            {
                x.r.PrepQuestionsJson,
                x.r.Transcript,
                x.r.RecordingUrl,
                x.r.EmailsJson,
                x.r.Notes,
                x.r.Feedback
            })
            .FirstOrDefaultAsync(ct);

        int adjustment = 0;
        if (target is not null)
        {
            if (!string.IsNullOrWhiteSpace(target.PrepQuestionsJson)
                && target.PrepQuestionsJson != "[]")
            { adjustment += 5; factors.Add(new OfferFactorDto("pp_prep_list")); }

            if (!string.IsNullOrWhiteSpace(target.Notes))
            { adjustment += 3; factors.Add(new OfferFactorDto("pp_has_notes")); }

            if (!string.IsNullOrWhiteSpace(target.EmailsJson)
                && target.EmailsJson != "[]")
            { adjustment += 2; factors.Add(new OfferFactorDto("pp_has_email")); }
        }

        // 查本轮所属条目的 Q&A 积累情况
        var entry = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId && e.Rounds.Any(r => r.Id == request.RoundId))
            .Select(e => new { e.Id, QaCount = e.Questions.Count })
            .FirstOrDefaultAsync(ct);
        if (entry is not null)
        {
            if (entry.QaCount > 0)
            { adjustment += Math.Min(5, entry.QaCount); factors.Add(new OfferFactorDto("pp_qa_accum", entry.QaCount, Math.Min(5, entry.QaCount))); }
        }

        var prob = Math.Clamp(baseProb + adjustment, 5, 95);
        var passedTotal = history.Count(r => r.Outcome == InterviewRoundOutcome.Passed);
        return Result.Success(new PassProbabilityDto(
            prob, historyCount, passedTotal,
            round.Stage, factors));
    }
}

/// <summary>整单 offer 概率:漏斗模型 —— 走到当前轮次的人,历史上有多少拿到 offer。</summary>
public sealed record OfferProbabilityDto(
    int Probability,           // 0-100
    int SampleSize,            // 走到当前轮次的历史样本数
    int OfferCount,            // 其中拿到 offer 的
    int CurrentRound,          // 当前第几轮
    List<OfferFactorDto> Factors);

/// <summary>概率因子:Key 由前端按 locale 格式化,避免后端硬编码中文。</summary>
public sealed record OfferFactorDto(string Key, int Arg1 = 0, int Arg2 = 0, int Arg3 = 0);

public sealed record GetOfferProbabilityQuery(Guid UserId, Guid EntryId) : IRequest<Result<OfferProbabilityDto>>;

public sealed class GetOfferProbabilityQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetOfferProbabilityQuery, Result<OfferProbabilityDto>>
{
    public async Task<Result<OfferProbabilityDto>> Handle(
        GetOfferProbabilityQuery request, CancellationToken ct)
    {
        // 当前条目:第几轮(按 rounds 数量)
        var current = await db.Entries.AsNoTracking()
            .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
            .Select(e => new { RoundCount = e.Rounds.Count, e.Result })
            .FirstOrDefaultAsync(ct);
        if (current is null) return Result.Failure<OfferProbabilityDto>(Error.NotFound("Entry"));

        int currentRound = Math.Max(1, current.RoundCount);
        var factors = new List<OfferFactorDto>();

        // 历史漏斗:走到 >= 当前轮次 且已有定论的条目
        var history = await db.Entries.AsNoTracking()
            .Where(e => e.Id != request.EntryId && e.UserId == request.UserId
                && e.Rounds.Count >= currentRound
                && (e.Result == "Passed" || e.Result == "Rejected"))
            .Select(e => e.Result)
            .ToListAsync(ct);

        int baseProb;
        if (history.Count == 0)
        {
            baseProb = 50;
            factors.Add(new OfferFactorDto("no_history", currentRound));
        }
        else
        {
            var offers = history.Count(r => r == "Passed");
            baseProb = (int)Math.Round(100.0 * offers / history.Count);
            factors.Add(new OfferFactorDto("history_rate", currentRound, offers, history.Count));
        }

        // 准备度:整单的 Q&A、轮次准备完整度微调
        var prep = await db.Entries.AsNoTracking()
            .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
            .Select(e => new
            {
                QaCount = e.Questions.Count,
                RoundPrepDone = e.Rounds.Count(r =>
                    r.PrepQuestionsJson != null && r.PrepQuestionsJson != "[]")
            })
            .FirstOrDefaultAsync(ct);

        int adjustment = 0;
        if (prep is not null)
        {
            if (prep.QaCount >= 5)
            { adjustment += 5; factors.Add(new OfferFactorDto("qa_bonus", prep.QaCount)); }
            if (prep.RoundPrepDone > 0)
            {
                int prepBonus = 3 * Math.Min(3, prep.RoundPrepDone);
                adjustment += prepBonus;
                factors.Add(new OfferFactorDto("prep_bonus", prep.RoundPrepDone, prepBonus));
            }
        }

        // 越往后的轮次,基础概率自然越高(已经筛掉了前面的人)
        int funnelBonus = Math.Min(15, (currentRound - 1) * 5);
        if (funnelBonus > 0)
        { adjustment += funnelBonus; factors.Add(new OfferFactorDto("funnel_bonus", currentRound, funnelBonus)); }

        var prob = Math.Clamp(baseProb + adjustment, 5, 95);
        return Result.Success(new OfferProbabilityDto(
            prob, history.Count,
            history.Count(r => r == "Passed"),
            currentRound, factors));
    }
}

public sealed class UpdateRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateRoundCommand, Result>
{
    public async Task<Result> Handle(UpdateRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        if (!Enum.TryParse<InterviewRoundOutcome>(request.Outcome, true, out var outcome))
            return Result.Failure(Error.Validation("Round.InvalidOutcome", "轮次结果无效"));

        try
        {
            e.UpdateRound(request.RoundId, request.Stage, request.ScheduledDate, request.Interviewers,
                request.Format, request.Location, outcome, request.Notes, request.Feedback);
            var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
            round?.UpdateDetails(request.MeetingLink, request.ScheduledTime, request.PrepQuestionsJson,
                request.EmailsJson, request.Transcript, request.RecordingUrl, request.ChecklistJson);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class RemoveRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RemoveRoundCommand, Result>
{
    public async Task<Result> Handle(RemoveRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try
        {
            e.RemoveRound(request.RoundId);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class AddWeaknessCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AddWeaknessCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddWeaknessCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<WeaknessCategory>(request.Category, true, out var cat))
            return Result.Failure<Guid>(Error.Validation("Weakness.InvalidCategory", "短板分类无效"));

        var w = e.AddWeakness(cat, request.Title, request.Detail, request.Evidence,
            request.Severity, request.Suggestion);
        await db.SaveChangesAsync(ct);
        return Result.Success(w.Id);
    }
}

public sealed class RemoveWeaknessCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RemoveWeaknessCommand, Result>
{
    public async Task<Result> Handle(RemoveWeaknessCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        e.RemoveWeakness(request.WeaknessId);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ============================ 指导材料(缺口2) ============================

/// <summary>手动触发:为该条目生成一份新的面试指导材料(版本号自动 +1)。</summary>
public sealed record GenerateGuidanceCommand(
    Guid UserId, Guid EntryId,
    string? JdText = null,
    string? ResumeText = null,
    string? InterviewExperiences = null,
    string? CustomRequirements = null) : IRequest<Result<GuidanceMaterialDto>>;

public sealed record GetGuidanceQuery(Guid UserId, Guid EntryId, int? Version = null)
    : IRequest<Result<GuidanceMaterialDto>>;

public sealed record ListGuidanceVersionsQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<IReadOnlyList<GuidanceVersionDto>>>;

public sealed class GenerateGuidanceCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GenerateGuidanceCommand, Result<GuidanceMaterialDto>>
{
    public async Task<Result<GuidanceMaterialDto>> Handle(GenerateGuidanceCommand request, CancellationToken ct)
    {
        var e = await db.Entries
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            .Include(x => x.Rounds)
            .Include(x => x.Materials)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<GuidanceMaterialDto>(Error.NotFound("面试条目"));

        var prompt = BuildPrompt(e, request);

        AiCompletionResult completion;
        try
        {
            completion = await ai.CompleteAsync("interview-guidance", prompt,
                "你是资深面试教练,只输出 Markdown,不要多余寒暄。", 0.7, 4000, ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            return Result.Failure<GuidanceMaterialDto>(
                Error.Validation("Guidance.AiNotConfigured", "AI 未配置:请先在设置里配置模型凭据。"));
        }
        catch (AiGatewayException ex)
        {
            return Result.Failure<GuidanceMaterialDto>(Error.Validation("Guidance.AiFailed", ex.Message));
        }

        var m = e.AddMaterial(completion.Text, completion.Model,
            completion.PromptTokens, completion.CompletionTokens);
        await db.SaveChangesAsync(ct);
        return Result.Success(m.ToDto());
    }

    private static string BuildPrompt(InterviewEntry e, GenerateGuidanceCommand req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 面试对象:{e.CompanyName} —— {e.Role}");
        if (!string.IsNullOrWhiteSpace(e.CompanyProfile))
            sb.AppendLine($"## 公司情况\n{e.CompanyProfile}");
        // JD:优先用本次传入的,fallback 到 entry 存的
        var jd = req.JdText;
        if (!string.IsNullOrWhiteSpace(jd))
            sb.AppendLine($"## JD(本次提供)\n{jd[..Math.Min(jd.Length, 8000)]}");
        else if (!string.IsNullOrWhiteSpace(e.JdSummary))
            sb.AppendLine($"## JD 要点\n{e.JdSummary}");
        else if (!string.IsNullOrWhiteSpace(e.JdText))
            sb.AppendLine($"## JD 原文(截断)\n{e.JdText[..Math.Min(e.JdText.Length, 6000)]}");
        // 简历
        if (!string.IsNullOrWhiteSpace(req.ResumeText))
            sb.AppendLine($"## 候选人简历\n{req.ResumeText[..Math.Min(req.ResumeText.Length, 8000)]}");
        // 全网面经
        if (!string.IsNullOrWhiteSpace(req.InterviewExperiences))
            sb.AppendLine($"## 相关面经(全网检索)\n{req.InterviewExperiences[..Math.Min(req.InterviewExperiences.Length, 8000)]}");
        // 用户自定义要求
        if (!string.IsNullOrWhiteSpace(req.CustomRequirements))
            sb.AppendLine($"## 用户特别要求\n{req.CustomRequirements}");

        var rounds = e.Rounds.OrderBy(r => r.Order).ToList();
        if (rounds.Count > 0)
        {
            sb.AppendLine("## 已知轮次");
            foreach (var r in rounds)
                sb.AppendLine($"- 第{r.Order}轮 {r.Stage}:面试官 {r.Interviewers ?? "未知"},形式 {r.Format ?? "未知"},结果 {r.Outcome}");
        }

        if (!string.IsNullOrWhiteSpace(e.AnalysisSummary))
            sb.AppendLine($"## AI 总评\n{e.AnalysisSummary}");
        sb.AppendLine($"## 六维分数:发音 {e.PronunciationScore},流利 {e.FluencyScore}," +
                      $"结构 {e.StructureScore},技术深度 {e.TechnicalDepthScore},相关度 {e.RelevanceScore}");

        var qs = e.Questions.OrderBy(q => q.Sequence).ToList();
        if (qs.Count > 0)
        {
            sb.AppendLine("## 历史问答(重点看答得不好的)");
            foreach (var q in qs.Take(30))
            {
                sb.AppendLine($"- [{q.Category}] {q.QuestionText}");
                if (!string.IsNullOrWhiteSpace(q.MyAnswerText))
                    sb.AppendLine($"  我的回答:{q.MyAnswerText[..Math.Min(q.MyAnswerText.Length, 500)]}");
                if (!string.IsNullOrWhiteSpace(q.Assessment))
                    sb.AppendLine($"  诊断:{q.Assessment}");
                if (q.GotStuck) sb.AppendLine($"  ⚠️ 当场卡壳:{q.StuckReason}");
                if (!string.IsNullOrWhiteSpace(q.RecommendedAnswer))
                    sb.AppendLine($"  推荐答案:{q.RecommendedAnswer[..Math.Min(q.RecommendedAnswer.Length, 500)]}");
            }
        }

        var ws = e.Weaknesses.OrderByDescending(w => w.Severity).ToList();
        if (ws.Count > 0)
        {
            sb.AppendLine("## 短板清单");
            foreach (var w in ws.Take(15))
                sb.AppendLine($"- [{w.Category} 严重度{w.Severity}] {w.Title}: {w.Suggestion}");
        }

        sb.AppendLine();
        sb.AppendLine("请输出一份面试指导材料(Markdown),结构:");
        sb.AppendLine("## 1. 公司与岗位速览(3-5 条要点)");
        sb.AppendLine("## 2. 必准备的高频问题(5-8 个,带答题要点,不是完整背稿)");
        sb.AppendLine("## 3. 我的短板针对性补救(结合上面的短板和卡壳点)");
        sb.AppendLine("## 4. 每轮的注意事项(结合已知轮次)");
        sb.AppendLine("## 5. 临场 checklist(开场 30 秒 / 结构骨架 / trade-off 话术)");
        sb.AppendLine("要求:具体、可执行、不说空话;英文术语保留原文。");
        return sb.ToString();
    }
}

public sealed class GetGuidanceQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetGuidanceQuery, Result<GuidanceMaterialDto>>
{
    public async Task<Result<GuidanceMaterialDto>> Handle(GetGuidanceQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<GuidanceMaterialDto>(Error.NotFound("面试条目"));

        var q = db.GuidanceMaterials.Where(m => m.InterviewEntryId == request.EntryId);
        var m = request.Version.HasValue
            ? await q.FirstOrDefaultAsync(x => x.Version == request.Version.Value, ct)
            : await q.OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (m is null) return Result.Failure<GuidanceMaterialDto>(Error.NotFound("指导材料"));
        return Result.Success(m.ToDto());
    }
}

public sealed class ListGuidanceVersionsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListGuidanceVersionsQuery, Result<IReadOnlyList<GuidanceVersionDto>>>
{
    public async Task<Result<IReadOnlyList<GuidanceVersionDto>>> Handle(
        ListGuidanceVersionsQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<IReadOnlyList<GuidanceVersionDto>>(Error.NotFound("面试条目"));

        var list = await db.GuidanceMaterials
            .Where(m => m.InterviewEntryId == request.EntryId)
            .OrderByDescending(m => m.Version)
            .Select(m => new GuidanceVersionDto(m.Id, m.Version, m.GeneratedAt, m.Model))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<GuidanceVersionDto>>(list);
    }
}

// ============================ 问答候选(缺口3) ============================

/// <summary>把该条目的问答转成候选列表,供前端勾选导入 TechStack。默认全选由前端做。</summary>
public sealed record ListQuestionCandidatesQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<IReadOnlyList<QuestionCandidateDto>>>;

public sealed class ListQuestionCandidatesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListQuestionCandidatesQuery, Result<IReadOnlyList<QuestionCandidateDto>>>
{
    public async Task<Result<IReadOnlyList<QuestionCandidateDto>>> Handle(
        ListQuestionCandidatesQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists) return Result.Failure<IReadOnlyList<QuestionCandidateDto>>(Error.NotFound("面试条目"));

        var list = await db.Questions
            .Where(q => q.InterviewEntryId == request.EntryId)
            .OrderBy(q => q.Sequence)
            .Select(q => q.ToCandidateDto())
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<QuestionCandidateDto>>(list);
    }
}

// ============================ Playbook 重做(A1–A7):Handler ============================

/// <summary>A1:设置题目掌握度。</summary>
public sealed class UpdateQuestionMasteryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateQuestionMasteryCommand, Result>
{
    public async Task<Result> Handle(UpdateQuestionMasteryCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.SetQuestionMastery(request.QuestionId, request.Mastery);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

/// <summary>A2:待办列表 —— 未完成在前,同组按创建时间 asc。</summary>
public sealed class ListActionsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListActionsQuery, Result<IReadOnlyList<ActionDto>>>
{
    public async Task<Result<IReadOnlyList<ActionDto>>> Handle(ListActionsQuery request,
        CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists) return Result.Failure<IReadOnlyList<ActionDto>>(Error.NotFound("面试条目"));

        var list = await db.Actions.AsNoTracking()
            .Where(a => a.InterviewEntryId == request.EntryId)
            .OrderBy(a => a.IsDone).ThenBy(a => a.CreatedAt)
            .Select(a => new ActionDto(a.Id, a.Title, a.Detail, a.DueDate, a.IsDone, a.CreatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<ActionDto>>(list);
    }
}

public sealed class CreateActionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<CreateActionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateActionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Actions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        try
        {
            var a = e.AddAction(request.Title, request.Detail, request.DueDate);
            await db.SaveChangesAsync(ct);
            return Result.Success(a.Id);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Validation("Action.Invalid", ex.Message));
        }
    }
}

public sealed class UpdateActionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateActionCommand, Result>
{
    public async Task<Result> Handle(UpdateActionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Actions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.UpdateAction(request.ActionId, request.Title, request.IsDone, request.DueDate);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class DeleteActionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteActionCommand, Result>
{
    public async Task<Result> Handle(DeleteActionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Actions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.RemoveAction(request.ActionId);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

/// <summary>A3:分析趋势 —— 该条目的快照按时间 asc,只返回 id/createdAt/overallScore。</summary>
public sealed class GetAnalysisTrendQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetAnalysisTrendQuery, Result<IReadOnlyList<AnalysisTrendPointDto>>>
{
    public async Task<Result<IReadOnlyList<AnalysisTrendPointDto>>> Handle(
        GetAnalysisTrendQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists)
            return Result.Failure<IReadOnlyList<AnalysisTrendPointDto>>(Error.NotFound("面试条目"));

        var list = await db.AnalysisSnapshots.AsNoTracking()
            .Where(s => s.InterviewEntryId == request.EntryId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new AnalysisTrendPointDto(s.Id, s.CreatedAt, s.OverallScore))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<AnalysisTrendPointDto>>(list);
    }
}

/// <summary>A3 最新快照全文:按 CreatedAt desc 取第一条。</summary>
public sealed class GetLatestSnapshotQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetLatestSnapshotQuery, Result<AnalysisSnapshotDto?>>
{
    public async Task<Result<AnalysisSnapshotDto?>> Handle(
        GetLatestSnapshotQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists)
            return Result.Failure<AnalysisSnapshotDto?>(Error.NotFound("面试条目"));

        var s = await db.AnalysisSnapshots.AsNoTracking()
            .Where(x => x.InterviewEntryId == request.EntryId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new AnalysisSnapshotDto(
                x.Id, x.CreatedAt, x.OverallScore, x.DimensionsJson, x.SummaryBulletsJson))
            .FirstOrDefaultAsync(ct);
        return Result.Success(s);
    }
}

/// <summary>
/// A4:该题在其它面试中的回答版本。
/// 匹配规则:v1 只做归一化(小写去标点去空白)后的精确相等,不做语义相似(按 spec)。
/// 归一化函数进不了 SQL 翻译,先拉候选行再内存过滤 —— 问答表是用户个人数据,量级可控。
/// </summary>
public sealed class GetQuestionHistoryQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetQuestionHistoryQuery, Result<IReadOnlyList<QuestionHistoryDto>>>
{
    public async Task<Result<IReadOnlyList<QuestionHistoryDto>>> Handle(
        GetQuestionHistoryQuery request, CancellationToken ct)
    {
        var target = await db.Questions.AsNoTracking()
            .Where(q => q.Id == request.QuestionId && q.InterviewEntryId == request.EntryId)
            .Select(q => q.QuestionText)
            .FirstOrDefaultAsync(ct);
        if (target is null)
            return Result.Failure<IReadOnlyList<QuestionHistoryDto>>(Error.NotFound("问题"));

        var normalized = PlaybookText.NormalizeQuestionText(target);
        if (string.IsNullOrEmpty(normalized))
            return Result.Success<IReadOnlyList<QuestionHistoryDto>>([]);

        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Questions, (e, q) => new { e, q })
            .Where(x => x.q.Id != request.QuestionId)
            .Select(x => new
            {
                InterviewId = x.e.Id,
                x.e.CompanyName,
                x.e.Role,
                x.e.InterviewDate,
                x.q.QuestionText,
                x.q.MyAnswerText,
                x.q.Mastery
            })
            .ToListAsync(ct);

        var list = rows
            .Where(x => PlaybookText.NormalizeQuestionText(x.QuestionText) == normalized)
            .OrderByDescending(x => x.InterviewDate ?? DateOnly.MinValue)
            .Select(x => new QuestionHistoryDto(x.InterviewId, x.CompanyName, x.Role,
                x.InterviewDate, x.MyAnswerText, x.Mastery))
            .ToList();
        return Result.Success<IReadOnlyList<QuestionHistoryDto>>(list);
    }
}

/// <summary>
/// A5:全局技术覆盖图谱 —— 按 Category 聚合全部问答。
/// askedCount=被问次数,stuckCount=卡壳数,masteredCount=已掌握数(Mastery==2),
/// interviews=出过该类题的公司名(去重,按字母序)。
/// </summary>
public sealed class GetCoverageQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetCoverageQuery, Result<IReadOnlyList<CoverageCategoryDto>>>
{
    public async Task<Result<IReadOnlyList<CoverageCategoryDto>>> Handle(
        GetCoverageQuery request, CancellationToken ct)
    {
        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Questions, (e, q) => new
            {
                Category = q.Category.ToString(),
                q.GotStuck,
                q.Mastery,
                e.CompanyName
            })
            .ToListAsync(ct);

        var list = rows
            .GroupBy(x => x.Category)
            .Select(g => new CoverageCategoryDto(
                g.Key,
                g.Count(),
                g.Count(x => x.GotStuck),
                g.Count(x => x.Mastery == 2),
                g.Select(x => x.CompanyName).Distinct().OrderBy(c => c).ToList()))
            .OrderByDescending(x => x.AskedCount)
            .ToList();
        return Result.Success<IReadOnlyList<CoverageCategoryDto>>(list);
    }
}

/// <summary>A7:整体替换某轮次的邮件时间线(写入 InterviewRound.EmailsJson)。</summary>
public sealed class UpdateRoundEmailsCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateRoundEmailsCommand, Result>
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<Result> Handle(UpdateRoundEmailsCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            var json = JsonSerializer.Serialize(request.Emails, JsonOpts);
            e.SetRoundEmails(request.RoundId, json);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

// ============================ 备考方案(2026-10-19) ============================
// AI 生成某轮备考方案,存为 Kind=PrepDoc / Phase=Pre 的轮次资产:
//   - 面试前:输入 JD + 公司情况 + 简历 + 全网机经 + 面试要求
//   - 下一轮:自动带入之前所有轮次的数据(转录/题目/反馈/分析报告)
// 全网机经当前为输入字段(粘贴);自动 web 搜索待接入搜索 API key
// (Tavily/Serper),见 GeneratePrepPlanCommand.Experiences 注释。

public sealed record PrepPlanDto(
    Guid AssetId, Guid EntryId, Guid RoundId, int RoundOrder, string Markdown,
    string? Model, DateTimeOffset GeneratedAt);

/// <summary>生成某轮备考方案(存为轮次资产)。Experiences=全网机经文本(手动粘贴;自动搜索待配 key)。</summary>
public sealed record GeneratePrepPlanCommand(
    Guid UserId, Guid EntryId, Guid RoundId,
    string? JdText = null,
    string? ResumeText = null,
    string? Experiences = null,
    string? Requirements = null) : IRequest<Result<PrepPlanDto>>;

/// <summary>取某轮最新的备考方案(在线阅读用)。</summary>
public sealed record GetPrepPlanQuery(Guid UserId, Guid EntryId, Guid RoundId)
    : IRequest<Result<PrepPlanDto>>;

public sealed class GeneratePrepPlanCommandValidator : AbstractValidator<GeneratePrepPlanCommand>
{
    public GeneratePrepPlanCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
    }
}

public sealed class GeneratePrepPlanCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GeneratePrepPlanCommand, Result<PrepPlanDto>>
{
    public async Task<Result<PrepPlanDto>> Handle(GeneratePrepPlanCommand request, CancellationToken ct)
    {
        var e = await db.Entries
            .Include(x => x.Rounds)
            .Include(x => x.Assets)
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<PrepPlanDto>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<PrepPlanDto>(Error.NotFound("轮次"));

        var prompt = BuildPrompt(e, round, request);

        AiCompletionResult completion;
        try
        {
            completion = await ai.CompleteAsync("interview-prep-plan", prompt,
                "你是资深面试教练。只输出中文 Markdown 备考方案,结构清晰、可直接执行,不要多余寒暄。",
                0.7, 6000, ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            return Result.Failure<PrepPlanDto>(
                Error.Validation("PrepPlan.AiNotConfigured", "AI 未配置:请先在设置里配置模型凭据。"));
        }
        catch (AiGatewayException ex)
        {
            return Result.Failure<PrepPlanDto>(
                Error.Validation("PrepPlan.AiFailed", ex.Message));
        }

        try
        {
            var asset = e.AttachAsset(AssetKind.PrepDoc,
                $"备考方案-Round{round.Order}.md",
                "text/markdown",
                completion.Text.Length, null, null,
                transcriptText: completion.Text,
                roundId: round.Id, phase: AssetPhase.Pre);
            await db.SaveChangesAsync(ct);
            return Result.Success(new PrepPlanDto(asset.Id, e.Id, round.Id, round.Order,
                completion.Text, completion.Model, asset.UploadedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PrepPlanDto>(Error.Conflict("PrepPlan.InvalidState", ex.Message));
        }
    }

    public static string BuildPrompt(InterviewEntry e, InterviewRound round, GeneratePrepPlanCommand req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 备考目标:{e.CompanyName} —— {e.Role}(第 {round.Order} 轮 / {round.Stage})");
        if (!string.IsNullOrWhiteSpace(round.Interviewers))
            sb.AppendLine($"面试官:{round.Interviewers}");
        if (!string.IsNullOrWhiteSpace(round.Format))
            sb.AppendLine($"形式:{round.Format}");

        // 本轮输入
        var jd = req.JdText;
        if (!string.IsNullOrWhiteSpace(jd))
            sb.AppendLine($"## JD\n{Trunc(jd, 8000)}");
        else if (!string.IsNullOrWhiteSpace(e.JdSummary))
            sb.AppendLine($"## JD 要点\n{e.JdSummary}");
        else if (!string.IsNullOrWhiteSpace(e.JdText))
            sb.AppendLine($"## JD 原文(截断)\n{Trunc(e.JdText, 6000)}");
        if (!string.IsNullOrWhiteSpace(e.CompanyProfile))
            sb.AppendLine($"## 公司情况\n{Trunc(e.CompanyProfile, 4000)}");
        if (!string.IsNullOrWhiteSpace(req.ResumeText))
            sb.AppendLine($"## 候选人简历\n{Trunc(req.ResumeText, 8000)}");
        if (!string.IsNullOrWhiteSpace(req.Experiences))
            sb.AppendLine($"## 全网机经(候选人提供)\n{Trunc(req.Experiences, 6000)}");
        if (!string.IsNullOrWhiteSpace(req.Requirements))
            sb.AppendLine($"## 本轮面试要求\n{Trunc(req.Requirements, 4000)}");

        // 之前所有轮次的数据(下一轮备考的关键输入)
        var prev = e.Rounds.Where(r => r.Order < round.Order).OrderBy(r => r.Order).ToList();
        if (prev.Count > 0)
        {
            sb.AppendLine("## 之前轮次复盘(重点:吸取教训,本轮针对性改进)");
            foreach (var r in prev)
            {
                sb.AppendLine($"### 第 {r.Order} 轮({r.Stage},结果:{r.Outcome})");
                if (!string.IsNullOrWhiteSpace(r.Feedback))
                    sb.AppendLine($"反馈:{Trunc(r.Feedback, 2000)}");
                if (!string.IsNullOrWhiteSpace(r.Transcript))
                    sb.AppendLine($"转录:{Trunc(r.Transcript, 3000)}");
                var rAssets = e.Assets.Where(a => a.RoundId == r.Id && !string.IsNullOrWhiteSpace(a.TranscriptText)).ToList();
                foreach (var a in rAssets)
                    sb.AppendLine($"材料({a.Kind},{a.FileName}):{Trunc(a.TranscriptText!, 3000)}");
            }
        }

        // 题目库(问答导入):暴露过的弱点重点补
        var stuck = e.Questions.Where(q => q.GotStuck).Take(20).ToList();
        if (stuck.Count > 0)
        {
            sb.AppendLine("## 曾卡壳的题目(重点补强)");
            foreach (var q in stuck)
                sb.AppendLine($"- {Trunc(q.QuestionText, 300)}");
        }

        // 短板 + 分析报告
        if (e.Weaknesses.Count > 0)
        {
            sb.AppendLine("## 已识别短板");
            foreach (var w in e.Weaknesses.Take(15))
                sb.AppendLine($"- {w.Title}: {Trunc(w.Suggestion ?? w.Detail ?? "", 200)}");
        }
        if (!string.IsNullOrWhiteSpace(e.AnalysisSummary))
            sb.AppendLine($"## 分析报告摘要\n{Trunc(e.AnalysisSummary, 3000)}");

        sb.AppendLine();
        sb.AppendLine("请输出备考方案,包含:1)本轮考察重点预测 2)必准备的题目清单(带答题要点) " +
            "3)简历针对性包装建议 4)上一轮教训的改进动作(如有) 5)面试当天 Checklist。");
        return sb.ToString();
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed class GetPrepPlanQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetPrepPlanQuery, Result<PrepPlanDto>>
{
    public async Task<Result<PrepPlanDto>> Handle(GetPrepPlanQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<PrepPlanDto>(Error.NotFound("面试条目"));

        var a = await db.Assets.AsNoTracking()
            .Where(x => x.InterviewEntryId == request.EntryId
                && x.RoundId == request.RoundId
                && x.Kind == AssetKind.PrepDoc)
            .OrderByDescending(x => x.UploadedAt)
            .FirstOrDefaultAsync(ct);
        if (a is null) return Result.Failure<PrepPlanDto>(Error.NotFound("备考方案"));
        var order = await db.Entries.AsNoTracking()
            .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
            .SelectMany(e => e.Rounds)
            .Where(r => r.Id == request.RoundId)
            .Select(r => r.Order)
            .FirstOrDefaultAsync(ct);
        return Result.Success(new PrepPlanDto(a.Id, request.EntryId, request.RoundId, order,
            a.TranscriptText ?? "", null, a.UploadedAt));
    }
}

// ============================ 详情页 v2(2026-10-20 定稿) ============================
// 统一入口归属 / 逐题复盘 / 结构化备考报告 / 转写抽题 / 说话人改名 / ICS。
// JSON 结构由前后端共同约定(见 replica/playbook-redesign-spec.md),后端负责生成与存储。

/// <summary>改材料归属:RoundId=null → 条目级(沟通记录)。</summary>
public sealed record ReassignAssetCommand(Guid UserId, Guid EntryId, Guid AssetId, Guid? RoundId) : IRequest<Result>;

/// <summary>删除材料(2026-10-20 v2 补齐)。</summary>
public sealed record DeleteAssetCommand(Guid UserId, Guid EntryId, Guid AssetId) : IRequest<Result>;

public sealed class DeleteAssetCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteAssetCommand, Result>
{
    public async Task<Result> Handle(DeleteAssetCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.RemoveAsset(request.AssetId); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Asset.Delete", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ReassignAssetCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ReassignAssetCommand, Result>
{
    public async Task<Result> Handle(ReassignAssetCommand request, CancellationToken ct)
    {
        var e = await LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.ReassignAsset(request.AssetId, request.RoundId); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Asset.Reassign", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    internal static async Task<InterviewEntry?> LoadFull(InterviewsDbContext db, Guid id, Guid userId, CancellationToken ct)
        => await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
}

/// <summary>更新材料元数据 JSON(解析字段/AI 摘要/提取安排/处理状态)。</summary>
public sealed record UpdateAssetMetaCommand(Guid UserId, Guid EntryId, Guid AssetId, string? MetaJson) : IRequest<Result>;

public sealed class UpdateAssetMetaCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateAssetMetaCommand, Result>
{
    public async Task<Result> Handle(UpdateAssetMetaCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.UpdateAssetMeta(request.AssetId, request.MetaJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Asset.Meta", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>写入逐题复盘 JSON(含标准答案用户编辑版)。</summary>
public sealed record SetQuestionReviewCommand(Guid UserId, Guid EntryId, Guid QuestionId, string? ReviewJson) : IRequest<Result>;

public sealed class SetQuestionReviewCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SetQuestionReviewCommand, Result>
{
    public async Task<Result> Handle(SetQuestionReviewCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.SetQuestionReview(request.QuestionId, request.ReviewJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Question.Review", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>写入整轮复盘汇总 JSON。</summary>
public sealed record SetRoundReviewCommand(Guid UserId, Guid EntryId, Guid RoundId, string? ReviewJson) : IRequest<Result>;

public sealed class SetRoundReviewCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SetRoundReviewCommand, Result>
{
    public async Task<Result> Handle(SetRoundReviewCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.SetRoundReview(request.RoundId, request.ReviewJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Round.Review", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>写入结构化备考报告 JSON(前端编辑 Tech Stack 关联后回存也走这里)。</summary>
public sealed record SetRoundPrepReportCommand(Guid UserId, Guid EntryId, Guid RoundId, string? PrepReportJson) : IRequest<Result>;

public sealed class SetRoundPrepReportCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SetRoundPrepReportCommand, Result>
{
    public async Task<Result> Handle(SetRoundPrepReportCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.SetRoundPrepReport(request.RoundId, request.PrepReportJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Round.PrepReport", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>说话人批量改名(本轮分段 + 题目 AskedBy 全篇生效)。返回受影响处数。</summary>
public sealed record RenameSpeakerCommand(Guid UserId, Guid EntryId, Guid RoundId, string From, string To)
    : IRequest<Result<int>>;

public sealed class RenameSpeakerCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RenameSpeakerCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RenameSpeakerCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<int>(Error.NotFound("面试条目"));
        try
        {
            var n = e.RenameSpeakerInRound(request.RoundId, request.From, request.To);
            await db.SaveChangesAsync(ct);
            return Result.Success(n);
        }
        catch (InvalidOperationException ex) { return Result.Failure<int>(Error.Conflict("Speaker.Rename", ex.Message)); }
    }
}

/// <summary>生成整轮复盘:逐题四维分析 + 整轮汇总(含通过概率,AI 估计)。</summary>
public sealed record GenerateRoundReviewCommand(Guid UserId, Guid EntryId, Guid RoundId) : IRequest<Result<string>>;

public sealed class GenerateRoundReviewCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GenerateRoundReviewCommand, Result<string>>
{
    public async Task<Result<string>> Handle(GenerateRoundReviewCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<string>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<string>(Error.NotFound("轮次"));

        var questions = e.Questions.Where(q => q.RoundId == round.Id).OrderBy(q => q.Sequence).ToList();
        if (questions.Count == 0)
            return Result.Failure<string>(Error.Validation("Review.NoQuestions",
                "本轮还没有题目,请先从转写抽取问答或手工添加题目。"));

        var hasAudio = e.Assets.Any(a => a.RoundId == round.Id && a.Kind == AssetKind.Audio);
        var transcript = RoundTranscript(e, round);

        var sb = new StringBuilder();
        sb.AppendLine($"公司:{e.CompanyName} 岗位:{e.Role} 轮次:Round {round.Order}({round.Stage})");
        sb.AppendLine($"面试官:{round.Interviewers}");
        sb.AppendLine(hasAudio
            ? "本轮有录音:发音维度可评分(依据原声)。"
            : "★本轮无录音(转写来自粘贴文本):发音维度必须输出 null,禁止猜测发音。");
        if (!string.IsNullOrWhiteSpace(transcript))
            sb.AppendLine($"## 转写原文\n{Trunc(transcript, 12000)}");
        sb.AppendLine("## 逐题(序号|提问人|问题|我的回答)");
        foreach (var q in questions)
            sb.AppendLine($"{q.Sequence}|{q.AskedBy ?? "面试官"}|{q.QuestionText}|{q.MyAnswerText ?? "(无记录)"}");

        const string system =
            "你是北美科技公司面试复盘教练。只输出一个 JSON 对象,不要任何额外文字,不要 Markdown 代码块。\n" +
            "硬规矩:每个维度的 comment 里每句判断都必须给出依据——引用候选人原话片段、指出时间点、或写明『全段未出现 X』;没有依据的话不要写。\n" +
            "输出结构:{\"round\":{\"overall\":0-100整数,\"passProbability\":0-100整数(AI估计),\"passBasis\":\"依据\",\"strongest\":\"\",\"weakest\":\"\",\"summary\":\"\"}," +
            "\"questions\":[{\"sequence\":题序,\"overall\":0-10一位小数,\"verdict\":\"一句话总评\"," +
            "\"dims\":{\"relevance\":{\"score\":0-10,\"comment\":\"\",\"fix\":\"\"},\"depth\":{同},\"expression\":{同}," +
            "\"pronunciation\":{\"score\":0-10或null,\"comment\":\"\",\"fix\":\"\"}}," +
            "\"stdSpoken\":\"北美口语版标准回答(第一人称,可直接朗读)\",\"stdTechnical\":\"技术版标准回答(要点式)\"}]}\n" +
            "无录音时 pronunciation.score 必须为 null,comment 说明『无录音,不评分』。";

        AiCompletionResult completion;
        try
        {
            completion = await ai.CompleteAsync("round-review", sb.ToString(), system, 0.4, 6000, ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            return Result.Failure<string>(Error.Validation("Review.AiNotConfigured",
                "AI 未配置:请先在设置里配置模型凭据。"));
        }
        catch (AiGatewayException ex)
        {
            return Result.Failure<string>(Error.Validation("Review.AiFailed", ex.Message));
        }

        var json = ExtractJson(completion.Text);
        if (json is null)
            return Result.Failure<string>(Error.Validation("Review.BadAiJson", "AI 返回内容无法解析为 JSON,请重试。"));

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("round", out var roundEl))
                e.SetRoundReview(round.Id, roundEl.GetRawText());
            if (root.TryGetProperty("questions", out var qs))
            {
                foreach (var qEl in qs.EnumerateArray())
                {
                    if (!qEl.TryGetProperty("sequence", out var seqEl)) continue;
                    var q = questions.FirstOrDefault(x => x.Sequence == seqEl.GetInt32());
                    if (q is null) continue;
                    var review = new Dictionary<string, object?>();
                    foreach (var p in qEl.EnumerateObject())
                        if (p.Name != "sequence") review[p.Name] = p.Value.Clone();
                    e.SetQuestionReview(q.Id, JsonSerializer.Serialize(review));
                    // 兼容旧字段:RecommendedAnswer 同步技术版,Assessment 同步总评
                    var stdTech = qEl.TryGetProperty("stdTechnical", out var st) ? st.GetString() : null;
                    var verdict = qEl.TryGetProperty("verdict", out var vd) ? vd.GetString() : null;
                    q.UpdateFromAnalysis(q.QuestionText, q.MyAnswerText, verdict ?? q.Assessment,
                        q.Category, q.Difficulty, q.GotStuck, q.StuckReason,
                        stdTech ?? q.RecommendedAnswer, q.AskedAtSeconds);
                }
            }
        }
        catch (JsonException)
        {
            return Result.Failure<string>(Error.Validation("Review.BadAiJson", "AI 返回 JSON 结构不完整,请重试。"));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(round.ReviewJson ?? "{}");
    }

    internal static string RoundTranscript(InterviewEntry e, InterviewRound round)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(round.Transcript)) parts.Add(round.Transcript!);
        foreach (var a in e.Assets.Where(a => a.RoundId == round.Id
                     && (a.Kind is AssetKind.Transcript or AssetKind.Text or AssetKind.Notes)
                     && !string.IsNullOrWhiteSpace(a.TranscriptText)).OrderBy(a => a.UploadedAt))
            parts.Add(a.TranscriptText!);
        return string.Join("\n\n", parts);
    }

    internal static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>从本轮转写抽取问答对 → 生成本轮题目(提问人逐人标注)。</summary>
public sealed record ExtractRoundQuestionsCommand(Guid UserId, Guid EntryId, Guid RoundId) : IRequest<Result<int>>;

public sealed class ExtractRoundQuestionsCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<ExtractRoundQuestionsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ExtractRoundQuestionsCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<int>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<int>(Error.NotFound("轮次"));

        var transcript = GenerateRoundReviewCommandHandler.RoundTranscript(e, round);
        if (string.IsNullOrWhiteSpace(transcript))
            return Result.Failure<int>(Error.Validation("Extract.NoTranscript", "本轮还没有转写材料可抽取。"));

        // 已有本轮题目时不重复抽取(幂等保护)
        if (e.Questions.Any(q => q.RoundId == round.Id))
            return Result.Success(0);

        List<(string Question, string? Answer, string? AskedBy, string Category)> extracted;
        try
        {
            var prompt =
                $"下面是 {e.CompanyName} {e.Role} Round {round.Order}({round.Stage})的面试转写,面试官:{round.Interviewers}。\n" +
                "抽取全部问答对,只输出 JSON 数组:[{\"question\":\"面试官原话\",\"answer\":\"候选人回答原话\",\"askedBy\":\"提问人真名,分不清写 面试官 1/面试官 2\",\"category\":\"Technical|SystemDesign|Behavioral|Coding|Culture|Other\"}]\n" +
                "闲聊寒暄不要抽。转写:\n" + (transcript.Length > 12000 ? transcript[..12000] : transcript);
            var completion = await ai.CompleteAsync("round-qa-extract", prompt,
                "只输出 JSON 数组,不要其它文字。", 0.2, 6000, ct);
            extracted = ParseExtracted(completion.Text);
        }
        catch (AiGatewayException)
        {
            // AI 不可用时用本地分段规则兜底(按说话人轮次配对),保证功能可用
            extracted = FallbackExtract(transcript, round.Interviewers);
        }

        var created = 0;
        foreach (var (question, answer, askedBy, category) in extracted)
        {
            if (string.IsNullOrWhiteSpace(question)) continue;
            var cat = Enum.TryParse<QuestionCategory>(category, true, out var c) ? c : QuestionCategory.Technical;
            e.AddQuestion(question.Trim(), answer, cat, 3, round.Id, askedBy);
            created++;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(created);
    }

    private static List<(string, string?, string?, string)> ParseExtracted(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        var list = new List<(string, string?, string?, string)>();
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var q = el.TryGetProperty("question", out var qe) ? qe.GetString() : null;
                if (string.IsNullOrWhiteSpace(q)) continue;
                list.Add((q!,
                    el.TryGetProperty("answer", out var ae) ? ae.GetString() : null,
                    el.TryGetProperty("askedBy", out var be) ? be.GetString() : null,
                    el.TryGetProperty("category", out var ce) ? ce.GetString() ?? "Technical" : "Technical"));
            }
        }
        catch (JsonException) { /* 坏 JSON → 返回空,调用方可见 0 条 */ }
        return list;
    }

    /// <summary>无 AI 时的规则抽取:识别 "Name: 内容" 轮次行,面试官的问句 + 紧随的候选人回答配对。</summary>
    internal static List<(string, string?, string?, string)> FallbackExtract(string transcript, string? interviewers)
    {
        var turns = new List<(string Speaker, string Text)>();
        foreach (var rawLine in transcript.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^([^:：]{1,40})[:：]\s*(.+)$");
            if (m.Success) turns.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
        }
        var interviewerNames = (interviewers ?? "")
            .Split(new[] { ',', '，', ';', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"\(.*?\)", "").Trim())
            .Where(s => s.Length > 0).ToList();
        bool IsCandidate(string speaker) =>
            speaker.Contains("Forrest") || speaker.Equals("You", StringComparison.OrdinalIgnoreCase)
            || speaker.Contains("我") || speaker.Contains("Candidate", StringComparison.OrdinalIgnoreCase);
        string? MatchInterviewer(string speaker)
        {
            foreach (var n in interviewerNames)
                if (speaker.Contains(n) || n.Contains(speaker)) return n;
            return null;
        }

        var result = new List<(string, string?, string?, string)>();
        for (var i = 0; i < turns.Count; i++)
        {
            var (speaker, text) = turns[i];
            if (IsCandidate(speaker)) continue;
            var askedBy = MatchInterviewer(speaker);
            if (askedBy is null && interviewerNames.Count > 0 && !speaker.Contains("面试官")) continue;
            if (!text.Contains('?') && !text.Contains('？')) continue;
            string? answer = null;
            if (i + 1 < turns.Count && IsCandidate(turns[i + 1].Speaker)) answer = turns[i + 1].Text;
            result.Add((text, answer, askedBy ?? speaker, "Technical"));
        }
        return result;
    }
}

/// <summary>生成结构化备考报告(AI 优先;AI 未配置时本地聚合兜底并标注 generatedBy)。
/// 2026-10-21:生成语言跟随账号语言(Language 参数);数据源按 AiGateway 的用户级
/// 「备考数据源」配置接入 —— 题库/公司背景开关决定内部输入,网络面经/Glassdoor
/// 经网关检索后才进提示词,状态如实写进报告 sources.details,绝不编造面经。</summary>
public sealed record GeneratePrepReportCommand(
    Guid UserId, Guid EntryId, Guid RoundId, string? JdText = null, string? ResumeText = null,
    string? Experiences = null, string? Language = null) : IRequest<Result<string>>;

/// <summary>一次生成实际用到的数据源上下文(配置开关 + 外部检索结果)。</summary>
internal sealed record PrepSourceContext(
    bool QuestionBankEnabled, bool CompanyBackgroundEnabled,
    PrepSourceSearchDto? Web, PrepSourceSearchDto? Glassdoor);

public sealed class GeneratePrepReportCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GeneratePrepReportCommand, Result<string>>
{
    public async Task<Result<string>> Handle(GeneratePrepReportCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<string>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<string>(Error.NotFound("轮次"));

        var lang = NormalizeLanguage(request.Language);
        var ctx = await LoadSourceContext(e, round, ct);
        var sources = BuildSources(e, request, ctx);

        string reportJson;
        try
        {
            var prompt = BuildAiPrompt(e, round, request, ctx, lang);
            var completion = await ai.CompleteAsync("round-prep-report", prompt, SystemPromptFor(lang), 0.5, 8000, ct);
            var json = GenerateRoundReviewCommandHandler.ExtractJson(completion.Text);
            if (json is null) throw new AiGatewayException("AI 返回内容无法解析为 JSON");
            using var doc = JsonDocument.Parse(json); // 校验合法性
            var root = JsonNode.Parse(json)!.AsObject();
            root["generatedAt"] = DateTimeOffset.UtcNow.ToString("o");
            root["generatedBy"] = completion.Model;
            root["sources"] = sources;
            reportJson = root.ToJsonString();
        }
        catch (AiGatewayException)
        {
            // AI 未配置/失败:本地聚合兜底(JD/简历/历史原题/短板都是真实输入,只是不经 LLM 组织)
            reportJson = BuildLocalReport(e, round, request, ctx, lang, sources);
        }
        catch (JsonException)
        {
            reportJson = BuildLocalReport(e, round, request, ctx, lang, sources);
        }

        e.SetRoundPrepReport(round.Id, reportJson);
        await db.SaveChangesAsync(ct);
        return Result.Success(reportJson);
    }

    // ---------------- 语言 ----------------

    /// <summary>生成语言归一化:只认 zh/en/fr,其余(含空)按 en —— 与前端三语口径一致。</summary>
    internal static string NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch { "zh" => "zh", "fr" => "fr", _ => "en" };

    private static string LanguageName(string lang) => lang switch
    {
        "zh" => "Simplified Chinese (简体中文)",
        "fr" => "French (Français)",
        _ => "English"
    };

    private static string SystemPromptFor(string lang) =>
        "You are an interview coach for North American tech companies. Output exactly one JSON object, no other text.\n" +
        $"OUTPUT LANGUAGE: every natural-language value (titles, text, items, questions, standard answers) MUST be written in {LanguageName(lang)}. JSON keys and section ids stay exactly as specified below.\n" +
        "Structure: {\"sections\":[{\"id\":\"greeting|intro-listen|self-intro|deep-dive|tech-qa|behavioral|ask-back|closing\",\"title\":\"section title\",\"blocks\":[{\"type\":\"script|points|qa-ref|list\",\"title\":\"optional\",\"text\":\"body for script/points\",\"items\":[\"bullets for list\"]}],\"questions\":[{\"question\":\"...\",\"category\":\"...\",\"sourceTags\":[\"...\"],\"weaknessRelated\":true/false,\"stdSpoken\":\"spoken standard answer\",\"stdTechnical\":\"technical standard answer\"}]}]}\n" +
        "Output all 8 sections in real interview order: greeting, intro-listen, self-intro (60-second and 2-minute script versions in blocks), deep-dive, tech-qa (questions in questions), behavioral (STAR), ask-back, closing.\n" +
        "Technical questions may only be grounded in: JD keywords, the fetched interview experiences given in the prompt, the candidate's past questions, and identified weaknesses (weaknessRelated=true). Never invent interview experiences or attribute statements to a source that did not say them. Standard answers: first-person spoken version + bullet-style technical version, North American interview style.";

    // ---------------- 数据源上下文 ----------------

    /// <summary>
    /// 读 AiGateway 的备考数据源配置;已启用的外部源当场检索。
    /// 网关不可达时内部源按默认口径、外部源标 failed —— 不编造、不阻断生成。
    /// </summary>
    private async Task<PrepSourceContext> LoadSourceContext(InterviewEntry e, InterviewRound round, CancellationToken ct)
    {
        try
        {
            var s = await ai.GetPrepSourceSettingsAsync(ct);
            PrepSourceSearchDto? web = null, glassdoor = null;
            if (s.WebExperiencesEnabled) web = await SafeSearch("web", e, round, ct);
            if (s.GlassdoorEnabled) glassdoor = await SafeSearch("glassdoor", e, round, ct);
            return new PrepSourceContext(s.QuestionBankEnabled, s.CompanyBackgroundEnabled, web, glassdoor);
        }
        catch (AiGatewayException)
        {
            return new PrepSourceContext(true, true,
                new PrepSourceSearchDto("web", "failed", null, [], null),
                new PrepSourceSearchDto("glassdoor", "failed", null, [], null));
        }
    }

    private async Task<PrepSourceSearchDto> SafeSearch(string source, InterviewEntry e, InterviewRound round, CancellationToken ct)
    {
        try { return await ai.SearchPrepSourcesAsync(source, e.CompanyName, e.Role, round.Stage, ct); }
        catch (AiGatewayException) { return new PrepSourceSearchDto(source, "failed", null, [], null); }
    }

    /// <summary>
    /// 报告 sources:旧字段(jd/company/experiences/resume/pastQuestions/weaknesses)
    /// 保持兼容;details 逐项给真实状态(fetched/disabled/notConfigured/noData/failed)
    /// 与外部来源链接 —— 前端 Sources 行按它展示,不许猜。
    /// </summary>
    private static JsonObject BuildSources(InterviewEntry e, GeneratePrepReportCommand req, PrepSourceContext ctx)
    {
        var pastCount = ctx.QuestionBankEnabled ? e.Questions.Count : 0;
        var companyUsed = ctx.CompanyBackgroundEnabled && !string.IsNullOrWhiteSpace(e.CompanyProfile);
        var details = new JsonArray
        {
            Detail("questionBank",
                !ctx.QuestionBankEnabled ? "disabled" : pastCount > 0 ? "fetched" : "noData", pastCount),
            Detail("company",
                !ctx.CompanyBackgroundEnabled ? "disabled" : companyUsed ? "fetched" : "noData", null),
            Detail("web", ctx.Web?.Status ?? "disabled", ctx.Web?.Results.Count,
                ctx.Web?.Results.Select(r => r.Url)),
            Detail("glassdoor", ctx.Glassdoor?.Status ?? "disabled", ctx.Glassdoor?.Results.Count,
                ctx.Glassdoor?.Results.Select(r => r.Url)),
            Detail("manualExperiences",
                !string.IsNullOrWhiteSpace(req.Experiences) ? "fetched" : "noData", null)
        };
        return new JsonObject
        {
            ["jd"] = !string.IsNullOrWhiteSpace(req.JdText) || !string.IsNullOrWhiteSpace(e.JdText) || !string.IsNullOrWhiteSpace(e.JdSummary),
            ["company"] = companyUsed,
            ["experiences"] = ctx.Web?.Status == "fetched" || ctx.Glassdoor?.Status == "fetched"
                || !string.IsNullOrWhiteSpace(req.Experiences),
            ["resume"] = !string.IsNullOrWhiteSpace(req.ResumeText),
            ["pastQuestions"] = pastCount,
            ["weaknesses"] = e.Weaknesses.Count,
            ["details"] = details
        };
    }

    private static JsonObject Detail(string key, string status, int? count, IEnumerable<string>? links = null)
    {
        var o = new JsonObject { ["key"] = key, ["status"] = status };
        if (count is not null) o["count"] = count.Value;
        if (links is not null)
        {
            var arr = new JsonArray();
            foreach (var l in links) arr.Add(l);
            o["links"] = arr;
        }
        return o;
    }

    // ---------------- 提示词 ----------------

    private static string BuildAiPrompt(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand req, PrepSourceContext ctx, string lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Prep target: {e.CompanyName} — {e.Role} (Round {round.Order} / {round.Stage})");
        sb.AppendLine($"Output language: {LanguageName(lang)}");
        if (!string.IsNullOrWhiteSpace(round.Interviewers)) sb.AppendLine($"Interviewers: {round.Interviewers}");
        if (!string.IsNullOrWhiteSpace(round.Format)) sb.AppendLine($"Format: {round.Format}");
        var jd = !string.IsNullOrWhiteSpace(req.JdText) ? req.JdText
            : !string.IsNullOrWhiteSpace(e.JdText) ? e.JdText : e.JdSummary;
        if (!string.IsNullOrWhiteSpace(jd)) sb.AppendLine($"## JD\n{Trunc(jd!, 6000)}");
        if (ctx.CompanyBackgroundEnabled && !string.IsNullOrWhiteSpace(e.CompanyProfile))
            sb.AppendLine($"## Company background\n{Trunc(e.CompanyProfile, 3000)}");
        if (!string.IsNullOrWhiteSpace(req.ResumeText)) sb.AppendLine($"## Submitted resume\n{Trunc(req.ResumeText, 6000)}");
        if (!string.IsNullOrWhiteSpace(req.Experiences)) sb.AppendLine($"## Interview experiences pasted by the user\n{Trunc(req.Experiences, 5000)}");
        AppendFetched(sb, "## Fetched web interview experiences (search results; ground questions only in these)", ctx.Web);
        AppendFetched(sb, "## Fetched Glassdoor interview experiences (search results; ground questions only in these)", ctx.Glassdoor);
        if (ctx.QuestionBankEnabled)
        {
            var past = e.Questions.Take(30).ToList();
            if (past.Count > 0)
            {
                sb.AppendLine("## Past questions from similar roles (question bank)");
                foreach (var q in past) sb.AppendLine($"- {Trunc(q.QuestionText, 250)}");
            }
        }
        if (e.Weaknesses.Count > 0)
        {
            sb.AppendLine("## Identified technical weaknesses");
            foreach (var w in e.Weaknesses.Take(15)) sb.AppendLine($"- {w.Title}:{Trunc(w.Suggestion ?? w.Detail ?? "", 150)}");
        }
        return sb.ToString();
    }

    private static void AppendFetched(StringBuilder sb, string heading, PrepSourceSearchDto? search)
    {
        if (search is null || search.Status != "fetched" || search.Results.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var r in search.Results.Take(5))
            sb.AppendLine($"- {Trunc(r.Title, 200)} ({r.Url})\n  {Trunc(r.Snippet, 500)}");
    }

    // ---------------- 本地兜底(三语,跟随生成语言) ----------------

    /// <summary>本地聚合报告:结构与 AI 版一致;题库关 → 不带原题,公司背景关 → sources 如实标 disabled。</summary>
    internal static string BuildLocalReport(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand req, PrepSourceContext ctx, string lang, JsonObject sources)
    {
        var t = LocalTexts.For(lang);
        var questions = new List<object>();
        if (ctx.QuestionBankEnabled)
        {
            var weaknessTitles = e.Weaknesses.Select(w => w.Title).ToList();
            foreach (var q in e.Questions.OrderByDescending(q => q.GotStuck).Take(20))
            {
                var weak = q.GotStuck || weaknessTitles.Any(x =>
                    q.QuestionText.Contains(x, StringComparison.OrdinalIgnoreCase));
                questions.Add(new
                {
                    question = q.QuestionText,
                    category = q.Category.ToString(),
                    sourceTags = new[] { t.PastTag },
                    weaknessRelated = weak,
                    stdSpoken = (string?)null,
                    stdTechnical = q.RecommendedAnswer,
                    knowledgeId = (Guid?)null
                });
            }
        }
        var fetchedItems = new List<string>();
        foreach (var s in new[] { ctx.Web, ctx.Glassdoor })
        {
            if (s is null || s.Status != "fetched") continue;
            foreach (var r in s.Results.Take(5)) fetchedItems.Add($"{r.Title} — {r.Url}");
        }
        var techBlocks = new List<object>();
        if (fetchedItems.Count > 0)
            techBlocks.Add(new { type = "list", title = t.FetchedTitle, items = fetchedItems.ToArray() });

        var sections = new object[]
        {
            new { id = "greeting", title = t.Titles[0], blocks = new object[] {
                new { type = "points", items = t.Greeting } }, questions = Array.Empty<object>() },
            new { id = "intro-listen", title = t.Titles[1], blocks = new object[] {
                new { type = "points", items = t.IntroListen } }, questions = Array.Empty<object>() },
            new { id = "self-intro", title = t.Titles[2], blocks = new object[] {
                new { type = "script", title = t.SelfIntroTitle, text = (string?)null } }, questions = Array.Empty<object>() },
            new { id = "deep-dive", title = t.Titles[3], blocks = new object[] {
                new { type = "points", items = t.DeepDive } }, questions = Array.Empty<object>() },
            new { id = "tech-qa", title = t.Titles[4], blocks = techBlocks.ToArray(), questions = questions.ToArray() },
            new { id = "behavioral", title = t.Titles[5], blocks = new object[] {
                new { type = "points", items = t.Behavioral } }, questions = Array.Empty<object>() },
            new { id = "ask-back", title = t.Titles[6], blocks = new object[] {
                new { type = "list", items = t.AskBack } }, questions = Array.Empty<object>() },
            new { id = "closing", title = t.Titles[7], blocks = new object[] {
                new { type = "points", items = t.Closing } }, questions = Array.Empty<object>() },
        };
        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow.ToString("o"),
            generatedBy = "local-aggregation",
            sources,
            sections
        };
        return JsonSerializer.Serialize(report);
    }

    /// <summary>本地兜底文案三套(zh/en/fr)—— 与系统三语口径一致,禁止只写中文。</summary>
    private sealed record LocalTexts(
        string[] Titles, string[] Greeting, string[] IntroListen, string SelfIntroTitle,
        string[] DeepDive, string[] Behavioral, string[] AskBack, string[] Closing,
        string PastTag, string FetchedTitle)
    {
        public static LocalTexts For(string lang) => lang switch
        {
            "zh" => new LocalTexts(
                new[] { "1 · 开场寒暄", "2 · 听对方介绍", "3 · 自我介绍", "4 · 项目深挖", "5 · 技术问答", "6 · 行为题 STAR", "7 · 反问环节", "8 · 收尾与注意事项" },
                new[] { "提前 5 分钟进入会议", "开场一句:Hi, thanks for taking the time to speak with me today." },
                new[] { "记下团队规模、在招岗位的职责边界、当前项目关键词", "回应一句:That sounds great — the part about X is exactly what I've been doing." },
                "60 秒版(请按你的简历改写后朗读练习)",
                new[] { "准备 1 个核心项目的背景-难点-方案-结果四段式讲法", "数字先行:规模、性能提升、影响范围" },
                new[] { "每个故事按 Situation-Task-Action-Result 讲,Result 带数字", "准备:冲突、失败、赶工、学新技术 各一个" },
                new[] { "这个岗位前 90 天最重要的交付是什么?", "团队现在最大的技术挑战是什么?", "这个 role 的成功怎么衡量?" },
                new[] { "结束前重申兴趣:Based on what I've heard, this role is exactly what I'm looking for.", "当天:提前测试设备与网络,水杯与简历放手边" },
                "原题", "抓取到的面经来源"),
            "fr" => new LocalTexts(
                new[] { "1 · Accueil", "2 · Écouter leur présentation", "3 · Présentation", "4 · Projets en détail", "5 · Questions techniques", "6 · Comportement (STAR)", "7 · Vos questions", "8 · Conclusion et rappels" },
                new[] { "Rejoignez la réunion 5 minutes à l'avance", "Ouverture : Hi, thanks for taking the time to speak with me today." },
                new[] { "Notez la taille de l'équipe, les responsabilités du poste et les mots-clés du projet actuel", "Répondez : That sounds great — the part about X is exactly what I've been doing." },
                "Version 60 secondes (à adapter à votre CV et à pratiquer à voix haute)",
                new[] { "Préparez un projet clé en quatre temps : contexte → défi → approche → résultats", "Les chiffres d'abord : échelle, gains de performance, portée" },
                new[] { "Chaque histoire suit Situation-Tâche-Action-Résultat, avec des chiffres dans le résultat", "Préparez : un conflit, un échec, une urgence, un nouvel apprentissage" },
                new[] { "Quelles sont les livraisons les plus importantes des 90 premiers jours ?", "Quel est le plus grand défi technique de l'équipe en ce moment ?", "Comment mesure-t-on la réussite dans ce poste ?" },
                new[] { "Réaffirmez votre intérêt : Based on what I've heard, this role is exactly what I'm looking for.", "Le jour même : testez l'équipement et le réseau à l'avance, eau et CV à portée de main" },
                "Question passée", "Sources de retours récupérées"),
            _ => new LocalTexts(
                new[] { "1 · Warm-up", "2 · Hear their introduction", "3 · Self-introduction", "4 · Project deep-dive", "5 · Technical Q&A", "6 · Behavioral (STAR)", "7 · Questions to ask", "8 · Wrap-up & reminders" },
                new[] { "Join the meeting 5 minutes early", "Open with: Hi, thanks for taking the time to speak with me today." },
                new[] { "Note the team size, the role's responsibilities, and current project keywords", "Respond with: That sounds great — the part about X is exactly what I've been doing." },
                "60-second version (adapt it to your resume and practise aloud)",
                new[] { "Prepare one core project as background → challenge → approach → results", "Lead with numbers: scale, performance gains, impact" },
                new[] { "Tell every story as Situation-Task-Action-Result, with numbers in the Result", "Prepare one story each: conflict, failure, deadline crunch, learning something new" },
                new[] { "What are the most important deliverables in the first 90 days?", "What is the team's biggest technical challenge right now?", "How is success measured for this role?" },
                new[] { "Restate your interest: Based on what I've heard, this role is exactly what I'm looking for.", "On the day: test your equipment and network early; water and resume within reach" },
                "Past question", "Fetched experience sources")
        };
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
