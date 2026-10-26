using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  简历 PDF 文件(2026-10-19)
//
//  ApplicationId 为 null = 基础简历(一人一份,/resume 页管理);
//  有值 = 某条投递的 tailored 版本(一条投递一份,Documents tab 管理)。
//
//  去重(Forrest 定夺):Documents 里没 tailored 就直接引用基础简历,不复制。
//  所以"有效简历" = tailored 优先,没有就回落到基础。
// ============================================================================

/// <summary>简历文件元信息(不含二进制 —— 列表/引用场景用这个,下载走单独端点)。</summary>
public sealed record ResumeFileDto(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    /// <summary>提取文本(评分用)。列表场景不需要时可为空。</summary>
    string? TextContent);

/// <summary>某条投递的有效简历:tailored 优先,没有就回落基础文件,再没有回落文本简历。</summary>
public sealed record EffectiveResumeDto(
    /// <summary>"tailored" | "base" | "text" | "none"</summary>
    string Kind,
    ResumeFileDto? File,
    /// <summary>REQ-05:Kind=text 时的文本简历正文(主简历 resumes 表)。文件形态时为 null,文本走 File.TextContent。</summary>
    string? TextContent = null);

/// <summary>上传/覆盖简历 PDF。ApplicationId 为 null = 基础简历,有值 = 该投递的 tailored。</summary>
public sealed record SaveResumeFileCommand(
    Guid UserId,
    Guid? ApplicationId,
    string FileName,
    string ContentType,
    byte[] Data,
    string TextContent,
    // ★ 票111 段3(J12/J13 替换钉版):If-Match 回传的版本锚 —— J12(基础)= 简历文本行
    //   RowVersion(resumes 行 xmin,GET resume-text 已暴露);J13(tailored)= 申请行 Version
    //   (票101 J04 已暴露)。null = 不带头,兼容旧行为(LWW)。
    uint? ExpectedVersion = null) : IRequest<Result<ResumeFileDto>>;

/// <summary>PDF 上限 10MB —— 简历 PDF 通常 100KB~2MB,10MB 足够且防止恶意大文件拖垮行存储。</summary>
public sealed class SaveResumeFileCommandValidator : AbstractValidator<SaveResumeFileCommand>
{
    public SaveResumeFileCommandValidator()
    {
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Data).NotNull()
            .Must(d => d!.Length > 0).WithMessage("文件内容不能为空")
            .Must(d => d!.Length <= 10 * 1024 * 1024).WithMessage("PDF 不能超过 10MB");
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(100);
    }
}

public sealed class SaveResumeFileCommandHandler(JobsDbContext db)
    : IRequestHandler<SaveResumeFileCommand, Result<ResumeFileDto>>
{
    public async Task<Result<ResumeFileDto>> Handle(SaveResumeFileCommand request, CancellationToken ct)
    {
        // ★ 票111 段3:写前钉版比对 —— 锚行当前 xmin 与 If-Match 不符 → 409 先行拒绝,
        //   文件一行不落(与删除类服务端钉版同口径;不传 ExpectedVersion = 兼容 LWW)。
        if (request.ExpectedVersion is { } expected)
        {
            uint? anchor;
            if (request.ApplicationId is Guid aid)
            {
                var row = await db.Applications.AsNoTracking()
                    .Where(x => x.Id == aid && x.UserId == request.UserId)
                    .Select(x => new { V = EF.Property<uint>(x, "xmin") }).FirstOrDefaultAsync(ct);
                anchor = row?.V;
            }
            else
            {
                var row = await db.Resumes.AsNoTracking()
                    .Where(x => x.UserId == request.UserId)
                    .Select(x => new { V = EF.Property<uint>(x, "xmin") }).FirstOrDefaultAsync(ct);
                anchor = row?.V;
            }
            if (anchor is null || anchor.Value != expected)
                return Result.Failure<ResumeFileDto>(Error.Conflict("concurrency_conflict",
                    "简历已被其他页签修改,本次上传未生效,请刷新后重试"));
        }

        // tailored 必须挂在真实存在的投递上
        if (request.ApplicationId is Guid appId)
        {
            var exists = await db.Applications.AsNoTracking()
                .AnyAsync(x => x.Id == appId, ct);
            if (!exists)
                return Result.Failure<ResumeFileDto>(Error.NotFound("投递记录"));
        }

        var existing = await db.ResumeFiles.FirstOrDefaultAsync(x =>
            x.UserId == request.UserId &&
            x.ApplicationId == request.ApplicationId, ct);

        if (existing is null)
        {
            var created = new ResumeFile(request.UserId, request.FileName, request.ContentType,
                request.Data, request.TextContent ?? string.Empty, request.ApplicationId);
            db.ResumeFiles.Add(created);
            await db.SaveChangesAsync(ct);
            return Result.Success(ToDto(created, includeText: false));
        }

        existing.Update(request.FileName, request.ContentType, request.Data,
            request.TextContent ?? string.Empty);
        await db.SaveChangesAsync(ct);
        return Result.Success(ToDto(existing, includeText: false));
    }

    internal static ResumeFileDto ToDto(ResumeFile f, bool includeText) => new(
        f.Id, f.FileName, f.ContentType, f.Data.LongLength,
        f.CreatedAt, f.UpdatedAt, includeText ? f.TextContent : null);
}

