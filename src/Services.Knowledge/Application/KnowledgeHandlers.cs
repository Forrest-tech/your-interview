using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================ DTO ============================

public sealed record ReviewLogDto(Guid Id, DateTimeOffset ReviewedAt, string Result,
    int? ConfidenceBefore, int? ConfidenceAfter, string? Note, int? DurationSeconds);

public sealed record RelationDto(Guid Id, Guid RelatedItemId, string RelationType, string? Note);

public sealed record KnowledgeItemDto(
    Guid Id, string Title, string Topic, string? SubTopic, string Question,
    string Source, Guid? SourceInterviewEntryId, string? SourceCompanyName, DateOnly? SourceDate,
    int? SourceRoundNo, string? SourceRoundStage, Guid? SourceJobApplicationId,
    int Difficulty, int Importance, string? TagsJson,
    string Mastery, int ReviewCount, DateTimeOffset? LastReviewedAt, DateTimeOffset? NextReviewAt,
    double EasinessFactor, int RepetitionStreak, int? LastIntervalDays,
    string? ConceptExplanation, string? MyAnswer, string? BetterAnswer,
    string? KeyPointsJson, string? CommonMistakesJson, string? FollowUpsJson, string? ReferencesJson,
    bool IsDue, int DueInDays,
    List<ReviewLogDto> ReviewLogs, List<RelationDto> Relations,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record KnowledgeListItemDto(
    Guid Id, string Title, string Topic, string? SubTopic, string Mastery, string Source,
    int Difficulty, int Importance, int ReviewCount, DateTimeOffset? NextReviewAt,
    bool IsDue, string? SourceCompanyName, Guid? SourceInterviewEntryId, DateOnly? SourceDate,
    int? SourceRoundNo, string? SourceRoundStage, Guid? SourceJobApplicationId,
    DateTimeOffset CreatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
    public bool HasNext => Page < TotalPages;
    public bool HasPrevious => Page > 1;
}

public sealed record TopicSummaryDto(string Topic, int Total, int Mastered, int DueToday,
    double? AverageReviewCount);

public sealed record MasteryBucketDto(string Mastery, int Count);

public sealed record KnowledgeStatsDto(
    int TotalItems, int DueToday, int DueThisWeek, int ReviewedThisWeek, int TotalReviews,
    double? AverageReviewCount, double? MasteryRate,
    List<MasteryBucketDto> ByMastery, List<TopicSummaryDto> ByTopic,
    List<CategoryCountDto> BySource, List<CategoryCountDto> TopWeakTopics);

public sealed record CategoryCountDto(string Name, int Count);

public sealed record DuePlanDto(DateOnly Date, List<KnowledgeListItemDto> Items);

// ============================ 查询 ============================

public sealed record ListKnowledgeQuery(
    int Page = 1, int PageSize = 20, string? Topic = null, string? Mastery = null,
    string? Source = null, string? Tag = null, string? Search = null, bool DueOnly = false,
    string? Company = null, Guid? EntryId = null)
    : IRequest<Result<PagedResult<KnowledgeListItemDto>>>;

public sealed record GetKnowledgeItemQuery(Guid Id) : IRequest<Result<KnowledgeItemDto>>;

public sealed record GetTopicsQuery : IRequest<Result<IReadOnlyList<TopicSummaryDto>>>;

public sealed record GetKnowledgeStatsQuery : IRequest<Result<KnowledgeStatsDto>>;

public sealed record GetDuePlanQuery(int Days = 7) : IRequest<Result<IReadOnlyList<DuePlanDto>>>;

/// <summary>来源公司清单(需求 6.4.4:按"这题来自哪家公司"反查)。</summary>
public sealed record GetSourceCompaniesQuery : IRequest<Result<IReadOnlyList<SourceCompanyDto>>>;

public sealed record SourceCompanyDto(string Company, int Count, int MasteredCount,
    int DueCount, DateOnly? LastDate);

/// <summary>
/// 反查单条知识点的来源(需求 6.4.4)。
/// 除了自身来源信息,还带出"同一场面试一起导进来的其他题" ——
/// 复习时最有用的不是孤立的一题,而是"那一场我被问住的这一串"。
/// </summary>
public sealed record GetSourceTrailQuery(Guid Id) : IRequest<Result<SourceTrailDto>>;

public sealed record SourceTrailDto(
    Guid ItemId, string? Company, DateOnly? Date, int? RoundNo, string? RoundStage,
    Guid? EntryId, Guid? ApplicationId, string? Role,
    IReadOnlyList<KnowledgeListItemDto> Siblings);

/// <summary>重复条目检测(需求 6.4.3 去重)。同分类 + 标题归一后相同即视为重复。</summary>
public sealed record FindDuplicatesQuery(int MaxGroups = 50) : IRequest<Result<IReadOnlyList<DuplicateGroupDto>>>;

public sealed record DuplicateGroupDto(string Topic, string Key, IReadOnlyList<KnowledgeListItemDto> Items);

/// <summary>合并两条重复条目:把被合并方的复习次数与空字段并进保留方,再软删被合并方。</summary>
public sealed record MergeDuplicatesCommand(Guid KeepId, Guid MergeId) : IRequest<Result>;

// ============================ 命令 ============================

public sealed record CreateKnowledgeCommand(
    string Title, string Topic, string? SubTopic, string Question, string Source = "Personal",
    int Difficulty = 3, int Importance = 3, string? TagsJson = null,
    string? ConceptExplanation = null, string? MyAnswer = null, string? BetterAnswer = null,
    string? KeyPointsJson = null, string? CommonMistakesJson = null, string? FollowUpsJson = null,
    string? ReferencesJson = null, Guid? SourceInterviewEntryId = null,
    string? SourceCompanyName = null, DateOnly? SourceDate = null,
    int? SourceRoundNo = null, string? SourceRoundStage = null,
    Guid? SourceJobApplicationId = null) : IRequest<Result<Guid>>;

public sealed record UpdateKnowledgeCommand(
    Guid Id, string Title, string Topic, string? SubTopic, string Question,
    int Difficulty, int Importance, string? TagsJson, string? ConceptExplanation,
    string? MyAnswer, string? BetterAnswer, string? KeyPointsJson, string? CommonMistakesJson,
    string? FollowUpsJson, string? ReferencesJson) : IRequest<Result>;

public sealed record DeleteKnowledgeCommand(Guid Id) : IRequest<Result>;

public sealed record RecordReviewCommand(
    Guid Id, string Result, int? ConfidenceBefore = null, int? ConfidenceAfter = null,
    string? Note = null, int? DurationSeconds = null) : IRequest<Result<ReviewOutcomeDto>>;

public sealed record ReviewOutcomeDto(DateTimeOffset? NextReviewAt, int IntervalDays,
    double EasinessFactor, string Mastery, int RepetitionStreak);

public sealed record ChangeMasteryCommand(Guid Id, string Mastery) : IRequest<Result>;

public sealed record AttachRelationCommand(Guid Id, Guid RelatedItemId, string RelationType,
    string? Note = null) : IRequest<Result<Guid>>;

public sealed record ImportKnowledgeCommand(List<ImportKnowledgeRow> Items) : IRequest<Result<int>>;

public sealed record ImportKnowledgeRow(string Title, string Topic, string Question,
    string? SubTopic = null, int Difficulty = 3, int Importance = 3,
    string? ConceptExplanation = null, string? KeyPointsJson = null,
    string? CommonMistakesJson = null, string? ReferencesJson = null,
    string? Mastery = null, string Source = "Personal", string? SourceCompanyName = null,
    int? SourceRoundNo = null, string? SourceRoundStage = null);

/// <summary>
/// 从实战机经的分析结果导入候选问题(需求 6.4.2 / 验收 5)。
///
/// 幂等是这里的第一要求:同一场面试的分析结果可能被反复点导入,
/// 用户也可能手滑连点两次。所以判重键按优先级取:
///   1) 有 EntryId → (EntryId + 归一化的 topic|title)—— 同一场面试的同一题只进一次;
///   2) 有 ClientKey → 该键全局唯一(前端为每条候选生成的稳定键);
///   3) 都没有 → 退回 (topic + title)。
/// 命中已存在的条目不报错、不覆盖,只在回包里计入 Skipped ——
/// "用户点第二次导入" 应当是安静地什么都没发生,而不是弹一个红条。
/// </summary>
public sealed record ImportInterviewCandidatesCommand(
    Guid? EntryId, string? Company, DateOnly? Date, int? RoundNo, string? RoundStage,
    Guid? ApplicationId, IReadOnlyList<InterviewCandidateRow> Items)
    : IRequest<Result<CandidateImportResultDto>>;

public sealed record InterviewCandidateRow(
    string Title, string Topic, string Question, string? SubTopic = null,
    int Difficulty = 3, int Importance = 3, string? ConceptExplanation = null,
    string? BetterAnswer = null, string? KeyPointsJson = null, string? TagsJson = null,
    string? ClientKey = null);

public sealed record CandidateImportResultDto(int Created, int Skipped, int Total, List<Guid> Ids);

// ============================ 校验器 ============================

public sealed class CreateKnowledgeCommandValidator : AbstractValidator<CreateKnowledgeCommand>
{
    public CreateKnowledgeCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Topic).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Question).NotEmpty().MaximumLength(8000);
        RuleFor(x => x.Difficulty).InclusiveBetween(1, 5);
        RuleFor(x => x.Importance).InclusiveBetween(1, 5);
    }
}

