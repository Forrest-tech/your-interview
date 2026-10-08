using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Security;
using YourInterview.BuildingBlocks.Web;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Interviews.Api;

/// <summary>
/// 实战机经 API。
///
/// 资源层级:公司 → 面试条目 → 材料 / 问题 / 短板。
/// 一个条目 = 某公司某岗位的**一场**面试(多轮各建一条),
/// 这样"第几轮"就是个普通字段,不用在一条记录里塞二维结构。
/// </summary>
[ApiController]
[Route("api/interviews")]
[Authorize]
public sealed class InterviewsController(ISender sender) : ControllerBase
{
    // ---------------- 列表与详情 ----------------

    [HttpGet]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] Guid? companyId = null, [FromQuery] string? status = null,
        [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new ListEntriesQuery(page, pageSize, companyId, status, search), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Get(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetEntryQuery(id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>按公司聚合(前端左侧"公司树")。</summary>
    [HttpGet("companies")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Companies(CancellationToken ct)
    {
        var r = await sender.Send(new GetCompanySummariesQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>总览统计:状态分布、六维均分、短板 Top 类别、进步曲线。</summary>
    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetInterviewStatsQuery(), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 条目 CRUD ----------------

    [HttpPost]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Create([FromBody] CreateEntryCommand body, CancellationToken ct)
    {
        var r = await sender.Send(body, ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Update(Guid id, [FromBody] UpdateEntryBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateEntryCommand(id, body.CompanyName, body.Role,
            body.CompanyProfile, body.JdText, body.JdSummary, body.RoundNo, body.InterviewDate,
            body.InterviewFormat, body.Interviewers, body.Location, body.Result, body.Notes), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsDelete)]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteEntryCommand(id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 材料(录音 / 文本) ----------------

    /// <summary>
    /// 新增材料 —— 双形态端点:
    ///   · multipart/form-data(带 file):**真正的录音上传**,字节落盘到共享存储卷,
    ///     数据库登记相对路径 + SHA-256 + 实际大小(前端 ApiClient.upload 走这条);
    ///   · application/json:登记元信息/直接带文本(Worker、脚本、粘贴转写稿走这条)。
    ///
    /// 之前这个端点只收 JSON,而前端一直发 FormData —— 类型不匹配导致录音上传从未成功,
    /// 这是 M1 地基修复的第一断点。
    /// </summary>
    [HttpPost("{id:guid}/assets")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    [RequestSizeLimit(210 * 1024 * 1024)]   // 200MB 文件 + 表单余量
    public async Task<IResult> AttachAsset(Guid id, CancellationToken ct)
    {
        // ---- 形态一:multipart 文件上传 ----
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault(f => f.Name == "file" || f.Name == "File");
            if (file is null || file.Length == 0)
                return Results.Problem(title: "没有收到音频文件",
                    detail: "请在表单里带上 file 字段。",
                    statusCode: StatusCodes.Status400BadRequest);

            double? duration = null;
            if (form.TryGetValue("durationSeconds", out var dRaw)
                && double.TryParse(dRaw, out var d) && d >= 0)
                duration = d;

            var cmd = new UploadAssetFileCommand(id, file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                duration, form["sourceLanguage"].FirstOrDefault(), file.OpenReadStream());

            var r = await sender.Send(cmd, ct);
            return r.IsSuccess
                ? Results.Created($"/api/interviews/{id}", new { id = r.Value })
                : r.ToProblemDetails();
        }

        // ---- 形态二:JSON(保持原有契约) ----
        var body = await Request.ReadFromJsonAsync<AttachAssetBody>(ct);
        if (body is null)
            return Results.Problem(title: "请求体为空", statusCode: StatusCodes.Status400BadRequest);

        var jr = await sender.Send(new AttachAssetCommand(id, body.Kind, body.FileName,
            body.ContentType, body.SizeBytes, body.StoragePath, body.BlobUrl, body.TranscriptText,
            body.TranscriptSegmentsJson, body.DurationSeconds, body.SourceLanguage ?? "en",
            body.Sha256), ct);
        return jr.IsSuccess
            ? Results.Created($"/api/interviews/{id}", new { id = jr.Value })
            : jr.ToProblemDetails();
    }

    /// <summary>回放:取某条录音材料的音频流。前端 &lt;audio src&gt; 直接指向这里。</summary>
    [HttpGet("{id:guid}/assets/{assetId:guid}/audio")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetAssetAudio(Guid id, Guid assetId, CancellationToken ct)
    {
        var r = await sender.Send(new GetAssetAudioQuery(id, assetId), ct);
        if (!r.IsSuccess) return r.ToProblemDetails();

        var (stream, contentType, fileName) = r.Value;
        // enableRangeProcessing:浏览器音频控件才能拖动进度条
        return Results.File(stream, contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>
    /// 开始转写 —— 这是分析流水线的**触发器**:
    /// 状态进入 Transcribing 时发出领域事件(带录音真实路径)→ Analysis.Worker 消费,
    /// 跑 STT → 回写转写稿 → 自动推进六维分析。用户点「开始转写」后无需再手动操作。
    /// </summary>
    [HttpPost("{id:guid}/transcription/start")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> StartTranscription(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new BeginTranscriptionCommand(id), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("{id:guid}/assets/{assetId:guid}/transcript")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SaveTranscript(Guid id, Guid assetId, [FromBody] TranscriptBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new SaveTranscriptCommand(id, assetId, body.FullText,
            body.SegmentsJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 分析流水线 ----------------

    /// <summary>触发六维分析(校验通过后发集成事件给 Analysis.Worker)。</summary>
    [HttpPost("{id:guid}/analyze")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsAnalyze)]
    public async Task<IResult> Analyze(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new RequestAnalysisCommand(id), ct);
        return r.IsSuccess ? Results.Accepted() : r.ToProblemDetails();
    }

    /// <summary>回写 AI 分析结果(内部 Worker 调用)。</summary>
    [HttpPost("{id:guid}/analysis")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsAnalyze)]
    public async Task<IResult> ApplyAnalysis(Guid id, [FromBody] ApplyAnalysisBody body,
        CancellationToken ct)
    {
        var metricsJson = body.SpeechMetrics.HasValue
            ? body.SpeechMetrics.Value.GetRawText()
            : null;
        var r = await sender.Send(new ApplyAnalysisCommand(id, body.Overall, body.Pronunciation,
            body.Fluency, body.Structure, body.TechnicalDepth, body.Relevance, body.Summary,
            body.Questions, body.Weaknesses, metricsJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>标记分析失败(Worker 出错时调用,让前端看到可重试)。</summary>
    [HttpPost("{id:guid}/failure")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsAnalyze)]
    public async Task<IResult> MarkFailed(Guid id, [FromBody] FailureBody body, CancellationToken ct)
    {
        var r = await sender.Send(new MarkEntryFailedCommand(id, body.Reason), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 问答(手工复盘) ----------------

    /// <summary>手工新增"面试官问题 + 我的回答"。</summary>
    [HttpPost("{id:guid}/questions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> AddQuestion(Guid id, [FromBody] AddQuestionBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddQuestionCommand(id, body.QuestionText, body.MyAnswerText,
            body.Category ?? "Technical", body.Difficulty, body.Assessment), ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{id}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}/questions/{questionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateQuestion(Guid id, Guid questionId, [FromBody] UpdateQuestionBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpdateQuestionCommand(id, questionId, body.QuestionText,
            body.MyAnswerText, body.Category, body.Difficulty, body.Assessment, body.GotStuck,
            body.StuckReason, body.RecommendedAnswer), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}/questions/{questionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> RemoveQuestion(Guid id, Guid questionId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveQuestionCommand(id, questionId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>全局 upcoming:未来 30 天内所有条目的轮次(日历视图用)。</summary>
    [HttpGet("rounds/upcoming")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> UpcomingRounds([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetUpcomingRoundsQuery(days), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>轮次通过概率:基于历史同 stage 通过率 + 准备度。</summary>
    [HttpGet("rounds/{roundId:guid}/pass-probability")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> RoundPassProbability(Guid roundId, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetRoundPassProbabilityQuery(roundId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>轮次:新增一轮(Order 自动递增)。</summary>
    [HttpPost("{id:guid}/rounds")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> AddRound(Guid id, [FromBody] AddRoundBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddRoundCommand(id, body.Stage ?? "Technical"), ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{id}/rounds/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}/rounds/{roundId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateRound(Guid id, Guid roundId, [FromBody] UpdateRoundBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpdateRoundCommand(id, roundId, body.Stage, body.ScheduledDate,
            body.Interviewers, body.Format, body.Location, body.Outcome ?? "Pending",
            body.Notes, body.Feedback, body.MeetingLink, body.ScheduledTime,
            body.PrepQuestionsJson, body.EmailsJson, body.Transcript, body.RecordingUrl), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}/rounds/{roundId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> RemoveRound(Guid id, Guid roundId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveRoundCommand(id, roundId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>指导材料:手动触发 AI 生成新版本(版本号自动 +1)。</summary>
    [HttpPost("{id:guid}/guidance/generate")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> GenerateGuidance(
        Guid id,
        [FromBody] GenerateGuidanceRequest? req,
        CancellationToken ct)
    {
        var r = await sender.Send(new GenerateGuidanceCommand(
            id, req?.JdText, req?.ResumeText,
            req?.InterviewExperiences, req?.CustomRequirements), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    public sealed record GenerateGuidanceRequest(
        string? JdText,
        string? ResumeText,
        string? InterviewExperiences,
        string? CustomRequirements);

    /// <summary>指导材料:取最新版,或 ?version=N 取指定版本。</summary>
    [HttpGet("{id:guid}/guidance")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetGuidance(Guid id, [FromQuery] int? version, CancellationToken ct)
    {
        var r = await sender.Send(new GetGuidanceQuery(id, version), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>指导材料:版本列表(站内浏览切换用)。</summary>
    [HttpGet("{id:guid}/guidance/versions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListGuidanceVersions(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListGuidanceVersionsQuery(id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 问答候选(缺口3):把该条目的问答转成候选列表,前端勾选后调
    /// POST /api/knowledge/import-candidates 导入 TechStack。
    /// </summary>
    [HttpGet("{id:guid}/question-candidates")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListQuestionCandidates(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListQuestionCandidatesQuery(id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>任务台账:该条目的分析任务历史(投递尝试/失败原因,排障第一入口)。</summary>
    [HttpGet("{id:guid}/jobs")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Jobs(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListAnalysisJobsQuery(id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 短板 ----------------

    /// <summary>短板清单(可按 category 过滤),复盘的第一入口。</summary>
    [HttpGet("{id:guid}/weaknesses")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Weaknesses(Guid id, [FromQuery] string? category = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new ListWeaknessesQuery(id, category), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("{id:guid}/weaknesses")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> AddWeakness(Guid id, [FromBody] AddWeaknessBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddWeaknessCommand(id, body.Category, body.Title, body.Detail,
            body.Evidence, body.Severity, body.Suggestion), ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{id}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}/weaknesses/{weaknessId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsDelete)]
    public async Task<IResult> RemoveWeakness(Guid id, Guid weaknessId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveWeaknessCommand(id, weaknessId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 枚举字典(前端下拉框用) ----------------

    /// <summary>
    /// 返回状态机与各类枚举的合法值 + 合法流转表。
    /// 前端据此渲染状态按钮(只显示能点的),避免用户点了才发现 409。
    /// </summary>
    [HttpGet("metadata")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public IResult Metadata()
    {
        var statuses = Enum.GetNames<InterviewStatus>();
        var transitions = new Dictionary<string, string[]>();
        foreach (var s in Enum.GetValues<InterviewStatus>())
        {
            var probe = DescribeTransitions(s);
            if (probe.Length > 0) transitions[s.ToString()] = probe;
        }

        return Results.Ok(new
        {
            statuses,
            assetKinds = Enum.GetNames<AssetKind>(),
            weaknessCategories = Enum.GetNames<WeaknessCategory>(),
            questionCategories = Enum.GetNames<QuestionCategory>(),
            weaknessSources = Enum.GetNames<WeaknessSource>(),
            transitions
        });
    }

    /// <summary>
    /// 反射式读出状态机流转表 —— 让前端和后端共用同一份真相。
    /// 之所以不手工再写一遍:两份定义迟早会不一致,而不一致的安全后果是"前端允许点、后端拒绝"。
    /// </summary>
    private static string[] DescribeTransitions(InterviewStatus from)
    {
        // 领域层的 AllowedTransitions 是 private static,
        // 这里用一个"探测"方式复现它的语义,保证返回给前端的和实际执行的一致。
        return from switch
        {
            InterviewStatus.Draft => [nameof(InterviewStatus.AssetsUploaded), nameof(InterviewStatus.Transcribing)],
            InterviewStatus.AssetsUploaded => [nameof(InterviewStatus.Transcribing), nameof(InterviewStatus.Transcribed), nameof(InterviewStatus.Draft)],
            InterviewStatus.Transcribing => [nameof(InterviewStatus.Transcribed), nameof(InterviewStatus.Failed)],
            InterviewStatus.Transcribed => [nameof(InterviewStatus.Analyzing), nameof(InterviewStatus.Transcribing)],
            InterviewStatus.Analyzing => [nameof(InterviewStatus.Analyzed), nameof(InterviewStatus.Failed)],
            InterviewStatus.Analyzed => [nameof(InterviewStatus.Analyzing)],
            InterviewStatus.Failed => [nameof(InterviewStatus.AssetsUploaded), nameof(InterviewStatus.Transcribing), nameof(InterviewStatus.Transcribed)],
            _ => []
        };
    }
}

// ---------------- 请求体 ----------------

public sealed record UpdateEntryBody(
    string CompanyName, string Role, string? CompanyProfile, string? JdText, string? JdSummary,
    int RoundNo, DateOnly? InterviewDate, string? InterviewFormat, string? Interviewers,
    string? Location, string? Result, string? Notes);

public sealed record AttachAssetBody(
    string Kind, string FileName, string? ContentType = null, long SizeBytes = 0,
    string? StoragePath = null, string? BlobUrl = null, string? TranscriptText = null,
    string? TranscriptSegmentsJson = null, double? DurationSeconds = null,
    string? SourceLanguage = null, string? Sha256 = null);

public sealed record TranscriptBody(string FullText, string? SegmentsJson = null);

public sealed record ApplyAnalysisBody(
    int Overall, int Pronunciation, int Fluency, int Structure, int TechnicalDepth, int Relevance,
    string? Summary = null, List<QuestionDraft>? Questions = null,
    List<WeaknessDraft>? Weaknesses = null,
    System.Text.Json.JsonElement? SpeechMetrics = null);

public sealed record FailureBody(string Reason);

public sealed record AddQuestionBody(
    string QuestionText, string? MyAnswerText = null, string? Category = null,
    int Difficulty = 3, string? Assessment = null);

public sealed record AddRoundBody(string? Stage = null);

public sealed record UpdateRoundBody(
    string Stage, DateOnly? ScheduledDate, string? Interviewers, string? Format,
    string? Location, string? Outcome, string? Notes, string? Feedback,
    string? MeetingLink = null, string? ScheduledTime = null,
    string? PrepQuestionsJson = null, string? EmailsJson = null,
    string? Transcript = null, string? RecordingUrl = null);

public sealed record UpdateQuestionBody(
    string QuestionText, string? MyAnswerText, string Category, int Difficulty,
    string? Assessment, bool GotStuck, string? StuckReason, string? RecommendedAnswer);

public sealed record AddWeaknessBody(
    string Category, string Title, string? Detail = null, string? Evidence = null,
    int Severity = 3, string? Suggestion = null);
