using Xunit;
using FluentAssertions;
using YourInterview.BuildingBlocks.Storage;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// 票94 §7:文件存储 adapter 规范守卫(<see cref="StorageRelativePaths.ResolveUnderRoot"/>)全谱单测。
/// 两层:① 纯逻辑 —— 三个入口(ResolveUnderRoot / ResolveGuarded / CombineUnderRoot)对同一输入
/// 必须同判定(共用守卫的定义);② adapter 集成 —— 真实临时根目录里经守卫解析 + 原子写落盘 + 读回,
/// 根外文件物理存在也不可达。
/// Boundary 类别(§7.3):穿越形态(../、..\..\、混合分隔符、URL 编码单/双重/大小写、绝对/盘符/UNC)、
/// 根边界 ±(根内最后一层直达文件、根外一层、同前缀兄弟目录)、合法名(a..b、..hidden、Unicode、
/// 字面 % 名、300 字超长名)、空/null/空白/NUL、规范化后仍在根内(a/./b、a/../b)。
/// </summary>
public sealed class StoragePathTraversalGuardTests : IDisposable
{
    private const string Root = "/data/interviews";

    private readonly string _base = Path.Combine("/tmp", "t94tests-" + Guid.NewGuid().ToString("N"));

    public StoragePathTraversalGuardTests() => Directory.CreateDirectory(_base);
    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch { /* 清理失败不影响结论 */ } }

    /// <summary>三入口同判定:守卫只有一份实现,任何入口都不得出现口径分叉。</summary>
    private static void AssertAll(string root, string? rel, string? expected)
    {
        StorageRelativePaths.ResolveUnderRoot(root, rel).Should().Be(expected, "ResolveUnderRoot");
        StorageRelativePaths.ResolveGuarded(root, rel).Should().Be(expected, "ResolveGuarded");
        StorageRelativePaths.CombineUnderRoot(root, rel).Should().Be(expected, "CombineUnderRoot");
    }

    // ---------------- 纯逻辑:穿越形态全谱 → 拒绝 ----------------

    [Theory] // boundary: ../ 与 ..\ 的多形态穿越(含混合分隔符、段中穿越)
    [InlineData("../escape.mp3")]
    [InlineData("..\\escape.mp3")]
    [InlineData("..\\..\\escape.mp3")]
    [InlineData("../../escape.mp3")]
    [InlineData("a/../../escape.mp3")]
    [InlineData("a/b/../../../escape.mp3")]
    [InlineData("a\\..\\..\\escape.mp3")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("..\\")]
    public void Traversal_DotDotForms_Rejected(string rel) => AssertAll(Root, rel, null);

    [Theory] // boundary: URL 编码穿越(%2e 点、%2f/%5c 分隔符、双重编码、大小写混杂)
    [InlineData("%2e%2e%2fescape.mp3")]
    [InlineData("%2e%2e/escape.mp3")]
    [InlineData("%2e%2e%5cescape.mp3")]
    [InlineData("..%2fescape.mp3")]
    [InlineData("..%5cescape.mp3")]
    [InlineData("%252e%252e%252fescape.mp3")]
    [InlineData("%2E%2E%2Fescape.mp3")]
    [InlineData("a/%2e%2e/%2e%2e/escape.mp3")]
    public void Traversal_UrlEncoded_Rejected(string rel) => AssertAll(Root, rel, null);

    [Theory] // boundary: 绝对/rooted 逃逸(哪怕指向根内也拒 —— 相对路径契约不收绝对路径)
    [InlineData("/etc/passwd")]
    [InlineData("/data/interviews/entry/uuid.m4a")]
    [InlineData("\\abs\\file.mp3")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("\\\\server\\share\\evil.mp3")]
    public void Traversal_AbsoluteForms_Rejected(string rel) => AssertAll(Root, rel, null);

    [Theory] // boundary: 根边界 −(根外一层 / 同前缀兄弟目录,不许靠字符串前缀混过)
    [InlineData("../file.mp3")]                    // 根的上一层
    [InlineData("../interviews2/file.mp3")]        // 与根同前缀的兄弟目录
    [InlineData("a/../../../data/file.mp3")]       // 多段爬出
    public void Boundary_JustOutsideRoot_Rejected(string rel) => AssertAll(Root, rel, null);

    [Theory] // boundary: 空/null/空白/根本身(等于根不是文件)
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("a/..")]                           // 规范化后 = 根本身
    public void EmptyOrRootItself_Rejected(string? rel) => AssertAll(Root, rel, null);

    [Fact] // boundary: NUL 字符(GetFullPath 会抛 —— 守卫须吞成拒绝而非异常)
    public void NulChar_Rejected() => AssertAll(Root, "a/\0b.mp3", null);

    [Fact] // boundary: 根为空同样拒绝(不许无根组合)
    public void EmptyRoot_Rejected() => AssertAll("", "entry/uuid.m4a", null);

    // ---------------- 纯逻辑:合法路径 → 放行 ----------------

    [Theory] // boundary: 根边界 +(根内最后一层 = 根直下文件;深层合法路径;根带尾斜杠;根为 "/")
    [InlineData("/data/interviews", "file.mp3", "/data/interviews/file.mp3")]
    [InlineData("/data/interviews", "entry/uuid.m4a", "/data/interviews/entry/uuid.m4a")]
    [InlineData("/data/interviews/", "entry/uuid.m4a", "/data/interviews/entry/uuid.m4a")]
    [InlineData("/", "a/b.mp3", "/a/b.mp3")]
    public void Boundary_JustInsideRoot_Allowed(string root, string rel, string expected) =>
        AssertAll(root, rel, Path.GetFullPath(expected));

    [Theory] // boundary: 合法名 —— a..b 类(".." 仅子串)、点开头名、规范化后仍在根内的 ./ 与 ../ 段
    [InlineData("entry/a..b.mp3", "entry/a..b.mp3")]
    [InlineData("entry/..hidden.mp3", "entry/..hidden.mp3")]
    [InlineData("a/./b.mp3", "a/b.mp3")]
    [InlineData("a/../b.mp3", "b.mp3")]
    [InlineData("a//b.mp3", "a/b.mp3")]
    public void LegitNames_Allowed(string rel, string expectedRel) =>
        AssertAll(Root, rel, Path.GetFullPath(Path.Combine(
            Root, expectedRel.Replace('/', Path.DirectorySeparatorChar))));

    [Fact] // boundary: 混合分隔符(Windows 形态的合法相对路径)归一后放行
    public void MixedSeparators_Allowed() =>
        AssertAll(Root, "a\\b/c\\d.mp3", Path.Combine(Root, "a", "b", "c", "d.mp3"));

    [Fact] // boundary: Unicode 名(中文/emoji)原样放行
    public void UnicodeName_Allowed() =>
        AssertAll(Root, "条目甲/录音-🎙.m4a", Path.Combine(Root, "条目甲", "录音-🎙.m4a"));

    [Fact] // boundary: 字面 % 名 —— 解码仅用于穿越判定,不得改写/误杀合法百分号文件名
    public void LiteralPercentName_Allowed()
    {
        AssertAll(Root, "entry/%41-take.mp3", Path.Combine(Root, "entry", "%41-take.mp3"));
        AssertAll(Root, "entry/100%zz.mp3", Path.Combine(Root, "entry", "100%zz.mp3"));
    }

    [Fact] // boundary: 超长文件名(300 字)无长度守卫,规范判定只看根内/根外
    public void SuperLongName_Allowed()
    {
        var name = new string('a', 300) + ".mp3";
        AssertAll(Root, "e/" + name, Path.Combine(Root, "e", name));
    }

    // ---------------- adapter 集成:真实落盘根 + 守卫解析 + 读回 ----------------

    [Fact] // 集成:经守卫解析的路径原子写落盘后可读回(两 Store 读路径的共同组合)
    public async Task Integration_WriteThenReadBack_ThroughGuard()
    {
        var root = Path.Combine(_base, "root");
        var resolved = StorageRelativePaths.ResolveUnderRoot(root, "entry/uuid.m4a");
        resolved.Should().NotBeNull();

        await LocalFileStorage.WriteAtomicallyAsync(resolved!,
            fs => fs.WriteAsync(new byte[] { 5, 6, 7 }).AsTask(), CancellationToken.None);

        await using var s = new FileStream(resolved!, FileMode.Open, FileAccess.Read);
        var buf = new byte[3];
        (await s.ReadAsync(buf)).Should().Be(3);
        buf.Should().Equal(5, 6, 7);
    }

    [Fact] // 集成:根外文件物理存在,经守卫仍不可达(读路径拿不到它的路径)
    public void Integration_OutsideFileExists_ButGuardResolvesNull()
    {
        var root = Path.Combine(_base, "root");
        Directory.CreateDirectory(root);
        var secret = Path.Combine(_base, "secret.txt");
        File.WriteAllText(secret, "outside");

        File.Exists(secret).Should().BeTrue();   // 前提:文件真实存在
        AssertAll(root, "../secret.txt", null);
        AssertAll(root, "%2e%2e%2fsecret.txt", null);
        AssertAll(root, secret, null);            // 绝对路径形态同样拒绝
    }

    [Fact] // 集成:a..b 合法名落盘可达 + 根直下文件(根内最后一层)可达 + 兄弟目录不可达
    public async Task Integration_LegitNames_OnDisk()
    {
        var root = Path.Combine(_base, "root");
        var sibling = Path.Combine(_base, "root2");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "x.txt"), "sibling");

        var dotDotName = StorageRelativePaths.ResolveUnderRoot(root, "entry/a..b.m4a");
        dotDotName.Should().NotBeNull();
        await LocalFileStorage.WriteAtomicallyAsync(dotDotName!,
            fs => fs.WriteAsync(new byte[] { 1 }).AsTask(), CancellationToken.None);
        File.Exists(dotDotName).Should().BeTrue();

        StorageRelativePaths.ResolveUnderRoot(root, "top.mp3")
            .Should().Be(Path.Combine(root, "top.mp3"));
        AssertAll(root, "../root2/x.txt", null);
    }
}
