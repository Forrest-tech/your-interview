using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================ 查询 ============================

public sealed record ListKnowledgeQuery(
    Guid UserId, int Page = 1, int PageSize = 20, string? Topic = null, string? Mastery = null,
    string? Source = null, string? Tag = null, string? Search = null, bool DueOnly = false,
    string? Company = null, Guid? EntryId = null, bool IncludeDeleted = false)
    : IRequest<Result<PagedResult<KnowledgeListItemDto>>>;

public sealed record GetKnowledgeItemQuery(Guid UserId, Guid Id) : IRequest<Result<KnowledgeItemDto>>;

public sealed record GetTopicsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<TopicSummaryDto>>>;

public sealed record GetKnowledgeStatsQuery(Guid UserId) : IRequest<Result<KnowledgeStatsDto>>;

public sealed record GetDuePlanQuery(Guid UserId, int Days = 7) : IRequest<Result<IReadOnlyList<DuePlanDto>>>;

/// <summary>来源公司清单(需求 6.4.4:按"这题来自哪家公司"反查)。</summary>
public sealed record GetSourceCompaniesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<SourceCompanyDto>>>;

public sealed record SourceCompanyDto(string Company, int Count, int MasteredCount,
    int DueCount, DateOnly? LastDate);

/// <summary>SourcesJson 反序列化用(字段名与前端 KnowledgeSourceEntry 的 camelCase 对应)。</summary>
internal sealed class SourceEntryJson
{
    public string? CompanyName { get; set; }
    public string? Date { get; set; }
}

/// <summary>
/// 反查单条知识点的来源(需求 6.4.4)。
/// 除了自身来源信息,还带出"同一场面试一起导进来的其他题" ——
/// 复习时最有用的不是孤立的一题,而是"那一场我被问住的这一串"。
/// </summary>
public sealed record GetSourceTrailQuery(Guid UserId, Guid Id) : IRequest<Result<SourceTrailDto>>;

public sealed record SourceTrailDto(
    Guid ItemId, string? Company, DateOnly? Date, int? RoundNo, string? RoundStage,
    Guid? EntryId, Guid? ApplicationId, string? Role,
    IReadOnlyList<KnowledgeListItemDto> Siblings);

/// <summary>重复条目检测(需求 6.4.3 去重)。同分类 + 标题归一后相同即视为重复。</summary>
public sealed record FindDuplicatesQuery(Guid UserId, int MaxGroups = 50) : IRequest<Result<IReadOnlyList<DuplicateGroupDto>>>;

public sealed record DuplicateGroupDto(string Topic, string Key, IReadOnlyList<KnowledgeListItemDto> Items);

/// <summary>合并两条重复条目:把被合并方的复习次数与空字段并进保留方,再软删被合并方。</summary>
public sealed record MergeDuplicatesCommand(Guid UserId, Guid KeepId, Guid MergeId) : IRequest<Result>;

// ============================ 命令 ============================

public sealed record CreateKnowledgeCommand(
    Guid UserId, string Title, string Topic, string? SubTopic, string Question, string Source = "Personal",
    int Difficulty = 3, int Importance = 3, string? TagsJson = null,
    string? ConceptExplanation = null, string? MyAnswer = null, string? BetterAnswer = null,
    string? KeyPointsJson = null, string? CommonMistakesJson = null, string? FollowUpsJson = null,
    string? ReferencesJson = null, Guid? SourceInterviewEntryId = null,
    string? SourceCompanyName = null, DateOnly? SourceDate = null,
    int? SourceRoundNo = null, string? SourceRoundStage = null,
    Guid? SourceJobApplicationId = null) : IRequest<Result<Guid>>;

public sealed record UpdateKnowledgeCommand(
    Guid UserId, Guid Id, string Title, string Topic, string? SubTopic, string Question,
    int Difficulty, int Importance, string? TagsJson, string? ConceptExplanation,
    string? MyAnswer, string? BetterAnswer, string? KeyPointsJson, string? CommonMistakesJson,
    string? FollowUpsJson, string? ReferencesJson, string? SourcesJson = null) : IRequest<Result>;

/// <summary>只更新我的回答(Playbook Q&A 页签用,避免全量 PUT 误清空)。</summary>
public sealed record UpdateMyAnswerCommand(Guid UserId, Guid Id, string? MyAnswer) : IRequest<Result>;

/// <summary>只更新推荐答案(AI Practice From Tech 编辑写回用)。</summary>
public sealed record UpdateBetterAnswerCommand(Guid UserId, Guid Id, string? BetterAnswer) : IRequest<Result>;

/// <summary>只更新题目(AI Practice From Tech 重命名写回用)。</summary>
public sealed record UpdateQuestionCommand(Guid UserId, Guid Id, string? Question) : IRequest<Result>;

/// <summary>From Tech 一次写回题目+推荐答案(避免并发 xmin 冲突)。</summary>
public sealed record UpdateFromTechCommand(Guid UserId, Guid Id, string? Question, string? BetterAnswer) : IRequest<Result>;

public sealed record DeleteKnowledgeCommand(Guid UserId, Guid Id) : IRequest<Result>;
public sealed record RestoreKnowledgeCommand(Guid UserId, Guid Id) : IRequest<Result>;

/// <summary>重命名分类:返回受影响的条目数。新名字已存在时即为合并。</summary>
public sealed record RenameTopicCommand(Guid UserId, string OldName, string NewName) : IRequest<Result<int>>;

/// <summary>删除分类:名下条目的 Topic 置空。返回受影响的条目数。</summary>
public sealed record DeleteTopicCommand(Guid UserId, string Name) : IRequest<Result<int>>;

public sealed record RecordReviewCommand(
    Guid UserId, Guid Id, string Result, int? ConfidenceBefore = null, int? ConfidenceAfter = null,
    string? Note = null, int? DurationSeconds = null) : IRequest<Result<ReviewOutcomeDto>>;

public sealed record ReviewOutcomeDto(DateTimeOffset? NextReviewAt, int IntervalDays,
    double EasinessFactor, string Mastery, int RepetitionStreak);

public sealed record ChangeMasteryCommand(Guid UserId, Guid Id, string Mastery) : IRequest<Result>;

public sealed record AttachRelationCommand(Guid UserId, Guid Id, Guid RelatedItemId, string RelationType,
    string? Note = null) : IRequest<Result<Guid>>;

public sealed record ImportKnowledgeCommand(Guid UserId, List<ImportKnowledgeRow> Items) : IRequest<Result<int>>;

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
    Guid UserId, Guid? EntryId, string? Company, DateOnly? Date, int? RoundNo, string? RoundStage,
    Guid? ApplicationId, IReadOnlyList<InterviewCandidateRow> Items)
    : IRequest<Result<CandidateImportResultDto>>;

public sealed record InterviewCandidateRow(
    string Title, string Topic, string Question, string? SubTopic = null,
    int Difficulty = 3, int Importance = 3, string? ConceptExplanation = null,
    string? BetterAnswer = null, string? KeyPointsJson = null, string? TagsJson = null,
    string? ClientKey = null);

public sealed record CandidateImportResultDto(int Created, int Skipped, int Total, List<Guid> Ids);