public sealed class UpdateKnowledgeCommandValidator : AbstractValidator<UpdateKnowledgeCommand>
{
    public UpdateKnowledgeCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Topic).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Difficulty).InclusiveBetween(1, 5);
        RuleFor(x => x.Importance).InclusiveBetween(1, 5);
    }
}

public sealed class RecordReviewCommandValidator : AbstractValidator<RecordReviewCommand>
{
    private static readonly string[] Allowed = ["Again", "Hard", "Good", "Easy"];

    public RecordReviewCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Result).Must(r => Allowed.Contains(r, StringComparer.OrdinalIgnoreCase))
            .WithMessage("复习结果只能是 Again / Hard / Good / Easy");
    }
}

// ============================ 映射 ============================

/// <summary>是否到复习时间。没排过计划的(NextReviewAt 为空)一律算"该复习了"。</summary>
internal static class KnowledgeDue
{
    public static bool IsDue(KnowledgeItem item, DateTimeOffset now)
        => item.NextReviewAt is null || item.NextReviewAt <= now;
}


/// <summary>
/// 把 JSON 字符串数组形式的引用列表写进聚合。
/// 聚合只暴露 AddReference(单条 + 去重),这里做"先清空再逐条写"的批量覆盖。
/// 放静态助手而不是塞进聚合,是为了让聚合的领域规则保持单一职责。
/// </summary>
internal static class ReferenceApplier
{
    public static void ApplyReferences(KnowledgeItem item, string? referencesJson)
    {
        if (string.IsNullOrWhiteSpace(referencesJson)) return;
        foreach (var r in KnowledgeJson.ParseReferenceList(referencesJson))
            item.AddReference(r.Url, r.Title, r.Kind);
    }
}

