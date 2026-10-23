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
    string? SourcesJson,
    bool IsDue, int DueInDays,
    List<ReviewLogDto> ReviewLogs, List<RelationDto> Relations,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    // 票70:读时 xmin 并发令牌。详情编辑保存时原样回传(见 UpdateKnowledgeCommand.Version),
    // 服务端据此判过期 → DbUpdateConcurrencyException → 409 concurrency_conflict。
    // 仅详情 GET 填充真值;其余构造点缺省 0 表示未知。
    uint Version = 0);

public sealed record KnowledgeListItemDto(
    Guid Id, string Title, string Topic, string? SubTopic, string Mastery, string Source,
    int Difficulty, int Importance, int ReviewCount, DateTimeOffset? NextReviewAt,
    bool IsDue, string? SourceCompanyName, Guid? SourceInterviewEntryId, DateOnly? SourceDate,
    int? SourceRoundNo, string? SourceRoundStage, Guid? SourceJobApplicationId,
    string? SourcesJson, string? Question, string? MyAnswer, string? BetterAnswer,
    DateTimeOffset CreatedAt,
    // ★ 票113(K07 残余):条目行读时 xmin —— 批量删(逐项 DELETE)时 If-Match 回传钉版用
    //   (票111 只在详情 DTO 有 Version,列表项无版本可带,批量删仍走兼容 LWW)。
    //   仅列表查询填充真值;DuePlan/去重等非删除来源缺省 null = 不钉版。
    uint? Version = null);

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

