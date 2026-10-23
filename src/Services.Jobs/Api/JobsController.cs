using Microsoft.AspNetCore.Authorization;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
public sealed class JobsController(ISender sender, ICurrentUser currentUser, ILogger<JobsController> logger) : ControllerBase
{
    /// <summary>当前用户(COD-01/票03):公司/投递/沟通均已按 UserId 隔离,未登录直接 401,不再回落 Guid.Empty。</summary>
    private Guid Me => currentUser.RequireUserId();

    // ---------- 公司 ----------

    [HttpGet("companies")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListCompanies([FromQuery] string? search, [FromQuery] bool includeBlacklisted = false, CancellationToken ct = default)
    {
        var r = await sender.Send(new ListCompaniesQuery(Me, search, includeBlacklisted), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("companies/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetCompany(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetCompanyQuery(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("companies")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateCompany([FromBody] CreateCompanyCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command with { UserId = Me }, ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/companies/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("companies/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateCompany(Guid id, [FromBody] UpdateCompanyBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateCompanyCommand(Me, id, body.Name, body.Website, body.Industry,
            body.Location, body.LogoUrl, body.Notes, body.CompanyType, body.EmployeeCount, body.IsBlacklisted, body.Version), ct);
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
        var r = await sender.Send(new SetCompanyProfileCommand(Me, id, body.Profile, body.ProfileSourcesJson, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("companies/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteCompany(Guid id, CancellationToken ct,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        var r = await sender.Send(new DeleteCompanyCommand(Me, id, ConcurrencyPin.ParseIfMatch(ifMatch)), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 投递记录 ----------

    [HttpGet("applications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListApplications([FromQuery] string? search, [FromQuery] string? status,
        [FromQuery] string? priority, [FromQuery] Guid? companyId, [FromQuery] string? sortBy = "updated",
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] bool? archived = null,
        CancellationToken ct = default)
    {
        var r = await sender.Send(new ListApplicationsQuery(Me, search, status, priority, companyId, sortBy, page, pageSize, archived), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("applications/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetApplication(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new GetApplicationQuery(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("applications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateApplication([FromBody] CreateApplicationCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command with { UserId = Me }, ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateApplication(Guid id, [FromBody] UpdateApplicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateApplicationCommand(Me, id, body.Role, body.Location, body.Link,
            body.Salary, body.WorkMode, body.Source, body.JdSummary, body.Notes, body.Priority, body.Deadline,
            body.AppliedDate, body.CompanyDescription, body.WorkAuthorization, null,
            body.Requirements, body.Benefits, body.JdText, body.Version), ct);
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
        var r = await sender.Send(new SetJdContentCommand(Me, id, body.JdText, body.JdSourceUrl, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>状态流转(状态机校验在聚合内,非法流转返回 409)。</summary>
    [HttpPost("applications/{id:guid}/status")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ChangeStatus(Guid id, [FromBody] ChangeStatusBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ChangeApplicationStatusCommand(Me, id, body.Status, body.Note, body.RejectionReason), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("applications/{id:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteApplication(Guid id, CancellationToken ct,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        var r = await sender.Send(new DeleteApplicationCommand(Me, id, ConcurrencyPin.ParseIfMatch(ifMatch)), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>归档申请 (Simplify 对等)。</summary>
    [HttpPost("applications/{id:guid}/archive")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ArchiveApplication(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ArchiveApplicationCommand(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>取消归档。</summary>
    [HttpPost("applications/{id:guid}/unarchive")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UnarchiveApplication(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new UnarchiveApplicationCommand(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>写入简历评分/通过率评估/匹配关键词(魁星的分析产出回填)。</summary>
    [HttpPut("applications/{id:guid}/analysis")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetAnalysis(Guid id, [FromBody] AnalysisBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetApplicationAnalysisCommand(Me, id, body.ResumeScore, body.PassRateEstimate, body.MatchKeywords, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>记录 poster 名字 / 是否需先 Connect / 待发消息(2026-09-02 口径)。</summary>
    [HttpPut("applications/{id:guid}/outreach")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetOutreach(Guid id, [FromBody] OutreachBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetOutreachCommand(Me, id, body.PosterName, body.NeedsConnectFirst, body.Message, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPost("applications/{id:guid}/outreach/sent")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> MarkOutreachSent(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new MarkOutreachSentCommand(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}/follow-up")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ScheduleFollowUp(Guid id, [FromBody] FollowUpBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ScheduleFollowUpCommand(Me, id, body.At, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 面试轮次 ----------

    [HttpPost("applications/{id:guid}/rounds")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> AddRound(Guid id, [FromBody] AddRoundBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddInterviewRoundCommand(Me, id, body.Stage, body.ScheduledDate,
            body.Interviewer, body.Format, body.Notes), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{id}/rounds/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}/rounds/{roundId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateRound(Guid id, Guid roundId, [FromBody] UpdateRoundBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateInterviewRoundCommand(Me, id, roundId, body.Stage, body.ScheduledDate,
            body.Interviewer, body.Format, body.Outcome, body.Notes, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- 统计 / 看板 ----------

    [HttpGet("stats")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> Stats(CancellationToken ct)
    {
        var r = await sender.Send(new GetTrackerStatsQuery(Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("action-center")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ActionCenter([FromQuery] int followUpDays = 7, [FromQuery] int deadlineDays = 14, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetActionCenterQuery(Me, followUpDays, deadlineDays), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("follow-ups")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> FollowUps([FromQuery] int days = 7, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetUpcomingFollowUpsQuery(Me, days), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpGet("deadlines")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> Deadlines([FromQuery] int days = 14, CancellationToken ct = default)
    {
        var r = await sender.Send(new GetDeadlinesQuery(Me, days), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>批量导入(job-tracker.json / Simplify 导出 / CSV 都能喂进来)。</summary>
    [HttpPost("applications/bulk-import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> BulkImport([FromBody] BulkImportApplicationsCommand command, CancellationToken ct)
    {
        var r = await sender.Send(command with { UserId = Me }, ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    // ---------- 智能粘贴(需求 6.2.2 / 6.2.3) ----------

    /// <summary>
    /// 解析粘贴内容(JD 链接 / JD 文本 / 图片)。
    /// 只返回草稿,不落库 —— 用户要在界面上确认并改过再保存(需求明确要求"可编辑后再存")。
    /// </summary>
    [HttpPost("parse-jd")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ParseJd([FromBody] ParseJdBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ParseJdCommand(body.Raw ?? string.Empty, body.UseAi), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 2026-10-19:LinkedIn 职位页自动抓取 —— 用用户登录态抓页面 HTML，
    /// 转结构化文本后走规则解析，bullet 结构完整保留。
    /// 票47(COD-20):失败统一 502 ProblemDetails(code 区分需要登录/一般失败),
    /// 不再 200 + 异常原文;成功路径响应体不变。
    /// </summary>
    [HttpPost("fetch-linkedin")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> FetchLinkedIn(
        [FromBody] FetchLinkedInBody body,
        [FromServices] Application.LinkedInFetcher fetcher,
        CancellationToken ct)
    {
        var url = body.Url?.Trim() ?? string.Empty;
        if (!url.Contains("linkedin.com/jobs/view/"))
            return Microsoft.AspNetCore.Http.Results.Problem(
                title: "Not a LinkedIn job URL",
                detail: "Only LinkedIn job page URLs (linkedin.com/jobs/view/...) can be fetched.",
                statusCode: Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?>
                    { ["code"] = Application.LinkedInFetchFailure.NotLinkedInJobUrlCode });

        try
        {
            var (ok, html, err) = await fetcher.FetchJobHtmlAsync(url, ct);
            if (!ok)
            {
                // 票47(COD-20):失败不再 200 + 原文 —— 统一 502 ProblemDetails;
                // fetcher 的原始 err(含异常原文)只进服务端日志,绝不下发。
                var failKind = Application.LinkedInFetchFailure.Classify(err);
                if (failKind == Application.LinkedInFetchFailure.Kind.FetchFailed)
                    logger.LogWarning("LinkedIn 抓取失败: {Url} 原始错误: {Error}", url, err);
                return FetchProblem(failKind);
            }

            // HTML → 规则解析（保留 bullet）
            var kind = Application.JdPasteParser.DetectKind(html!);
            var parsed = Application.JdPasteParser.Parse(html!, kind);
            return Microsoft.AspNetCore.Http.Results.Ok(new
            {
                ok = true,
                parsed.SourceKind,
                parsed.Status,
                parsed.Company,
                parsed.Role,
                parsed.Location,
                parsed.Salary,
                parsed.WorkMode,
                parsed.JobType,
                parsed.JdSummary,
                parsed.Requirements,
                parsed.Benefits,
                parsed.CompanyDescription,
                parsed.Link,
                parsed.LogoUrl,
                // 2026-10-21:此前这里直接回原始整页 HTML,前端存进 jdText 后在
                // Playbook/Tracker 展示成标签糊脸;改回解析后的结构化纯文本。
                JdText = parsed.JdText
            });
        }
        catch (Exception ex)
        {
            // 票47(COD-20):异常原文只进日志;对前端统一 502 ProblemDetails
            // (改前为 200 + "fetch_error: " + ex.Message,内部细节外泄且监控无感)。
            logger.LogWarning(ex, "LinkedIn 抓取异常: {Url}", url);
            return FetchProblem(Application.LinkedInFetchFailure.Kind.FetchFailed);
        }
    }

    /// <summary>票47(COD-20):抓取失败的统一 ProblemDetails 出口(502 + code 扩展,前端按 code 分流提示)。</summary>
    private static IResult FetchProblem(Application.LinkedInFetchFailure.Kind kind)
        => Microsoft.AspNetCore.Http.Results.Problem(
            title: Application.LinkedInFetchFailure.TitleFor(kind),
            detail: Application.LinkedInFetchFailure.DetailFor(kind),
            statusCode: Application.LinkedInFetchFailure.StatusCode,
            extensions: new Dictionary<string, object?>
                { ["code"] = Application.LinkedInFetchFailure.CodeFor(kind) });

    /// <summary>
    /// 一键建档:把(用户改过的)解析结果落成公司与投递记录。
    /// 公司按名解析,不存在则新建 —— 粘贴入口不该要求用户先去建公司。
    /// </summary>
    [HttpPost("applications/smart-add")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SmartAddApplication([FromBody] SmartAddApplicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SmartAddApplicationCommand(Me, body.Company, body.Role, body.Location,
            body.Salary, body.WorkMode, body.JobType, body.JdSummary, body.MatchKeywords, body.Priority,
            body.Link, body.JdText, body.Source, body.Status, body.Deadline,
            body.CompanyDescription, body.WorkAuthorization, body.Requirements, body.Benefits), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(r.Value)
            : r.ToProblemDetails();
    }

    /// <summary>解析面试邀请邮件/消息(时间、地点、形式、面试官、轮次)。</summary>
    [HttpPost("parse-invite")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ParseInvite([FromBody] ParseJdBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ParseInviteCommand(body.Raw ?? string.Empty, body.UseAi), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>
    /// 一键关联邀请:登记面试轮次并推进状态(同时触发机经草稿创建)。
    /// </summary>
    [HttpPost("applications/{id:guid}/invite")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> LinkInvite(Guid id, [FromBody] LinkInviteBody body, CancellationToken ct)
    {
        var r = await sender.Send(new LinkInviteCommand(Me, id, body.Stage, body.ScheduledDate,
            body.Interviewer, body.Format, body.Notes), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    public sealed record UpdateCompanyBody(string Name, string? Website, string? Industry, string? Location,
        string? LogoUrl, string? Notes, string CompanyType, int? EmployeeCount, bool IsBlacklisted,
        uint? Version = null); // 票101:读时 xmin 回传钉版
    public sealed record UpdateApplicationBody(string Role, string? Location, string? Link, string? Salary,
        string? WorkMode, string? Source, string? JdSummary, string? Notes, string? Priority, DateOnly? Deadline,
        DateOnly? AppliedDate, string? CompanyDescription, string? WorkAuthorization,
        string? Requirements = null, string? Benefits = null,
        // FUN-02:编辑负载补齐 JD 全文。不带该字段 (null) 时后端保持原值,见 UpdateApplicationCommand。
        string? JdText = null,
        uint? Version = null); // 票101:同 UpdateCompanyBody
    public sealed record ChangeStatusBody(string Status, string? Note, string? RejectionReason);
    /// <summary>JD 全文提交。空串由领域方法归一为 null。</summary>
    public sealed record JdContentBody(string? JdText, string? JdSourceUrl, uint? Version = null); // 票101 Version:同上

    /// <summary>公司情报提交。ProfileSourcesJson 是来源 URL 的 JSON 数组字符串。</summary>
    public sealed record CompanyProfileBody(string? Profile, string? ProfileSourcesJson, uint? Version = null); // 票101
    public sealed record AnalysisBody(int? ResumeScore, string? PassRateEstimate, string? MatchKeywords, uint? Version = null); // 票101
    public sealed record OutreachBody(string? PosterName, bool NeedsConnectFirst, string? Message, uint? Version = null); // 票101
    public sealed record FollowUpBody(DateTimeOffset? At, uint? Version = null); // 票101
    public sealed record AddRoundBody(string Stage, DateOnly? ScheduledDate, string? Interviewer, string? Format, string? Notes);
    public sealed record UpdateRoundBody(string? Stage, DateOnly? ScheduledDate, string? Interviewer,
        string? Format, string Outcome, string? Notes, uint? Version = null); // 票101:轮次行 xmin

    // ---- 智能粘贴请求体 ----

    /// <summary>粘贴原文。UseAi=false 时只走规则解析(未配 AI 凭据也能用)。</summary>
    public sealed record ParseJdBody(string? Raw, bool UseAi = true);
    public sealed record FetchLinkedInBody(string? Url);

    /// <summary>解析结果(经用户改过)提交建档。</summary>
    public sealed record SmartAddApplicationBody(
        string? Company, string? Role, string? Location, string? Salary, string? WorkMode, string? JobType,
        string? JdSummary, string? MatchKeywords, string? Priority, string? Link,
        string? JdText, string? Source, string? Status, DateOnly? Deadline = null,
        string? CompanyDescription = null, string? WorkAuthorization = null,
        string? Requirements = null, string? Benefits = null);

    public sealed record LinkInviteBody(string? Stage, DateOnly? ScheduledDate, string? Interviewer,
        string? Format, string? Notes);

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
                  rowVersion = r.Value.RowVersion,
                  updatedAt = r.Value.UpdatedAt
              })
            : r.ToProblemDetails();
    }

    /// <summary>存/覆盖当前用户的简历正文(版本号自增)。</summary>
    [HttpPut("resume-text")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SaveResumeText([FromBody] ResumeTextBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SaveResumeCommand(Me, body.ResumeText ?? string.Empty, body.RowVersion), ct);
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

    // ---------- 简历 PDF 文件(2026-10-19) ----------
    //
    // 为什么文件和正文分两个端点:
    //   正文(text)是匹配分析/求职信生成的输入 —— 读文本不需要拖几 MB 的 PDF;
    //   PDF 是给人看的(Documents tab 预览)。分开存、分开读,各走各的轻量路径。
    //
    // 去重(Forrest 定夺):某条投递没传 tailored 时,Documents 直接引用基础简历,
    // 不复制。GET applications/{id}/resume-file 会自动回落到基础文件。

    /// <summary>
    /// 上传基础简历 PDF(/resume 页)。
    /// multipart/form-data:file=PDF,textContent=前端 pdf.js 提取的文本(评分/求职信用)。
    /// 文本会同步写进 resumes 表 —— 老的文本消费者(求职信生成等)不受影响。
    /// </summary>
    [HttpPost("resume-file")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IResult> UploadResumeFile(
        IFormFile file, [FromForm] string? textContent, CancellationToken ct,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        if (file is null || file.Length == 0)
            return Microsoft.AspNetCore.Http.Results.Problem(title: "没有收到文件",
                detail: "请选择 PDF 简历再上传。", statusCode: StatusCodes.Status400BadRequest);
        if (!IsPdf(file))
            return Microsoft.AspNetCore.Http.Results.Problem(title: "只支持 PDF",
                detail: "请上传 .pdf 文件。", statusCode: StatusCodes.Status400BadRequest);

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var r = await sender.Send(new SaveResumeFileCommand(Me, null, file.FileName,
            file.ContentType, ms.ToArray(), textContent ?? string.Empty,
            ConcurrencyPin.ParseIfMatch(ifMatch)), ct);
        if (!r.IsSuccess) return r.ToProblemDetails();

        // 文本同步进 resumes 表 —— 求职信生成/匹配分析的老链路继续走文本,不碰 PDF。
        if (!string.IsNullOrWhiteSpace(textContent))
            await sender.Send(new SaveResumeCommand(Me, textContent), ct);

        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            fileName = r.Value.FileName,
            size = r.Value.Size,
            updatedAt = r.Value.UpdatedAt ?? r.Value.CreatedAt
        });
    }

    /// <summary>基础简历 PDF 元信息(文件名/大小/更新时间)—— 刷新页面后前端仍能显示文件名。</summary>
    [HttpGet("resume-file/meta")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetResumeFileMeta(CancellationToken ct)
    {
        var r = await sender.Send(new GetResumeFileQuery(Me, null), ct);
        if (!r.IsSuccess) return r.ToProblemDetails();
        if (r.Value is null) return Microsoft.AspNetCore.Http.Results.NotFound();
        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            fileName = r.Value.FileName,
            size = r.Value.Data.LongLength,
            updatedAt = r.Value.UpdatedAt ?? r.Value.CreatedAt
        });
    }

    /// <summary>下载基础简历 PDF。</summary>
    [HttpGet("resume-file")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> DownloadResumeFile(CancellationToken ct)
    {
        var r = await sender.Send(new GetResumeFileQuery(Me, null), ct);
        if (!r.IsSuccess) return r.ToProblemDetails();
        if (r.Value is null) return Microsoft.AspNetCore.Http.Results.NotFound();
        return Results.File(r.Value.Data, r.Value.ContentType, r.Value.FileName);
    }

    /// <summary>删除基础简历 PDF(文本记录不动 —— /resume 页的"清空"才清文本)。</summary>
    [HttpDelete("resume-file")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteResumeFile(CancellationToken ct)
    {
        var r = await sender.Send(new DeleteResumeFileCommand(Me, null), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 某条投递的有效简历元信息。kind=tailored(投递版本) / base(引用基础文件) /
    /// text(REQ-05 回落:主简历只有文本版) / none(都没有)。
    /// textContent 供 Resume Score 按投递版本算分 —— 有 tailored 就用 tailored 的文本;
    /// kind=text 时为文本简历正文,fileName/size 为 null(无文件可下载)。
    /// </summary>
    [HttpGet("applications/{applicationId:guid}/resume")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> GetApplicationResume(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new GetEffectiveResumeQuery(Me, applicationId), ct);
        if (!r.IsSuccess) return r.ToProblemDetails();
        var v = r.Value;
        var rawText = v.File?.TextContent ?? v.TextContent;
        return Microsoft.AspNetCore.Http.Results.Ok(new
        {
            kind = v.Kind,
            fileName = v.File?.FileName,
            size = v.File?.Size,
            textContent = string.IsNullOrWhiteSpace(rawText) ? null : rawText
        });
    }

    /// <summary>
    /// 下载某条投递的有效简历 PDF —— 有 tailored 下 tailored,没有回落基础(去重:不复制)。
    /// </summary>
    [HttpGet("applications/{applicationId:guid}/resume-file")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> DownloadApplicationResumeFile(Guid applicationId, CancellationToken ct)
    {
        var tailored = await sender.Send(new GetResumeFileQuery(Me, applicationId), ct);
        if (!tailored.IsSuccess) return tailored.ToProblemDetails();
        var file = tailored.Value
            ?? (await sender.Send(new GetResumeFileQuery(Me, null), ct)).Value;
        if (file is null) return Microsoft.AspNetCore.Http.Results.NotFound();
        return Results.File(file.Data, file.ContentType, file.FileName);
    }

    /// <summary>
    /// 上传某条投递的 tailored 简历 PDF(Documents tab)。
    /// multipart/form-data:file=PDF,textContent=提取文本(评分用)。
    /// </summary>
    [HttpPost("applications/{applicationId:guid}/resume")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IResult> UploadApplicationResume(Guid applicationId,
        IFormFile file, [FromForm] string? textContent, CancellationToken ct,
        [FromHeader(Name = "If-Match")] string? ifMatch = null)
    {
        if (file is null || file.Length == 0)
            return Microsoft.AspNetCore.Http.Results.Problem(title: "没有收到文件",
                detail: "请选择 PDF 简历再上传。", statusCode: StatusCodes.Status400BadRequest);
        if (!IsPdf(file))
            return Microsoft.AspNetCore.Http.Results.Problem(title: "只支持 PDF",
                detail: "请上传 .pdf 文件。", statusCode: StatusCodes.Status400BadRequest);

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var r = await sender.Send(new SaveResumeFileCommand(Me, applicationId, file.FileName,
            file.ContentType, ms.ToArray(), textContent ?? string.Empty,
            ConcurrencyPin.ParseIfMatch(ifMatch)), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Ok(new { fileName = r.Value.FileName, size = r.Value.Size })
            : r.ToProblemDetails();
    }

    /// <summary>删除某条投递的 tailored 简历 —— 删完回落到基础简历(引用,不复制)。</summary>
    [HttpDelete("applications/{applicationId:guid}/resume")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteApplicationResume(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteResumeFileCommand(Me, applicationId), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    private static bool IsPdf(IFormFile file)
    {
        var name = file.FileName ?? string.Empty;
        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
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
        var r = await sender.Send(new SaveCoverLetterCommand(applicationId, Me, body.Content ?? string.Empty, body.Version), ct);
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
        var r = await sender.Send(new DeleteCoverLetterCommand(applicationId, Me), ct);
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
        var r = await sender.Send(new UpdateAnswerTemplateCommand(id, Me, body.Category, body.Question, body.Answer, body.Version), ct);
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
        var r = await sender.Send(new ListCommunicationsQuery(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    [HttpPost("applications/{id:guid}/communications")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> CreateCommunication(Guid id, [FromBody] CommunicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new CreateCommunicationCommand(Me, id, body.Type, body.Subject, body.Content,
            body.ContactName, body.ContactEmail, body.OccurredAt, body.MetaJson, body.HandlingStatus), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{id}/communications/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    /// <summary>Playbook v2:更新沟通元数据 / 安排处理状态(红点处理走这里)。</summary>
    [HttpPut("applications/{id:guid}/communications/{commId:guid}/meta")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> SetCommunicationMeta(Guid id, Guid commId,
        [FromBody] CommunicationMetaBody body, CancellationToken ct)
    {
        var r = await sender.Send(new SetCommunicationMetaCommand(Me, commId, id,
            body.MetaJson, body.HandlingStatus), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpPut("applications/{id:guid}/communications/{commId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> UpdateCommunication(Guid id, Guid commId, [FromBody] CommunicationBody body, CancellationToken ct)
    {
        var r = await sender.Send(new UpdateCommunicationCommand(Me, commId, id, body.Type, body.Subject, body.Content,
            body.ContactName, body.ContactEmail, body.OccurredAt, body.Version), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    [HttpDelete("applications/{id:guid}/communications/{commId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsDelete)]
    public async Task<IResult> DeleteCommunication(Guid id, Guid commId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteCommunicationCommand(Me, commId, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    // ---------- COD-10a/票14:Tracker 收藏与按投递自备问题(原 localStorage 上后端) ----------

    /// <summary>当前用户的全部收藏(列表与详情共用同一读路径,前端单一数据源)。</summary>
    [HttpGet("favorites")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListFavorites(CancellationToken ct)
    {
        var r = await sender.Send(new ListFavoritesQuery(Me), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>加收藏(幂等)。</summary>
    [HttpPut("favorites/{applicationId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> AddFavorite(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new AddFavoriteCommand(Me, applicationId), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>取消收藏(幂等)。</summary>
    [HttpDelete("favorites/{applicationId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> RemoveFavorite(Guid applicationId, CancellationToken ct)
    {
        var r = await sender.Send(new RemoveFavoriteCommand(Me, applicationId), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 一次性导入本地收藏(localStorage yi-tracker-favorites)。幂等,返回对账
    /// { imported, total } —— 重复导入 imported=0、total 不变。
    /// </summary>
    [HttpPost("favorites/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ImportFavorites([FromBody] ImportFavoritesBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportFavoritesCommand(Me, body.ApplicationIds ?? []), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>某条投递的自备问题(按 SortOrder 升序)。</summary>
    [HttpGet("applications/{id:guid}/questions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsRead)]
    public async Task<IResult> ListApplicationQuestions(Guid id, CancellationToken ct)
    {
        var r = await sender.Send(new ListApplicationQuestionsQuery(Me, id), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    /// <summary>给某条投递加一条自备问题。</summary>
    [HttpPost("applications/{id:guid}/questions")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> AddApplicationQuestion(Guid id, [FromBody] ApplicationQuestionBody body, CancellationToken ct)
    {
        var r = await sender.Send(new AddApplicationQuestionCommand(Me, id, body.Text ?? string.Empty), ct);
        return r.IsSuccess
            ? Microsoft.AspNetCore.Http.Results.Created($"/api/jobs/applications/{id}/questions/{r.Value}", new { id = r.Value })
            : r.ToProblemDetails();
    }

    /// <summary>
    /// 删某条投递的一条自备问题。权限用 JobsWrite 而非 JobsDelete:默认 User 角色
    /// 没有 JobsDelete(只有 PowerUser/Admin),删自己的备考问题是普通用户日常操作 ——
    /// 与 DeleteCoverLetter 用 JobsWrite 的先例同口径。
    /// </summary>
    [HttpDelete("applications/{id:guid}/questions/{questionId:guid}")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> DeleteApplicationQuestion(Guid id, Guid questionId, CancellationToken ct)
    {
        var r = await sender.Send(new DeleteApplicationQuestionCommand(Me, id, questionId), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.NoContent() : r.ToProblemDetails();
    }

    /// <summary>
    /// 一次性导入某条投递的本地自备问题(yi-tracker-questions-{id})。幂等(按正文去重),
    /// 返回对账 { imported, total }。
    /// </summary>
    [HttpPost("applications/{id:guid}/questions/import")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.JobsWrite)]
    public async Task<IResult> ImportApplicationQuestions(Guid id, [FromBody] ImportQuestionsBody body, CancellationToken ct)
    {
        var r = await sender.Send(new ImportApplicationQuestionsCommand(Me, id, body.Questions ?? []), ct);
        return r.IsSuccess ? Microsoft.AspNetCore.Http.Results.Ok(r.Value) : r.ToProblemDetails();
    }

    public sealed record ImportFavoritesBody(Guid[]? ApplicationIds);
    public sealed record ApplicationQuestionBody(string? Text);
    public sealed record ImportQuestionsBody(string[]? Questions);

    public sealed record AnswerTemplateBody(string Category, string Question, string Answer, uint? Version = null); // 票101

    public sealed record CommunicationBody(string Type, string? Subject, string Content,
        string? ContactName, string? ContactEmail, DateTimeOffset OccurredAt,
        string? MetaJson = null, string? HandlingStatus = null, uint? Version = null); // 票101 Version:读时 xmin

    public sealed record CommunicationMetaBody(string? MetaJson = null, string? HandlingStatus = null);

}

/// <summary>简历正文提交。空串由 Handler 归一为"清空"。</summary>
public sealed record ResumeTextBody(string? ResumeText, uint? RowVersion = null); // 票101 RowVersion:读时 xmin

/// <summary>求职信人工保存体。</summary>
public sealed record CoverLetterBody(string? Content, uint? Version = null); // 票101

/// <summary>求职信生成体。Overwrite=false 时已有内容不会被覆盖。</summary>
public sealed record GenerateCoverLetterBody(string? ExtraInstructions, bool Overwrite = false);