public static class KnowledgeMappingExtensions
{
    public static ReviewLogDto ToDto(this KnowledgeReviewLog l) => new(
        l.Id, l.ReviewedAt, l.Result.ToString(), l.ConfidenceBefore, l.ConfidenceAfter,
        l.Note, l.DurationSeconds);

    public static RelationDto ToDto(this KnowledgeRelation r) => new(
        r.Id, r.RelatedItemId, r.RelationType.ToString(), r.Note);

    public static KnowledgeItemDto ToDto(this KnowledgeItem k, DateTimeOffset now)
    {
        var dueInDays = k.NextReviewAt is null
            ? 0
            : (int)Math.Floor((k.NextReviewAt.Value - now).TotalDays);

        return new KnowledgeItemDto(
            k.Id, k.Title, k.Topic, k.SubTopic, k.Question, k.Source.ToString(),
            k.SourceInterviewEntryId, k.SourceCompanyName, k.SourceDate,
            k.SourceRoundNo, k.SourceRoundStage, k.SourceJobApplicationId,
            k.Difficulty, k.Importance, k.TagsJson,
            k.Mastery.ToString(), k.ReviewCount, k.LastReviewedAt, k.NextReviewAt,
            Math.Round(k.EasinessFactor, 2), k.RepetitionStreak, k.LastIntervalDays,
            k.ConceptExplanation, k.MyAnswer, k.BetterAnswer,
            k.KeyPointsJson, k.CommonMistakesJson, k.FollowUpsJson, k.ReferencesJson,
            KnowledgeDue.IsDue(k, now), dueInDays,
            k.ReviewLogs.OrderByDescending(l => l.ReviewedAt).Select(l => l.ToDto()).ToList(),
            k.Relations.Select(r => r.ToDto()).ToList(),
            k.CreatedAt, k.UpdatedAt);
    }

    public static KnowledgeListItemDto ToListItemDto(this KnowledgeItem k, DateTimeOffset now) => new(
        k.Id, k.Title, k.Topic, k.SubTopic, k.Mastery.ToString(), k.Source.ToString(),
        k.Difficulty, k.Importance, k.ReviewCount, k.NextReviewAt, KnowledgeDue.IsDue(k, now),
        k.SourceCompanyName, k.SourceInterviewEntryId, k.SourceDate,
        k.SourceRoundNo, k.SourceRoundStage, k.SourceJobApplicationId, k.CreatedAt);
}

// ============================ Handler:查询 ============================

