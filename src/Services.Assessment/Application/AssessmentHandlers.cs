using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;

namespace YourInterview.Services.Assessment.Application;

// =====================================================================================
//  DTO
// =====================================================================================

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record DimensionIssueDto(Guid Id, string Dimension, string Title, string? Detail,
    string? Evidence, string? Suggestion, int Severity);

public sealed record SessionListItemDto(Guid Id, string Title, string Mode, string? Topic,
    string? CompanyStyle, string Status, int QuestionCount, int AnsweredCount, int ScoredCount,
    int? OverallScore, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string? WeakestDimension);

public sealed record QuestionDto(Guid Id, int Sequence, string QuestionText, string Type,
    int Difficulty, string State, string? ExpectedPointsJson, string? AnswerText,
    double? AnswerDurationSeconds, string? Comment, string? RecommendedAnswer,
    string? BetterStructure, string? FillerWordsJson, int? Overall,
    ScoresDto? Scores, IReadOnlyList<DimensionIssueDto> Issues, string? TopIssueTitle);

public sealed record ScoresDto(int Pronunciation, int Fluency, int SentenceIntegrity,
    int Structure, int TechnicalDepth, int Relevance, int Overall, string WeakestDimension);

public sealed record SessionDetailDto(Guid Id, string Title, string Mode, string? Topic,
    string? CompanyStyle, string Status, int TargetQuestionCount, string Language,
    DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? OverallSummary,
    string? PriorityAction, int? OverallScore, ScoresDto? AverageScores,
    IReadOnlyList<QuestionDto> Questions, IReadOnlyList<WeaknessBucketDto> Weaknesses);

public sealed record WeaknessBucketDto(string Dimension, int Count, double AvgSeverity);

public sealed record AssessmentStatsDto(int TotalSessions, int CompletedSessions,
    int InProgressSessions, int TotalQuestions, int ScoredQuestions, int TotalIssues,
    int? AverageOverall, ScoresDto? AverageScores,
    IReadOnlyList<WeaknessBucketDto> IssuesByDimension,
    IReadOnlyList<ProgressPointDto> Progress,
    IReadOnlyList<DimensionInfoDto> Dimensions);

/// <summary>进步曲线上的一个点(每次完成的练习一个点)。</summary>
public sealed record ProgressPointDto(Guid SessionId, string Title, DateTimeOffset CompletedAt,
    int Overall, ScoresDto Scores);

/// <summary>六维说明 —— 前端雷达图的轴定义 + 用户教育(告诉用户每一维在看什么)。</summary>
public sealed record DimensionInfoDto(string Key, string NameZh, string NameEn, string WhatItMeasures,
    string HowToImprove, double Weight);

public sealed record QuestionDraftIn(string QuestionText, string Type, int Difficulty,
    string? ExpectedPointsJson);

public sealed record IssueDraftIn(string Dimension, string Title, string? Detail, string? Evidence,
    string? Suggestion, int Severity);

// =====================================================================================
//  查询
// =====================================================================================

/// <summary>会话列表。</summary>
public sealed record ListSessionsQuery(Guid UserId, int Page = 1, int PageSize = 20,
    string? Status = null, string? Mode = null) : IRequest<Result<PagedResult<SessionListItemDto>>>;

