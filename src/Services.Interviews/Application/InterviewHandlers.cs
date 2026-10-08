using System.Text;
using System.Text.Json;
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
    string? IntegrityStatus = null, DateTimeOffset? LastVerifiedAt = null);

public sealed record QuestionDto(
    Guid Id, int Sequence, string QuestionText, string? MyAnswerText, string Category,
    int Difficulty, string? Assessment, bool GotStuck, string? StuckReason,
    string? RecommendedAnswer, double? AskedAtSeconds, string? WeaknessTagsJson,
    string? FollowUpQuestionsJson, string? MissedPointsJson);

public sealed record WeaknessDto(
    Guid Id, string Category, string Title, string? Detail, string? Evidence,
    int Severity, string? Suggestion, int OccurrenceCount, string SourceType,
    DateTimeOffset CreatedAt);

public sealed record RoundDto(
    Guid Id, int Order, string Stage, DateOnly? ScheduledDate, string? Interviewers,
    string? Format, string? Location, string Outcome, string? Notes, string? Feedback);

/// <summary>全局 upcoming:跨所有条目的未来轮次,带公司/职位上下文,供日历视图用。</summary>
public sealed record UpcomingRoundDto(
    Guid RoundId, Guid EntryId, string CompanyName, string Role,
    int Order, string Stage, DateOnly? ScheduledDate, string? ScheduledTime,
    string? Interviewers, string? Format, string? Location,
    string? MeetingLink, string Outcome);

public sealed record GetUpcomingRoundsQuery(int Days = 30) : IRequest<Result<List<UpcomingRoundDto>>>;

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
    SpeechMetricsDto? SpeechMetrics,
    int AssetCount, int QuestionCount, int WeaknessCount, int RoundCount,
    List<AssetDto> Assets, List<QuestionDto> Questions, List<WeaknessDto> Weaknesses,
    List<RoundDto> Rounds,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record InterviewEntryListItemDto(
    Guid Id, Guid CompanyId, string CompanyName, string Role, int RoundNo,
    DateOnly? InterviewDate, string? InterviewFormat, string Status, string? Result,
    int? OverallScore, int? PronunciationScore, int? FluencyScore, int? StructureScore,
    int? TechnicalDepthScore, int? RelevanceScore,
    int AssetCount, int QuestionCount, int WeaknessCount, bool HasTranscript,
    DateTimeOffset CreatedAt, Guid? JobApplicationId = null);

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

// ============================ 查询 ============================

public sealed record ListEntriesQuery(
    int Page = 1, int PageSize = 20, Guid? CompanyId = null, string? Status = null,
    string? Search = null) : IRequest<Result<PagedResult<InterviewEntryListItemDto>>>;

public sealed record GetEntryQuery(Guid Id) : IRequest<Result<InterviewEntryDto>>;

public sealed record GetCompanySummariesQuery : IRequest<Result<IReadOnlyList<CompanySummaryDto>>>;

public sealed record GetInterviewStatsQuery : IRequest<Result<InterviewStatsDto>>;

public sealed record ListWeaknessesQuery(Guid EntryId, string? Category = null)
    : IRequest<Result<IReadOnlyList<WeaknessDto>>>;

/// <summary>任务台账:该条目的分析任务历史(含投递尝试与失败原因)。</summary>
public sealed record ListAnalysisJobsQuery(Guid EntryId)
    : IRequest<Result<IReadOnlyList<AnalysisJobDto>>>;

/// <summary>任务台账行 DTO —— 给前端"流水线记录"区直出。</summary>
public sealed record AnalysisJobDto(
    Guid Id, string JobType, string Status, int Attempts, int MaxAttempts,
    string? FailureReason, string? LastError,
    DateTimeOffset CreatedAt, DateTimeOffset? DispatchedAt, DateTimeOffset? CompletedAt);

// ============================ 命令 ============================

public sealed record CreateEntryCommand(
    Guid? CompanyId, string CompanyName, string Role, Guid? JobApplicationId = null,
    string? CompanyProfile = null, string? JdText = null, string? JdSummary = null,
    string? InterviewFormat = null, string? Interviewers = null, DateOnly? InterviewDate = null,
    string? Location = null, string? Notes = null)
    : IRequest<Result<Guid>>;