/// <summary>取某份简历文件(基础或某投递的 tailored)。不存在返回 null,不是失败。</summary>
public sealed record GetResumeFileQuery(Guid UserId, Guid? ApplicationId) : IRequest<Result<ResumeFile?>>;

public sealed class GetResumeFileQueryHandler(JobsDbContext db)
    : IRequestHandler<GetResumeFileQuery, Result<ResumeFile?>>
{
    public async Task<Result<ResumeFile?>> Handle(GetResumeFileQuery request, CancellationToken ct)
    {
        var f = await db.ResumeFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId
                && x.ApplicationId == request.ApplicationId, ct);
        return Result.Success(f);
    }
}

/// <summary>某条投递的有效简历:tailored 优先,没有回落基础文件,再没有回落文本简历,都没有返回 Kind=none。</summary>
public sealed record GetEffectiveResumeQuery(Guid UserId, Guid ApplicationId)
    : IRequest<Result<EffectiveResumeDto>>;

public sealed class GetEffectiveResumeQueryHandler(JobsDbContext db)
    : IRequestHandler<GetEffectiveResumeQuery, Result<EffectiveResumeDto>>
{
    public async Task<Result<EffectiveResumeDto>> Handle(GetEffectiveResumeQuery request, CancellationToken ct)
    {
        var tailored = await db.ResumeFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId
                && x.ApplicationId == request.ApplicationId, ct);
        if (tailored is not null)
            return Result.Success(new EffectiveResumeDto("tailored",
                SaveResumeFileCommandHandler.ToDto(tailored, includeText: true)));

        var baseFile = await db.ResumeFiles.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId
                && x.ApplicationId == null, ct);
        if (baseFile is not null)
            return Result.Success(new EffectiveResumeDto("base",
                SaveResumeFileCommandHandler.ToDto(baseFile, includeText: true)));

        // REQ-05 回落:主简历只有文本版(resumes 表,无文件)时,Documents 不该显示「No resume yet」。
        // 归属口径与文件查询一致 —— 只查当前用户自己的文本简历(票03 先例),他人文本不回落。
        // 空白文本视为无简历:SaveResume 本就拒绝空白存入,但存量/直写数据可能留空白行,
        // 与控制器 textContent 的空白→null 口径保持一致。
        var textResume = await db.Resumes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (textResume is not null && !string.IsNullOrWhiteSpace(textResume.Content))
            return Result.Success(new EffectiveResumeDto("text", null, textResume.Content));

        return Result.Success(new EffectiveResumeDto("none", null));
    }
}

/// <summary>删除简历文件(基础或某投递的 tailored)。幂等:没有也算成功。</summary>
public sealed record DeleteResumeFileCommand(Guid UserId, Guid? ApplicationId) : IRequest<Result>;

public sealed class DeleteResumeFileCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteResumeFileCommand, Result>
{
    public async Task<Result> Handle(DeleteResumeFileCommand request, CancellationToken ct)
    {
        var existing = await db.ResumeFiles.FirstOrDefaultAsync(x =>
            x.UserId == request.UserId &&
            x.ApplicationId == request.ApplicationId, ct);
        if (existing is null) return Result.Success();

        db.ResumeFiles.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
