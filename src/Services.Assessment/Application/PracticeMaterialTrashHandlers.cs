using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// 票124:AI Practice 素材回收站
//
// 事故背景:整树覆盖保存会把「未出现在提交树里的存量节点」一律软删
// (MarkDeleted),而应用内没有任何列出/恢复已删素材的入口 —— 用户只能
// 求助并最终靠一条经批准的 UPDATE 才恢复(135 条素材)。
//
// 本文件补这个缺口:列出我的顶层已删素材 + 单项恢复 + 全部恢复。
//
// 三条硬约束(照规格 §5/§6):
//   1. 零迁移 —— 删除时间取行 UpdatedAt(规格 §4.2 已证:一行处于已删状态时,
//      它的 UpdatedAt 必等于最近一次被 MarkDeleted 的时刻)。
//   2. 恢复响应回**整树包** { restored, treeVersion, nodes }—— 只回版本号不回
//      节点会制造「版本已对齐、内容缺项」的再删通道(规格 §6 契约 C1)。
//   3. 恢复单位 = 目标节点 + 全部已删后代 + 已删祖先链(规格 §5.3/D3),
//      删除以子树为单位发生,恢复必须同粒度。
// ============================================================

/// <summary>
/// 回收站清单项(顶层已删节点)。
/// </summary>
/// <param name="Id">该行 id。</param>
/// <param name="Name">素材名。</param>
/// <param name="Folder">是否文件夹。</param>
/// <param name="ParentId">原父节点 id(父行已删/不存在时仍为原值)。</param>
/// <param name="Path">祖先名链(不含自身,根项为 null);分隔符 " / "。</param>
/// <param name="CategoryId">该项所属根的类别(自身是根则取自身)。</param>
/// <param name="CategoryName">类别名;类别不存在/未分类为 null。</param>
/// <param name="MarkColor">标记色(null = 无)。</param>
/// <param name="DeletedAt">删除时刻代理 = 行 UpdatedAt(规格 §4.2)。</param>
/// <param name="DescendantTotal">全部后代数(删/未删都算)。</param>
/// <param name="DescendantDeleted">其中已删数。</param>
/// <param name="RecordingCount">子树内文件所挂未删录音数。</param>
public sealed record DeletedMaterialDto(
    Guid Id, string Name, bool Folder, Guid? ParentId, string? Path,
    Guid? CategoryId, string? CategoryName, string? MarkColor,
    DateTimeOffset DeletedAt, int DescendantTotal, int DescendantDeleted, int RecordingCount);

/// <summary>恢复结果 —— 整树包 + 本次实际翻转的行数(规格 §5.1/D5)。</summary>
public sealed record RestoreMaterialsResult(int Restored, string TreeVersion, IReadOnlyList<MaterialNodeDto> Nodes);

/// <summary>取我的回收站清单(顶层已删节点)。</summary>
public sealed record GetMaterialTrashQuery(Guid UserId) : MediatR.IRequest<IReadOnlyList<DeletedMaterialDto>>;

/// <summary>恢复某个已删素材(含已删祖先链 + 全部已删后代)。</summary>
public sealed record RestoreMaterialCommand(Guid UserId, Guid MaterialId) : MediatR.IRequest<Result<RestoreMaterialsResult>>;

/// <summary>恢复我的全部已删素材。</summary>
public sealed record RestoreAllMaterialsCommand(Guid UserId) : MediatR.IRequest<Result<RestoreMaterialsResult>>;

/// <summary>
/// 素材树拼装 —— 与 GetMaterialTreeQueryHandler.Build **同口径**。
/// </summary>
/// <remarks>
/// 规格 §5.1 允许「抽共用或复制其私有 Build 的等价实现」,并要求用测试钉住两边
/// 一致(验收 T8)。这里抽成 internal 静态方法供两处共用 —— 单一事实源,
/// 天然不可能漂移。
/// <para>
/// 入参 <paramref name="all"/> 必须**已按 (SortOrder, CreatedAt) 升序排好**,
/// 逐层筛选保持全局排序 → 同层内按 (SortOrder, CreatedAt) 升序,
/// 与 GetMaterialTreeQueryHandler 的查询层排序一致。
/// </para>
/// </remarks>
internal static class MaterialTreeBuilder
{
    /// <summary>内存里拼树 —— 素材树只有几十个节点,一次查完比递归 SQL 更清晰也更快。</summary>
    public static List<MaterialNodeDto> Build(List<PracticeMaterial> all, Guid? parentId) =>
        all.Where(x => x.ParentId == parentId)
            .Select(x => new MaterialNodeDto(
                x.Id, x.Name, x.Kind == MaterialKind.Folder, x.SortOrder, x.IsExpanded,
                x.Content, Build(all, x.Id), x.MarkColor, x.CategoryId))
            .ToList();
}

