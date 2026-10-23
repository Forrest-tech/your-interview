using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

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
            k.SourcesJson,
            KnowledgeDue.IsDue(k, now), dueInDays,
            k.ReviewLogs.OrderByDescending(l => l.ReviewedAt).Select(l => l.ToDto()).ToList(),
            k.Relations.Select(r => r.ToDto()).ToList(),
            k.CreatedAt, k.UpdatedAt);
    }

    public static KnowledgeListItemDto ToListItemDto(this KnowledgeItem k, DateTimeOffset now) => new(
        k.Id, k.Title, k.Topic, k.SubTopic, k.Mastery.ToString(), k.Source.ToString(),
        k.Difficulty, k.Importance, k.ReviewCount, k.NextReviewAt, KnowledgeDue.IsDue(k, now),
        k.SourceCompanyName, k.SourceInterviewEntryId, k.SourceDate,
        k.SourceRoundNo, k.SourceRoundStage, k.SourceJobApplicationId, k.SourcesJson,
        k.Question, k.MyAnswer, k.BetterAnswer, k.CreatedAt);
}