public sealed class ListKnowledgeQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<ListKnowledgeQuery, Result<PagedResult<KnowledgeListItemDto>>>
{
    public async Task<Result<PagedResult<KnowledgeListItemDto>>> Handle(
        ListKnowledgeQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var page = request.Page < 1 ? 1 : request.Page;
        var size = request.PageSize is < 1 or > 200 ? 20 : request.PageSize;

        var q = db.Items.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Topic))
            q = q.Where(x => x.Topic == request.Topic);

        if (!string.IsNullOrWhiteSpace(request.Mastery)
            && Enum.TryParse<MasteryLevel>(request.Mastery, true, out var m))
            q = q.Where(x => x.Mastery == m);

        if (!string.IsNullOrWhiteSpace(request.Source)
            && Enum.TryParse<KnowledgeSource>(request.Source, true, out var src))
            q = q.Where(x => x.Source == src);

        if (!string.IsNullOrWhiteSpace(request.Tag))
            q = q.Where(x => x.TagsJson != null && x.TagsJson.Contains(request.Tag));

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(x => x.Title.ToLower().Contains(s)
                          || x.Question.ToLower().Contains(s)
                          || x.Topic.ToLower().Contains(s));
        }

        // 今日待复习:没排过复习计划的(NextReviewAt 为空)也算待复习 —— 新知识点本来就该学
        if (request.DueOnly)
            q = q.Where(x => x.NextReviewAt == null || x.NextReviewAt <= now);

        // 按来源公司反查(需求 6.4.4):大小写不敏感 —— 公司名来自不同服务的录入,大小写不可控
        if (!string.IsNullOrWhiteSpace(request.Company))
            q = q.Where(x => x.SourceCompanyName != null
                          && x.SourceCompanyName.ToLower() == request.Company.Trim().ToLower());

        // 按某一场面试反查
        if (request.EntryId is not null && request.EntryId != Guid.Empty)
            q = q.Where(x => x.SourceInterviewEntryId == request.EntryId);

        var total = await q.CountAsync(ct);

        // 排序:待复习优先 → 重要度 → 难度 → 最近创建
        var items = await q
            .OrderByDescending(x => x.NextReviewAt == null || x.NextReviewAt <= now)
            .ThenByDescending(x => x.Importance)
            .ThenBy(x => x.Mastery)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return Result.Success(new PagedResult<KnowledgeListItemDto>(
            items.Select(x => x.ToListItemDto(now)).ToList(), total, page, size));
    }
}

public sealed class GetKnowledgeItemQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetKnowledgeItemQuery, Result<KnowledgeItemDto>>
{
    public async Task<Result<KnowledgeItemDto>> Handle(GetKnowledgeItemQuery request, CancellationToken ct)
    {
        var k = await db.Items.AsNoTracking()
            .Include(x => x.ReviewLogs)
            .Include(x => x.Relations)
            .FirstOrDefaultAsync(x => x.Id == request.Id, ct);

        return k is null
            ? Result.Failure<KnowledgeItemDto>(Error.NotFound("知识点"))
            : Result.Success(k.ToDto(clock.GetUtcNow()));
    }
}

public sealed class GetTopicsQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetTopicsQuery, Result<IReadOnlyList<TopicSummaryDto>>>
{
    public async Task<Result<IReadOnlyList<TopicSummaryDto>>> Handle(GetTopicsQuery request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var rows = await db.Items.AsNoTracking()
            .GroupBy(x => x.Topic)
            .Select(g => new TopicSummaryDto(
                g.Key,
                g.Count(),
                g.Count(x => x.Mastery == MasteryLevel.Mastered),
                g.Count(x => x.NextReviewAt == null || x.NextReviewAt <= now),
                g.Average(x => (double?)x.ReviewCount)))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<TopicSummaryDto>>(
            rows.OrderByDescending(r => r.Total).ToList());
    }
}

