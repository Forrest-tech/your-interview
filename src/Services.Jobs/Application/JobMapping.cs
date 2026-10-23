using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ 映射 ============================

internal static class JobsMapping
{
    public static ApplicationDto ToDto(this JobApplication a, string companyName, string? companyLogoUrl = null) => new(
        a.Id, a.CompanyId, companyName, companyLogoUrl, a.Role, a.Location, a.Link, a.Salary, a.WorkMode, a.Source, a.JdSummary,
        // JD 全文随详情一并带出(2026-09-18)。列表查询同样走本映射 ——
        // 实测投递记录量级为个人求职(几十条),40000 字符全文带来的负载可接受;
        // 若未来记录上千,再拆出"不含全文"的精简映射给列表用。
        a.JdText, a.JdSourceUrl, a.CompanyDescription, a.WorkAuthorization, a.JobType,
        a.Requirements, a.Benefits,
        a.Status.ToString(), a.Priority.ToString(), a.AppliedDate, a.Deadline, a.NextFollowUpAt,
        a.ResumeScore, a.PassRateEstimate, a.MatchKeywords, a.Notes, a.PosterName, a.NeedsConnectFirst,
        a.OutreachMessage, a.OutreachSentAt, a.RejectionReason,
        a.History.OrderBy(h => h.ChangedAt).Select(h => new StatusChangeDto(h.Status.ToString(), h.Note, h.ChangedAt)).ToList(),
        a.Rounds.OrderBy(r => r.Order).Select(r => new InterviewRoundDto(r.Id, r.Order, r.Stage,
            r.ScheduledDate, r.Interviewer, r.Format, r.Outcome.ToString(), r.Notes, r.Feedback)).ToList(),
        a.CreatedAt, a.UpdatedAt);

    public static CompanyDto ToDto(this Company c, int applicationCount) => new(
        c.Id, c.Name, c.Website, c.Industry, c.Location, c.LogoUrl, c.Notes, c.IsBlacklisted,
        c.CompanyType, applicationCount, c.Profile, c.ProfileSourcesJson, c.CreatedAt);
}