/// <summary>
/// 取我的回收站清单(顶层已删节点)。
/// </summary>
public sealed class GetMaterialTrashQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<GetMaterialTrashQuery, IReadOnlyList<DeletedMaterialDto>>
{
    public async Task<IReadOnlyList<DeletedMaterialDto>> Handle(GetMaterialTrashQuery r, CancellationToken ct)
    {
        // IgnoreQueryFilters:全局过滤器只放未删行 —— 回收站要的就是已删行。
        var all = await db.Materials.IgnoreQueryFilters()
            .Where(x => x.UserId == r.UserId)
            .ToListAsync(ct);

        if (all.Count == 0) return [];

        var byId = all.ToDictionary(x => x.Id);
        var childrenOf = BuildChildIndex(all);

        // 类别表(仅取本用户的;类别被硬删时素材 CategoryId 已被 SetNull 置空)。
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.UserId == r.UserId)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // 录音:子树内全部节点 id 集合上数未删录音(规格 §5.3 第 4 步)。
        var allIds = all.Select(x => x.Id).ToList();
        var recordingCounts = await db.Recordings.AsNoTracking()
            .Where(x => x.UserId == r.UserId && !x.IsDeleted && allIds.Contains(x.MaterialId))
            .GroupBy(x => x.MaterialId)
            .Select(g => new { MaterialId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.MaterialId, x => x.Count, ct);

        // 顶层已删节点 = IsDeleted && (父行不存在 || 父行未删)。
        // 已删文件夹内部的已删子项不单独列(它们是该文件夹的子树计数的一部分);
        // 父行存活的已删项(在活文件夹里删的单个文件)照常单独列出。
        var tops = all.Where(x => x.IsDeleted
                && (x.ParentId is null || !byId.TryGetValue(x.ParentId.Value, out var p) || !p.IsDeleted))
            .ToList();

        var result = new List<DeletedMaterialDto>(tops.Count);
        foreach (var top in tops)
        {
            var subtreeIds = CollectSubtreeIds(top.Id, childrenOf);

            // Path:沿 ParentId 上溯至根的祖先名链。上溯走**全量图**(含已删行):
            // 祖先链可含已删祖先(如「父行存活、祖父行已删」的孤儿链下的顶层项);
            // 不得只在存活行上拼 Path,否则这类项的路径会被截断。
            var path = BuildPath(top, byId);

            // 类别取其**根祖先**的 CategoryId(自身是根则取自身)。
            var root = FindRoot(top, byId);
            var categoryId = root.CategoryId;

            result.Add(new DeletedMaterialDto(
                Id: top.Id,
                Name: top.Name,
                Folder: top.Kind == MaterialKind.Folder,
                ParentId: top.ParentId,
                Path: path,
                CategoryId: categoryId,
                CategoryName: categoryId is not null && categories.TryGetValue(categoryId.Value, out var cn) ? cn : null,
                MarkColor: top.MarkColor,
                // deletedAt := UpdatedAt(规格 §4.2 的等价性证明)。
                DeletedAt: top.UpdatedAt,
                DescendantTotal: subtreeIds.Count,
                DescendantDeleted: subtreeIds.Count(id => byId[id].IsDeleted),
                // ★ D-124-1 复测修复:规格 §5.3 写「子树内**全部**节点」—— 含顶层项自身。
                //   CollectSubtreeIds 只收后代(祖先名链/后代计数语义如此),
                //   录音数若只对它求和会漏掉顶层文件自己挂的录音(复测活体两次复现)。
                RecordingCount: subtreeIds.Sum(id => recordingCounts.GetValueOrDefault(id))
                    + recordingCounts.GetValueOrDefault(top.Id)));
        }

        // 排序:DeletedAt 降序,同值以 CreatedAt 降序、再以 Id 升序定序(确定性,便于测试钉住)。
        return result
            .OrderByDescending(x => x.DeletedAt)
            .ThenByDescending(x => byId[x.Id].CreatedAt)
            .ThenBy(x => x.Id)
            .ToList();
    }

