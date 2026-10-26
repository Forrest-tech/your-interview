using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

// ============================ DTO ============================
// 形状与后续 Playbook/Tech Stack 两面(COD-10 的 15/16 票)保持一致口径:
// 收藏 = (applicationId + createdAt);自备问题 = (id + applicationId + text + sortOrder)。

public sealed record FavoriteDto(Guid ApplicationId, DateTimeOffset CreatedAt);

public sealed record ApplicationQuestionDto(
    Guid Id, Guid ApplicationId, string Text, int SortOrder,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>一次性导入(localStorage → 后端)的对账结果:本次新导入条数 + 导入后总数。</summary>
public sealed record ImportResultDto(int Imported, int Total);

// ============================ 收藏 ============================

public sealed record ListFavoritesQuery(Guid UserId) : IRequest<Result<IReadOnlyList<FavoriteDto>>>;

/// <summary>加收藏(幂等:已收藏含软删恢复,一律成功,不重复建行)。</summary>
public sealed record AddFavoriteCommand(Guid UserId, Guid ApplicationId) : IRequest<Result>;

/// <summary>取消收藏(幂等:未收藏也成功)。</summary>
public sealed record RemoveFavoriteCommand(Guid UserId, Guid ApplicationId) : IRequest<Result>;

/// <summary>
/// 一次性导入本地收藏(localStorage yi-tracker-favorites → 后端)。
/// 幂等:输入先去重,只导入「属于当前用户且尚未收藏」的投递;重复调用 Imported=0。
/// 不属于当前用户的 id 静默跳过(不报错 —— 本地可能残留已删/他人数据的脏 id,
/// 导入不该因此整批失败;对账靠返回的 Imported/Total)。
/// </summary>
public sealed record ImportFavoritesCommand(Guid UserId, IReadOnlyList<Guid> ApplicationIds)
    : IRequest<Result<ImportResultDto>>;

// ============================ 按投递自备问题 ============================

public sealed record ListApplicationQuestionsQuery(Guid UserId, Guid ApplicationId)
    : IRequest<Result<IReadOnlyList<ApplicationQuestionDto>>>;

public sealed record AddApplicationQuestionCommand(Guid UserId, Guid ApplicationId, string Text)
    : IRequest<Result<Guid>>;

public sealed record DeleteApplicationQuestionCommand(Guid UserId, Guid ApplicationId, Guid QuestionId)
    : IRequest<Result>;

/// <summary>
/// 一次性导入某条投递的本地自备问题(yi-tracker-questions-{id} → 后端)。
/// 幂等:按 Trim 后的正文与已有问题逐字比对,已存在的跳过;重复调用 Imported=0。
/// 顺序保持输入顺序(SortOrder 续接当前最大值)。
/// </summary>
public sealed record ImportApplicationQuestionsCommand(
    Guid UserId, Guid ApplicationId, IReadOnlyList<string> Questions)
    : IRequest<Result<ImportResultDto>>;

// ============================ 校验 ============================

public sealed class AddApplicationQuestionCommandValidator : AbstractValidator<AddApplicationQuestionCommand>
{
    public AddApplicationQuestionCommandValidator()
    {
        // 票107:必填/长度文案分挂两条规则(WithMessage 只挂紧邻的前一条规则)。
        RuleFor(x => x.Text).NotEmpty().WithMessage("问题必填")
            .MaximumLength(4000).WithMessage("问题长度不能超过 {MaxLength} 个字符");
    }
}

public sealed class ImportFavoritesCommandValidator : AbstractValidator<ImportFavoritesCommand>
{
    public ImportFavoritesCommandValidator()
    {
        // Must 谓词须 null-safe:FluentValidation 同链后续规则在 NotNull 失败后仍会执行,
        // 直接 ids.Count 会在 null 列表上抛 NRE 把 400 变成 500(boundary 补测抓到)。
        RuleFor(x => x.ApplicationIds).NotNull().Must(ids => ids is null || ids.Count <= 5000)
            .WithMessage("一次导入的收藏数过多");
    }
}

public sealed class ImportApplicationQuestionsCommandValidator : AbstractValidator<ImportApplicationQuestionsCommand>
{
    public ImportApplicationQuestionsCommandValidator()
    {
        RuleFor(x => x.Questions).NotNull().Must(qs => qs is null || qs.Count <= 1000)
            .WithMessage("一次导入的问题数过多");
        RuleForEach(x => x.Questions).MaximumLength(4000).WithMessage("问题过长");
    }
}

// ============================ Handler:收藏 ============================

public sealed class ListFavoritesQueryHandler(JobsDbContext db)
    : IRequestHandler<ListFavoritesQuery, Result<IReadOnlyList<FavoriteDto>>>
{
    public async Task<Result<IReadOnlyList<FavoriteDto>>> Handle(ListFavoritesQuery request, CancellationToken ct)
    {
        var items = await db.Favorites.AsNoTracking()
            .Where(f => f.UserId == request.UserId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new FavoriteDto(f.ApplicationId, f.CreatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<FavoriteDto>>(items);
    }
}

public sealed class AddFavoriteCommandHandler(JobsDbContext db)
    : IRequestHandler<AddFavoriteCommand, Result>
{
    public async Task<Result> Handle(AddFavoriteCommand request, CancellationToken ct)
    {
        if (!await db.Applications.AsNoTracking()
                .AnyAsync(a => a.Id == request.ApplicationId && a.UserId == request.UserId, ct))
            return Result.Failure(Error.NotFound("投递记录"));

        // 含软删行查:取消收藏是软删,重加必须恢复原行 —— (UserId, ApplicationId)
        // 唯一索引不允许第二行。IgnoreQueryFilters 后谓词自带归属,不放宽隔离。
        var existing = await db.Favorites.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.UserId == request.UserId && f.ApplicationId == request.ApplicationId, ct);
        if (existing is not null)
        {
            if (existing.IsDeleted) { existing.Restore(); await db.SaveChangesAsync(ct); }
            return Result.Success();
        }

        db.Favorites.Add(new ApplicationFavorite(request.UserId, request.ApplicationId));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class RemoveFavoriteCommandHandler(JobsDbContext db)
    : IRequestHandler<RemoveFavoriteCommand, Result>
{
    public async Task<Result> Handle(RemoveFavoriteCommand request, CancellationToken ct)
    {
        var fav = await db.Favorites.FirstOrDefaultAsync(
            f => f.UserId == request.UserId && f.ApplicationId == request.ApplicationId, ct);
        if (fav is null) return Result.Success(); // 幂等:没收藏也算取消成功

        fav.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportFavoritesCommandHandler(JobsDbContext db)
    : IRequestHandler<ImportFavoritesCommand, Result<ImportResultDto>>
{
    public async Task<Result<ImportResultDto>> Handle(ImportFavoritesCommand request, CancellationToken ct)
    {
        var wanted = request.ApplicationIds.Distinct().ToList();

        // 只导入属于当前用户的投递(本地脏 id / 他人 id 跳过)
        var owned = await db.Applications.AsNoTracking()
            .Where(a => a.UserId == request.UserId && wanted.Contains(a.Id))
            .Select(a => a.Id).ToListAsync(ct);

        // 已收藏(含软删行):未删的跳过,软删的恢复 —— 都不算「新导入」
        var existing = await db.Favorites.IgnoreQueryFilters()
            .Where(f => f.UserId == request.UserId && owned.Contains(f.ApplicationId))
            .ToListAsync(ct);
        var existingIds = existing.Select(f => f.ApplicationId).ToHashSet();

        var imported = 0;
        var restored = 0;
        foreach (var fav in existing.Where(f => f.IsDeleted)) { fav.Restore(); restored++; }
        foreach (var appId in owned.Where(id => !existingIds.Contains(id)))
        {
            db.Favorites.Add(new ApplicationFavorite(request.UserId, appId));
            imported++;
        }
        if (imported > 0 || restored > 0)
            await db.SaveChangesAsync(ct);

        var total = await db.Favorites.AsNoTracking().CountAsync(f => f.UserId == request.UserId, ct);
        return Result.Success(new ImportResultDto(imported, total));
    }
}

// ============================ Handler:自备问题 ============================

public sealed class ListApplicationQuestionsQueryHandler(JobsDbContext db)
    : IRequestHandler<ListApplicationQuestionsQuery, Result<IReadOnlyList<ApplicationQuestionDto>>>
{
    public async Task<Result<IReadOnlyList<ApplicationQuestionDto>>> Handle(
        ListApplicationQuestionsQuery request, CancellationToken ct)
    {
        if (!await db.Applications.AsNoTracking()
                .AnyAsync(a => a.Id == request.ApplicationId && a.UserId == request.UserId, ct))
            return Result.Failure<IReadOnlyList<ApplicationQuestionDto>>(Error.NotFound("投递记录"));

        var items = await db.ApplicationQuestions.AsNoTracking()
            .Where(q => q.ApplicationId == request.ApplicationId && q.UserId == request.UserId)
            .OrderBy(q => q.SortOrder).ThenBy(q => q.CreatedAt)
            .Select(q => new ApplicationQuestionDto(q.Id, q.ApplicationId, q.Text, q.SortOrder, q.CreatedAt, q.UpdatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<ApplicationQuestionDto>>(items);
    }
}

public sealed class AddApplicationQuestionCommandHandler(JobsDbContext db)
    : IRequestHandler<AddApplicationQuestionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddApplicationQuestionCommand request, CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.ApplicationId && a.UserId == request.UserId, ct);
        if (app is null) return Result.Failure<Guid>(Error.NotFound("投递记录"));

        var nextOrder = await db.ApplicationQuestions.AsNoTracking()
            .Where(q => q.ApplicationId == request.ApplicationId && q.UserId == request.UserId)
            .MaxAsync(q => (int?)q.SortOrder, ct) ?? 0;

        // 归属从所属投递盖章(不从请求另传)—— 问题的主人恒等于投递的主人(同沟通记录口径)
        var q = new ApplicationQuestion(app.UserId, request.ApplicationId, request.Text.Trim(), nextOrder + 1);
        db.ApplicationQuestions.Add(q);
        await db.SaveChangesAsync(ct);
        return Result.Success(q.Id);
    }
}

public sealed class DeleteApplicationQuestionCommandHandler(JobsDbContext db)
    : IRequestHandler<DeleteApplicationQuestionCommand, Result>
{
    public async Task<Result> Handle(DeleteApplicationQuestionCommand request, CancellationToken ct)
    {
        var q = await db.ApplicationQuestions.FirstOrDefaultAsync(
            x => x.Id == request.QuestionId && x.ApplicationId == request.ApplicationId
                 && x.UserId == request.UserId, ct);
        if (q is null) return Result.Failure(Error.NotFound("自备问题"));

        q.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportApplicationQuestionsCommandHandler(JobsDbContext db)
    : IRequestHandler<ImportApplicationQuestionsCommand, Result<ImportResultDto>>
{
    public async Task<Result<ImportResultDto>> Handle(
        ImportApplicationQuestionsCommand request, CancellationToken ct)
    {
        var app = await db.Applications.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == request.ApplicationId && a.UserId == request.UserId, ct);
        if (app is null) return Result.Failure<ImportResultDto>(Error.NotFound("投递记录"));

        var existing = await db.ApplicationQuestions.AsNoTracking()
            .Where(q => q.ApplicationId == request.ApplicationId && q.UserId == request.UserId)
            .Select(q => q.Text).ToListAsync(ct);
        var seen = new HashSet<string>(existing, StringComparer.Ordinal);
        var nextOrder = await db.ApplicationQuestions.AsNoTracking()
            .Where(q => q.ApplicationId == request.ApplicationId && q.UserId == request.UserId)
            .MaxAsync(q => (int?)q.SortOrder, ct) ?? 0;

        var imported = 0;
        foreach (var raw in request.Questions)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0 || !seen.Add(text)) continue; // 空行与已存在(含本批重复)跳过 → 幂等
            db.ApplicationQuestions.Add(new ApplicationQuestion(app.UserId, request.ApplicationId, text, ++nextOrder));
            imported++;
        }
        if (imported > 0) await db.SaveChangesAsync(ct);

        var total = await db.ApplicationQuestions.AsNoTracking()
            .CountAsync(q => q.ApplicationId == request.ApplicationId && q.UserId == request.UserId, ct);
        return Result.Success(new ImportResultDto(imported, total));
    }
}
