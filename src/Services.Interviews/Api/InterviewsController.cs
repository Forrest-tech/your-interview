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
public sealed class InterviewsController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>当前用户(COD-01/票03):所有查询/命令显式带入,Handler 按 (Id, UserId) 收口。</summary>
    private Guid Me => currentUser.RequireUserId();

    // ---------------- 列表与详情 ----------------

    [HttpGet]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] Guid? companyId = null, [FromQuery] string? status = null,
        [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new ListEntriesQuery(Me, page, pageSize, companyId, status, search), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Get(Guid id, [FromQuery] Guid? ownerUserId = null,
        CancellationToken ct = default)
    {
        // ownerUserId:Worker 服务账号读路径(裁决 B)—— Handler 内 IgnoreQueryFilters + 校验条目归属
        var r = await sender.Send(new GetEntryQuery(Me, id, ownerUserId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>按公司聚合(前端左侧"公司树")。</summary>
    [HttpGet("companies")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Companies(CancellationToken ct)
    {
        var r = await sender.Send(new GetCompanySummariesQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>某公司的全部轮次(供 Tech Stack 来源选择:选轮次自动带日期)。</summary>
    [HttpGet("companies/{companyId:guid}/rounds")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> CompanyRounds(Guid companyId, CancellationToken ct)
    {
        var r = await sender.Send(new GetCompanyRoundsQuery(Me, companyId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>总览统计:状态分布、六维均分、短板 Top 类别、进步曲线。</summary>
    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetInterviewStatsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 条目 CRUD ----------------

    [HttpPost]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Create([FromBody] CreateEntryCommand body, CancellationToken ct)
    {
        var r = await sender.Send(body with { UserId = Me }, ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> Update(Guid id, [FromBody] UpdateEntryBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateEntryCommand(Me, id, body.CompanyName, body.Role,
            body.CompanyProfile, body.JdText, body.JdSummary, body.RoundNo, body.InterviewDate,
            body.InterviewFormat, body.Interviewers, body.Location, body.Result, body.Notes,
            body.QuestionsToAskJson, body.DebriefJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 回填 Tracker 投递关联(2026-10-21):详情页发现条目未关联、且按公司名+岗位
    /// 在 Tracker 里唯一匹配到一条投递时调用。已有关联不覆盖(additive)。
    /// </summary>
    [HttpPost("{id:guid}/link-application")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> LinkApplication(Guid id, [FromBody] LinkApplicationBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new LinkJobApplicationCommand(Me, id, body.JobApplicationId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsDelete)]
    public async Task<IResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteEntryCommand(Me, id), ct);
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

            Guid? roundId = form.TryGetValue("roundId", out var ridRaw)
                && Guid.TryParse(ridRaw.FirstOrDefault(), out var rid) ? rid : null;
            var cmd = new UploadAssetFileCommand(Me, id, file.FileName,
                string.IsNullOrWhiteSpace(file.ContentType) ? null : file.ContentType,
                duration, form["sourceLanguage"].FirstOrDefault(), file.OpenReadStream(),
                roundId, form["phase"].FirstOrDefault());

            var r = await sender.Send(cmd, ct);
            return r.IsSuccess
                ? Results.Created($"/api/interviews/{id}", new { id = r.Value })
                : r.ToProblemDetails();
        }

        // ---- 形态二:JSON(保持原有契约) ----
        var body = await Request.ReadFromJsonAsync<AttachAssetBody>(ct);
        if (body is null)
            return Results.Problem(title: "请求体为空", statusCode: StatusCodes.Status400BadRequest);

        // A6:纯文本资产 —— body 带 textContent 时按 Kind=Text 登记(粘贴文本按钮走这条)。
        // 文本内容直接就是转写稿,不走 STT;文件名缺省为 pasted-text.txt。
        var kind = body.Kind;
        var transcriptText = body.TranscriptText;
        var fileName = body.FileName;
        if (!string.IsNullOrWhiteSpace(body.TextContent))
        {
            kind = nameof(AssetKind.Text);
            transcriptText = body.TextContent;
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "pasted-text.txt";
        }

        var jr = await sender.Send(new AttachAssetCommand(Me, id, kind, fileName,
            body.ContentType, body.SizeBytes, body.StoragePath, body.BlobUrl, transcriptText,
            body.TranscriptSegmentsJson, body.DurationSeconds, body.SourceLanguage ?? "en",
            body.Sha256, body.RoundId, body.Phase, body.MetaJson), ct);
        return jr.IsSuccess
            ? Results.Created($"/api/interviews/{id}", new { id = jr.Value })
            : jr.ToProblemDetails();
    }

    /// <summary>回放:取某条录音材料的音频流。前端 &lt;audio src&gt; 直接指向这里。</summary>
    [HttpGet("{id:guid}/assets/{assetId:guid}/audio")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetAssetAudio(Guid id, Guid assetId, CancellationToken ct)
    {
        var r = await sender.Send(new GetAssetAudioQuery(Me, id, assetId), ct);
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
    public async Task<IResult> StartTranscription(Guid id, [FromQuery] Guid? ownerUserId = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new BeginTranscriptionCommand(Me, id, ownerUserId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 按材料开始转写(COD-07)—— 前端轮次页在**某一轮的录音**上点「开始转写」走这里。
    /// 与条目级入口共用同一命令/状态机/台账管线,只是把 assetId 透传下去精确选材:
    /// 条目级入口选的是全条目第一件录音,多轮条目会转错轮次的材料。
    /// 材料不存在或不是可转写的录音 → 409(命令的 InvalidOperationException 映射)。
    /// </summary>
    [HttpPost("{id:guid}/assets/{assetId:guid}/transcribe")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> StartAssetTranscription(Guid id, Guid assetId,
        [FromQuery] Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new BeginTranscriptionCommand(Me, id, ownerUserId, assetId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("{id:guid}/assets/{assetId:guid}/transcript")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SaveTranscript(Guid id, Guid assetId, [FromBody] TranscriptBody body,
        [FromQuery] Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new SaveTranscriptCommand(Me, id, assetId, body.FullText,
            body.SegmentsJson, ownerUserId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 读取转写稿 —— 供 Analysis.Worker 文本模式(粘贴文本、无音频)取文本。
    /// 返回 { fullText, segmentsJson }:segmentsJson 优先解析,为空/非法时用 fullText 切分。
    /// </summary>
    [HttpGet("{id:guid}/assets/{assetId:guid}/transcript")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetTranscript(Guid id, Guid assetId,
        [FromQuery] Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetTranscriptQuery(Me, id, assetId, ownerUserId), ct);
        return r.IsSuccess
            ? Results.Ok(new { fullText = r.Value.FullText, segmentsJson = r.Value.SegmentsJson })
            : r.ToProblemDetails();
    }

    // ---------------- 分析流水线 ----------------

    /// <summary>触发六维分析(校验通过后发集成事件给 Analysis.Worker)。</summary>
    [HttpPost("{id:guid}/analyze")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsAnalyze)]
    public async Task<IResult> Analyze(Guid id, [FromQuery] Guid? ownerUserId = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new RequestAnalysisCommand(Me, id, ownerUserId), ct);
        return r.IsSuccess ? Results.Accepted() : r.ToProblemDetails();
    }

    /// <summary>回写 AI 分析结果(内部 Worker 调用)。</summary>
    [HttpPost("{id:guid}/analysis")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsAnalyze)]
    public async Task<IResult> ApplyAnalysis(Guid id, [FromBody] ApplyAnalysisBody body,
        [FromQuery] Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var metricsJson = body.SpeechMetrics.HasValue
            ? body.SpeechMetrics.Value.GetRawText()
            : null;
        var r = await sender.Send(new ApplyAnalysisCommand(Me, id, body.Overall, body.Pronunciation,
            body.Fluency, body.Structure, body.TechnicalDepth, body.Relevance, body.Summary,
            body.Questions, body.Weaknesses, metricsJson, body.CandidateTranscript,
            OwnerUserId: ownerUserId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>标记分析失败(Worker 出错时调用,让前端看到可重试)。</summary>
    [HttpPost("{id:guid}/failure")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsAnalyze)]
    public async Task<IResult> MarkFailed(Guid id, [FromBody] FailureBody body,
        [FromQuery] Guid? ownerUserId = null, CancellationToken ct = default)
    {
        var r = await sender.Send(new MarkEntryFailedCommand(Me, id, body.Reason, ownerUserId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 问答(手工复盘) ----------------

    /// <summary>手工新增"面试官问题 + 我的回答"。</summary>
    [HttpPost("{id:guid}/questions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> AddQuestion(Guid id, [FromBody] AddQuestionBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddQuestionCommand(Me, id, body.QuestionText, body.MyAnswerText,
            body.Category ?? "Technical", body.Difficulty, body.Assessment,
            body.RoundId, body.AskedBy), ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{id}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}/questions/{questionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateQuestion(Guid id, Guid questionId, [FromBody] UpdateQuestionBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpdateQuestionCommand(Me, id, questionId, body.QuestionText,
            body.MyAnswerText, body.Category, body.Difficulty, body.Assessment, body.GotStuck,
            body.StuckReason, body.RecommendedAnswer), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}/questions/{questionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> RemoveQuestion(Guid id, Guid questionId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveQuestionCommand(Me, id, questionId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>A1:设置题目掌握度(0=待练,1=练习中,2=已掌握,3=还卡壳)。</summary>
    [HttpPut("{id:guid}/questions/{questionId:guid}/mastery")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SetQuestionMastery(Guid id, Guid questionId,
        [FromBody] SetQuestionMasteryBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateQuestionMasteryCommand(Me, id, questionId, body.Mastery), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 待办事项 Next actions(A2) ----------------

    /// <summary>A2:待办列表(未完成在前)。</summary>
    [HttpGet("{id:guid}/actions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListActions(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListActionsQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>A2:新增待办。</summary>
    [HttpPost("{id:guid}/actions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> CreateAction(Guid id, [FromBody] CreateActionBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new CreateActionCommand(Me, id, body.Title, body.Detail,
            body.DueDate), ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{id}/actions/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    /// <summary>A2:部分更新待办(null 字段不动)。</summary>
    [HttpPatch("{id:guid}/actions/{actionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateAction(Guid id, Guid actionId,
        [FromBody] UpdateActionBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateActionCommand(Me, id, actionId, body.Title,
            body.IsDone, body.DueDate), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}/actions/{actionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> DeleteAction(Guid id, Guid actionId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteActionCommand(Me, id, actionId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    // ---------------- 分析快照趋势(A3) ----------------

    /// <summary>A3:分析趋势 —— 该条目的快照按时间 asc,只返回 id/createdAt/overallScore。</summary>
    [HttpGet("{id:guid}/analysis/trend")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> AnalysisTrend(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetAnalysisTrendQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>A3:最新分析快照全文(结论 bullets + 维度 JSON)。无快照时返回 null。</summary>
    [HttpGet("{id:guid}/analysis/latest")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> LatestSnapshot(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetLatestSnapshotQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>A4:该题在其它面试中的回答版本(归一化题干精确匹配,排除自己)。</summary>
    [HttpGet("{id:guid}/questions/{questionId:guid}/history")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> QuestionHistory(Guid id, Guid questionId, CancellationToken ct)
    {
        var r = await sender.Send(new GetQuestionHistoryQuery(Me, id, questionId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>A5:全局技术覆盖图谱 —— 按 Category 聚合全部问答。</summary>
    [HttpGet("coverage")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Coverage(CancellationToken ct)
    {
        var r = await sender.Send(new GetCoverageQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>全局 upcoming:未来 30 天内所有条目的轮次(日历视图用)。</summary>
    [HttpGet("rounds/upcoming")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> UpcomingRounds([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetUpcomingRoundsQuery(Me, days), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>轮次通过概率:基于历史同 stage 通过率 + 准备度。</summary>
    [HttpGet("rounds/{roundId:guid}/pass-probability")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> RoundPassProbability(Guid roundId, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetRoundPassProbabilityQuery(Me, roundId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>整单 offer 概率:漏斗模型。</summary>
    [HttpGet("{id:guid}/offer-probability")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> OfferProbability(Guid id, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetOfferProbabilityQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>轮次:新增一轮(Order 自动递增)。</summary>
    [HttpPost("{id:guid}/rounds")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> AddRound(Guid id, [FromBody] AddRoundBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddRoundCommand(Me, id, body.Stage ?? "Technical",
            body.ScheduledDate, body.Interviewers, body.Format, body.Location,
            body.ScheduledTime), ct);
        return r.IsSuccess
            ? Results.Created($"/api/interviews/{id}/rounds/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("{id:guid}/rounds/{roundId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateRound(Guid id, Guid roundId, [FromBody] UpdateRoundBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpdateRoundCommand(Me, id, roundId, body.Stage, body.ScheduledDate,
            body.Interviewers, body.Format, body.Location, body.Outcome ?? "Pending",
            body.Notes, body.Feedback, body.MeetingLink, body.ScheduledTime,
            body.PrepQuestionsJson, body.EmailsJson, body.Transcript, body.RecordingUrl,
            body.ChecklistJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("{id:guid}/rounds/{roundId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> RemoveRound(Guid id, Guid roundId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveRoundCommand(Me, id, roundId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// A7:整体替换某轮次的邮件时间线 —— body { emails: [{from, subject, date, snippet}] },
    /// 写入 InterviewRound.EmailsJson。传空数组即清空。
    /// </summary>
    [HttpPut("{id:guid}/rounds/{roundId:guid}/emails")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateRoundEmails(Guid id, Guid roundId,
        [FromBody] UpdateRoundEmailsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateRoundEmailsCommand(Me, id, roundId, body.Emails), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 2026-10-19:生成某轮备考方案(AI),存为 Kind=PrepDoc / Phase=Pre 的轮次资产。
    /// 面试前输入 JD/公司/简历/机经/要求;下一轮自动带入之前所有轮次数据。
    /// </summary>
    [HttpPost("{id:guid}/rounds/{roundId:guid}/prep-plan")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> GeneratePrepPlan(Guid id, Guid roundId,
        [FromBody] GeneratePrepPlanBody body, CancellationToken ct)
    {
        var r = await sender.Send(new GeneratePrepPlanCommand(Me, id, roundId,
            body.JdText, body.ResumeText, body.Experiences, body.Requirements), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>2026-10-19:取某轮最新的备考方案(在线阅读)。</summary>
    [HttpGet("{id:guid}/rounds/{roundId:guid}/prep-plan")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> GetPrepPlan(Guid id, Guid roundId, CancellationToken ct)
    {
        var r = await sender.Send(new GetPrepPlanQuery(Me, id, roundId), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
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
            Me, id, req?.JdText, req?.ResumeText,
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
        var r = await sender.Send(new GetGuidanceQuery(Me, id, version), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>指导材料:版本列表(站内浏览切换用)。</summary>
    [HttpGet("{id:guid}/guidance/versions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListGuidanceVersions(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListGuidanceVersionsQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------------- 详情页 v2(2026-10-20 定稿) ----------------

    /// <summary>v2:改材料归属。body { roundId } —— null = 移到条目级(沟通记录)。</summary>
    [HttpPut("{id:guid}/assets/{assetId:guid}/assignment")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ReassignAsset(Guid id, Guid assetId,
        [FromBody] ReassignAssetBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ReassignAssetCommand(Me, id, assetId, body.RoundId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>v2:更新材料元数据 JSON(解析字段/截图引用/AI 摘要/提取安排/处理状态)。</summary>
    [HttpPut("{id:guid}/assets/{assetId:guid}/meta")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpdateAssetMeta(Guid id, Guid assetId,
        [FromBody] AssetMetaBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateAssetMetaCommand(Me, id, assetId, body.MetaJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>v2:删除一件材料(此前全站无此入口,材料页签补齐)。</summary>
    [HttpDelete("{id:guid}/assets/{assetId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> DeleteAsset(Guid id, Guid assetId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteAssetCommand(Me, id, assetId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>v2:保存逐题复盘 JSON(标准答案用户编辑也走这里)。</summary>
    [HttpPut("{id:guid}/questions/{questionId:guid}/review")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SetQuestionReview(Guid id, Guid questionId,
        [FromBody] ReviewJsonBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetQuestionReviewCommand(Me, id, questionId, body.ReviewJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>v2:保存整轮复盘汇总 JSON。</summary>
    [HttpPut("{id:guid}/rounds/{roundId:guid}/review")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SetRoundReview(Guid id, Guid roundId,
        [FromBody] ReviewJsonBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetRoundReviewCommand(Me, id, roundId, body.ReviewJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>v2:AI 生成整轮复盘(逐题四维 + 汇总),落库后返回汇总 JSON。</summary>
    [HttpPost("{id:guid}/rounds/{roundId:guid}/review/generate")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> GenerateRoundReview(Guid id, Guid roundId, CancellationToken ct)
    {
        var r = await sender.Send(new GenerateRoundReviewCommand(Me, id, roundId), ct);
        return r.IsSuccess ? Results.Ok(new { reviewJson = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>v2:从本轮转写抽取问答对生成题目(已有本轮题目时幂等跳过)。</summary>
    [HttpPost("{id:guid}/rounds/{roundId:guid}/questions/extract")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ExtractRoundQuestions(Guid id, Guid roundId, CancellationToken ct)
    {
        var r = await sender.Send(new ExtractRoundQuestionsCommand(Me, id, roundId), ct);
        return r.IsSuccess ? Results.Ok(new { created = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>v2:保存结构化备考报告 JSON(Tech Stack 关联回存走这里)。</summary>
    [HttpPut("{id:guid}/rounds/{roundId:guid}/prep-report")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> SetPrepReport(Guid id, Guid roundId,
        [FromBody] PrepReportBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetRoundPrepReportCommand(Me, id, roundId, body.PrepReportJson), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>v2:生成结构化备考报告(AI 优先,未配置时本地聚合兜底)。</summary>
    [HttpPost("{id:guid}/rounds/{roundId:guid}/prep-report/generate")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> GeneratePrepReport(Guid id, Guid roundId,
        [FromBody] GeneratePrepReportBody? body, CancellationToken ct)
    {
        var r = await sender.Send(new GeneratePrepReportCommand(Me, id, roundId,
            body?.JdText, body?.ResumeText, body?.Experiences, body?.Language), ct);
        return r.IsSuccess ? Results.Ok(new { prepReportJson = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>v2:说话人批量改名(本轮转写分段 + 题目提问人全篇生效)。</summary>
    [HttpPost("{id:guid}/rounds/{roundId:guid}/speakers/rename")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> RenameSpeaker(Guid id, Guid roundId,
        [FromBody] RenameSpeakerBody body, CancellationToken ct)
    {
        var r = await sender.Send(new RenameSpeakerCommand(Me, id, roundId, body.From, body.To), ct);
        return r.IsSuccess ? Results.Ok(new { changed = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>v2:导出本轮日历 .ics(提醒提前 1 天 + 提前 1 小时)。</summary>
    [HttpGet("{id:guid}/rounds/{roundId:guid}/ics")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> RoundIcs(Guid id, Guid roundId, CancellationToken ct)
    {
        var r = await sender.Send(new GetEntryQuery(Me, id), ct);
        if (!r.IsSuccess) return r.ToProblemDetails();
        var round = r.Value.Rounds.FirstOrDefault(x => x.Id == roundId);
        if (round is null)
            return Results.Problem(title: "轮次不存在", statusCode: StatusCodes.Status404NotFound);
        if (round.ScheduledDate is null)
            return Results.Problem(title: "本轮尚未安排日期,无法导出日历",
                statusCode: StatusCodes.Status400BadRequest);

        var date = round.ScheduledDate.Value;
        var start = TimeOnly.TryParse(round.ScheduledTime, out var t) ? t : new TimeOnly(10, 0);
        var dtStart = new DateTime(date.Year, date.Month, date.Day, start.Hour, start.Minute, 0);
        var dtEnd = dtStart.AddHours(1);
        string Fmt(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss");
        string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");
        var summary = Esc($"{r.Value.CompanyName} 面试 Round {round.Order} · {round.Stage}");
        var desc = Esc($"岗位:{r.Value.Role}\n面试官:{round.Interviewers ?? "-"}\n形式:{round.Format ?? "-"}" +
                       (string.IsNullOrWhiteSpace(round.MeetingLink) ? "" : $"\n会议链接:{round.MeetingLink}"));
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//YourInterview//Playbook//EN",
            "BEGIN:VEVENT",
            $"UID:{round.Id}@your-interview",
            $"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}",
            $"DTSTART:{Fmt(dtStart)}", $"DTEND:{Fmt(dtEnd)}",
            $"SUMMARY:{summary}", $"DESCRIPTION:{desc}",
            string.IsNullOrWhiteSpace(round.Location) ? "" : $"LOCATION:{Esc(round.Location!)}",
            "BEGIN:VALARM", "TRIGGER:-P1D", "ACTION:DISPLAY", $"DESCRIPTION:{summary}", "END:VALARM",
            "BEGIN:VALARM", "TRIGGER:-PT1H", "ACTION:DISPLAY", $"DESCRIPTION:{summary}", "END:VALARM",
            "END:VEVENT", "END:VCALENDAR", "");
        return Results.File(System.Text.Encoding.UTF8.GetBytes(ics), "text/calendar",
            $"{r.Value.CompanyName}-Round{round.Order}.ics");
    }

    /// <summary>
    /// 问答候选(缺口3):把该条目的问答转成候选列表,前端勾选后调
    /// POST /api/knowledge/import-candidates 导入 TechStack。
    /// </summary>
    [HttpGet("{id:guid}/question-candidates")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListQuestionCandidates(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListQuestionCandidatesQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>任务台账:该条目的分析任务历史(投递尝试/失败原因,排障第一入口)。</summary>
    [HttpGet("{id:guid}/jobs")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> Jobs(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListAnalysisJobsQuery(Me, id), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
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
    // ---------------- Playbook 本地数据(COD-10b/票15) ----------------
    // STAR 故事库 + 历史复盘/粘贴材料孤儿键归档。此前只存浏览器 localStorage,
    // 清缓存即丢;现落 Interviews 后端,前端首次加载一次性导入本地存量。
    // 删除故事用 InterviewsWrite(默认 User 角色无 InterviewsDelete,同票14
    // Jobs 面删自备问题用 JobsWrite 的先例)。

    /// <summary>当前用户的 STAR 故事库(按 SortOrder 升序)。</summary>
    [HttpGet("star-stories")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListStarStories(CancellationToken ct)
    {
        var r = await sender.Send(new ListStarStoriesQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>保存一个故事(按 clientId upsert,幂等)。</summary>
    [HttpPut("star-stories/{clientId}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> UpsertStarStory(string clientId, [FromBody] StarStoryBody body,
        CancellationToken ct)
    {
        var r = await sender.Send(new UpsertStarStoryCommand(Me, new StarStoryInput(
            clientId, body.Title ?? string.Empty, body.Situation, body.Task,
            body.Action, body.Result, body.Tags)), ct);
        return r.IsSuccess ? Results.Ok(new { id = r.Value }) : r.ToProblemDetails();
    }

    /// <summary>删除一个故事(软删,幂等)。</summary>
    [HttpDelete("star-stories/{clientId}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> DeleteStarStory(string clientId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteStarStoryCommand(Me, clientId), ct);
        return r.IsSuccess ? Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 一次性导入本地故事库(localStorage pb.starStories)。幂等(按 clientId
    /// 去重),返回对账 { imported, total } —— 重复导入 imported=0、total 不变。
    /// </summary>
    [HttpPost("star-stories/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ImportStarStories([FromBody] ImportStarStoriesBody body,
        CancellationToken ct)
    {
        var stories = (body.Stories ?? []).Select(s => new StarStoryInput(
            s.ClientId ?? string.Empty, s.Title ?? string.Empty, s.Situation, s.Task,
            s.Action, s.Result, s.Tags)).ToList();
        var r = await sender.Send(new ImportStarStoriesCommand(Me, stories), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>当前用户归档的历史复盘(原 pb.debrief.* 本地键,原始 JSON 逐字)。</summary>
    [HttpGet("legacy-debriefs")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListLegacyDebriefs(CancellationToken ct)
    {
        var r = await sender.Send(new ListLegacyDebriefsQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 一次性导入本地历史复盘(pb.debrief.&lt;后缀&gt;)。幂等(按归档键去重,
    /// 首导为准不覆盖),返回对账 { imported, total };票77 起后缀不要求是
    /// 现存条目 id —— 孤儿键以后缀原文归档。
    /// </summary>
    [HttpPost("legacy-debriefs/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ImportLegacyDebriefs([FromBody] ImportLegacyDebriefsBody body,
        CancellationToken ct)
    {
        var items = (body.Items ?? []).Select(i => new LegacyDebriefImportItem(
            i.EntryId ?? string.Empty, i.PayloadJson ?? string.Empty)).ToList();
        var r = await sender.Send(new ImportLegacyDebriefsCommand(Me, items), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>当前用户归档的历史粘贴材料(原 pb-pasted-* 本地键,原文逐字)。</summary>
    [HttpGet("legacy-pasted")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsRead)]
    public async Task<IResult> ListLegacyPasted(CancellationToken ct)
    {
        var r = await sender.Send(new ListLegacyPastedQuery(Me), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>一次性导入本地历史粘贴材料(pb-pasted-&lt;后缀&gt;),口径同复盘导入。</summary>
    [HttpPost("legacy-pasted/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.InterviewsWrite)]
    public async Task<IResult> ImportLegacyPasted([FromBody] ImportLegacyPastedBody body,
        CancellationToken ct)
    {
        var items = (body.Items ?? []).Select(i => new LegacyPastedImportItem(
            i.EntryId ?? string.Empty, i.Content ?? string.Empty)).ToList();
        var r = await sender.Send(new ImportLegacyPastedCommand(Me, items), ct);
        return r.IsSuccess ? Results.Ok(r.Value) : r.ToProblemDetails();
    }

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

public sealed record StarStoryBody(
    string? Title = null, string? Situation = null, string? Task = null,
    string? Action = null, string? Result = null, string? Tags = null);

public sealed record StarStoryImportItemBody(
    string? ClientId = null, string? Title = null, string? Situation = null,
    string? Task = null, string? Action = null, string? Result = null, string? Tags = null);

public sealed record ImportStarStoriesBody(List<StarStoryImportItemBody>? Stories);

public sealed record LegacyDebriefImportItemBody(string? EntryId = null, string? PayloadJson = null);

public sealed record ImportLegacyDebriefsBody(List<LegacyDebriefImportItemBody>? Items);

public sealed record LegacyPastedImportItemBody(string? EntryId = null, string? Content = null);

public sealed record ImportLegacyPastedBody(List<LegacyPastedImportItemBody>? Items);

public sealed record LinkApplicationBody(Guid JobApplicationId);

public sealed record UpdateEntryBody(
    string CompanyName, string Role, string? CompanyProfile, string? JdText, string? JdSummary,
    int RoundNo, DateOnly? InterviewDate, string? InterviewFormat, string? Interviewers,
    string? Location, string? Result, string? Notes, string? QuestionsToAskJson = null,
    string? DebriefJson = null);

public sealed record AttachAssetBody(
    string Kind, string FileName, string? ContentType = null, long SizeBytes = 0,
    string? StoragePath = null, string? BlobUrl = null, string? TranscriptText = null,
    string? TranscriptSegmentsJson = null, double? DurationSeconds = null,
    string? SourceLanguage = null, string? Sha256 = null, string? TextContent = null,
    // 2026-10-20 v2:RoundId 可空 = 条目级材料(沟通记录);MetaJson 承载解析字段等元数据。
    Guid? RoundId = null, string? Phase = null, string? MetaJson = null);

public sealed record GeneratePrepPlanBody(
    string? JdText = null, string? ResumeText = null, string? Experiences = null,
    string? Requirements = null);

// ---------------- 详情页 v2 请求体 ----------------

public sealed record ReassignAssetBody(Guid? RoundId);

public sealed record AssetMetaBody(string? MetaJson);

public sealed record ReviewJsonBody(string? ReviewJson);

public sealed record PrepReportBody(string? PrepReportJson);

public sealed record GeneratePrepReportBody(
    string? JdText = null, string? ResumeText = null, string? Experiences = null,
    string? Language = null);

public sealed record RenameSpeakerBody(string From, string To);

public sealed record TranscriptBody(string FullText, string? SegmentsJson = null);

public sealed record ApplyAnalysisBody(
    int Overall, int? Pronunciation, int Fluency, int Structure, int TechnicalDepth, int Relevance,
    string? Summary = null, List<QuestionDraft>? Questions = null,
    List<WeaknessDraft>? Weaknesses = null,
    System.Text.Json.JsonElement? SpeechMetrics = null,
    string? CandidateTranscript = null);

public sealed record FailureBody(string Reason);

public sealed record AddQuestionBody(
    string QuestionText, string? MyAnswerText = null, string? Category = null,
    int Difficulty = 3, string? Assessment = null,
    // 2026-10-20 v2:归轮 + 提问人。
    Guid? RoundId = null, string? AskedBy = null);

public sealed record AddRoundBody(string? Stage = null, DateOnly? ScheduledDate = null,
    string? Interviewers = null, string? Format = null, string? Location = null,
    string? ScheduledTime = null);

public sealed record UpdateRoundBody(
    string Stage, DateOnly? ScheduledDate, string? Interviewers, string? Format,
    string? Location, string? Outcome, string? Notes, string? Feedback,
    string? MeetingLink = null, string? ScheduledTime = null,
    string? PrepQuestionsJson = null, string? EmailsJson = null,
    string? Transcript = null, string? RecordingUrl = null,
    string? ChecklistJson = null);

public sealed record UpdateQuestionBody(
    string QuestionText, string? MyAnswerText, string Category, int Difficulty,
    string? Assessment, bool GotStuck, string? StuckReason, string? RecommendedAnswer);

/// <summary>A1:body { mastery: int }。</summary>
public sealed record SetQuestionMasteryBody(int Mastery);

/// <summary>A2:POST body { title, detail?, dueDate? }。</summary>
public sealed record CreateActionBody(
    string Title, string? Detail = null, DateTimeOffset? DueDate = null);

/// <summary>A2:PATCH body { isDone?, title?, dueDate? }(null 字段不动)。</summary>
public sealed record UpdateActionBody(
    string? Title = null, bool? IsDone = null, DateTimeOffset? DueDate = null);

/// <summary>A7:body { emails: [{from, subject, date, snippet}] }。</summary>
public sealed record UpdateRoundEmailsBody(List<RoundEmailItem> Emails);
