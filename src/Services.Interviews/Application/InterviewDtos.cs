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