public sealed class GetKnowledgeStatsQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetKnowledgeStatsQuery, Result<KnowledgeStatsDto>>
{
    public async Task<Result<KnowledgeStatsDto>> Handle(GetKnowledgeStatsQuery request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var weekEnd = now.AddDays(7);
        var weekStart = now.AddDays(-7);

        var items = await db.Items.AsNoTracking().ToListAsync(ct);
        var logs = await db.ReviewLogs.AsNoTracking().ToListAsync(ct);

        var byMastery = items.GroupBy(x => x.Mastery.ToString())
            .Select(g => new MasteryBucketDto(g.Key, g.Count()))
            .OrderBy(x => x.Mastery)
            .ToList();

        var byTopic = items.GroupBy(x => x.Topic)
            .Select(g => new TopicSummaryDto(g.Key, g.Count(),
                g.Count(x => x.Mastery == MasteryLevel.Mastered),
                g.Count(x => x.NextReviewAt == null || x.NextReviewAt <= now),
                Math.Round(g.Average(x => (double)x.ReviewCount), 1)))
            .OrderByDescending(x => x.Total)
            .ToList();

        var bySource = items.GroupBy(x => x.Source.ToString())
            .Select(g => new CategoryCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        // "最薄弱的技术主题" = 掌握度最低 + 复习次数多的 topic —— 给前端"该补哪里"用
        var weakTopics = items.GroupBy(x => x.Topic)
            .Select(g => new
            {
                Topic = g.Key,
                Score = g.Count(x => x.Mastery is MasteryLevel.New or MasteryLevel.NeedsReview)
                        * 2 + g.Count(x => x.Mastery == MasteryLevel.Learning)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(8)
            .Select(x => new CategoryCountDto(x.Topic, x.Score))
            .ToList();

        var mastered = items.Count(x => x.Mastery == MasteryLevel.Mastered);

        return Result.Success(new KnowledgeStatsDto(
            items.Count,
            items.Count(x => x.NextReviewAt == null || x.NextReviewAt <= now),
            items.Count(x => x.NextReviewAt > now && x.NextReviewAt <= weekEnd),
            logs.Count(l => l.ReviewedAt >= weekStart),
            logs.Count,
            items.Count == 0 ? null : Math.Round(items.Average(x => (double)x.ReviewCount), 1),
            items.Count == 0 ? null : Math.Round(mastered * 100.0 / items.Count, 1),
            byMastery, byTopic, bySource, weakTopics));
    }
}

public sealed class GetDuePlanQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetDuePlanQuery, Result<IReadOnlyList<DuePlanDto>>>
{
    public async Task<Result<IReadOnlyList<DuePlanDto>>> Handle(GetDuePlanQuery request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var days = request.Days is < 1 or > 90 ? 7 : request.Days;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var horizon = now.AddDays(days);

        var items = await db.Items.AsNoTracking()
            .Include(x => x.ReviewLogs)
            .Include(x => x.Relations)
            .Where(x => x.NextReviewAt == null || x.NextReviewAt <= horizon)
            .ToListAsync(ct);

        // 逾期的统一归到"今天",让复习计划不出现过去的日期
        var plan = items
            .GroupBy(x =>
            {
                if (x.NextReviewAt is null || x.NextReviewAt <= now) return today;
                var d = DateOnly.FromDateTime(x.NextReviewAt.Value.UtcDateTime);
                return d < today ? today : d;
            })
            .OrderBy(g => g.Key)
            .Select(g => new DuePlanDto(g.Key,
                g.OrderByDescending(x => x.Importance).Select(x => x.ToListItemDto(now)).ToList()))
            .ToList();

        return Result.Success<IReadOnlyList<DuePlanDto>>(plan);
    }
}

// ============================ Handler:命令 ============================

public sealed class CreateKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<CreateKnowledgeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateKnowledgeCommand request, CancellationToken ct)
    {
        if (!Enum.TryParse<KnowledgeSource>(request.Source, true, out var source))
            source = KnowledgeSource.Personal;

        var item = new KnowledgeItem(request.Title, request.Topic, request.Question, source,
            subTopic: request.SubTopic, difficulty: request.Difficulty, importance: request.Importance);

        item.UpdateDetails(request.Title, request.Topic, request.SubTopic, request.Question,
            request.Difficulty, request.Importance, request.TagsJson,
            request.ConceptExplanation, request.MyAnswer, request.BetterAnswer,
            request.KeyPointsJson, request.CommonMistakesJson, request.FollowUpsJson);
        ReferenceApplier.ApplyReferences(item, request.ReferencesJson);

        if (request.SourceInterviewEntryId is not null)
            item.LinkToInterview(request.SourceInterviewEntryId.Value, request.SourceCompanyName,
                request.SourceDate, request.SourceRoundNo, request.SourceRoundStage,
                request.SourceJobApplicationId);
        else if (!string.IsNullOrWhiteSpace(request.SourceCompanyName))
            // 只知道公司、没有机经条目时也要标来源 —— "我知道这题是面 A 公司时遇到的"
            // 本身就是有价值的信息,不该因为缺 EntryId 就退化成 Personal。
            item.LinkToInterview(Guid.Empty, request.SourceCompanyName, request.SourceDate,
                request.SourceRoundNo, request.SourceRoundStage, request.SourceJobApplicationId);

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return Result.Success(item.Id);
    }
}

public sealed class UpdateKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<UpdateKnowledgeCommand, Result>
{
    public async Task<Result> Handle(UpdateKnowledgeCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));

