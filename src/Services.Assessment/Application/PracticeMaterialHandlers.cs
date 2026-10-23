using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// 素材树:整树读写
// ============================================================

/// <summary>取我的整棵素材树(一次拿完,树规模小,不值得做增量加载)。</summary>
public sealed record GetMaterialTreeQuery(Guid UserId) : MediatR.IRequest<IReadOnlyList<MaterialNodeDto>>;

public sealed class GetMaterialTreeQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<GetMaterialTreeQuery, IReadOnlyList<MaterialNodeDto>>
{
    public async Task<IReadOnlyList<MaterialNodeDto>> Handle(GetMaterialTreeQuery r, CancellationToken ct)
    {
        var all = await db.Materials.AsNoTracking()
            .Where(x => x.UserId == r.UserId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return Build(all, null);
    }

    /// <summary>内存里拼树 —— 素材木只有几十个节点,一次查完比递归 SQL 更清晰也更快。</summary>
    private static List<MaterialNodeDto> Build(List<PracticeMaterial> all, Guid? parentId) =>
        all.Where(x => x.ParentId == parentId)
            .Select(x => new MaterialNodeDto(
                x.Id, x.Name, x.Kind == MaterialKind.Folder, x.SortOrder, x.IsExpanded,
                x.Content, Build(all, x.Id), x.MarkColor, x.CategoryId))
            .ToList();
}

/// <summary>整树覆盖写。</summary>
/// <remarks>
/// 为什么用"整树替换"而不是逐个增删改的细粒度端点:
///   前端的拖拽排序 / 重命名 / 改正文 / 新建删除是**高频小操作**,
///   如果每个都发一次请求,排序时会产生几十个请求且顺序敏感、容易半途失败留下脏状态。
///   整树提交是一个原子操作:要么全新状态,要么什么都没变。
///   素材树是纯私有小数据(几十节点、几 KB),整树传输的成本可以忽略。
/// </remarks>
public sealed record SaveMaterialTreeCommand(Guid UserId, IReadOnlyList<MaterialNodeIn> Nodes, bool Force = false)
    : MediatR.IRequest<Result<int>>;

/// <summary>前端提交的节点(Id 为 null = 新建)。MarkColor / CategoryId 可选 —— 旧客户端不传也能存。</summary>
public sealed record MaterialNodeIn(
    string? Id, string Name, bool Folder, string? Content, int SortOrder, bool Expanded,
    IReadOnlyList<MaterialNodeIn>? Children, string? MarkColor = null, Guid? CategoryId = null);

public sealed class SaveMaterialTreeCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<SaveMaterialTreeCommand, Result<int>>
{
    public async Task<Result<int>> Handle(SaveMaterialTreeCommand r, CancellationToken ct)
    {
        var existing = await db.Materials
            .IgnoreQueryFilters()
            .Where(x => x.UserId == r.UserId)
            .ToListAsync(ct);
        var byId = existing.ToDictionary(x => x.Id);

        // ★ 2026-09-25:类别校验集 —— 只接受"本用户自己的类别"。
        //   客户端传来的 categoryId 若是别人的/不存在的,一律当 null(未分类)处理,
        //   不让坏引用进库(外键虽然也会拦,但 500 不如静默修正体验好)。
        var myCategoryIds = (await db.Categories.AsNoTracking()
                .Where(x => x.UserId == r.UserId)
                .Select(x => x.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var incoming = new HashSet<Guid>();
        var touched = new List<PracticeMaterial>();

        // 根节点才挂类别 —— 类别是"练习场景",子节点跟随根,过滤才不会碎
        void ApplyCategory(PracticeMaterial entity, MaterialNodeIn n, Guid? parentId)
        {
            if (parentId is not null) return;
            Guid? cat = n.CategoryId is not null && myCategoryIds.Contains(n.CategoryId.Value)
                ? n.CategoryId
                : null;
            entity.SetCategory(cat);
        }

        void Walk(IReadOnlyList<MaterialNodeIn> nodes, Guid? parentId)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                var kind = n.Folder ? MaterialKind.Folder : MaterialKind.File;

                // 前端传来的 id:
                //   · 合法 GUID 且库里已存在(属当前用户) → 更新现有节点
                //   · 合法 GUID 但库里没有 → **采纳它作为新节点的 id**(见下)
                //   · "f_intro" 这类非 GUID 种子串 → 无法采纳,退化为服务端新发 GUID
                PracticeMaterial? entity = null;
                var hasClientId = Guid.TryParse(n.Id, out var gid);
                if (hasClientId) byId.TryGetValue(gid, out entity);

                if (entity is null)
                {
                    // ★ 第三十七轮 丢数据修复:采纳前端给的 GUID。
                    //   旧代码这里永远 new 一个服务端 GUID,导致
                    //   “前端发 A、服务端存 B” → 整树覆盖变成删了重建,
                    //   一旦有并发/重试就丢数据。采纳前端 id 后,
                    //   每次保存都是稳定的幂等 upsert。
                    entity = hasClientId
                        ? new PracticeMaterial(gid, r.UserId, parentId, kind, n.Name, n.Content, n.SortOrder)
                        : new PracticeMaterial(r.UserId, parentId, kind, n.Name, n.Content, n.SortOrder);
                    TryApplyMark(entity, n.MarkColor);   // ★ 第四十三轮:整树保存也带上标记色
                    ApplyCategory(entity, n, parentId);  // ★ 2026-09-25:新建即归类
                    db.Materials.Add(entity);
                }
                else
                {
                    // ★ 第三十九轮:若这个节点之前被软删过,重新出现就恢复它。
                    entity.Restore();
                    entity.Rename(n.Name);
                    // 文件夹不持有正文 → 不去碰它的 Content(避免无意义的 Touch);
                    // 文件才写正文。
                    if (kind == MaterialKind.File) entity.SetContent(n.Content);
                    entity.MoveTo(parentId, n.SortOrder);
                    entity.SetExpanded(n.Expanded);
                    TryApplyMark(entity, n.MarkColor);      // ★ 第四十三轮:整树保存也带上标记色
                    ApplyCategory(entity, n, parentId);     // ★ 2026-09-25:根节点类别随整树更新
                    touched.Add(entity);
                }

                incoming.Add(entity.Id);
                if (n.Children is { Count: > 0 }) Walk(n.Children, entity.Id);
            }
        }

        Walk(r.Nodes, null);

        // 前端没再提交的旧节点 = 用户已经删了它。
        //
        // ★★ 第三十九轮 数据安全修复(Forrest 报“录音记录都没了”):
        //   旧代码这里走 `db.Materials.RemoveRange(removed)` —— **硬删**。
        //   但整树覆盖只要出现一次不完整的提交(并发 PUT、网络重试、
        //   前端状态未回读、本地种子 id 未换 GUID),就会把用户真正存在的
        //   素材连根硬删,挂在其下的录音也随即变孤儿,
        //   界面按 MaterialId 查不到 → 用户看到“录音全没了”。
        //   **数据丢失是不可接受的** —— 现在一律改软删:
        //   标记 IsDeleted 并脱离子树(避免子节点被当成根节点重复显示)。
        //   已软删的行被查询过滤器隐藏,用户视角与真删一致,
        //   但数据仍在库里,可排查、可恢复。
        var removed = existing.Where(x => !incoming.Contains(x.Id) && !x.IsDeleted).ToList();

        // ★★ 第四十轮 数据安全熔断(Forrest 报"数据全丢失"):
        //   即使前端改成了软删,仍要防"客户端把初始/残缺树整套写回"这类
        //   覆盖事故 —— 它会一次性把大量真实节点标记删除。
        //   启发式:本次要抹掉的节点占比很高(>80%)、且抹掉数达到一定规模(>=5)
        //   时,先不动手,返回失败让客户端二次确认(除非显式 force)。
        //   为什么用比例而非绝对数:个人素材库体量小,5 个节点已算“有东西了”;
        //   单节点删除(改名/删一个)永远不会触发熔断,不影响正常使用。
        var aliveCount = existing.Count(x => !x.IsDeleted);
        if (!r.Force && removed.Count >= 5 && aliveCount > 0
            && (double)removed.Count / aliveCount > 0.8)
        {
            return Result.Failure<int>(Error.Validation("materials.guard",
                $"为防止误删,本次保存被拦下:它将删除 {removed.Count}/{aliveCount} 个素材"
                + "(超过 80%)。如确需删除请在请求中显式带上 force=true。"));
        }

        if (removed.Count > 0)
        {
            foreach (var m in removed) m.MarkDeleted();
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(incoming.Count);
    }

    /// <summary>
    /// ★ 第四十三轮:整树保存时应用标记色。
    ///   与单独的 PATCH 端点不同 —— 整树保存是"客户端眼里的全量快照",
    ///   这里若遇到非法颜色值**静默忽略**而不是抛异常:
    ///   一个坏颜色值不应该让用户整棵树的保存全部失败。
    /// </summary>
    private static void TryApplyMark(PracticeMaterial entity, string? markColor)
    {
        try { entity.SetMarkColor(markColor); }
        catch (ArgumentException) { /* 未知颜色值 → 保持原样 */ }
    }
}

// ============================================================
// ★ 第四十三轮(Forrest):单独设置标记色 —— 轻量端点
// ============================================================

/// <summary>
/// 为什么不直接复用整树 PUT:用户点一下颜色就该立即生效,
///   若为此发整树 PUT,会连带触发"防误删熔断/软删对比"整套逻辑,
///   又重又危险。标记是节点的单字段属性,一个 PATCH 足矣。
/// </summary>
public sealed record SetMaterialMarkCommand(Guid UserId, Guid MaterialId, string? MarkColor)
    : MediatR.IRequest<Result<bool>>;

public sealed class SetMaterialMarkCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<SetMaterialMarkCommand, Result<bool>>
{
    public async Task<Result<bool>> Handle(SetMaterialMarkCommand r, CancellationToken ct)
    {
        var entity = await db.Materials
            .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.Id == r.MaterialId, ct);
        if (entity is null) return Result.Failure<bool>(Error.NotFound("material"));

        try { entity.SetMarkColor(r.MarkColor); }
        catch (ArgumentException ex)
        {
            return Result.Failure<bool>(Error.Validation("materials.markColor", ex.Message));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(true);
    }
}

