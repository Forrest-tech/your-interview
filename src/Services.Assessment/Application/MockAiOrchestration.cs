using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Ai;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Assessment.Application;

// =====================================================================================
//  票95:Mock 出题/评分后端编排。纯解析(MockAiParsing)与编排 handler 分离:
//  解析是纯逻辑、单测全谱覆盖;handler 只做 读会话 → 调 AiGateway(票38 单一客户端)
//  → 解析 → 经既有 AddQuestionsCommand / ScoreQuestionCommand 落库(复用其校验口径)。
//  AiGatewayException 不在此吞:上抛控制器映射 503(未配置)/502(上游失败)。
// =====================================================================================

public static class MockAiParsing
{
    public const int MaxTextLength = 4000;

    private sealed record GenItem(string? QuestionText, string? Type, int? Difficulty,
        JsonElement? ExpectedPoints);

    private sealed record GenWrapper(List<GenItem>? Questions);

    /// <summary>解析 AI 出题响应。解析不出任何有效题 → 空集(调用方按失败口径处理,零落库)。</summary>
    public static IReadOnlyList<QuestionDraftIn> ParseQuestions(string? raw, int maxCount)
    {
        if (maxCount <= 0) return [];
        var json = LlmJson.ExtractJson(raw);
        if (json is null) return [];

        List<GenItem>? items;
        try
        {
            using var doc = JsonDocument.Parse(json);
            items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<GenItem>>(json, LlmJsonOpts())
                : JsonSerializer.Deserialize<GenWrapper>(json, LlmJsonOpts())?.Questions;
        }
        catch (JsonException) { return []; }

        if (items is null) return [];

        var result = new List<QuestionDraftIn>();
        foreach (var it in items)
        {
            if (result.Count >= maxCount) break;
            var text = it.QuestionText?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > MaxTextLength) text = text[..MaxTextLength];

            var type = Enum.TryParse<QuestionType>(it.Type, true, out var t)
                ? t.ToString() : QuestionType.Technical.ToString();
            var difficulty = Math.Clamp(it.Difficulty ?? 3, 1, 5);

            string? expected = null;
            if (it.ExpectedPoints is { } ep && ep.ValueKind != JsonValueKind.Null)
            {
                expected = ep.ValueKind == JsonValueKind.String ? ep.GetString() : ep.GetRawText();
                if (expected is not null && expected.Length > MaxTextLength)
                    expected = expected[..MaxTextLength];
            }

            result.Add(new QuestionDraftIn(text, type, difficulty, expected));
        }
        return result;
    }

    public sealed record ParsedScore(int Pronunciation, int Fluency, int SentenceIntegrity,
        int Structure, int TechnicalDepth, int Relevance, string? Comment,
        string? RecommendedAnswer, string? BetterStructure, string? FillerWordsJson,
        IReadOnlyList<IssueDraftIn> Issues);

    private sealed record ScoreRaw(int? Pronunciation, int? Fluency, int? SentenceIntegrity,
        int? Structure, int? TechnicalDepth, int? Relevance, string? Comment,
        string? RecommendedAnswer, string? BetterStructure, JsonElement? FillerWords,
        List<IssueRaw>? Issues);

    private sealed record IssueRaw(string? Dimension, string? Title, string? Detail,
        string? Evidence, string? Suggestion, int? Severity);

    /// <summary>
    /// 解析 AI 评分响应。钳制口径(票95 STATUS 定稿):
    /// 越界分 → Clamp [0,100](与 DimensionScores 域内 Clamp 对齐);
    /// 缺维度 → 已给维度均值(取整)补齐;六维全缺/坏 JSON → null(评分失败,不落库)。
    /// </summary>
    public static ParsedScore? ParseScore(string? raw)
    {
        var json = LlmJson.ExtractJson(raw);
        if (json is null) return null;

        ScoreRaw? s;
        try { s = JsonSerializer.Deserialize<ScoreRaw>(json, LlmJsonOpts()); }
        catch (JsonException) { return null; }
        if (s is null) return null;

        var given = new[] { s.Pronunciation, s.Fluency, s.SentenceIntegrity, s.Structure,
            s.TechnicalDepth, s.Relevance }
            .Where(v => v.HasValue).Select(v => Math.Clamp(v!.Value, 0, 100)).ToList();
        if (given.Count == 0) return null;
        var fill = (int)Math.Round(given.Average());
        int V(int? v) => v.HasValue ? Math.Clamp(v.Value, 0, 100) : fill;

        var issues = new List<IssueDraftIn>();
        foreach (var i in s.Issues ?? [])
        {
            if (string.IsNullOrWhiteSpace(i.Dimension) || string.IsNullOrWhiteSpace(i.Title))
                continue;
            issues.Add(new IssueDraftIn(
                Trunc(i.Dimension!.Trim(), 50)!, Trunc(i.Title!.Trim(), 300)!,
                Trunc(i.Detail), Trunc(i.Evidence), Trunc(i.Suggestion),
                Math.Clamp(i.Severity ?? 3, 1, 5)));
        }

        string? filler = null;
        if (s.FillerWords is { } fw && fw.ValueKind != JsonValueKind.Null)
            filler = Trunc(fw.ValueKind == JsonValueKind.String ? fw.GetString() : fw.GetRawText());

        return new ParsedScore(V(s.Pronunciation), V(s.Fluency), V(s.SentenceIntegrity),
            V(s.Structure), V(s.TechnicalDepth), V(s.Relevance),
            Trunc(s.Comment), Trunc(s.RecommendedAnswer), Trunc(s.BetterStructure),
            filler, issues);
    }

    private static string? Trunc(string? v, int max = MaxTextLength)
        => v is null ? null : v.Length > max ? v[..max] : v;

    private static JsonSerializerOptions LlmJsonOpts() => new()
    { PropertyNameCaseInsensitive = true };

    public static string BuildGenerationPrompt(MockSession s, int count)
    {
        var modeHint = s.Mode switch
        {
            SessionMode.TopicDrill => $"Focus on the topic: {s.Topic}",
            SessionMode.CompanyStyle => $"Mimic the interview style of: {s.CompanyStyle}",
            SessionMode.WeaknessFocus => $"Focus on the weak area: {s.Topic}",
            _ => "Mix technical, system design and behavioral questions like a real interview loop."
        };
        return $$"""
            You are an interviewer. Generate exactly {{count}} interview questions for a mock practice session.
            Session title: {{s.Title}}
            Mode: {{s.Mode}}
            {{modeHint}}
            Language for questions: {{s.Language}}
            Respond with ONLY a JSON array, no prose. Each item:
            {"questionText":"...","type":"Technical|SystemDesign|Behavioral|Coding|ProjectDeepDive|ReverseQuestion","difficulty":1-5,"expectedPoints":["point1","point2"]}
            """;
    }

    public static string BuildScoringPrompt(MockQuestion q)
    {
        return $$"""
            You are a strict but fair interview coach. Score this answer on six dimensions (0-100 each):
            pronunciation, fluency, sentenceIntegrity, structure, technicalDepth, relevance.
            Question: {{q.QuestionText}}
            Candidate answer: {{q.AnswerText ?? "(audio answer, transcript unavailable)"}}
            Respond with ONLY a JSON object, no prose:
            {"pronunciation":0,"fluency":0,"sentenceIntegrity":0,"structure":0,"technicalDepth":0,"relevance":0,
             "comment":"...","recommendedAnswer":"...","betterStructure":"...",
             "issues":[{"dimension":"structure","title":"...","detail":"...","evidence":"...","suggestion":"...","severity":1-5}]}
            """;
    }
}