        k.UpdateDetails(request.Title, request.Topic, request.SubTopic, request.Question,
            request.Difficulty, request.Importance, request.TagsJson,
            request.ConceptExplanation, request.MyAnswer, request.BetterAnswer,
            request.KeyPointsJson, request.CommonMistakesJson, request.FollowUpsJson);
        ReferenceApplier.ApplyReferences(k, request.ReferencesJson);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<DeleteKnowledgeCommand, Result>
{
    public async Task<Result> Handle(DeleteKnowledgeCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        k.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class RecordReviewCommandHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<RecordReviewCommand, Result<ReviewOutcomeDto>>
{
    public async Task<Result<ReviewOutcomeDto>> Handle(RecordReviewCommand request,
        CancellationToken ct)
    {
        var k = await db.Items.Include(x => x.ReviewLogs).FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (k is null) return Result.Failure<ReviewOutcomeDto>(Error.NotFound("知识点"));

        if (!Enum.TryParse<ReviewResult>(request.Result, true, out var result))
            return Result.Failure<ReviewOutcomeDto>(
                Error.Validation("Review.InvalidResult", "复习结果无效"));

        k.RecordReview(result, request.ConfidenceBefore, request.ConfidenceAfter,
            request.Note, request.DurationSeconds);

        await db.SaveChangesAsync(ct);   // 领域事件 → KnowledgeMasteryChanged 集成事件

        var interval = k.NextReviewAt is null
            ? 0
            : Math.Max(0, (int)Math.Round((k.NextReviewAt.Value - clock.GetUtcNow()).TotalDays));

        return Result.Success(new ReviewOutcomeDto(k.NextReviewAt, interval,
            Math.Round(k.EasinessFactor, 2), k.Mastery.ToString(), k.RepetitionStreak));
    }
}

public sealed class ChangeMasteryCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ChangeMasteryCommand, Result>
{
    public async Task<Result> Handle(ChangeMasteryCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));

        if (!Enum.TryParse<MasteryLevel>(request.Mastery, true, out var level))
            return Result.Failure(Error.Validation("Mastery.Invalid", "掌握度无效"));

        k.PromoteMastery(level);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AttachRelationCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<AttachRelationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AttachRelationCommand request, CancellationToken ct)
    {
        var k = await db.Items.Include(x => x.Relations).FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (k is null) return Result.Failure<Guid>(Error.NotFound("知识点"));

        if (request.Id == request.RelatedItemId)
            return Result.Failure<Guid>(Error.Validation("Relation.SelfReference", "不能关联自己"));

        if (!await db.Items.AnyAsync(x => x.Id == request.RelatedItemId, ct))
            return Result.Failure<Guid>(Error.Validation("Relation.TargetNotFound", "关联的知识点不存在"));

        if (!Enum.TryParse<KnowledgeRelationType>(request.RelationType, true, out var type))
            return Result.Failure<Guid>(Error.Validation("Relation.InvalidType", "关联类型无效"));

        var r = k.AttachRelation(request.RelatedItemId, type, request.Note);
        await db.SaveChangesAsync(ct);
        return Result.Success(r.Id);
    }
}

public sealed class ImportKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ImportKnowledgeCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ImportKnowledgeCommand request, CancellationToken ct)
    {
        if (request.Items.Count == 0)
            return Result.Failure<int>(Error.Validation("Import.Empty", "导入内容为空"));

        if (request.Items.Count > 500)
            return Result.Failure<int>(Error.Validation("Import.TooMany", "单次最多导入 500 条"));

        var created = 0;
        foreach (var row in request.Items)
        {
            if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.Topic))
                continue;   // 跳过无效行而不是整体失败 —— 批量导入要容错

            // 幂等:同 topic + 同 title 视为已存在,不重复插
            if (await db.Items.AnyAsync(x => x.Topic == row.Topic && x.Title == row.Title, ct))
                continue;

            if (!Enum.TryParse<KnowledgeSource>(row.Source, true, out var source))
                source = KnowledgeSource.Personal;

            var item = new KnowledgeItem(row.Title, row.Topic, row.Question, source,
                subTopic: row.SubTopic, difficulty: row.Difficulty, importance: row.Importance);

            item.UpdateDetails(row.Title, row.Topic, row.SubTopic, row.Question,
                row.Difficulty, row.Importance, null,
                row.ConceptExplanation, null, null,
                row.KeyPointsJson, row.CommonMistakesJson, null);
            ReferenceApplier.ApplyReferences(item, row.ReferencesJson);

            if (!string.IsNullOrWhiteSpace(row.Mastery)
                && Enum.TryParse<MasteryLevel>(row.Mastery, true, out var ml))
                item.PromoteMastery(ml);

            if (!string.IsNullOrWhiteSpace(row.SourceCompanyName))
                item.LinkToInterview(Guid.Empty, row.SourceCompanyName, null,
                    row.SourceRoundNo, row.SourceRoundStage);

            db.Items.Add(item);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(created);
    }
}

// ============================================================================
//  需求 6.4.3 / 6.4.4:去重 · 来源反查 · 机经候选题幂等导入
// ============================================================================

/// <summary>标题归一化指纹 —— 判重用。去大小写、去标点空白,中英文都保留字。</summary>
internal static class KnowledgeFingerprint
{
    public static string Of(string topic, string title)
    {
        var t = Regex.Replace($"{topic}|{title}", @"[\s\p{P}\p{S}]", string.Empty);
        return t.ToLowerInvariant();
    }
}

