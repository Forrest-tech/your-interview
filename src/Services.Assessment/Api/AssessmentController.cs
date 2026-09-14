using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Assessment.Api;

/// <summary>
/// AI 实战模拟 API。
///
/// 流程:建会话 → AI 出题 → 我作答 → 评分 → 总评。
/// 每一步都是显式端点,而不是"一次调用跑完全程"——
/// 因为真实练习是交互式的(答一题就想看一题反馈),不是批处理。
/// </summary>
[ApiController]
[Route("api/assessment")]
[Authorize]
public sealed class AssessmentController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    private Guid Me => currentUser.UserId ?? Guid.Empty;

    // ---------------- 会话 ----------------

    [HttpGet("sessions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null, [FromQuery] string? mode = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new ListSessionsQuery(Me, page, pageSize, status, mode), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("sessions/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetSessionQuery(id, Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("sessions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Create([FromBody] CreateSessionBody body, CancellationToken ct)
    {
        var mode = Enum.TryParse<SessionMode>(body.Mode, true, out var m) ? m : SessionMode.TopicDrill;
        var r = await sender.Send(new CreateSessionCommand(Me, body.Title, mode, body.Topic,
            body.CompanyStyle, body.TargetQuestionCount, body.Language ?? "en"), ct);
        return r.IsSuccess
            ? Results.Created($"/api/assessment/sessions/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpDelete("sessions/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteSessionCommand(id, Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>结束练习并生成总评。</summary>
    [HttpPost("sessions/{id:guid}/complete")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Complete(Guid id, [FromBody] CompleteBody body, CancellationToken ct)
    {
        var r = await sender.Send(new CompleteSessionCommand(id, Me, body.OverallSummary,
            body.PriorityAction), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("sessions/{id:guid}/abandon")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Abandon(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new AbandonSessionCommand(id, Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 题目 ----------------

    /// <summary>AI 出题(一次可追加多道)。</summary>
    [HttpPost("sessions/{id:guid}/questions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> AddQuestions(Guid id, [FromBody] AddQuestionsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddQuestionsCommand(id, Me, body.Questions), ct);
        return r.IsSuccess ? Results.Ok(new { ids = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>作答:提交文本或音频。</summary>
    [HttpPost("sessions/{id:guid}/questions/{questionId:guid}/answer")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Answer(Guid id, Guid questionId, [FromBody] AnswerBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new AnswerQuestionCommand(id, questionId, Me, body.AnswerText,
            body.AudioPath, body.DurationSeconds, body.TranscriptJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>评分:写回六维分数 + 明细问题 + 推荐答案。</summary>
    [HttpPost("sessions/{id:guid}/questions/{questionId:guid}/score")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Score(Guid id, Guid questionId, [FromBody] ScoreBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new ScoreQuestionCommand(id, questionId, Me,
            body.Pronunciation, body.Fluency, body.SentenceIntegrity, body.Structure,
            body.TechnicalDepth, body.Relevance, body.Comment, body.RecommendedAnswer,
            body.BetterStructure, body.FillerWordsJson, body.Issues ?? []), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("sessions/{id:guid}/questions/{questionId:guid}/skip")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockManage)]
    public async Task<IResult> Skip(Guid id, Guid questionId, CancellationToken ct)
    {
        var r = await sender.Send(new SkipQuestionCommand(id, questionId, Me), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 统计与联动 ----------------

    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetAssessmentStatsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>六维说明(雷达图轴定义 + 提升方法)。</summary>
    [HttpGet("dimensions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Dimensions(CancellationToken ct)
    {
        var r = await sender.Send(new GetDimensionsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>根据历史短板推荐下一次该练什么。</summary>
    [HttpGet("suggest")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.MockRead)]
    public async Task<IResult> Suggest([FromQuery] int questionCount = 5, CancellationToken ct = default)
    {
        var r = await sender.Send(new SuggestSessionCommand(Me, questionCount), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }
}

// ---------------- 请求体 ----------------

public sealed record CreateSessionBody(string Title, string Mode, string? Topic,
    string? CompanyStyle, int TargetQuestionCount = 5, string? Language = null);

public sealed record CompleteBody(string? OverallSummary, string? PriorityAction);

public sealed record AddQuestionsBody(IReadOnlyList<QuestionDraftIn> Questions);

public sealed record AnswerBody(string? AnswerText, string? AudioPath, double? DurationSeconds,
    string? TranscriptJson);

public sealed record ScoreBody(int Pronunciation, int Fluency, int SentenceIntegrity, int Structure,
    int TechnicalDepth, int Relevance, string? Comment, string? RecommendedAnswer,
    string? BetterStructure, string? FillerWordsJson, IReadOnlyList<IssueDraftIn>? Issues);
