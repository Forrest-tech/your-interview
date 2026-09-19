using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  Cover Letter 应用层(2026-09-18)
//
//  两类职责:
//    A. 读写/编辑求职信(与 AI 无关的纯 CRUD)
//    B. AI 生成 —— 把(简历 + JD 全文 + 公司情报 + 用户补充要求)装配成 prompt,
//       经 AiGateway 调 LLM,把结果写进 CoverLetter.Content
//
//  ⚠️ B 为什么不自己建 LLM 客户端:
//     凭据只存一处(AiGateway)。Jobs 只发"帮我生成",不碰 key/协议/端点。
//     Forrest 2026-09-18 定夺:输入 = 简历 + JD + 公司信息 + 用户额外要求。
// ============================================================================

// ---------------------------- DTO ----------------------------

public sealed record CoverLetterDto(
    Guid Id,
    Guid ApplicationId,
    string Content,
    string Status,
    string? GeneratedByModel,
    int? ResumeVersion,
    string? LastPromptHint,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? UpdatedAt,
    /// <summary>生成时所用简历的版本 vs 当前简历版本不一致 → 可能已过期。</summary>
    bool IsStale,
    int CurrentResumeVersion);

/// <summary>生成请求的输入齐备情况,供前端在点"生成"前提示缺什么。</summary>
public sealed record CoverLetterReadinessDto(
    bool HasResume, int ResumeVersion,
    bool HasJdText,
    bool HasCompanyProfile,
    string CompanyName, string Role,
    string? MissingHint);

// ---------------------------- 读 ----------------------------

/// <summary>读某条投递的求职信。不存在时返回 null 内容(DTO 为 null),不是失败。</summary>
public sealed record GetCoverLetterQuery(Guid ApplicationId, Guid UserId) : IRequest<Result<CoverLetterDto?>>;

public sealed class GetCoverLetterQueryHandler(JobsDbContext db)
    : IRequestHandler<GetCoverLetterQuery, Result<CoverLetterDto?>>
{
    public async Task<Result<CoverLetterDto?>> Handle(GetCoverLetterQuery request, CancellationToken ct)
    {
        var letter = await db.CoverLetters.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ApplicationId == request.ApplicationId, ct);
        if (letter is null) return Result.Success<CoverLetterDto?>(null);

        // 当前简历版本 —— 用于判断"这封信是基于旧版简历生成的"
        var resumeVersion = await db.Resumes.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .Select(x => (int?)x.Version)
            .FirstOrDefaultAsync(ct) ?? 0;

        return Result.Success<CoverLetterDto?>(ToDto(letter, resumeVersion));
    }

    /// <summary>统一映射口径 —— 各处返回同一形状,避免字段口径分叉。</summary>
    internal static CoverLetterDto ToDto(CoverLetter l, int currentResumeVersion) => new(
        l.Id, l.ApplicationId, l.Content, l.Status.ToString(),
        l.GeneratedByModel, l.ResumeVersion, l.LastPromptHint, l.GeneratedAt, l.UpdatedAt,
        // 只有 AI 生成过、且简历版本已前进,才算过期;人工写的信不参与版本比对
        IsStale: l.ResumeVersion is int rv && rv > 0 && currentResumeVersion > rv,
        CurrentResumeVersion: currentResumeVersion);
}

/// <summary>生成前的输入体检:告诉前端缺什么,而不是等生成了才报错。</summary>
public sealed record GetCoverLetterReadinessQuery(Guid ApplicationId, Guid UserId)
    : IRequest<Result<CoverLetterReadinessDto>>;