public sealed class GetSourceCompaniesQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetSourceCompaniesQuery, Result<IReadOnlyList<SourceCompanyDto>>>
{
    public async Task<Result<IReadOnlyList<SourceCompanyDto>>> Handle(
        GetSourceCompaniesQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 只投影分组需要的三列,聚合放内存做 ——
        // 不同提供器对 DateOnly 的 Max() 支持不一致,为了一个"最后面试日期"
        // 去赌 SQL 翻译不值得;这里是个人规模的知识库(几百到几千条),内存聚合足够快。
        var rows = await db.Items.AsNoTracking()
            .Where(x => x.SourceCompanyName != null && x.SourceCompanyName != string.Empty)
            .Select(x => new
            {
                Company = x.SourceCompanyName!,
                Mastered = x.Mastery == MasteryLevel.Mastered,
                Due = x.NextReviewAt == null || x.NextReviewAt <= now,
                Date = x.SourceDate
            })
            .ToListAsync(ct);

        var list = rows
            .GroupBy(x => x.Company)
            .Select(g => new SourceCompanyDto(
                g.Key,
                g.Count(),
                g.Count(x => x.Mastered),
                g.Count(x => x.Due),
                g.Max(x => x.Date)))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Company, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Result.Success<IReadOnlyList<SourceCompanyDto>>(list);
    }
}

public sealed class GetSourceTrailQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetSourceTrailQuery, Result<SourceTrailDto>>
{
    public async Task<Result<SourceTrailDto>> Handle(GetSourceTrailQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.Id, ct);
        if (item is null) return Result.Failure<SourceTrailDto>(Error.NotFound("知识点"));

        // 同场面试一起进来的题:这是"反查"真正有价值的部分 ——
        // 复习时想回忆的不是孤零零一题,而是"那一场我被问住的这一串"。
        var siblings = new List<KnowledgeListItemDto>();
        if (item.SourceInterviewEntryId is not null && item.SourceInterviewEntryId != Guid.Empty)
        {
            var rows = await db.Items.AsNoTracking()
                .Where(x => x.SourceInterviewEntryId == item.SourceInterviewEntryId && x.Id != item.Id)
                .OrderBy(x => x.SourceRoundNo ?? 0)
                .ThenByDescending(x => x.Importance)
                .Take(50)
                .ToListAsync(ct);
            siblings = rows.Select(x => x.ToListItemDto(now)).ToList();
        }

        return Result.Success(new SourceTrailDto(
            item.Id, item.SourceCompanyName, item.SourceDate, item.SourceRoundNo,
            item.SourceRoundStage, item.SourceInterviewEntryId, item.SourceJobApplicationId,
            null, siblings));
    }
}

public sealed class FindDuplicatesQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<FindDuplicatesQuery, Result<IReadOnlyList<DuplicateGroupDto>>>
{
    public async Task<Result<IReadOnlyList<DuplicateGroupDto>>> Handle(
        FindDuplicatesQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 归一化要去掉标点,数据库里做不到(不同 DB 的正则能力不一致),
        // 所以只把 (id, topic, title) 三列拉回内存做分组 —— 知识库是个人规模,这一列很轻。
        var rows = await db.Items.AsNoTracking()
            .Select(x => new { x.Id, x.Topic, x.Title })
            .ToListAsync(ct);

        var groups = rows
            .GroupBy(x => KnowledgeFingerprint.Of(x.Topic, x.Title))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .Take(request.MaxGroups is < 1 or > 200 ? 50 : request.MaxGroups)
            .ToList();

        if (groups.Count == 0) return Result.Success<IReadOnlyList<DuplicateGroupDto>>([]);

        var ids = groups.SelectMany(g => g.Select(x => x.Id)).ToList();
        var full = await db.Items.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
        var byId = full.ToDictionary(x => x.Id);

        var result = groups.Select(g => new DuplicateGroupDto(
            g.First().Topic,
            g.Key,
            g.Select(x => byId[x.Id].ToListItemDto(now)).ToList()))
            .ToList();

        return Result.Success<IReadOnlyList<DuplicateGroupDto>>(result);
    }
}