public sealed record UpdateEntryCommand(
    Guid Id, string CompanyName, string Role, string? CompanyProfile, string? JdText,
    string? JdSummary, int RoundNo, DateOnly? InterviewDate, string? InterviewFormat,
    string? Interviewers, string? Location, string? Result, string? Notes) : IRequest<Result>;

public sealed record DeleteEntryCommand(Guid Id) : IRequest<Result>;

public sealed record AttachAssetCommand(
    Guid EntryId, string Kind, string FileName, string? ContentType, long SizeBytes,
    string? StoragePath, string? BlobUrl, string? TranscriptText, string? TranscriptSegmentsJson,
    double? DurationSeconds, string SourceLanguage = "en", string? Sha256 = null) : IRequest<Result<Guid>>;

/// <summary>
/// 真正的录音文件上传(multipart):字节流经 IInterviewAudioStore 原子落盘,
/// 数据库只登记相对路径 + SHA-256 + 实际大小。之前 /assets 只收 JSON 元信息,
/// 文件本体从来没存过 —— Worker 永远找不到文件,这是 M1 修复的第一断点。
/// </summary>
public sealed record UploadAssetFileCommand(
    Guid EntryId, string FileName, string? ContentType, double? DurationSeconds,
    string? SourceLanguage, Stream Content) : IRequest<Result<Guid>>;

/// <summary>回放:取某条材料的音频流(前端 &lt;audio&gt; 直接指向这里)。</summary>
public sealed record GetAssetAudioQuery(Guid EntryId, Guid AssetId)
    : IRequest<Result<(Stream Stream, string ContentType, string FileName)>>;

public sealed record SaveTranscriptCommand(Guid EntryId, Guid AssetId, string FullText,
    string? SegmentsJson) : IRequest<Result>;

/// <summary>开始转写(由分析 Worker 调用,把条目从 AssetsUploaded 推进到 Transcribing)。</summary>
public sealed record BeginTranscriptionCommand(Guid EntryId) : IRequest<Result>;

public sealed class BeginTranscriptionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<BeginTranscriptionCommand, Result>
{
    public async Task<Result> Handle(BeginTranscriptionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.StartTranscription();   // 幂等:已在 Transcribed/Analyzed 直接返回
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Transcription.InvalidState", ex.Message));
        }
    }
}

public sealed record RequestAnalysisCommand(Guid EntryId) : IRequest<Result>;

public sealed record ApplyAnalysisCommand(
    Guid EntryId, int Overall, int Pronunciation, int Fluency, int Structure,
    int TechnicalDepth, int Relevance, string? Summary,
    List<QuestionDraft>? Questions, List<WeaknessDraft>? Weaknesses,
    string? SpeechMetricsJson = null) : IRequest<Result>;

public sealed record MarkEntryFailedCommand(Guid EntryId, string Reason) : IRequest<Result>;

public sealed record AddQuestionCommand(
    Guid EntryId, string QuestionText, string? MyAnswerText, string Category, int Difficulty,
    string? Assessment = null) : IRequest<Result<Guid>>;

public sealed record UpdateQuestionCommand(
    Guid EntryId, Guid QuestionId, string QuestionText, string? MyAnswerText, string Category,
    int Difficulty, string? Assessment, bool GotStuck, string? StuckReason,
    string? RecommendedAnswer) : IRequest<Result>;

public sealed record RemoveQuestionCommand(Guid EntryId, Guid QuestionId) : IRequest<Result>;

// ============================ 轮次(缺口1) ============================

public sealed record AddRoundCommand(
    Guid EntryId, string Stage = "Technical") : IRequest<Result<Guid>>;

public sealed record UpdateRoundCommand(
    Guid EntryId, Guid RoundId, string Stage, DateOnly? ScheduledDate, string? Interviewers,
    string? Format, string? Location, string Outcome, string? Notes,
    string? Feedback, string? MeetingLink = null, string? ScheduledTime = null,
    string? PrepQuestionsJson = null, string? EmailsJson = null,
    string? Transcript = null, string? RecordingUrl = null) : IRequest<Result>;