public sealed class ListSessionsQueryHandler(AssessmentDbContext db)
    : IRequestHandler<ListSessionsQuery, Result<PagedResult<SessionListItemDto>>>
{
    public async Task<Result<PagedResult<SessionListItemDto>>> Handle(ListSessionsQuery r,
        CancellationToken ct)
    {
        var q = db.Sessions.AsNoTracking()
            .Include(x => x.Questions).ThenInclude(x => x.Issues)
            .Where(x => x.UserId == r.UserId);

        if (!string.IsNullOrWhiteSpace(r.Status) &&
            Enum.TryParse<SessionStatus>(r.Status, true, out var st))
            q = q.Where(x => x.Status == st);

        if (!string.IsNullOrWhiteSpace(r.Mode) &&
            Enum.TryParse<SessionMode>(r.Mode, true, out var md))
            q = q.Where(x => x.Mode == md);

        var total = await q.CountAsync(ct);

        var items = await q.OrderByDescending(x => x.StartedAt)
            .Skip((Math.Max(r.Page, 1) - 1) * r.PageSize)
            .Take(r.PageSize)
            .ToListAsync(ct);

        var dtos = items.Select(x =>
        {
            var scored = x.Questions.Where(q2 => q2.Scores is not null).ToList();
            return new SessionListItemDto(
                x.Id, x.Title, x.Mode.ToString(), x.Topic, x.CompanyStyle, x.Status.ToString(),
                x.Questions.Count,
                x.Questions.Count(q2 => q2.State != QuestionState.Asked),
                scored.Count,
                x.OverallScore,
                x.StartedAt, x.CompletedAt,
                scored.Count == 0 ? null : scored
                    .SelectMany(q2 => q2.Issues)
                    .GroupBy(i => i.Dimension)
                    .OrderByDescending(g => g.Average(i => i.Severity))
                    .First().Key);
        }).ToList();

        return Result.Success(new PagedResult<SessionListItemDto>(dtos, total,
            Math.Max(r.Page, 1), r.PageSize));
    }
}

/// <summary>会话详情(含每题评分与短板)。</summary>
public sealed record GetSessionQuery(Guid Id, Guid UserId) : IRequest<Result<SessionDetailDto>>;

public sealed class GetSessionQueryHandler(AssessmentDbContext db)
    : IRequestHandler<GetSessionQuery, Result<SessionDetailDto>>
{
    public async Task<Result<SessionDetailDto>> Handle(GetSessionQuery r, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking()
            .Include(x => x.Questions).ThenInclude(x => x.Issues)
            .FirstOrDefaultAsync(x => x.Id == r.Id && x.UserId == r.UserId, ct);

        if (s is null) return Result.Failure<SessionDetailDto>(Error.NotFound("模拟练习"));

        return Result.Success(new SessionDetailDto(
            s.Id, s.Title, s.Mode.ToString(), s.Topic, s.CompanyStyle, s.Status.ToString(),
            s.TargetQuestionCount, s.Language, s.StartedAt, s.CompletedAt,
            s.OverallSummary, s.PriorityAction, s.OverallScore,
            ToScores(s.AverageScores()),
            s.Questions.OrderBy(q => q.Sequence).Select(ToQuestion).ToList(),
            s.WeaknessSummary().Select(w => new WeaknessBucketDto(w.Dimension, w.Count, w.AvgSeverity)).ToList()));
    }

    internal static QuestionDto ToQuestion(MockQuestion q) => new(
        q.Id, q.Sequence, q.QuestionText, q.Type.ToString(), q.Difficulty, q.State.ToString(),
        q.ExpectedPointsJson, q.AnswerText, q.AnswerDurationSeconds,
        q.Comment, q.RecommendedAnswer, q.BetterStructure, q.FillerWordsJson,
        q.Scores?.Overall,
        q.Scores is null ? null : new ScoresDto(q.Scores.Pronunciation, q.Scores.Fluency,
            q.Scores.SentenceIntegrity, q.Scores.Structure, q.Scores.TechnicalDepth,
            q.Scores.Relevance, q.Scores.Overall, q.Scores.WeakestDimension()),
        q.Issues.Select(i => new DimensionIssueDto(i.Id, i.Dimension, i.Title, i.Detail,
            i.Evidence, i.Suggestion, i.Severity)).ToList(),
        q.TopIssue?.Title);

    internal static ScoresDto? ToScores((int Pronunciation, int Fluency, int SentenceIntegrity,
        int Structure, int TechnicalDepth, int Relevance)? t)
    {
        if (t is null) return null;
        var d = new DimensionScores(t.Value.Pronunciation, t.Value.Fluency, t.Value.SentenceIntegrity,
            t.Value.Structure, t.Value.TechnicalDepth, t.Value.Relevance);
        return new ScoresDto(d.Pronunciation, d.Fluency, d.SentenceIntegrity, d.Structure,
            d.TechnicalDepth, d.Relevance, d.Overall, d.WeakestDimension());
    }
}

