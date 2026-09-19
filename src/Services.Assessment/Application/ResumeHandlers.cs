using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;

namespace YourInterview.Services.Assessment.Application;

// ============================================================================
//  简历正文(2026-09-18)
//
//  用途:简历匹配分析(简历 vs JD 关键词比对)+ 面试前准备包的输入。
//  一人一份主版本 —— 简历是长期资产,内容稳定,按岗微调不改主版本。
// ============================================================================

/// <summary>简历读取结果。未设置时返回空内容 + Version 0(不是失败)。</summary>
public sealed record ResumeDto(string Content, int Version, DateTimeOffset? UpdatedAt);

/// <summary>读简历。未设置返回空 DTO 而不是 NotFound —— 前端据此提示"先录入简历"。</summary>
public sealed record GetResumeQuery(Guid UserId) : IRequest<Result<ResumeDto>>;

public sealed class GetResumeQueryHandler(AssessmentDbContext db)
    : IRequestHandler<GetResumeQuery, Result<ResumeDto>>
{
    public async Task<Result<ResumeDto>> Handle(GetResumeQuery request, CancellationToken ct)
    {
        var r = await db.Resumes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);

        // 未设置是正常状态,不是错误。
        return Result.Success(r is null
            ? new ResumeDto(string.Empty, 0, null)
            : new ResumeDto(r.Content, r.Version, r.UpdatedAt));
    }
}

/// <summary>
/// 存/覆盖简历。空内容视为"清空"(删除记录),避免库里留一条空字符串记录 ——
/// 空串和"没设置"在查询里表现不同,会制造"明明清空了却还说有简历"的怪象。
/// </summary>
public sealed record SaveResumeCommand(Guid UserId, string Content) : IRequest<Result<ResumeDto>>;

public sealed class SaveResumeCommandHandler(AssessmentDbContext db)
    : IRequestHandler<SaveResumeCommand, Result<ResumeDto>>
{
    public async Task<Result<ResumeDto>> Handle(SaveResumeCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return Result.Failure<ResumeDto>(Error.Validation("Resume.Empty", "简历内容不能为空"));

        var existing = await db.Resumes.FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);

        if (existing is null)
        {
            var created = new UserResume(request.UserId, request.Content.Trim());
            db.Resumes.Add(created);
            await db.SaveChangesAsync(ct);
            return Result.Success(new ResumeDto(created.Content, created.Version, created.UpdatedAt));
        }

        existing.Update(request.Content.Trim());
        await db.SaveChangesAsync(ct);
        return Result.Success(new ResumeDto(existing.Content, existing.Version, existing.UpdatedAt));
    }
}

/// <summary>清空简历(回到未设置状态)。幂等:没有记录也算成功。</summary>
public sealed record DeleteResumeCommand(Guid UserId) : IRequest<Result>;

public sealed class DeleteResumeCommandHandler(AssessmentDbContext db)
    : IRequestHandler<DeleteResumeCommand, Result>
{
    public async Task<Result> Handle(DeleteResumeCommand request, CancellationToken ct)
    {
        var existing = await db.Resumes.FirstOrDefaultAsync(x => x.UserId == request.UserId, ct);
        if (existing is null) return Result.Success();   // 幂等

        db.Resumes.Remove(existing);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