public sealed class MergeDuplicatesCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<MergeDuplicatesCommand, Result>
{
    public async Task<Result> Handle(MergeDuplicatesCommand request, CancellationToken ct)
    {
        if (request.KeepId == request.MergeId)
            return Result.Failure(Error.Validation("Merge.Self", "不能与自己合并"));

        var keep = await db.Items.FirstOrDefaultAsync(x => x.Id == request.KeepId, ct);
        if (keep is null) return Result.Failure(Error.NotFound("要保留的知识点"));
        var dup = await db.Items.FirstOrDefaultAsync(x => x.Id == request.MergeId, ct);
        if (dup is null) return Result.Failure(Error.NotFound("要合并的知识点"));

        // 合并策略:保留方已有的字段一律不动(用户的编辑优先),
        // 只把保留方为空、被合并方有内容的字段补进来 —— 合并是"补缺",不是"覆盖"。
        keep.UpdateDetails(
            keep.Title, keep.Topic, keep.SubTopic ?? dup.SubTopic, keep.Question,
            Math.Max(keep.Difficulty, dup.Difficulty), Math.Max(keep.Importance, dup.Importance),
            keep.TagsJson ?? dup.TagsJson,
            keep.ConceptExplanation ?? dup.ConceptExplanation,
            keep.MyAnswer ?? dup.MyAnswer,
            keep.BetterAnswer ?? dup.BetterAnswer,
            keep.KeyPointsJson ?? dup.KeyPointsJson,
            keep.CommonMistakesJson ?? dup.CommonMistakesJson,
            keep.FollowUpsJson ?? dup.FollowUpsJson);

        ReferenceApplier.ApplyReferences(keep, dup.ReferencesJson);
        keep.SetImportKey(keep.ImportKey ?? dup.ImportKey);

        // 复习次数刻意不叠加:那是真实的复习历史,合并两条记录不该凭空多出复习次数。
        if (keep.SourceInterviewEntryId is null && dup.SourceInterviewEntryId is not null)
            keep.LinkToInterview(dup.SourceInterviewEntryId.Value, dup.SourceCompanyName, dup.SourceDate,
                dup.SourceRoundNo, dup.SourceRoundStage, dup.SourceJobApplicationId);

        dup.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportInterviewCandidatesCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ImportInterviewCandidatesCommand, Result<CandidateImportResultDto>>
{
    public async Task<Result<CandidateImportResultDto>> Handle(
        ImportInterviewCandidatesCommand request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count == 0)
            return Result.Failure<CandidateImportResultDto>(
                Error.Validation("Import.Empty", "没有待导入的题目"));
        if (request.Items.Count > 300)
            return Result.Failure<CandidateImportResultDto>(
                Error.Validation("Import.TooMany", "单次最多导入 300 条"));

        // ---- 先建"已有指纹"集合,保证幂等 ----
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (request.EntryId is not null && request.EntryId != Guid.Empty)
        {
            var rows = await db.Items.AsNoTracking()
                .Where(x => x.SourceInterviewEntryId == request.EntryId)
                .Select(x => new { x.Title, x.Topic, x.ImportKey })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                seen.Add(KnowledgeFingerprint.Of(r.Topic, r.Title));
                if (!string.IsNullOrWhiteSpace(r.ImportKey)) seen.Add("key:" + r.ImportKey);
            }
        }

        var keys = request.Items
            .Select(x => x.ClientKey?.Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!)
            .Distinct()
            .ToList();

        if (keys.Count > 0)
        {
            var hit = await db.Items.AsNoTracking()
                .Where(x => x.ImportKey != null && keys.Contains(x.ImportKey))
                .Select(x => x.ImportKey!)
                .ToListAsync(ct);
            foreach (var h in hit) seen.Add("key:" + h);
        }

        var created = 0;
        var skipped = 0;
        var ids = new List<Guid>();

        foreach (var row in request.Items)
        {
            if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.Topic))
            {
                skipped++;   // 无效行跳过而不是整体失败 —— 批量导入要容错
                continue;
            }

            var fp = KnowledgeFingerprint.Of(row.Topic, row.Title);
            var kf = string.IsNullOrWhiteSpace(row.ClientKey) ? null : "key:" + row.ClientKey!.Trim();

            // 幂等命中:安静跳过。用户重复点"导入"不该看到报错。
            if (seen.Contains(fp) || (kf is not null && seen.Contains(kf))) { skipped++; continue; }

            var item = new KnowledgeItem(row.Title, row.Topic, row.Question,
                KnowledgeSource.FromInterview, subTopic: row.SubTopic,
                difficulty: row.Difficulty, importance: row.Importance);

            item.UpdateDetails(row.Title, row.Topic, row.SubTopic, row.Question,
                row.Difficulty, row.Importance, row.TagsJson,
                row.ConceptExplanation, null, row.BetterAnswer,
                row.KeyPointsJson, null, null);
            item.SetImportKey(row.ClientKey);

            if (request.EntryId is not null && request.EntryId != Guid.Empty)
                item.LinkToInterview(request.EntryId.Value, request.Company, request.Date,
                    request.RoundNo, request.RoundStage, request.ApplicationId);
            else if (!string.IsNullOrWhiteSpace(request.Company))
                item.LinkToInterview(Guid.Empty, request.Company, request.Date,
                    request.RoundNo, request.RoundStage, request.ApplicationId);

            db.Items.Add(item);
            seen.Add(fp);
            if (kf is not null) seen.Add(kf);
            ids.Add(item.Id);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(new CandidateImportResultDto(created, skipped, request.Items.Count, ids));
    }
}
