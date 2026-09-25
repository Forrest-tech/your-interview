using Microsoft.AspNetCore.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Web;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Jobs.Application;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Jobs.Api;

/// <summary>求职跟踪(Tracker)—— 模仿 Simplify.jobs 的数据与交互。</summary>
[ApiController]
[Route("api/jobs")]
[Authorize]
[Produces("application/json")]
public sealed class JobsController(ISender sender, ICurrentUser currentUser) : ControllerBase
{
    /// <summary>当前用户。简历是用户级资产(有 UserId),公司/投递是单租户空间(无 UserId)。</summary>
    private Guid Me => currentUser.UserId ?? Guid.Empty;

    // ---------- 公司 ----------

    [HttpGet("companies")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListCompanies([FromQuery] string? search, [FromQuery] bool includeBlacklisted = false, CancellationToken ct = default)
    {
        var r = await sender.Send(new ListCompaniesQuery(search, includeBlacklisted), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("companies/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetCompany(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetCompanyQuery(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("companies")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateCompany([FromBody] CreateCompanyCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command, ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/companies/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("companies/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateCompany(Guid id, [FromBody] UpdateCompanyBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateCompanyCommand(id, body.Name, body.Website, body.Industry,
            body.Location, body.LogoUrl, body.Notes, body.CompanyType, body.EmployeeCount, body.IsBlacklisted), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 设置公司情报长文本(2026-09-18:面试前准备包的输入)。
    /// 与 PUT companies/{id} 分开 —— 编辑基本资料不该覆盖已填好的长文本。
    /// </summary>
    [HttpPut("companies/{id:guid}/profile")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetCompanyProfile(Guid id, [FromBody] CompanyProfileBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetCompanyProfileCommand(id, body.Profile, body.ProfileSourcesJson), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("companies/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteCompany(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteCompanyCommand(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 投递记录 ----------

    [HttpGet("applications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListApplications([FromQuery] string? search, [FromQuery] string? status,
        [FromQuery] string? priority, [FromQuery] Guid? companyId, [FromQuery] string? sortBy = "updated",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var r = await sender.Send(new ListApplicationsQuery(search, status, priority, companyId, sortBy, page, pageSize), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("applications/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetApplication(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetApplicationQuery(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("applications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateApplication([FromBody] CreateApplicationCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command, ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateApplication(Guid id, [FromBody] UpdateApplicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateApplicationCommand(id, body.Role, body.Location, body.Link,
            body.Salary, body.WorkMode, body.Source, body.JdSummary, body.Notes, body.Priority, body.Deadline), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 设置 JD 全文与出处(2026-09-18:面试前准备包的输入)。
    /// 独立端点而非并入 PUT applications/{id} —— 粘贴 JD 是高频动作,
    /// 不该要求把 Role/Salary 等字段一起传(会互相覆盖)。
    /// </summary>
    [HttpPut("applications/{id:guid}/jd")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetJdContent(Guid id, [FromBody] JdContentBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetJdContentCommand(id, body.JdText, body.JdSourceUrl), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>状态流转(状态机校验在聚合内,非法流转返回 409)。</summary>
    [HttpPost("applications/{id:guid}/status")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ChangeStatus(Guid id, [FromBody] ChangeStatusBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ChangeApplicationStatusCommand(id, body.Status, body.Note, body.RejectionReason), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("applications/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteApplication(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteApplicationCommand(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>写入简历评分/通过率评估/匹配关键词(魁星的分析产出回填)。</summary>
    [HttpPut("applications/{id:guid}/analysis")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetAnalysis(Guid id, [FromBody] AnalysisBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetApplicationAnalysisCommand(id, body.ResumeScore, body.PassRateEstimate, body.MatchKeywords), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>记录 poster 名字 / 是否需先 Connect / 待发消息(2026-09-02 口径)。</summary>
    [HttpPut("applications/{id:guid}/outreach")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetOutreach(Guid id, [FromBody] OutreachBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetOutreachCommand(id, body.PosterName, body.NeedsConnectFirst, body.Message), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("applications/{id:guid}/outreach/sent")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> MarkOutreachSent(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new MarkOutreachSentCommand(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}/follow-up")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ScheduleFollowUp(Guid id, [FromBody] FollowUpBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ScheduleFollowUpCommand(id, body.At), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 面试轮次 ----------

    [HttpPost("applications/{id:guid}/rounds")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> AddRound(Guid id, [FromBody] AddRoundBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddInterviewRoundCommand(id, body.Stage, body.ScheduledDate,
            body.Interviewer, body.Format, body.Notes), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{id}/rounds/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}/rounds/{roundId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateRound(Guid id, Guid roundId, [FromBody] UpdateRoundBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateInterviewRoundCommand(id, roundId, body.Stage, body.ScheduledDate,
            body.Interviewer, body.Format, body.Outcome, body.Notes), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 统计 / 看板 ----------

    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetTrackerStatsQuery(), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("follow-ups")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> FollowUps([FromQuery] int days = 7, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetUpcomingFollowUpsQuery(days), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("deadlines")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> Deadlines([FromQuery] int days = 14, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetDeadlinesQuery(days), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>批量导入(job-tracker.json / Simplify 导出 / CSV 都能喂进来)。</summary>
    [HttpPost("applications/bulk-import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> BulkImport([FromBody] BulkImportApplicationsCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command, ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    public sealed record UpdateCompanyBody(string Name, string? Website, string? Industry, string? Location,
        string? LogoUrl, string? Notes, string CompanyType, int? EmployeeCount, bool IsBlacklisted);
    public sealed record UpdateApplicationBody(string Role, string? Location, string? Link, string? Salary,
        string? WorkMode, string? Source, string? JdSummary, string? Notes, string? Priority, DateOnly? Deadline);
    public sealed record ChangeStatusBody(string Status, string? Note, string? RejectionReason);
    /// <summary>JD 全文提交。空串由领域方法归一为 null。</summary>
    public sealed record JdContentBody(string? JdText, string? JdSourceUrl);

    /// <summary>公司情报提交。ProfileSourcesJson 是来源 URL 的 JSON 数组字符串。</summary>
    public sealed record CompanyProfileBody(string? Profile, string? ProfileSourcesJson);
    public sealed record AnalysisBody(int? ResumeScore, string? PassRateEstimate, string? MatchKeywords);
    public sealed record OutreachBody(string? PosterName, bool NeedsConnectFirst, string? Message);
    public sealed record FollowUpBody(DateTimeOffset? At);
    public sealed record AddRoundBody(string Stage, DateOnly? ScheduledDate, string? Interviewer, string? Format, string? Notes);
    public sealed record UpdateRoundBody(string? Stage, DateOnly? ScheduledDate, string? Interviewer,
        string? Format, string Outcome, string? Notes);

    // ======================= 简历正文(2026-09-18)=======================
    // 用途:简历匹配分析(简历 vs JD 关键词比对)+ 面试前准备包的输入。
    //
    // 为什么放 Jobs 而不是 Assessment:简历是求职资产 ——
    // 与 JD/公司/投递同属求职域,主要消费者是匹配分析。放这里让比对成为同库操作。

    /// <summary>
    /// 读当前用户的简历正文。
    /// 未设置时返回 resumeText = null(前端据此提示"先录入简历"),
    /// 而不是 404 —— 404 会被前端当成错误弹出提示条。
    /// </summary>
    [HttpGet("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetResumeText(CancellationToken ct)
    {
        var r = await sender.Send(new GetResumeQuery(Me), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(new
              {
                  resumeText = string.IsNullOrEmpty(r.Value.Content) ? null : r.Value.Content,
                  version = r.Value.Version,
                  updatedAt = r.Value.UpdatedAt
              })
            : r.ToProblemDetails();
    }

    /// <summary>存/覆盖当前用户的简历正文(版本号自增)。</summary>
    [HttpPut("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SaveResumeText([FromBody] ResumeTextBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveResumeCommand(Me, body.ResumeText ?? string.Empty), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(new { version = r.Value.Version, updatedAt = r.Value.UpdatedAt })
            : r.ToProblemDetails();
    }

    /// <summary>清空简历(回到未设置状态)。</summary>
    [HttpDelete("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteResumeText(CancellationToken ct)
    {
        var r = await sender.Send(new DeleteResumeCommand(Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 求职信(Cover Letter,2026-09-18) ----------

    /// <summary>读某条投递的求职信。未撰写时返回 200 + null(前端据此显示空编辑器)。</summary>
    [HttpGet("applications/{applicationId:guid}/cover-letter")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetCoverLetter(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new GetCoverLetterQuery(applicationId, Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>生成前的输入体检 —— 缺简历/JD/公司情报会明确指出,而不是等生成失败。</summary>
    [HttpGet("applications/{applicationId:guid}/cover-letter/readiness")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetCoverLetterReadiness(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new GetCoverLetterReadinessQuery(applicationId, Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>人工保存/覆盖求职信内容(不存在则创建)。</summary>
    [HttpPut("applications/{applicationId:guid}/cover-letter")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SaveCoverLetter(Guid applicationId, [FromBody] CoverLetterBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveCoverLetterCommand(applicationId, Me, body.Content ?? string.Empty), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// AI 生成求职信。输入 = 简历 + JD 全文 + 公司情报 + 可选额外要求。
    /// ⚠️ 长请求(几十秒)—— 前端需给足超时。
    /// </summary>
    [HttpPost("applications/{applicationId:guid}/cover-letter/generate")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> GenerateCoverLetter(Guid applicationId, [FromBody] GenerateCoverLetterBody? body, CancellationToken ct)
    {
        var r = await sender.Send(new GenerateCoverLetterCommand(
            applicationId, Me, body?.ExtraInstructions, body?.Overwrite ?? false), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>标记为已确认(可发出)。</summary>
    [HttpPost("applications/{applicationId:guid}/cover-letter/final")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> MarkCoverLetterFinal(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new MarkCoverLetterFinalCommand(applicationId, Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>删除求职信(幂等)。</summary>
    [HttpDelete("applications/{applicationId:guid}/cover-letter")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> DeleteCoverLetter(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteCoverLetterCommand(applicationId), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- M3:申请问答库(用户级) ----------

    [HttpGet("answer-templates")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListAnswerTemplates([FromQuery] string? category, CancellationToken ct)
    {
        var r = await sender.Send(new ListAnswerTemplatesQuery(Me, category), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("answer-templates")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateAnswerTemplate([FromBody] AnswerTemplateBody body, CancellationToken ct)
    {
        var r = await sender.Send(new CreateAnswerTemplateCommand(Me, body.Category, body.Question, body.Answer), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/answer-templates/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("answer-templates/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateAnswerTemplate(Guid id, [FromBody] AnswerTemplateBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateAnswerTemplateCommand(id, Me, body.Category, body.Question, body.Answer), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("answer-templates/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteAnswerTemplate(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteAnswerTemplateCommand(id, Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- M3:沟通记录(投递级) ----------

    [HttpGet("applications/{id:guid}/communications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListCommunications(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListCommunicationsQuery(id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("applications/{id:guid}/communications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateCommunication(Guid id, [FromBody] CommunicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new CreateCommunicationCommand(id, body.Type, body.Subject, body.Content,
            body.ContactName, body.ContactEmail, body.OccurredAt), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{id}/communications/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}/communications/{commId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateCommunication(Guid id, Guid commId, [FromBody] CommunicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateCommunicationCommand(commId, id, body.Type, body.Subject, body.Content,
            body.ContactName, body.ContactEmail, body.OccurredAt), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("applications/{id:guid}/communications/{commId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteCommunication(Guid id, Guid commId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteCommunicationCommand(commId, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    public sealed record AnswerTemplateBody(string Category, string Question, string Answer);

    public sealed record CommunicationBody(string Type, string? Subject, string Content,
        string? ContactName, string? ContactEmail, DateTimeOffset OccurredAt);

}

/// <summary>简历正文提交。空串由 Handler 归一为"清空"。</summary>
public sealed record ResumeTextBody(string? ResumeText);

/// <summary>求职信人工保存体。</summary>
public sealed record CoverLetterBody(string? Content);

/// <summary>求职信生成体。Overwrite=false 时已有内容不会被覆盖。</summary>
public sealed record GenerateCoverLetterBody(string? ExtraInstructions, bool Overwrite = false);