/// <summary>总览统计 + 进步曲线 + 六维说明(前端仪表盘一次拿全)。</summary>
public sealed record GetAssessmentStatsQuery(Guid UserId) : IRequest<Result<AssessmentStatsDto>>;

public sealed class GetAssessmentStatsQueryHandler(AssessmentDbContext db)
    : IRequestHandler<GetAssessmentStatsQuery, Result<AssessmentStatsDto>>
{
    public async Task<Result<AssessmentStatsDto>> Handle(GetAssessmentStatsQuery r, CancellationToken ct)
    {
        var sessions = await db.Sessions.AsNoTracking()
            .Include(x => x.Questions).ThenInclude(x => x.Issues)
            .Where(x => x.UserId == r.UserId)
            .OrderBy(x => x.StartedAt)
            .ToListAsync(ct);

        var allScored = sessions.SelectMany(s => s.Questions).Where(q => q.Scores is not null).ToList();
        var allIssues = allScored.SelectMany(q => q.Issues).ToList();

        var buckets = allIssues
            .GroupBy(i => i.Dimension)
            .Select(g => new WeaknessBucketDto(g.Key, g.Count(), Math.Round(g.Average(i => i.Severity), 2)))
            .OrderByDescending(b => b.Count)
            .ToList();

        var progress = sessions
            .Where(s => s.Status == SessionStatus.Completed)
            .Select(s => new ProgressPointDto(s.Id, s.Title, s.CompletedAt ?? s.StartedAt,
                s.OverallScore ?? 0, GetSessionQueryHandler.ToScores(s.AverageScores())!))
            .Where(p => p.Scores is not null)
            .ToList();

        ScoresDto? avg = null;
        int? avgOverall = null;
        if (allScored.Count > 0)
        {
            var d = new DimensionScores(
                (int)Math.Round(allScored.Average(q => q.Scores!.Pronunciation)),
                (int)Math.Round(allScored.Average(q => q.Scores!.Fluency)),
                (int)Math.Round(allScored.Average(q => q.Scores!.SentenceIntegrity)),
                (int)Math.Round(allScored.Average(q => q.Scores!.Structure)),
                (int)Math.Round(allScored.Average(q => q.Scores!.TechnicalDepth)),
                (int)Math.Round(allScored.Average(q => q.Scores!.Relevance)));
            avg = new ScoresDto(d.Pronunciation, d.Fluency, d.SentenceIntegrity, d.Structure,
                d.TechnicalDepth, d.Relevance, d.Overall, d.WeakestDimension());
            avgOverall = d.Overall;
        }

        return Result.Success(new AssessmentStatsDto(
            sessions.Count,
            sessions.Count(s => s.Status == SessionStatus.Completed),
            sessions.Count(s => s.Status == SessionStatus.InProgress),
            sessions.Sum(s => s.Questions.Count),
            allScored.Count,
            allIssues.Count,
            avgOverall, avg, buckets, progress, Dimensions));
    }

    /// <summary>
    /// 六维定义 —— 平台里唯一的权威说明。
    /// 权重与 DimensionScores.Overall 的计算保持一致;
    /// "怎么提升"写的是可执行动作,不是"多练习"这种废话。
    /// </summary>
    internal static readonly IReadOnlyList<DimensionInfoDto> Dimensions =
    [
        new("pronunciation", "发音", "Pronunciation",
            "术语读音、重音位置、清晰度。注意:历史数据显示发音通常**不是**瓶颈(92.8/100)。",
            "只当低分集中在专业术语时才专项练:建术语发音表,C#=see-sharp、thread、algorithm、vision 逐个纠。",
            0.08),
        new("fluency", "流利度", "Fluency",
            "停顿、填充词(um/uh)、语速、自我重复。压力下用重复换思考时间是典型问题。",
            "用\"Let me think for a second\"的静默两秒替代填充词;宁可停顿,不要 um/uh。",
            0.14),
        new("sentenceIntegrity", "语句完整性", "Sentence Integrity",
            "句子是否完整、平均句长、有没有残句。平均句长 7.5 词 = 信息被切成碎片。",
            "练\"结论句 + 两个支撑句\"的三句话单元,把短句合并成带因果/让步关系的复合句。",
            0.10),
        new("structure", "结构", "Structure",
            "有没有骨架:结论先行 + First/Second/Third + trade-off。缺骨架是最大增量项。",
            "背万能骨架:\"The short answer is X. Three things I'd focus on. First... Second... Third... The main trade-off is...\"",
            0.22),
        new("technicalDepth", "技术深度", "Technical Depth",
            "知识点是否正确、有没有讲到原理与权衡(而不是只给结论)。",
            "技术栈模块按 SM-2 复习;每题必须能说出\"为什么这么做\"和\"代价是什么\"。",
            0.28),
        new("relevance", "相关性", "Relevance",
            "有没有答到点子上。答非所问(把 system design 题当项目回忆答)是历史重灾区。",
            "答题前先复述问题确认理解:\"So you're asking about X, right?\" 再开始。",
            0.18)
    ];
}