    /// <summary>父 id → 子 id 列表(全量图,含已删行)。</summary>
    private static Dictionary<Guid, List<Guid>> BuildChildIndex(List<PracticeMaterial> all)
    {
        var index = new Dictionary<Guid, List<Guid>>();
        foreach (var x in all)
        {
            if (x.ParentId is null) continue;
            if (!index.TryGetValue(x.ParentId.Value, out var list))
                index[x.ParentId.Value] = list = [];
            list.Add(x.Id);
        }
        return index;
    }

    /// <summary>沿全量图 BFS 收集某节点的全部后代 id(不含自身)。</summary>
    private static HashSet<Guid> CollectSubtreeIds(Guid root, Dictionary<Guid, List<Guid>> childrenOf)
    {
        var ids = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!childrenOf.TryGetValue(cur, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!ids.Add(kid)) continue;
                queue.Enqueue(kid);
            }
        }
        return ids;
    }

    /// <summary>祖先名链(不含自身);无可达祖先为 null。走全量图,含已删祖先。</summary>
    private static string? BuildPath(PracticeMaterial node, Dictionary<Guid, PracticeMaterial> byId)
    {
        var names = new List<string>();
        var cur = node.ParentId;
        var guard = new HashSet<Guid>();
        while (cur is not null && byId.TryGetValue(cur.Value, out var parent))
        {
            // 防环(悬空/异常数据):同一节点不重复上溯。
            if (!guard.Add(parent.Id)) break;
            names.Add(parent.Name);
            cur = parent.ParentId;
        }
        if (names.Count == 0) return null;
        names.Reverse();
        return string.Join(" / ", names);
    }

    /// <summary>找到该节点的根祖先(自身即根时返回自身)。走全量图。</summary>
    private static PracticeMaterial FindRoot(PracticeMaterial node, Dictionary<Guid, PracticeMaterial> byId)
    {
        var cur = node;
        var guard = new HashSet<Guid>();
        while (cur.ParentId is not null && byId.TryGetValue(cur.ParentId.Value, out var parent))
        {
            if (!guard.Add(parent.Id)) break;
            cur = parent;
        }
        return cur;
    }
}

/// <summary>
/// 恢复某个已删素材(含已删祖先链 + 全部已删后代)。
/// </summary>
public sealed class RestoreMaterialCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<RestoreMaterialCommand, Result<RestoreMaterialsResult>>
{
    public async Task<Result<RestoreMaterialsResult>> Handle(RestoreMaterialCommand r, CancellationToken ct)
    {
        // IgnoreQueryFilters + IsDeleted:三种不合资格情形(不存在/他人的/未删的)
        // 同口径 404,不泄露存在性 —— 同 SetMaterialMarkCommandHandler 的 NotFound 惯例。
        var target = await db.Materials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == r.MaterialId && x.UserId == r.UserId && x.IsDeleted, ct);
        if (target is null)
            return Result.Failure<RestoreMaterialsResult>(Error.NotFound("material"));

        var all = await db.Materials.IgnoreQueryFilters()
            .Where(x => x.UserId == r.UserId)
            .ToListAsync(ct);
        var byId = all.ToDictionary(x => x.Id);
        var childrenOf = BuildChildIndex(all);

        var restored = 0;

        // ---- 祖先链修复(规格 §5.3 第 2 步)----
        // 沿 ParentId 上溯全图,直至到达根(ParentId == null)或断裂点。
        //   · 遇已删祖先 → 一并 Restore()(根向先行);
        //   · 断裂点(某节点的父行在全图中不存在)→ 对它 MoveTo(null, 原 SortOrder) 重根,
        //     使其下整条链(含目标与途中恢复的祖先)成为根级可达。
        // 断裂点不限于目标的直接父行:断在链上任意一层同此办理 ——
        // 只重根目标而其祖先仍指向不存在的父行,不满足「成功后该节点必出现在树里」。
        // 重根不改断裂点节点的 CategoryId(MoveTo 不触及该字段,原样保留)。
        var chain = new List<PracticeMaterial>();
        var cur = target;
        var guard = new HashSet<Guid>();
        while (true)
        {
            if (cur.ParentId is null) break;
            if (!byId.TryGetValue(cur.ParentId.Value, out var parent))
            {
                // 断裂点 = cur(它的父行不存在)→ 重根。
                // 传原 SortOrder:SortOrder 不变、CategoryId 不变(规格 §5.3 明文要求)。
                cur.MoveTo(null, cur.SortOrder);
                break;
            }
            if (!guard.Add(parent.Id)) break; // 防环
            chain.Add(parent);
            cur = parent;
        }

