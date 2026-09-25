using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// ★ 2026-09-25(Forrest):练习类别 —— CRUD + 拖拽排序。
//
// 为什么独立成文件而不挤进 PracticeHandlers:素材树那套是"整树读写"
// 的聚合操作,类别是独立的小聚合(单表、无子实体),生命周期完全不同。
// 分开放,两边都不会越改越大。
//
// 端点设计(与前端 UX 对应):
//   GET    /categories          列表(空则播种默认类别)
//   POST   /categories          新增(追加到末尾)
//   PUT    /categories/{id}     重命名
//   PUT    /categories/reorder  按提交顺序整体重排(拖拽弹窗用)
//   DELETE /categories/{id}     删除(素材由 DB 层 SetNull 回"未分类")
// ============================================================

/// <summary>与前端 PracticeCategory 形状对齐。</summary>
public sealed record PracticeCategoryDto(Guid Id, string Name, int SortOrder);

// ---------- 列表 ----------

public sealed record ListCategoriesQuery(Guid UserId)
    : MediatR.IRequest<IReadOnlyList<PracticeCategoryDto>>;

public sealed class ListCategoriesQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<ListCategoriesQuery, IReadOnlyList<PracticeCategoryDto>>
{
    public async Task<IReadOnlyList<PracticeCategoryDto>> Handle(ListCategoriesQuery r, CancellationToken ct)
    {
        var list = await db.Categories.AsNoTracking()
            .Where(x => x.UserId == r.UserId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        // 首次使用播种默认类别 —— 让下拉一开始就有内容,而不是逼用户先去"配置"。
        // 只在"一个类别都没有"时播种;用户删光全部类别后不再复活(那是明确意图)。
        // 删光后又重新打开页面会重新播种 —— 权衡后接受:删光的用户极少数,
        // 且播种的只是两个演示类别,删除成本一行一次点击。
        if (list.Count == 0)
        {
            var seeded = new[]
            {
                new PracticeCategory(r.UserId, "工作面试", 0),
                new PracticeCategory(r.UserId, "生活英语", 1)
            };
            db.Categories.AddRange(seeded);
            await db.SaveChangesAsync(ct);
            return seeded.Select(x => new PracticeCategoryDto(x.Id, x.Name, x.SortOrder)).ToList();
        }

        return list.Select(x => new PracticeCategoryDto(x.Id, x.Name, x.SortOrder)).ToList();
    }
}

// ---------- 新增 ----------

public sealed record CreateCategoryCommand(Guid UserId, string Name)
    : MediatR.IRequest<Result<PracticeCategoryDto>>;

public sealed class CreateCategoryCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<CreateCategoryCommand, Result<PracticeCategoryDto>>
{
    public async Task<Result<PracticeCategoryDto>> Handle(CreateCategoryCommand r, CancellationToken ct)
    {
        var name = (r.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return Result.Failure<PracticeCategoryDto>(Error.Validation("category.name", "类别名不能为空"));
        if (name.Length > 100)
            return Result.Failure<PracticeCategoryDto>(Error.Validation("category.name", "类别名最长 100 字符"));

        // 同名(忽略大小写)拒绝 —— 下拉里出现两个"工作面试"只会让人困惑
        var dup = await db.Categories.AsNoTracking()
            .AnyAsync(x => x.UserId == r.UserId
                && x.Name.ToLower() == name.ToLower(), ct);
        if (dup)
            return Result.Failure<PracticeCategoryDto>(
                Error.Validation("category.duplicate", $"类别「{name}」已存在"));

        var maxOrder = await db.Categories.AsNoTracking()
            .Where(x => x.UserId == r.UserId)
            .MaxAsync(x => (int?)x.SortOrder, ct) ?? -1;

        var entity = new PracticeCategory(r.UserId, name, maxOrder + 1);
        db.Categories.Add(entity);
        await db.SaveChangesAsync(ct);
        return Result.Success(new PracticeCategoryDto(entity.Id, entity.Name, entity.SortOrder));
    }
}

// ---------- 重命名 ----------

public sealed record RenameCategoryCommand(Guid UserId, Guid CategoryId, string Name)
    : MediatR.IRequest<Result<bool>>;

public sealed class RenameCategoryCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<RenameCategoryCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(RenameCategoryCommand r, CancellationToken ct)
    {
        var name = (r.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 100)
            return Result.Failure<bool>(Error.Validation("category.name", "类别名需为 1-100 字符"));

        var entity = await db.Categories
            .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.Id == r.CategoryId, ct);
        if (entity is null) return Result.Failure<bool>(Error.NotFound("category"));

        var dup = await db.Categories.AsNoTracking()
            .AnyAsync(x => x.UserId == r.UserId && x.Id != r.CategoryId
                && x.Name.ToLower() == name.ToLower(), ct);
        if (dup)
            return Result.Failure<bool>(
                Error.Validation("category.duplicate", $"类别「{name}」已存在"));

        entity.Rename(name);
        await db.SaveChangesAsync(ct);
        return Result.Success(true);
    }
}

// ---------- 排序 ----------

/// <summary>按提交顺序整体重排。列表里没出现的类别排到最后(保持原有相对顺序)。</summary>
public sealed record ReorderCategoriesCommand(Guid UserId, IReadOnlyList<Guid> OrderedIds)
    : MediatR.IRequest<Result<bool>>;

public sealed class ReorderCategoriesCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<ReorderCategoriesCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(ReorderCategoriesCommand r, CancellationToken ct)
    {
        var all = await db.Categories
            .Where(x => x.UserId == r.UserId)
            .ToListAsync(ct);

        var order = 0;
        foreach (var id in r.OrderedIds)
        {
            var entity = all.FirstOrDefault(x => x.Id == id);
            if (entity is null) continue;   // 不属于该用户的 id 直接跳过,不报错
            entity.SetSortOrder(order++);
        }
        // 剩下没提到的按原顺序排到后面
        foreach (var entity in all
            .Where(x => !r.OrderedIds.Contains(x.Id))
            .OrderBy(x => x.SortOrder))
        {
            entity.SetSortOrder(order++);
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(true);
    }
}

// ---------- 删除 ----------

/// <summary>删类别本身;挂在它下面的素材由数据库 SetNull 自动回"未分类"。</summary>
public sealed record DeleteCategoryCommand(Guid UserId, Guid CategoryId)
    : MediatR.IRequest<Result<bool>>;

public sealed class DeleteCategoryCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<DeleteCategoryCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(DeleteCategoryCommand r, CancellationToken ct)
    {
        var entity = await db.Categories
            .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.Id == r.CategoryId, ct);
        if (entity is null) return Result.Failure<bool>(Error.NotFound("category"));

        db.Categories.Remove(entity);
        await db.SaveChangesAsync(ct);
        return Result.Success(true);
    }
}