public sealed record RemoveRoundCommand(Guid EntryId, Guid RoundId) : IRequest<Result>;

public sealed record AddWeaknessCommand(
    Guid EntryId, string Category, string Title, string? Detail, string? Evidence,
    int Severity, string? Suggestion) : IRequest<Result<Guid>>;

public sealed record RemoveWeaknessCommand(Guid EntryId, Guid WeaknessId) : IRequest<Result>;

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
    private static readonly string[] AllowedKinds = ["Audio", "Transcript", "Notes"];

    public AttachAssetCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Kind).Must(k => AllowedKinds.Contains(k, StringComparer.OrdinalIgnoreCase))
            .WithMessage("材料类型只能是 Audio / Transcript / Notes");
        // 文本类材料必须有内容 —— 这条规则挡掉了"传了个空的 Transcript"
        RuleFor(x => x.TranscriptText)
            .NotEmpty()
            .When(x => !string.Equals(x.Kind, "Audio", StringComparison.OrdinalIgnoreCase))
            .WithMessage("文本类材料必须提供内容");
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

// ============================ 映射(扩展方法,和 Jobs 服务保持一致风格) ============================

public static class InterviewMappingExtensions
{
    public static AssetDto ToDto(this InterviewAsset a) => new(
        a.Id, a.Kind.ToString(), a.FileName, a.ContentType, a.SizeBytes, a.BlobUrl,
        a.DurationSeconds, a.SourceLanguage, a.HasTranscript,
        a.TranscriptText?.Length ?? 0, a.UploadedAt,
        a.StoragePath, a.Sha256, a.FileExists,
        a.IntegrityStatus?.ToString(), a.LastVerifiedAt);

    public static QuestionDto ToDto(this InterviewQuestion q) => new(
        q.Id, q.Sequence, q.QuestionText, q.MyAnswerText, q.Category.ToString(), q.Difficulty,
        q.Assessment, q.GotStuck, q.StuckReason, q.RecommendedAnswer, q.AskedAtSeconds,
        q.WeaknessTagsJson, q.FollowUpQuestionsJson, q.MissedPointsJson);

    public static WeaknessDto ToDto(this InterviewWeakness w) => new(
        w.Id, w.Category.ToString(), w.Title, w.Detail, w.Evidence, w.Severity, w.Suggestion,
        w.OccurrenceCount, w.SourceType.ToString(), w.CreatedAt);

    public static RoundDto ToDto(this InterviewRound r) => new(
        r.Id, r.Order, r.Stage, r.ScheduledDate, r.Interviewers, r.Format, r.Location,
        r.Outcome.ToString(), r.Notes, r.Feedback);

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
        ParseSpeechMetrics(e.SpeechMetricsJson),
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count, e.Rounds.Count,
        e.Assets.OrderBy(x => x.UploadedAt).Select(x => x.ToDto()).ToList(),
        e.Questions.OrderBy(x => x.Sequence).Select(x => x.ToDto()).ToList(),
        e.Weaknesses.OrderByDescending(x => x.Severity).Select(x => x.ToDto()).ToList(),
        e.Rounds.OrderBy(x => x.Order).Select(x => x.ToDto()).ToList(),
        e.CreatedAt, e.UpdatedAt);

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
        e.JobApplicationId);
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
            .Include(x => x.Assets)
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
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
        var e = await db.Entries.AsNoTracking()
            .Include(x => x.Assets)
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct);

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