/// <summary>六维说明单独暴露(前端不需要拿统计也能渲染轴定义)。</summary>
public sealed record GetDimensionsQuery : IRequest<Result<IReadOnlyList<DimensionInfoDto>>>;

public sealed class GetDimensionsQueryHandler
    : IRequestHandler<GetDimensionsQuery, Result<IReadOnlyList<DimensionInfoDto>>>
{
    public Task<Result<IReadOnlyList<DimensionInfoDto>>> Handle(GetDimensionsQuery r, CancellationToken ct)
        => Task.FromResult(Result.Success(GetAssessmentStatsQueryHandler.Dimensions));
}

// =====================================================================================
//  命令
// =====================================================================================

/// <summary>开始一次练习。</summary>
public sealed record CreateSessionCommand(Guid UserId, string Title, SessionMode Mode,
    string? Topic, string? CompanyStyle, int TargetQuestionCount, string Language)
    : IRequest<Result<Guid>>;

public sealed class CreateSessionCommandValidator : AbstractValidator<CreateSessionCommand>
{
    public CreateSessionCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.TargetQuestionCount).InclusiveBetween(1, 20);
        RuleFor(x => x.Language).NotEmpty().MaximumLength(10);
        RuleFor(x => x.Topic).MaximumLength(200);
        RuleFor(x => x.CompanyStyle).MaximumLength(200);

        // 主题练习必须给主题,公司风格练习必须给公司 —— 否则 AI 无从出题
        RuleFor(x => x.Topic).NotEmpty()
            .When(x => x.Mode == SessionMode.TopicDrill)
            .WithMessage("主题练习必须指定 Topic");
        RuleFor(x => x.CompanyStyle).NotEmpty()
            .When(x => x.Mode == SessionMode.CompanyStyle)
            .WithMessage("公司风格练习必须指定 CompanyStyle");
    }
}

public sealed class CreateSessionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<CreateSessionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateSessionCommand r, CancellationToken ct)
    {
        var s = new MockSession(r.UserId, r.Title, r.Mode, r.Topic, r.CompanyStyle,
            r.TargetQuestionCount, r.Language);
        db.Sessions.Add(s);
        await db.SaveChangesAsync(ct);
        return Result.Success(s.Id);
    }
}

/// <summary>AI 出题:把生成的题追加进会话。</summary>
public sealed record AddQuestionsCommand(Guid SessionId, Guid UserId,
    IReadOnlyList<QuestionDraftIn> Questions) : IRequest<Result<IReadOnlyList<Guid>>>;

public sealed class AddQuestionsCommandValidator : AbstractValidator<AddQuestionsCommand>
{
    public AddQuestionsCommandValidator()
    {
        RuleFor(x => x.Questions).NotEmpty().WithMessage("至少要给一道题");
        RuleForEach(x => x.Questions).ChildRules(q =>
        {
            q.RuleFor(i => i.QuestionText).NotEmpty().MaximumLength(4000);
            q.RuleFor(i => i.Difficulty).InclusiveBetween(1, 5);
            q.RuleFor(i => i.Type).NotEmpty();
        });
    }
}

