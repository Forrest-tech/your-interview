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

// ============================ Playbook 重做(A1–A7):Handler ============================

/// <summary>A1:设置题目掌握度。</summary>
public sealed class UpdateQuestionMasteryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateQuestionMasteryCommand, Result>
{
    public async Task<Result> Handle(UpdateQuestionMasteryCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.SetQuestionMastery(request.QuestionId, request.Mastery);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

/// <summary>A2:待办列表 —— 未完成在前,同组按创建时间 asc。</summary>
public sealed class ListActionsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListActionsQuery, Result<IReadOnlyList<ActionDto>>>
{
    public async Task<Result<IReadOnlyList<ActionDto>>> Handle(ListActionsQuery request,
        CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists) return Result.Failure<IReadOnlyList<ActionDto>>(Error.NotFound("面试条目"));

        var list = await db.Actions.AsNoTracking()
            .Where(a => a.InterviewEntryId == request.EntryId)
            .OrderBy(a => a.IsDone).ThenBy(a => a.CreatedAt)
            .Select(a => new ActionDto(a.Id, a.Title, a.Detail, a.DueDate, a.IsDone, a.CreatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<ActionDto>>(list);
    }
}

public sealed class CreateActionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<CreateActionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateActionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Actions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        try
        {
            var a = e.AddAction(request.Title, request.Detail, request.DueDate);
            await db.SaveChangesAsync(ct);
            return Result.Success(a.Id);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Validation("Action.Invalid", ex.Message));
        }
    }
}

public sealed class UpdateActionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateActionCommand, Result>
{
    public async Task<Result> Handle(UpdateActionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Actions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.UpdateAction(request.ActionId, request.Title, request.IsDone, request.DueDate);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class DeleteActionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteActionCommand, Result>
{
    public async Task<Result> Handle(DeleteActionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Actions)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.RemoveAction(request.ActionId);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

/// <summary>A3:分析趋势 —— 该条目的快照按时间 asc,只返回 id/createdAt/overallScore。</summary>
public sealed class GetAnalysisTrendQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetAnalysisTrendQuery, Result<IReadOnlyList<AnalysisTrendPointDto>>>
{
    public async Task<Result<IReadOnlyList<AnalysisTrendPointDto>>> Handle(
        GetAnalysisTrendQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists)
            return Result.Failure<IReadOnlyList<AnalysisTrendPointDto>>(Error.NotFound("面试条目"));

        var list = await db.AnalysisSnapshots.AsNoTracking()
            .Where(s => s.InterviewEntryId == request.EntryId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new AnalysisTrendPointDto(s.Id, s.CreatedAt, s.OverallScore))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<AnalysisTrendPointDto>>(list);
    }
}

/// <summary>A3 最新快照全文:按 CreatedAt desc 取第一条。</summary>
public sealed class GetLatestSnapshotQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetLatestSnapshotQuery, Result<AnalysisSnapshotDto?>>
{
    public async Task<Result<AnalysisSnapshotDto?>> Handle(
        GetLatestSnapshotQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists)
            return Result.Failure<AnalysisSnapshotDto?>(Error.NotFound("面试条目"));

        // 票19:Json 字段存版本信封,EF 投影直读列会漏出信封形 —— 先加载实体再经属性 getter 映射
        var snap = await db.AnalysisSnapshots.AsNoTracking()
            .Where(x => x.InterviewEntryId == request.EntryId)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var s = snap is null ? null : new AnalysisSnapshotDto(
            snap.Id, snap.CreatedAt, snap.OverallScore, snap.DimensionsJson, snap.SummaryBulletsJson);
        return Result.Success(s);
    }
}

/// <summary>
/// A4:该题在其它面试中的回答版本。
/// 匹配规则:v1 只做归一化(小写去标点去空白)后的精确相等,不做语义相似(按 spec)。
/// 归一化函数进不了 SQL 翻译,先拉候选行再内存过滤 —— 问答表是用户个人数据,量级可控。
/// </summary>
public sealed class GetQuestionHistoryQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetQuestionHistoryQuery, Result<IReadOnlyList<QuestionHistoryDto>>>
{
    public async Task<Result<IReadOnlyList<QuestionHistoryDto>>> Handle(
        GetQuestionHistoryQuery request, CancellationToken ct)
    {
        var target = await db.Questions.AsNoTracking()
            .Where(q => q.Id == request.QuestionId && q.InterviewEntryId == request.EntryId)
            .Select(q => q.QuestionText)
            .FirstOrDefaultAsync(ct);
        if (target is null)
            return Result.Failure<IReadOnlyList<QuestionHistoryDto>>(Error.NotFound("问题"));

        var normalized = PlaybookText.NormalizeQuestionText(target);
        if (string.IsNullOrEmpty(normalized))
            return Result.Success<IReadOnlyList<QuestionHistoryDto>>([]);

        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Questions, (e, q) => new { e, q })
            .Where(x => x.q.Id != request.QuestionId)
            .Select(x => new
            {
                InterviewId = x.e.Id,
                x.e.CompanyName,
                x.e.Role,
                x.e.InterviewDate,
                x.q.QuestionText,
                x.q.MyAnswerText,
                x.q.Mastery
            })
            .ToListAsync(ct);

        var list = rows
            .Where(x => PlaybookText.NormalizeQuestionText(x.QuestionText) == normalized)
            .OrderByDescending(x => x.InterviewDate ?? DateOnly.MinValue)
            .Select(x => new QuestionHistoryDto(x.InterviewId, x.CompanyName, x.Role,
                x.InterviewDate, x.MyAnswerText, x.Mastery))
            .ToList();
        return Result.Success<IReadOnlyList<QuestionHistoryDto>>(list);
    }
}

/// <summary>
/// A5:全局技术覆盖图谱 —— 按 Category 聚合全部问答。
/// askedCount=被问次数,stuckCount=卡壳数,masteredCount=已掌握数(Mastery==2),
/// interviews=出过该类题的公司名(去重,按字母序)。
/// </summary>
public sealed class GetCoverageQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetCoverageQuery, Result<IReadOnlyList<CoverageCategoryDto>>>
{
    public async Task<Result<IReadOnlyList<CoverageCategoryDto>>> Handle(
        GetCoverageQuery request, CancellationToken ct)
    {
        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Questions, (e, q) => new
            {
                Category = q.Category.ToString(),
                q.GotStuck,
                q.Mastery,
                e.CompanyName
            })
            .ToListAsync(ct);

        var list = rows
            .GroupBy(x => x.Category)
            .Select(g => new CoverageCategoryDto(
                g.Key,
                g.Count(),
                g.Count(x => x.GotStuck),
                g.Count(x => x.Mastery == 2),
                g.Select(x => x.CompanyName).Distinct().OrderBy(c => c).ToList()))
            .OrderByDescending(x => x.AskedCount)
            .ToList();
        return Result.Success<IReadOnlyList<CoverageCategoryDto>>(list);
    }
}

/// <summary>A7:整体替换某轮次的邮件时间线(写入 InterviewRound.EmailsJson)。</summary>
public sealed class UpdateRoundEmailsCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateRoundEmailsCommand, Result>
{

    public async Task<Result> Handle(UpdateRoundEmailsCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            var json = JsonFieldAdapter.Serialize(request.Emails);
            e.SetRoundEmails(request.RoundId, json);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

