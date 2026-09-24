using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
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

public sealed record InterviewEntryDto(
    Guid Id, Guid CompanyId, string CompanyName, Guid? JobApplicationId, string Role,
    string? CompanyProfile, string? JdText, string? JdSummary,
    int RoundNo, DateOnly? InterviewDate, string? InterviewFormat, string? Interviewers,
    string? Location, string? Result, string? Notes,
    string Status, string? FailureReason, DateTimeOffset? TranscribedAt, DateTimeOffset? AnalyzedAt,
    int? OverallScore, int? PronunciationScore, int? FluencyScore, int? StructureScore,
    int? TechnicalDepthScore, int? RelevanceScore, string? AnalysisSummary,
    int AssetCount, int QuestionCount, int WeaknessCount,
    List<AssetDto> Assets, List<QuestionDto> Questions, List<WeaknessDto> Weaknesses,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record InterviewEntryListItemDto(
    Guid Id, Guid CompanyId, string CompanyName, string Role, int RoundNo,
    DateOnly? InterviewDate, string? InterviewFormat, string Status, string? Result,
    int? OverallScore, int? PronunciationScore, int? FluencyScore, int? StructureScore,
    int? TechnicalDepthScore, int? RelevanceScore,
    int AssetCount, int QuestionCount, int WeaknessCount, bool HasTranscript,
    DateTimeOffset CreatedAt);

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
    Guid CompanyId, string CompanyName, string Role, Guid? JobApplicationId = null,
    string? CompanyProfile = null, string? JdText = null, string? JdSummary = null,
    string? InterviewFormat = null, string? Interviewers = null, DateOnly? InterviewDate = null)
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
    List<QuestionDraft>? Questions, List<WeaknessDraft>? Weaknesses) : IRequest<Result>;

public sealed record MarkEntryFailedCommand(Guid EntryId, string Reason) : IRequest<Result>;

public sealed record AddQuestionCommand(
    Guid EntryId, string QuestionText, string? MyAnswerText, string Category, int Difficulty,
    string? Assessment = null) : IRequest<Result<Guid>>;

public sealed record UpdateQuestionCommand(
    Guid EntryId, Guid QuestionId, string QuestionText, string? MyAnswerText, string Category,
    int Difficulty, string? Assessment, bool GotStuck, string? StuckReason,
    string? RecommendedAnswer) : IRequest<Result>;

public sealed record RemoveQuestionCommand(Guid EntryId, Guid QuestionId) : IRequest<Result>;

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

    public static InterviewEntryDto ToDto(this InterviewEntry e) => new(
        e.Id, e.CompanyId, e.CompanyName, e.JobApplicationId, e.Role,
        e.CompanyProfile, e.JdText, e.JdSummary,
        e.RoundNo, e.InterviewDate, e.InterviewFormat, e.Interviewers, e.Location, e.Result, e.Notes,
        e.Status.ToString(), e.FailureReason, e.TranscribedAt, e.AnalyzedAt,
        e.OverallScore, e.PronunciationScore, e.FluencyScore, e.StructureScore,
        e.TechnicalDepthScore, e.RelevanceScore, e.AnalysisSummary,
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count,
        e.Assets.OrderBy(x => x.UploadedAt).Select(x => x.ToDto()).ToList(),
        e.Questions.OrderBy(x => x.Sequence).Select(x => x.ToDto()).ToList(),
        e.Weaknesses.OrderByDescending(x => x.Severity).Select(x => x.ToDto()).ToList(),
        e.CreatedAt, e.UpdatedAt);

    /// <summary>列表项只带聚合的标量字段(不带子集合),避免列表页把大文本拖出来。</summary>
    public static InterviewEntryListItemDto ToListItemDto(this InterviewEntry e) => new(
        e.Id, e.CompanyId, e.CompanyName, e.Role, e.RoundNo, e.InterviewDate, e.InterviewFormat,
        e.Status.ToString(), e.Result,
        e.OverallScore, e.PronunciationScore, e.FluencyScore, e.StructureScore,
        e.TechnicalDepthScore, e.RelevanceScore,
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count,
        e.Assets.Any(a => a.TranscriptText != null),
        e.CreatedAt);
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
        var entry = new InterviewEntry(request.CompanyId, request.CompanyName, request.Role,
            request.JobApplicationId);

        if (request.CompanyProfile is not null || request.JdText is not null
            || request.Interviewers is not null || request.InterviewDate is not null)
        {
            entry.UpdateBasicInfo(request.CompanyName, request.Role, request.CompanyProfile,
                request.JdText, request.JdSummary, 1, request.InterviewDate,
                request.InterviewFormat, request.Interviewers, null, null, null);
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
                request.Questions, request.Weaknesses);
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