// ---------------- 编排命令 ----------------

public sealed record GenerateQuestionsCommand(Guid SessionId, Guid UserId)
    : IRequest<Result<int>>;

public sealed class GenerateQuestionsCommandHandler(AssessmentDbContext db,
    IAiGatewayClient ai, ISender sender)
    : IRequestHandler<GenerateQuestionsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(GenerateQuestionsCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking().Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure<int>(Error.NotFound("模拟练习"));
        if (s.Status is SessionStatus.Completed or SessionStatus.Abandoned)
            return Result.Failure<int>(Error.Validation("Mock.SessionClosed", "会话已结束,不能再出题"));

        var missing = s.TargetQuestionCount - s.Questions.Count;
        if (missing <= 0) return Result.Success(s.Questions.Count);

        var completion = await ai.CompleteAsync("mock-generate",
            MockAiParsing.BuildGenerationPrompt(s, missing),
            "You generate interview questions and reply with JSON only.", 0.7, null, ct);

        var drafts = MockAiParsing.ParseQuestions(completion.Text, missing);
        if (drafts.Count == 0)
            return Result.Failure<int>(new Error("Mock.AiInvalidResponse",
                "AI 返回内容无法解析为题目,未生成任何题目。可重试。", ErrorType.Failure));

        var added = await sender.Send(new AddQuestionsCommand(r.SessionId, r.UserId, drafts), ct);
        if (added.IsFailure) return Result.Failure<int>(added.Error);
        return Result.Success(s.Questions.Count + added.Value.Count);
    }
}

public sealed record ScoreQuestionWithAiCommand(Guid SessionId, Guid QuestionId, Guid UserId)
    : IRequest<Result>;

public sealed class ScoreQuestionWithAiCommandHandler(AssessmentDbContext db,
    IAiGatewayClient ai, ISender sender)
    : IRequestHandler<ScoreQuestionWithAiCommand, Result>
{
    public async Task<Result> Handle(ScoreQuestionWithAiCommand r, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking().Include(x => x.Questions).ThenInclude(x => x.Issues)
            .FirstOrDefaultAsync(x => x.Id == r.SessionId && x.UserId == r.UserId, ct);
        if (s is null) return Result.Failure(Error.NotFound("模拟练习"));

        var q = s.Questions.FirstOrDefault(x => x.Id == r.QuestionId);
        if (q is null) return Result.Failure(Error.NotFound("题目"));
        if (q.Scores is not null) return Result.Success();
        if (q.State != QuestionState.Answered)
            return Result.Failure(Error.Validation("Mock.NotAnswered", "该题尚未作答,不能评分"));

        var completion = await ai.CompleteAsync("mock-score",
            MockAiParsing.BuildScoringPrompt(q),
            "You score interview answers and reply with JSON only.", 0.3, null, ct);

        var parsed = MockAiParsing.ParseScore(completion.Text);
        if (parsed is null)
            return Result.Failure(new Error("Mock.AiInvalidResponse",
                "AI 返回内容无法解析为评分,未落库。可重试评分。", ErrorType.Failure));

        return await sender.Send(new ScoreQuestionCommand(r.SessionId, r.QuestionId, r.UserId,
            parsed.Pronunciation, parsed.Fluency, parsed.SentenceIntegrity, parsed.Structure,
            parsed.TechnicalDepth, parsed.Relevance, parsed.Comment, parsed.RecommendedAnswer,
            parsed.BetterStructure, parsed.FillerWordsJson, parsed.Issues), ct);
    }
}
