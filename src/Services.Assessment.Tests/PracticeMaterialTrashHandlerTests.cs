using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票124(AI Practice 素材回收站)后端行为测试。
///
/// 覆盖规格 §11.1 的 T1–T14:
///   T1 清单集合与顶层规则 / T2 排序与 deletedAt / T3 恢复单文件(+录音) /
///   T4 恢复文件夹(含「父删子活」) / T5 祖先链恢复 / T6 隔离与 404 /
///   T7 全部恢复 / T8 树包一致性 / T9 不再被碎纸 / T10 陈旧保存被拦 /
///   T11 同名不冲突 / T12 类别已删 / T13 悬空父行 / T14 多级断裂。
///
/// DB 门控同 Ticket111 先例:连不上库跳过;每例独立随机 UserId,Dispose 清数据
/// (素材清理须 IgnoreQueryFilters —— 软删行被全局过滤器挡住)。
/// </summary>
public sealed class PracticeMaterialTrashHandlerTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb();
        var mats = await db.Materials.IgnoreQueryFilters().Where(m => _users.Contains(m.UserId)).ToListAsync();
        db.Materials.RemoveRange(mats);
        var recs = await db.Recordings.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)).ToListAsync();
        db.Recordings.RemoveRange(recs);
        var cats = await db.Categories.Where(c => _users.Contains(c.UserId)).ToListAsync();
        db.Categories.RemoveRange(cats);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    // ---------------- 造数帮手 ----------------

    private async Task<PracticeMaterial> SeedAsync(
        Guid user, Guid? parent, MaterialKind kind, string name, string? content = null,
        int sortOrder = 0, string? markColor = null, Guid? categoryId = null)
    {
        await using var db = NewDb();
        var m = new PracticeMaterial(user, parent, kind, name, content, sortOrder);
        if (markColor is not null) m.SetMarkColor(markColor);
        if (categoryId is not null) m.SetCategory(categoryId);
        db.Materials.Add(m);
        await db.SaveChangesAsync();
        return m;
    }

    /// <summary>软删一批行(模拟整树保存把未提交节点标记删除)。</summary>
    private async Task SoftDeleteAsync(Guid user, params Guid[] ids)
    {
        await using var db = NewDb();
        var rows = await db.Materials.IgnoreQueryFilters()
            .Where(x => x.UserId == user && ids.Contains(x.Id)).ToListAsync();
        foreach (var r in rows) r.MarkDeleted();
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<DeletedMaterialDto>> ListTrashAsync(Guid user)
    {
        await using var db = NewDb();
        return await new GetMaterialTrashQueryHandler(db).Handle(new GetMaterialTrashQuery(user), default);
    }

    private async Task<Result<RestoreMaterialsResult>> RestoreAsync(Guid user, Guid id)
    {
        await using var db = NewDb();
        return await new RestoreMaterialCommandHandler(db).Handle(new RestoreMaterialCommand(user, id), default);
    }

    private async Task<Result<RestoreMaterialsResult>> RestoreAllAsync(Guid user)
    {
        await using var db = NewDb();
        return await new RestoreAllMaterialsCommandHandler(db).Handle(new RestoreAllMaterialsCommand(user), default);
    }

    private async Task<MaterialTreeDto> GetTreeAsync(Guid user)
    {
        await using var db = NewDb();
        return await new GetMaterialTreeQueryHandler(db).Handle(new GetMaterialTreeQuery(user), default);
    }

    private async Task<bool> IsDeletedAsync(Guid user, Guid id)
    {
        await using var db = NewDb();
        var row = await db.Materials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == user);
        return row?.IsDeleted ?? false;
    }

    private static MaterialNodeDto? Find(IReadOnlyList<MaterialNodeDto> nodes, string name)
    {
        foreach (var n in nodes)
        {
            if (n.Name == name) return n;
            var hit = Find(n.Children, name);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>把树摊平成列表(便于「全树含某节点」类断言)。</summary>
    private static List<MaterialNodeDto> Flatten(IReadOnlyList<MaterialNodeDto> nodes)
    {
        var acc = new List<MaterialNodeDto>();
        void Walk(IReadOnlyList<MaterialNodeDto> ns)
        {
            foreach (var n in ns) { acc.Add(n); Walk(n.Children); }
        }
        Walk(nodes);
        return acc;
    }

    // ---------------- T1 清单集合与顶层规则 ----------------

    [Fact] // 删文件夹(整子树已删)+ 在活文件夹内删单文件 → 清单恰含两项;子树计数与造数一致;单文件 Path = 父文件夹名
    public async Task T1_TrashList_TopLevelRule_And_SubtreeCounts()
    {
        if (!_available) return;
        var user = NewUser();

        var folderA = await SeedAsync(user, null, MaterialKind.Folder, "A");
        var a1 = await SeedAsync(user, folderA.Id, MaterialKind.File, "a1", "c-a1");
        var a2 = await SeedAsync(user, folderA.Id, MaterialKind.File, "a2", "c-a2");
        var liveFolder = await SeedAsync(user, null, MaterialKind.Folder, "Live");
        var liveFile = await SeedAsync(user, liveFolder.Id, MaterialKind.File, "live1", "c-live1");

        // 删文件夹 A(其子树随之在清理时被标记)+ 删活文件夹内的单文件
        await SoftDeleteAsync(user, folderA.Id, a1.Id, a2.Id, liveFile.Id);

        var trash = await ListTrashAsync(user);

        // 顶层已删节点 = {A, live1};a1/a2 是 A 的子树,不单独列。
        trash.Select(x => x.Name).Should().BeEquivalentTo(["A", "live1"]);

        var a = trash.Single(x => x.Name == "A");
        a.Folder.Should().BeTrue();
        a.DescendantTotal.Should().Be(2);
        a.DescendantDeleted.Should().Be(2);

        // 活文件夹内的已删单文件:父行存活 → 单独列出,Path = 父文件夹名
        var lf = trash.Single(x => x.Name == "live1");
        lf.Folder.Should().BeFalse();
        lf.Path.Should().Be("Live");
        lf.DescendantTotal.Should().Be(0);
        lf.DescendantDeleted.Should().Be(0);
    }

    // ---------------- T2 排序与 deletedAt ----------------

    [Fact] // 三项不同时刻删除 → 清单按 UpdatedAt 降序;DeletedAt 字段 == 行 UpdatedAt
    public async Task T2_TrashList_SortedByDeletedAtDesc_AndDeletedAtEqualsUpdatedAt()
    {
        if (!_available) return;
        var user = NewUser();

        var first = await SeedAsync(user, null, MaterialKind.File, "first");
        await Task.Delay(30);
        var second = await SeedAsync(user, null, MaterialKind.File, "second");
        await Task.Delay(30);
        var third = await SeedAsync(user, null, MaterialKind.File, "third");

        await SoftDeleteAsync(user, first.Id);
        await Task.Delay(30);
        await SoftDeleteAsync(user, second.Id);
        await Task.Delay(30);
        await SoftDeleteAsync(user, third.Id);

        var trash = await ListTrashAsync(user);
        trash.Select(x => x.Name).Should().Equal("third", "second", "first");

        // deletedAt == 行 UpdatedAt(零迁移:删除时刻代理)
        await using var db = NewDb();
        var row = await db.Materials.IgnoreQueryFilters().FirstAsync(x => x.Id == third.Id);
        trash.Single(x => x.Name == "third").DeletedAt.Should().Be(row.UpdatedAt);
    }

    // ---------------- T3 恢复单文件(内容/标记/类别/位置原样)+ 录音复现 ----------------

    [Fact] // 恢复后 GET 树含该文件,Content/MarkColor/CategoryId/SortOrder 与删前逐字段相等;录音 id 集合不变
    public async Task T3_RestoreSingleFile_FieldsPreserved_AndRecordingsIntact()
    {
        if (!_available) return;
        var user = NewUser();

        var cat = await SeedCategoryAsync(user, "面试");
        var folder = await SeedAsync(user, null, MaterialKind.Folder, "F");
        var file = await SeedAsync(user, folder.Id, MaterialKind.File, "target", "content-x",
            sortOrder: 7, markColor: "red", categoryId: cat.Id);

        // 挂一条录音
        Guid recId;
        await using (var db = NewDb())
        {
            var rec = new PracticeRecording(Guid.NewGuid(), user, file.Id, "t124/x.wav", "audio/wav", 100, 1.5);
            db.Recordings.Add(rec);
            await db.SaveChangesAsync();
            recId = rec.Id;
        }

        var before = Flatten((await GetTreeAsync(user)).Nodes);
        var beforeFile = before.Single(n => n.Id == file.Id);

        await SoftDeleteAsync(user, file.Id);
        (await ListTrashAsync(user)).Should().HaveCount(1);

        var restored = await RestoreAsync(user, file.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);
        restored.Value.Restored.Should().Be(1);

        var after = Flatten((await GetTreeAsync(user)).Nodes);
        var afterFile = after.Single(n => n.Id == file.Id);
        afterFile.Content.Should().Be(beforeFile.Content);
        afterFile.MarkColor.Should().Be(beforeFile.MarkColor);
        afterFile.SortOrder.Should().Be(beforeFile.SortOrder);
        afterFile.CategoryId.Should().Be(beforeFile.CategoryId);
        afterFile.Folder.Should().Be(beforeFile.Folder);
        afterFile.Expanded.Should().Be(beforeFile.Expanded);

        // 录音原样复现(R5):录音行不被恢复动作触碰
        await using var db2 = NewDb();
        var recs = await db2.Recordings.Where(r => r.UserId == user && !r.IsDeleted).ToListAsync();
        recs.Select(r => r.Id).Should().Contain(recId);
    }

    private async Task<PracticeCategory> SeedCategoryAsync(Guid user, string name)
    {
        await using var db = NewDb();
        var c = new PracticeCategory(user, name);
        db.Categories.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    // ---------------- T4 恢复文件夹(含「父删子活」) ----------------

    [Fact] // 恢复文件夹 → 全部已删后代翻转为未删;「父删子活」子项随祖先恢复重新可见
    public async Task T4_RestoreFolder_AllDeletedDescendants_AndOrphanChildRevived()
    {
        if (!_available) return;
        var user = NewUser();

        var folder = await SeedAsync(user, null, MaterialKind.Folder, "Folder");
        var d1 = await SeedAsync(user, folder.Id, MaterialKind.File, "d1", "c1");
        var d2 = await SeedAsync(user, folder.Id, MaterialKind.File, "d2", "c2");
        // 「父删子活」:子项未删、父已删 → 恢复父文件夹后子项在树中可见
        var alive = await SeedAsync(user, folder.Id, MaterialKind.File, "aliveChild", "c3");

        await SoftDeleteAsync(user, folder.Id, d1.Id, d2.Id);
        await SoftDeleteAsync(user, folder.Id); // 只删父(aliveChild 仍存活)

        var restored = await RestoreAsync(user, folder.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        var tree = await GetTreeAsync(user);
        var f = Find(tree.Nodes, "Folder");
        f.Should().NotBeNull();
        Find(f!.Children, "d1").Should().NotBeNull();
        Find(f.Children, "d2").Should().NotBeNull();
        Find(f.Children, "aliveChild").Should().NotBeNull("「父删子活」子项应随祖先恢复重现");
    }

    // ---------------- T5 祖先链恢复 ----------------

    [Fact] // 对深层已删子项 id 调 restore → 其已删祖先一并恢复、该项出现在树里
    public async Task T5_RestoreDeepChild_AlsoRestoresDeletedAncestors()
    {
        if (!_available) return;
        var user = NewUser();

        var root = await SeedAsync(user, null, MaterialKind.Folder, "Root");
        var mid = await SeedAsync(user, root.Id, MaterialKind.Folder, "Mid");
        var leaf = await SeedAsync(user, mid.Id, MaterialKind.File, "Leaf", "c");

        await SoftDeleteAsync(user, root.Id, mid.Id, leaf.Id);

        var restored = await RestoreAsync(user, leaf.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);
        restored.Value.Restored.Should().Be(3, "祖先链 + 目标都应计入翻转行数");

        var tree = await GetTreeAsync(user);
        var r = Find(tree.Nodes, "Root");
        r.Should().NotBeNull();
        Find(r!.Children, "Mid").Should().NotBeNull();
        Find(Find(r.Children, "Mid")!.Children, "Leaf").Should().NotBeNull();
    }

    // ---------------- T6 隔离与 404 ----------------

    [Fact] // 用户 B 清单为空;B 对 A 的已删项 restore → 404;A 对未删项 / 不存在 id restore → 404(三者同口径)
    public async Task T6_CrossUserIsolation_And404_SameCode()
    {
        if (!_available) return;
        var a = NewUser();
        var b = NewUser();

        var deleted = await SeedAsync(a, null, MaterialKind.File, "a-deleted");
        var alive = await SeedAsync(a, null, MaterialKind.File, "a-alive");
        await SoftDeleteAsync(a, deleted.Id);

        // B 的清单为空
        (await ListTrashAsync(b)).Should().BeEmpty();

        // B 恢复 A 的已删项 → 404
        var cross = await RestoreAsync(b, deleted.Id);
        cross.IsSuccess.Should().BeFalse();
        cross.Error.Type.Should().Be(ErrorType.NotFound);

        // A 恢复未删项 → 404
        var notDeleted = await RestoreAsync(a, alive.Id);
        notDeleted.IsSuccess.Should().BeFalse();
        notDeleted.Error.Type.Should().Be(ErrorType.NotFound);

        // A 恢复不存在的 id → 404
        var missing = await RestoreAsync(a, Guid.NewGuid());
        missing.IsSuccess.Should().BeFalse();
        missing.Error.Type.Should().Be(ErrorType.NotFound);

        // 三者同口径:错误码相同
        cross.Error.Code.Should().Be(notDeleted.Error.Code);
        notDeleted.Error.Code.Should().Be(missing.Error.Code);
    }

    // ---------------- T7 全部恢复 ----------------

    [Fact] // 多项已删 → restore-all 后清单为空、树完整;再调一次 → restored=0 不报错
    public async Task T7_RestoreAll_ClearsTrash_AndIsIdempotent()
    {
        if (!_available) return;
        var user = NewUser();

        var f = await SeedAsync(user, null, MaterialKind.Folder, "F");
        var c = await SeedAsync(user, f.Id, MaterialKind.File, "c");
        var r = await SeedAsync(user, null, MaterialKind.File, "r");
        await SoftDeleteAsync(user, f.Id, c.Id, r.Id);

        (await ListTrashAsync(user)).Should().HaveCount(2);

        var all = await RestoreAllAsync(user);
        all.IsSuccess.Should().BeTrue(all.Error.Description);
        all.Value.Restored.Should().Be(2, "顶层已删节点数(A 的子树折叠计数内)");

        (await ListTrashAsync(user)).Should().BeEmpty();

        var tree = await GetTreeAsync(user);
        Find(tree.Nodes, "F").Should().NotBeNull();
        Find(tree.Nodes, "r").Should().NotBeNull();

        // 再调一次 → restored=0,不报错
        var again = await RestoreAllAsync(user);
        again.IsSuccess.Should().BeTrue(again.Error.Description);
        again.Value.Restored.Should().Be(0);
    }

    // ---------------- T8 树包一致性 ----------------

    [Fact] // 恢复响应的 treeVersion 与紧随的 GET materials 逐字相等;nodes 深度相等(两处拼装同口径)
    public async Task T8_RestoreResponse_TreePackage_MatchesGetMaterials()
    {
        if (!_available) return;
        var user = NewUser();

        var folder = await SeedAsync(user, null, MaterialKind.Folder, "F");
        var child = await SeedAsync(user, folder.Id, MaterialKind.File, "c", "cc");
        var root = await SeedAsync(user, null, MaterialKind.File, "r", "rr");
        await SoftDeleteAsync(user, folder.Id, child.Id, root.Id);

        var restored = await RestoreAsync(user, root.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        var get = await GetTreeAsync(user);
        restored.Value.TreeVersion.Should().Be(get.TreeVersion);

        // nodes 深度相等:逐层结构一致
        var respFlat = Flatten(restored.Value.Nodes);
        var getFlat = Flatten(get.Nodes);
        respFlat.Select(n => (n.Id, n.Name, n.Folder, n.SortOrder, n.Expanded, n.Content, n.MarkColor, n.CategoryId))
            .Should().BeEquivalentTo(getFlat.Select(n => (n.Id, n.Name, n.Folder, n.SortOrder, n.Expanded, n.Content, n.MarkColor, n.CategoryId)));
    }

    // ---------------- T9 不再被碎纸(核心回归) ----------------

    [Fact] // 恢复后以恢复响应的树为 payload 整树保存 → 该项仍未删、树查询仍含它(S1)
    public async Task T9_RestoreThenSave_TreeItemNotSilentlyDeleted()
    {
        if (!_available) return;
        var user = NewUser();

        var keep = await SeedAsync(user, null, MaterialKind.File, "keep", "c-keep");
        var gone = await SeedAsync(user, null, MaterialKind.File, "gone", "c-gone");
        await SoftDeleteAsync(user, gone.Id);

        var restored = await RestoreAsync(user, gone.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        // 以恢复响应的树为 payload 整树保存(模拟 C1 采纳)
        await using (var db = NewDb())
        {
            var save = await new SaveMaterialTreeCommandHandler(db).Handle(
                new SaveMaterialTreeCommand(user, ToNodes(restored.Value.Nodes), false, restored.Value.TreeVersion), default);
            save.IsSuccess.Should().BeTrue(save.Error.Description);
        }

        (await IsDeletedAsync(user, gone.Id)).Should().BeFalse("恢复项不得被后续整树保存静默再删");
        (await GetTreeAsync(user)).Nodes.Select(n => n.Name).Should().Contain("gone");
    }

    private static List<MaterialNodeIn> ToNodes(IReadOnlyList<MaterialNodeDto> nodes) =>
        nodes.Select(n => new MaterialNodeIn(
            n.Id.ToString(), n.Name, n.Folder, n.Content, n.SortOrder, n.Expanded,
            ToNodes(n.Children), n.MarkColor)).ToList();

    // ---------------- T10 陈旧保存被拦 ----------------

    [Fact] // 恢复前取得的 treeVersion 在恢复后提交整树保存 → 409 concurrency_conflict 且库内行状态零变化(S2)
    public async Task T10_StaleSaveAfterRestore_Rejected_ZeroWrites()
    {
        if (!_available) return;
        var user = NewUser();

        var keep = await SeedAsync(user, null, MaterialKind.File, "keep", "c-keep");
        var gone = await SeedAsync(user, null, MaterialKind.File, "gone", "c-gone");
        var staleVersion = (await GetTreeAsync(user)).TreeVersion;

        await SoftDeleteAsync(user, gone.Id);
        var restored = await RestoreAsync(user, gone.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        // 持恢复前的陈旧版本提交旧树 → 前置拒绝,零写入
        await using (var db = NewDb())
        {
            var save = await new SaveMaterialTreeCommandHandler(db).Handle(
                new SaveMaterialTreeCommand(user,
                    ToNodes([new MaterialNodeDto(keep.Id, "keep", false, keep.SortOrder, keep.IsExpanded,
                        "c-keep", [], null, null)]),
                    false, staleVersion), default);
            save.IsSuccess.Should().BeFalse();
            save.Error.Code.Should().Be("concurrency_conflict");
        }

        // 库内行状态零变化:keep 仍在、gone 仍是恢复态
        (await GetTreeAsync(user)).Nodes.Select(n => n.Name).Should().Contain("keep");
        (await IsDeletedAsync(user, gone.Id)).Should().BeFalse();
    }

    // ---------------- T11 同名不冲突 ----------------

    [Fact] // 活文件夹内有同名文件时恢复已删同名文件 → 成功,两行并存
    public async Task T11_SameName_NoConflict_BothRowsSurvive()
    {
        if (!_available) return;
        var user = NewUser();

        var folder = await SeedAsync(user, null, MaterialKind.Folder, "F");
        var live = await SeedAsync(user, folder.Id, MaterialKind.File, "same", "live-content");
        var del = await SeedAsync(user, folder.Id, MaterialKind.File, "same", "deleted-content");
        await SoftDeleteAsync(user, del.Id);

        var restored = await RestoreAsync(user, del.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        var tree = await GetTreeAsync(user);
        var f = Find(tree.Nodes, "F");
        f.Should().NotBeNull();
        f!.Children.Count(c => c.Name == "same").Should().Be(2, "同名两行应并存");
    }

    // ---------------- T12 类别已删 ----------------

    [Fact] // 根项所属类别被硬删后恢复该项 → 回到未分类(CategoryId 为 null)且不报错
    public async Task T12_CategoryHardDeleted_RestoreGoesUncategorized()
    {
        if (!_available) return;
        var user = NewUser();

        var cat = await SeedCategoryAsync(user, "将被删类别");
        var m = await SeedAsync(user, null, MaterialKind.File, "m", "c", categoryId: cat.Id);

        // 硬删类别 → FK SetNull,素材 CategoryId 自动置空
        await using (var db = NewDb())
        {
            var row = await db.Categories.FirstAsync(c => c.Id == cat.Id);
            db.Categories.Remove(row);
            await db.SaveChangesAsync();
        }

        await SoftDeleteAsync(user, m.Id);

        var restored = await RestoreAsync(user, m.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        await using var db2 = NewDb();
        var after = await db2.Materials.IgnoreQueryFilters().FirstAsync(x => x.Id == m.Id);
        after.CategoryId.Should().BeNull("类别被硬删后素材回到未分类");
    }

    // ---------------- T13 悬空父行(直接父行缺失) ----------------

    [Fact] // 造数:关引用完整性触发器物理删父行 → 对其已删子项 restore → 子项被重根(ParentId=null)并出现在树根级
    public async Task T13_DanglingParent_RestoreReRootsToTopLevel()
    {
        if (!_available) return;
        var user = NewUser();

        var parent = await SeedAsync(user, null, MaterialKind.Folder, "P");
        var child = await SeedAsync(user, parent.Id, MaterialKind.File, "child", "c");

        // 物理移除父行(关引用完整性触发器绕 Cascade FK)
        await RemoveRowPhysicallyAsync(user, parent.Id);

        await SoftDeleteAsync(user, child.Id);

        var restored = await RestoreAsync(user, child.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        await using var db = NewDb();
        var row = await db.Materials.IgnoreQueryFilters().FirstAsync(x => x.Id == child.Id);
        row.ParentId.Should().BeNull("父行缺失 → 断裂点重根");

        var tree = await GetTreeAsync(user);
        tree.Nodes.Select(n => n.Name).Should().Contain("child", "重根后应出现在树根级");
    }

    // ---------------- T14 多级断裂(断裂点在直接父行之上) ----------------

    [Fact] // R→M→T 三层链,物理移除 M 的父行 R → 对 T 调 restore → 断裂点 M 被重根,T 出现在 GET 树的 M 之下
    public async Task T14_MultiLevelBreak_RestoreReRootsBreakPoint_TreeContainsNode()
    {
        if (!_available) return;
        var user = NewUser();

        var r = await SeedAsync(user, null, MaterialKind.Folder, "R");
        var m = await SeedAsync(user, r.Id, MaterialKind.Folder, "M");
        var t = await SeedAsync(user, m.Id, MaterialKind.File, "T", "tc");

        // 物理移除 R(M 的父行缺失 → 断裂点在直接父行之上)
        await RemoveRowPhysicallyAsync(user, r.Id);

        await SoftDeleteAsync(user, t.Id);

        var restored = await RestoreAsync(user, t.Id);
        restored.IsSuccess.Should().BeTrue(restored.Error.Description);

        var tree = await GetTreeAsync(user);
        var mNode = Find(tree.Nodes, "M");
        mNode.Should().NotBeNull("断裂点 M 应被重根到根级");
        Find(mNode!.Children, "T").Should().NotBeNull("后置条件:T 应出现在树里 M 之下");

        // 断裂点 M 被重根:库内 ParentId = null
        await using var db = NewDb();
        var mRow = await db.Materials.IgnoreQueryFilters().FirstAsync(x => x.Id == m.Id);
        mRow.ParentId.Should().BeNull("断裂点 M 应被重根到根级");
    }

    /// <summary>
    /// 物理移除一行(绕开 Cascade FK):
    /// 素材自引用 FK 在模型快照中是 Cascade,真实 PG 上普通删父行必级联删子行,
    /// 造不出悬空态 —— 故先 SET session_replication_role='replica' 关掉引用完整性
    /// 触发器,以原生 SQL 直删,再复位。
    /// </summary>
    private async Task RemoveRowPhysicallyAsync(Guid user, Guid id)
    {
        await using var db = NewDb();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = 'replica'");
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM practice_materials WHERE id = {0} AND user_id = {1}", id, user);
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = 'origin'");
    }
}
