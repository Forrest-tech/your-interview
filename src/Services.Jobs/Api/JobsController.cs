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
public sealed class JobsController(ISender sender) : ControllerBase
{
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
    public sealed record AnalysisBody(int? ResumeScore, string? PassRateEstimate, string? MatchKeywords);
    public sealed record OutreachBody(string? PosterName, bool NeedsConnectFirst, string? Message);
    public sealed record FollowUpBody(DateTimeOffset? At);
    public sealed record AddRoundBody(string Stage, DateOnly? ScheduledDate, string? Interviewer, string? Format, string? Notes);
    public sealed record UpdateRoundBody(string? Stage, DateOnly? ScheduledDate, string? Interviewer,
        string? Format, string Outcome, string? Notes);
}