public sealed class ListWeaknessesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListWeaknessesQuery, Result<IReadOnlyList<WeaknessDto>>>
{
    public async Task<Result<IReadOnlyList<WeaknessDto>>> Handle(
        ListWeaknessesQuery request, CancellationToken ct)
    {
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
        var entries = await db.Entries.AsNoTracking().ToListAsync(ct);
        var weaknesses = await db.Weaknesses.AsNoTracking().ToListAsync(ct);
        var questions = await db.Questions.AsNoTracking().ToListAsync(ct);

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
        // CompanyId 可空:Tracker 一键创建时只知道公司名,按名复用已有公司的 Id,
        // 找不到则生成新的 —— 避免 Guid.Empty 导致公司树聚合错乱。
        var companyId = request.CompanyId;
        if (companyId is null || companyId == Guid.Empty)
        {
            var existing = await db.Entries.AsNoTracking()
                .Where(x => x.CompanyName == request.CompanyName)
                .Select(x => x.CompanyId)
                .FirstOrDefaultAsync(ct);
            companyId = existing == Guid.Empty ? Guid.NewGuid() : existing;
        }

        var entry = new InterviewEntry(companyId.Value, request.CompanyName, request.Role,
            request.JobApplicationId);

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
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        e.UpdateBasicInfo(request.CompanyName, request.Role, request.CompanyProfile,
            request.JdText, request.JdSummary, request.RoundNo, request.InterviewDate,
            request.InterviewFormat, request.Interviewers, request.Location, request.Result,
            request.Notes);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteEntryCommand, Result>
{
    public async Task<Result> Handle(DeleteEntryCommand request, CancellationToken ct)
    {
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
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
        // 带子集合加载 —— 聚合方法要往集合里加东西
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<AssetKind>(request.Kind, true, out var kind))
            return Result.Failure<Guid>(Error.Validation("Asset.InvalidKind", "材料类型无效"));

        try
        {
            var asset = e.AttachAsset(kind, request.FileName, request.ContentType, request.SizeBytes,
                request.StoragePath, request.BlobUrl, request.TranscriptText,
                request.TranscriptSegmentsJson, request.DurationSeconds, request.SourceLanguage,
                request.Sha256);
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
            .Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        try
        {
            var asset = e.AttachAsset(AssetKind.Audio, request.FileName, request.ContentType,
                stored.SizeBytes, stored.RelativePath, null, null, null,
                request.DurationSeconds, request.SourceLanguage ?? "en", stored.Sha256);
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

public sealed class SaveTranscriptCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SaveTranscriptCommand, Result>
{
    public async Task<Result> Handle(SaveTranscriptCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.ApplyAnalysis(request.Overall, request.Pronunciation, request.Fluency,
                request.Structure, request.TechnicalDepth, request.Relevance, request.Summary,
                request.Questions, request.Weaknesses, request.SpeechMetricsJson);
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
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<QuestionCategory>(request.Category, true, out var cat))
            return Result.Failure<Guid>(Error.Validation("Question.InvalidCategory", "问题类型无效"));

        var q = e.AddQuestion(request.QuestionText, request.MyAnswerText, cat, request.Difficulty);
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
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        var r = e.AddRound(request.Stage);
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
    List<string> Factors);     // 影响因素说明

public sealed record GetRoundPassProbabilityQuery(Guid RoundId) : IRequest<Result<PassProbabilityDto>>;

public sealed class GetRoundPassProbabilityQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetRoundPassProbabilityQuery, Result<PassProbabilityDto>>
{
    public async Task<Result<PassProbabilityDto>> Handle(
        GetRoundPassProbabilityQuery request, CancellationToken ct)
    {
        // 找到本轮,取其 stage
        var round = await db.Entries.AsNoTracking()
            .SelectMany(e => e.Rounds, (e, r) => new { e, r })
            .Where(x => x.r.Id == request.RoundId)
            .Select(x => new { x.r.Stage, RoundId = x.r.Id })
            .FirstOrDefaultAsync(ct);
        if (round is null) return Result.Failure<PassProbabilityDto>(Error.NotFound("Round"));

        // 历史同 stage 轮次:只算有定论的 (Passed / Rejected)
        var history = await db.Entries.AsNoTracking()
            .SelectMany(e => e.Rounds)
            .Where(r => r.Stage == round.Stage
                && r.Id != round.RoundId
                && (r.Outcome == InterviewRoundOutcome.Passed
                    || r.Outcome == InterviewRoundOutcome.Rejected))
            .ToListAsync(ct);

        var factors = new List<string>();
        int baseProb;
        int historyCount = history.Count;
        if (historyCount == 0)
        {
            // 无历史数据:中性 50%,提示多攒数据
            baseProb = 50;
            factors.Add("暂无同类型轮次历史,按 50% 中性估算");
        }
        else
        {
            var passed = history.Count(r => r.Outcome == InterviewRoundOutcome.Passed);
            baseProb = (int)Math.Round(100.0 * passed / historyCount);
            factors.Add($"历史同类轮次 {passed}/{historyCount} 通过");
        }

        // 准备度信号:本轮的准备越充分,微调概率
        var target = await db.Entries.AsNoTracking()
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
            { adjustment += 5; factors.Add("已准备面试问题清单 +5%"); }

            if (!string.IsNullOrWhiteSpace(target.Notes))
            { adjustment += 3; factors.Add("有轮次笔记 +3%"); }

            if (!string.IsNullOrWhiteSpace(target.EmailsJson)
                && target.EmailsJson != "[]")
            { adjustment += 2; factors.Add("已关联 scheduling 邮件 +2%"); }
        }

        // 查本轮所属条目的 Q&A 积累情况
        var entry = await db.Entries.AsNoTracking()
            .Where(e => e.Rounds.Any(r => r.Id == request.RoundId))
            .Select(e => new { e.Id, QaCount = e.Questions.Count })
            .FirstOrDefaultAsync(ct);
        if (entry is not null)
        {
            if (entry.QaCount > 0)
            { adjustment += Math.Min(5, entry.QaCount); factors.Add($"已积累 {entry.QaCount} 条 Q&A +{Math.Min(5, entry.QaCount)}%"); }
        }

        var prob = Math.Clamp(baseProb + adjustment, 5, 95);
        var passedTotal = history.Count(r => r.Outcome == InterviewRoundOutcome.Passed);
        return Result.Success(new PassProbabilityDto(
            prob, historyCount, passedTotal,
            round.Stage, factors));
    }
}

public sealed class UpdateRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateRoundCommand, Result>
{
    public async Task<Result> Handle(UpdateRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        if (!Enum.TryParse<InterviewRoundOutcome>(request.Outcome, true, out var outcome))
            return Result.Failure(Error.Validation("Round.InvalidOutcome", "轮次结果无效"));

        try
        {
            e.UpdateRound(request.RoundId, request.Stage, request.ScheduledDate, request.Interviewers,
                request.Format, request.Location, outcome, request.Notes, request.Feedback);
            var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
            round?.UpdateDetails(request.MeetingLink, request.ScheduledTime, request.PrepQuestionsJson,
                request.EmailsJson, request.Transcript, request.RecordingUrl);
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
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
        var e = await db.Entries.Include(x => x.Weaknesses).FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        e.RemoveWeakness(request.WeaknessId);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

// ============================ 指导材料(缺口2) ============================

/// <summary>手动触发:为该条目生成一份新的面试指导材料(版本号自动 +1)。</summary>
public sealed record GenerateGuidanceCommand(
    Guid EntryId,
    string? JdText = null,
    string? ResumeText = null,
    string? InterviewExperiences = null,
    string? CustomRequirements = null) : IRequest<Result<GuidanceMaterialDto>>;

public sealed record GetGuidanceQuery(Guid EntryId, int? Version = null)
    : IRequest<Result<GuidanceMaterialDto>>;

public sealed record ListGuidanceVersionsQuery(Guid EntryId)
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
            .FirstOrDefaultAsync(x => x.Id == request.EntryId, ct);
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
public sealed record ListQuestionCandidatesQuery(Guid EntryId)
    : IRequest<Result<IReadOnlyList<QuestionCandidateDto>>>;

public sealed class ListQuestionCandidatesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListQuestionCandidatesQuery, Result<IReadOnlyList<QuestionCandidateDto>>>
{
    public async Task<Result<IReadOnlyList<QuestionCandidateDto>>> Handle(
        ListQuestionCandidatesQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId, ct);
        if (!exists) return Result.Failure<IReadOnlyList<QuestionCandidateDto>>(Error.NotFound("面试条目"));

        var list = await db.Questions
            .Where(q => q.InterviewEntryId == request.EntryId)
            .OrderBy(q => q.Sequence)
            .Select(q => q.ToCandidateDto())
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<QuestionCandidateDto>>(list);
    }
}
