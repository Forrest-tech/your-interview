using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票111 段2 §7(A01/A02 树摘要乙案):
/// 摘要纯函数稳定性/敏感性 + 整树保存前置比对(串行双写第二写 409、
/// 删除集场景 A 后建节点不被 B 旧树软删、mark 殃及被拦、无摘要兼容)。
/// DB 门控同 Ticket108 先例:连不上库跳过;每例独立随机 UserId,Dispose 清数据。
/// </summary>
public sealed class Ticket111TreeVersionTests : IAsyncLifetime
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
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    // ---------------- 摘要纯函数(定版算法锁定) ----------------

    [Fact] // 同行集同摘要;输入顺序无关(内部按 Id 排序)
    public void TreeVersion_Stable_And_OrderIndependent()
    {
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        var a = MaterialTreeVersion.Compute([(id1, 100u), (id2, 200u)]);
        var b = MaterialTreeVersion.Compute([(id2, 200u), (id1, 100u)]);
        a.Should().Be(b);
        a.Should().HaveLength(64);
    }

    [Fact] // 任一行 xmin 变 → 摘要变;行集增删 → 摘要变;空树 = SHA256("")
    public void TreeVersion_Sensitive_ToRowChange_And_RowSet()
    {
        var id1 = Guid.NewGuid(); var id2 = Guid.NewGuid();
        var baseHash = MaterialTreeVersion.Compute([(id1, 100u), (id2, 200u)]);
        MaterialTreeVersion.Compute([(id1, 101u), (id2, 200u)]).Should().NotBe(baseHash);
        MaterialTreeVersion.Compute([(id1, 100u)]).Should().NotBe(baseHash);
        MaterialTreeVersion.Compute([(id1, 100u), (id2, 200u), (Guid.NewGuid(), 1u)]).Should().NotBe(baseHash);
        MaterialTreeVersion.Compute([]).Should().Be(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    // ---------------- 整树保存前置比对(DB) ----------------

    private static MaterialNodeIn Node(PracticeMaterial m) =>
        new(m.Id.ToString(), m.Name, m.Kind == MaterialKind.Folder, m.Content, m.SortOrder, m.IsExpanded, [], m.MarkColor);

    private async Task<List<PracticeMaterial>> SeedTreeAsync(Guid user, params string[] names)
    {
        await using var db = NewDb();
        var rows = names.Select((n, i) =>
            new PracticeMaterial(user, null, MaterialKind.File, n, $"content-{n}", i)).ToList();
        db.Materials.AddRange(rows);
        await db.SaveChangesAsync();
        return rows;
    }

    private async Task<MaterialTreeDto> GetTreeAsync(Guid user)
    {
        await using var db = NewDb();
        return await new GetMaterialTreeQueryHandler(db).Handle(new GetMaterialTreeQuery(user), default);
    }

    private async Task<YourInterview.BuildingBlocks.Results.Result<SaveTreeResult>> SaveTreeAsync(
        Guid user, IReadOnlyList<MaterialNodeIn> nodes, string? treeVersion, bool force = false)
    {
        await using var db = NewDb();
        return await new SaveMaterialTreeCommandHandler(db).Handle(
            new SaveMaterialTreeCommand(user, nodes, force, treeVersion), default);
    }

    [Fact] // 串行双写:A 持读时摘要保存成功并回新摘要;B 持旧摘要保存 → 409,A 数据完好
    public async Task TreeSave_StaleVersion_Rejected_ADataIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = await SeedTreeAsync(user, "n1", "n2");
        var read = await GetTreeAsync(user);
        read.TreeVersion.Should().NotBeNullOrEmpty();
        read.Nodes.Should().HaveCount(2);

        // A:改 n1 正文后整树保存(摘要对齐)
        var aNodes = rows.Select(m => m.Name == "n1"
            ? Node(m) with { Content = "A-edited" } : Node(m)).ToList();
        var a = await SaveTreeAsync(user, aNodes, read.TreeVersion);
        a.IsSuccess.Should().BeTrue(a.Error.Description);
        a.Value.TreeVersion.Should().NotBe(read.TreeVersion);

        // B:仍持旧摘要,试图把 n1 改回
        var bNodes = rows.Select(m => m.Name == "n1"
            ? Node(m) with { Content = "B-stale" } : Node(m)).ToList();
        var b = await SaveTreeAsync(user, bNodes, read.TreeVersion);
        b.IsSuccess.Should().BeFalse();
        b.Error.Code.Should().Be("concurrency_conflict");

        var after = await GetTreeAsync(user);
        after.Nodes.Single(n => n.Name == "n1").Content.Should().Be("A-edited");
        after.TreeVersion.Should().Be(a.Value.TreeVersion);
    }

    [Fact] // 删除集场景:A 新建节点后,B 持旧摘要的旧树保存 → 409,A 的新节点不被软删
    public async Task TreeSave_StaleVersion_DoesNotSoftDelete_NewNodes()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = await SeedTreeAsync(user, "base1");
        var read = await GetTreeAsync(user);

        // A:在读后新建一个节点(无摘要兼容保存,模拟另一页签的正常保存)
        var aNodes = rows.Select(Node).ToList();
        aNodes.Add(new MaterialNodeIn(Guid.NewGuid().ToString(), "A-new", false, "fresh", 9, false, []));
        var a = await SaveTreeAsync(user, aNodes, treeVersion: null);
        a.IsSuccess.Should().BeTrue(a.Error.Description);

        // B:持旧摘要提交旧树(不含 A-new)→ 前置拒绝
        var b = await SaveTreeAsync(user, rows.Select(Node).ToList(), read.TreeVersion);
        b.IsSuccess.Should().BeFalse();
        b.Error.Code.Should().Be("concurrency_conflict");

        var after = await GetTreeAsync(user);
        after.Nodes.Select(n => n.Name).Should().Contain("A-new");
    }

    [Fact] // A02 殃及面:A 单独 mark 改色推进摘要后,B 旧树保存 → 409,mark 不被写回
    public async Task TreeSave_AfterMark_StaleVersion_Rejected_MarkIntact()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = await SeedTreeAsync(user, "m1");
        var read = await GetTreeAsync(user);

        await using (var db = NewDb())
        {
            var mark = await new SetMaterialMarkCommandHandler(db).Handle(
                new SetMaterialMarkCommand(user, rows[0].Id, "red"), default);
            mark.IsSuccess.Should().BeTrue(mark.Error.Description);
        }

        var b = await SaveTreeAsync(user, rows.Select(Node).ToList(), read.TreeVersion);
        b.IsSuccess.Should().BeFalse();
        b.Error.Code.Should().Be("concurrency_conflict");

        (await GetTreeAsync(user)).Nodes.Single().MarkColor.Should().Be("red");
    }

    [Fact] // 兼容:不带摘要的旧调用方整树保存照常成功,且响应回新摘要可续接
    public async Task TreeSave_NoVersion_Compatible_And_ReturnsVersion()
    {
        if (!_available) return;
        var user = NewUser();
        var rows = await SeedTreeAsync(user, "c1");

        var saved = await SaveTreeAsync(user, rows.Select(Node).ToList(), treeVersion: null);
        saved.IsSuccess.Should().BeTrue(saved.Error.Description);
        saved.Value.TreeVersion.Should().NotBeNullOrEmpty();

        // 用回传的新摘要再存一次 → 应成功(续接口径)
        var again = await SaveTreeAsync(user, rows.Select(Node).ToList(), saved.Value.TreeVersion);
        again.IsSuccess.Should().BeTrue(again.Error.Description);
    }
}