public sealed class AddQuestionsCommandHandler(AssessmentDbContext db)
    : IRequestHandler<AddQuestionsCommand, Result<IReadOnlyList<Guid>>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(AddQuestionsCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<IReadOnlyList<Guid>>(Error.NotFound("模拟练习"));

        var ids = new List<Guid>();
        try
        {
            foreach (var d in r.Questions)
            {
                var type = Enum.TryParse<QuestionType>(d.Type, true, out var t) ? t : QuestionType.Technical;
                var q = s.AddQuestion(d.QuestionText, type, d.ExpectedPointsJson, d.Difficulty);
                ids.Add(q.Id);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<IReadOnlyList<Guid>>(Error.Validation("Questions", ex.Message));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success<IReadOnlyList<Guid>>(ids);
    }
}

/// <summary>作答。</summary>
public sealed record AnswerQuestionCommand(Guid SessionId, Guid QuestionId, Guid UserId,
    string? AnswerText, string? AudioPath, double? DurationSeconds, string? TranscriptJson)
    : IRequest<Result<Guid>>;

public sealed class AnswerQuestionCommandValidator : AbstractValidator<AnswerQuestionCommand>
{
    public AnswerQuestionCommandValidator()
    {
        // 文本和音频至少有一个 —— 纯音频回答是完全合法的(练口述)
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.AnswerText) ||
                                  !string.IsNullOrWhiteSpace(x.AudioPath))
            .WithMessage("回答必须提供文本或音频至少一项");
    }
}

public sealed class AnswerQuestionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<AnswerQuestionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AnswerQuestionCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<Guid>(Error.NotFound("模拟练习"));

        try
        {
            s.AnswerQuestion(r.QuestionId, r.AnswerText, r.AudioPath, r.DurationSeconds, r.TranscriptJson);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Validation("Answer", ex.Message));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(r.QuestionId);
    }
}

/// <summary>评分:写回六维分数 + 明细问题 + 点评 + 推荐答案。</summary>
public sealed record ScoreQuestionCommand(Guid SessionId, Guid QuestionId, Guid UserId,
    int Pronunciation, int Fluency, int SentenceIntegrity, int Structure, int TechnicalDepth,
    int Relevance, string? Comment, string? RecommendedAnswer, string? BetterStructure,
    string? FillerWordsJson, IReadOnlyList<IssueDraftIn> Issues) : IRequest<Result>;

public sealed class ScoreQuestionCommandValidator : AbstractValidator<ScoreQuestionCommand>
{
    public ScoreQuestionCommandValidator()
    {
        // 六维全部 0-100。区间校验放在这里而不是只靠领域层 Clamp ——
        // Clamp 会让越界数据静默通过,而 AI 给出 120 分明显是错的,应该报错让人看见。
        RuleFor(x => x.Pronunciation).InclusiveBetween(0, 100);
        RuleFor(x => x.Fluency).InclusiveBetween(0, 100);
        RuleFor(x => x.SentenceIntegrity).InclusiveBetween(0, 100);
        RuleFor(x => x.Structure).InclusiveBetween(0, 100);
        RuleFor(x => x.TechnicalDepth).InclusiveBetween(0, 100);
        RuleFor(x => x.Relevance).InclusiveBetween(0, 100);
        RuleForEach(x => x.Issues).ChildRules(i =>
        {
            i.RuleFor(v => v.Dimension).NotEmpty().MaximumLength(50);
            i.RuleFor(v => v.Title).NotEmpty().MaximumLength(300);
            i.RuleFor(v => v.Severity).InclusiveBetween(1, 5);
        });
    }
}