public sealed class GetCoverLetterReadinessQueryHandler(JobsDbContext db)
    : IRequestHandler<GetCoverLetterReadinessQuery, Result<CoverLetterReadinessDto>>
{
    public async Task<Result<CoverLetterReadinessDto>> Handle(GetCoverLetterReadinessQuery request, CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.ApplicationId, ct);
        if (app is null)
            return Result.Failure<CoverLetterReadinessDto>(Error.NotFound("投递记录"));

        var resume = await db.Resumes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);

        var company = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == app.CompanyId, ct);

        var hasResume = resume is not null && !string.IsNullOrWhiteSpace(resume.Content);
        var hasJd = !string.IsNullOrWhiteSpace(app.JdText);
        var hasProfile = company is not null && !string.IsNullOrWhiteSpace(company.Profile);

        // 简历是硬前提(没有简历就没有"基于什么写的");JD 与公司情报是加分项。
        // 只有简历缺失才提示"必须补",其余用措辞说明"补了效果更好"。
        string? hint = !hasResume
            ? "尚未录入简历 —— 求职信需要基于你的简历撰写,请先在\"技术栈/简历\"里录入。"
            : !hasJd ? "未填 JD 全文 —— 补上后生成的信会更有针对性(岗位职责与要求)。"
            : !hasProfile ? "未填公司情报 —— 补上后 AI 能写出针对该公司的话术。" 
            : null;

        return Result.Success(new CoverLetterReadinessDto(
            hasResume, resume?.Version ?? 0, hasJd, hasProfile,
            company?.Name ?? "(未知公司)", app.Role, hint));
    }
}

// ---------------------------- 写(人工) ----------------------------

/// <summary>人工保存/覆盖求职信内容。不存在则创建。</summary>
public sealed record SaveCoverLetterCommand(Guid ApplicationId, Guid UserId, string Content)
    : IRequest<Result<CoverLetterDto>>;

public sealed class SaveCoverLetterCommandValidator : AbstractValidator<SaveCoverLetterCommand>
{
    public SaveCoverLetterCommandValidator()
        => RuleFor(x => x.Content).NotEmpty().WithMessage("求职信内容不能为空。");
}

public sealed class SaveCoverLetterCommandHandler(JobsDbContext db)
    : IRequestHandler<SaveCoverLetterCommand, Result<CoverLetterDto>>
{
    public async Task<Result<CoverLetterDto>> Handle(SaveCoverLetterCommand request, CancellationToken ct)
    {
        var app = await db.Applications.FirstOrDefaultAsync(x => x.Id == request.ApplicationId, ct);
        if (app is null)
            return Result.Failure<CoverLetterDto>(Error.NotFound("投递记录"));

        var letter = await db.CoverLetters.FirstOrDefaultAsync(x => x.ApplicationId == request.ApplicationId, ct);
        var content = request.Content.Trim();

        if (letter is null)
        {
            letter = new CoverLetter(request.ApplicationId, request.UserId, content);
            db.CoverLetters.Add(letter);
        }
        else
        {
            letter.UpdateContent(content);
        }

        await db.SaveChangesAsync(ct);

        var rv = await db.Resumes.AsNoTracking()
            .Where(x => x.UserId == request.UserId).Select(x => (int?)x.Version).FirstOrDefaultAsync(ct) ?? 0;

        return Result.Success(GetCoverLetterQueryHandler.ToDto(letter, rv));
    }
}

/// <summary>标记为已确认(可发出)。</summary>
public sealed record MarkCoverLetterFinalCommand(Guid ApplicationId, Guid UserId) : IRequest<Result<CoverLetterDto>>;

public sealed class MarkCoverLetterFinalCommandHandler(JobsDbContext db)
    : IRequestHandler<MarkCoverLetterFinalCommand, Result<CoverLetterDto>>
{
    public async Task<Result<CoverLetterDto>> Handle(MarkCoverLetterFinalCommand request, CancellationToken ct)
    {
        var letter = await db.CoverLetters.FirstOrDefaultAsync(x => x.ApplicationId == request.ApplicationId, ct);
        if (letter is null)
            return Result.Failure<CoverLetterDto>(Error.NotFound("求职信"));

        try { letter.MarkFinal(); }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<CoverLetterDto>(Error.Validation("CoverLetter.Empty", ex.Message));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(GetCoverLetterQueryHandler.ToDto(letter, 0));
    }
}

/// <summary>删除求职信。幂等:不存在也算成功。</summary>
public sealed record DeleteCoverLetterCommand(Guid ApplicationId) : IRequest<Result>;

public sealed class DeleteCoverLetterCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteCoverLetterCommand, Result>
{
    public async Task<Result> Handle(DeleteCoverLetterCommand request, CancellationToken ct)
    {
        var letter = await db.CoverLetters.FirstOrDefaultAsync(x => x.ApplicationId == request.ApplicationId, ct);
        if (letter is not null)
        {
            letter.MarkDeleted();
            await db.SaveChangesAsync(ct);
        }
        return Result.Success();
    }
}