        // 根向先行:把链上(自顶向下)已删的祖先逐个恢复。
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var ancestor = chain[i];
            if (!ancestor.IsDeleted) continue;
            ancestor.Restore();
            restored++;
        }

        // ---- 目标本身 ----
        if (target.IsDeleted)
        {
            target.Restore();
            restored++;
        }

        // ---- 后代恢复(规格 §5.3 第 3 步):目标的全部已删后代逐个 Restore() ----
        foreach (var id in CollectSubtreeIds(target.Id, childrenOf))
        {
            var child = byId[id];
            if (!child.IsDeleted) continue;
            child.Restore();
            restored++;
        }

        await db.SaveChangesAsync(ct);

        // 回树包:treeVersion 以 MaterialTreeVersion.ComputeForUserAsync 重算,
        // nodes 以与树查询同一拼装产出(契约 C1 —— 必须回节点,不只是版本号)。
        var tree = await BuildTreePackageAsync(db, r.UserId, ct);
        return Result.Success(new RestoreMaterialsResult(restored, tree.TreeVersion, tree.Nodes));
    }

    internal static Dictionary<Guid, List<Guid>> BuildChildIndex(List<PracticeMaterial> all)
    {
        var index = new Dictionary<Guid, List<Guid>>();
        foreach (var x in all)
        {
            if (x.ParentId is null) continue;
            if (!index.TryGetValue(x.ParentId.Value, out var list))
                index[x.ParentId.Value] = list = [];
            list.Add(x.Id);
        }
        return index;
    }

    internal static HashSet<Guid> CollectSubtreeIds(Guid root, Dictionary<Guid, List<Guid>> childrenOf)
    {
        var ids = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!childrenOf.TryGetValue(cur, out var kids)) continue;
            foreach (var kid in kids)
            {
                if (!ids.Add(kid)) continue;
                queue.Enqueue(kid);
            }
        }
        return ids;
    }

    /// <summary>产出与 GET /materials 完全同口径的树包(拼装共用 MaterialTreeBuilder)。</summary>
    internal static async Task<MaterialTreeDto> BuildTreePackageAsync(
        AssessmentDbContext db, Guid userId, CancellationToken ct)
    {
        var visible = await db.Materials.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var treeVersion = await MaterialTreeVersion.ComputeForUserAsync(db, userId, ct);
        return new MaterialTreeDto(treeVersion, MaterialTreeBuilder.Build(visible, null));
    }
}

/// <summary>
/// 恢复我的全部已删素材。
/// </summary>
public sealed class RestoreAllMaterialsCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<RestoreAllMaterialsCommand, Result<RestoreMaterialsResult>>
{
    public async Task<Result<RestoreMaterialsResult>> Handle(RestoreAllMaterialsCommand r, CancellationToken ct)
    {
        var deleted = await db.Materials.IgnoreQueryFilters()
            .Where(x => x.UserId == r.UserId && x.IsDeleted)
            .ToListAsync(ct);

        // restored = 本次实际翻转的行数(规格 §5.3 RestoreAll)。
        // Restore() 是幂等的(已删才翻),这里因筛选条件已限定 IsDeleted,
        // 每一行都必然翻转 → 计数即行数;仍按行判 IsDeleted 以口径统一。
        var restored = 0;
        foreach (var m in deleted)
        {
            if (!m.IsDeleted) continue;
            m.Restore();
            restored++;
        }

        await db.SaveChangesAsync(ct);

        var tree = await RestoreMaterialCommandHandler.BuildTreePackageAsync(db, r.UserId, ct);
        return Result.Success(new RestoreMaterialsResult(restored, tree.TreeVersion, tree.Nodes));
    }
}