public sealed class ScoreQuestionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<ScoreQuestionCommand, Result>
{
    public async Task<Result> Handle(ScoreQuestionCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.Include(x => x.Questions).ThenInclude(x => x.Issues)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<Guid>(Error.NotFound("模拟练习"));

        var scores = new DimensionScores(r.Pronunciation, r.Fluency, r.SentenceIntegrity,
            r.Structure, r.TechnicalDepth, r.Relevance);

        var issues = r.Issues.Select(i => new DimensionIssue(i.Dimension, i.Title, i.Detail,
            i.Evidence, i.Suggestion, i.Severity)).ToList();

        try
        {
            s.ScoreQuestion(r.QuestionId, scores, issues, r.Comment, r.RecommendedAnswer,
                r.BetterStructure, r.FillerWordsJson);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Validation("Score", ex.Message));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record SkipQuestionCommand(Guid SessionId, Guid QuestionId, Guid UserId)
    : IRequest<Result>;

public sealed class SkipQuestionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<SkipQuestionCommand, Result>
{
    public async Task<Result> Handle(SkipQuestionCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<Guid>(Error.NotFound("模拟练习"));

        try { s.SkipQuestion(r.QuestionId); }
        catch (InvalidOperationException ex) { return Result.Failure<Guid>(Error.Validation("Skip", ex.Message)); }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>结束练习并生成总评。</summary>
public sealed record CompleteSessionCommand(Guid SessionId, Guid UserId, string? OverallSummary,
    string? PriorityAction) : IRequest<Result<SessionSummaryResult>>;

public sealed record SessionSummaryResult(int? OverallScore, int ScoredQuestions,
    IReadOnlyList<WeaknessBucketDto> Weaknesses, string? WeakestDimension);

public sealed class CompleteSessionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<CompleteSessionCommand, Result<SessionSummaryResult>>
{
    public async Task<Result<SessionSummaryResult>> Handle(CompleteSessionCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.Include(x => x.Questions).ThenInclude(x => x.Issues)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<SessionSummaryResult>(Error.NotFound("模拟练习"));

        s.Complete(r.OverallSummary, r.PriorityAction);
        await db.SaveChangesAsync(ct);

        var ws = s.WeaknessSummary()
            .Select(w => new WeaknessBucketDto(w.Dimension, w.Count, Math.Round(w.AvgSeverity, 2)))
            .ToList();

        return Result.Success(new SessionSummaryResult(s.OverallScore,
            s.Questions.Count(q => q.Scores is not null), ws, ws.FirstOrDefault()?.Dimension));
    }
}

public sealed record AbandonSessionCommand(Guid SessionId, Guid UserId) : IRequest<Result>;

public sealed class AbandonSessionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<AbandonSessionCommand, Result>
{
    public async Task<Result> Handle(AbandonSessionCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<SessionSummaryResult>(Error.NotFound("模拟练习"));

        s.Abandon();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record DeleteSessionCommand(Guid SessionId, Guid UserId) : IRequest<Result>;

public sealed class DeleteSessionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<DeleteSessionCommand, Result>
{
    public async Task<Result> Handle(DeleteSessionCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<SessionSummaryResult>(Error.NotFound("模拟练习"));

        s.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// 从知识库薄弱点自动生成练习。
/// 这是"技术栈 → 模拟练习"的联动入口:把不会的题直接变成要开口答的题。
/// </summary>
public sealed record SuggestSessionCommand(Guid UserId, int QuestionCount = 5)
    : IRequest<Result<SuggestedSessionDto>>;

public sealed record SuggestedSessionDto(string Title, SessionMode Mode, string? Topic,
    int TargetQuestionCount, string Rationale);

public sealed class SuggestSessionCommandHandler(AssessmentDbContext db)
    : IRequestHandler<SuggestSessionCommand, Result<SuggestedSessionDto>>
{
    public async Task<Result<SuggestedSessionDto>> Handle(SuggestSessionCommand r, CancellationToken ct)
    {
        // 找出所有历史评分里出现最多的短板维度 —— 用它来决定练什么主题。
        var issues = await db.Sessions.AsNoTracking()
            .Where(x => x.UserId == r.UserId)
            .SelectMany(x => x.Questions)
            .SelectMany(q => q.Issues)
            .GroupBy(i => i.Dimension)
            .Select(g => new { Dimension = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);

        var top = issues.FirstOrDefault();
        var topic = top?.Dimension ?? "综合";
        var rationale = top is null
            ? "还没有历史练习记录,先从综合模拟起步建立基线分。"
            : $"历史评分里 \"{top.Dimension}\" 这一维扣分最多(共 {top.Count} 条问题),优先强攻。";

        return Result.Success(new SuggestedSessionDto(
            $"薄弱点强化 · {topic}", SessionMode.WeaknessFocus, topic,
            Math.Clamp(r.QuestionCount, 1, 20), rationale));
    }
}
