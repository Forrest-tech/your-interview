using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using YourInterview.BuildingBlocks.Storage;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// 票39/ARC-12 §7:文件存储 adapter 纯逻辑单测。
/// 类别标注:
/// 。
/// 凡 characterization 用例都是把改前 Store 的既有行为**原样锁住**(含不理想的安全口径),
/// 不是在批准新行为 —— 变更口径须另行申报(见 adapter 文档)。
/// </summary>
public sealed class FileStorageAdapterTests: IDisposable
{
// 测试目录固定建在 /tmp 下:其祖先链无.git /.sln / docker-compose.yml,
// 不会污染「向上找根」类用例(进程 TMPDIR 在 workspace 内,祖先不可控)。
private readonly string _base = Path.Combine("/tmp", "t39tests-" + Guid.NewGuid().ToString("N"));

public FileStorageAdapterTests() => Directory.CreateDirectory(_base);
public void Dispose() { try { Directory.Delete(_base, recursive: true);} catch { /* 清理失败不影响结论 */}}

private string MkDir(params string[] parts)
{
var p = Path.Combine(new[] { _base}.Concat(parts).ToArray());
Directory.CreateDirectory(p);
return p;
}

// ---------------- Assessment 根链 ----------------

[Fact] // 配置 > 环境变量 > 兜底
public void AssessmentRoot_ConfiguredWinsOverEnvAndFallback()
{
StorageRootResolver.ResolveAssessmentDefaultRoot("/cfg/root", "/env/root", _base)
.Should().Be("/cfg/root");
}

[Fact] // 环境变量第二
public void AssessmentRoot_EnvWinsWhenConfiguredMissing()
{
StorageRootResolver.ResolveAssessmentDefaultRoot(null, "/env/root", _base)
.Should().Be("/env/root");
}

[Theory] // 配置与环境变量为空/空白都视为未配置,继续下探
[InlineData("", "")]
[InlineData(" ", null)]
[InlineData(null, " ")]
public void AssessmentRoot_BlankFallsThrough(string? configured, string? env)
{
var cwd = MkDir("plain");
StorageRootResolver.ResolveAssessmentDefaultRoot(configured, env, cwd)
.Should().Be(Path.Combine(cwd, "storage", "assessment"));
}

[Fact] //.git 在祖先:落到仓库同级 recordings/assessment
public void AssessmentRoot_GitSiblingFallback()
{
var repo = MkDir("repo");
Directory.CreateDirectory(Path.Combine(repo, ".git"));
var deep = MkDir("repo", "src", "deep");

StorageRootResolver.ResolveAssessmentDefaultRoot(null, null, deep)
.Should().Be(Path.GetFullPath(Path.Combine(_base, "recordings", "assessment")));
}

[Fact] // 退回 cwd/storage/assessment
public void AssessmentRoot_NoGitFallback()
{
var cwd = MkDir("nogit", "sub");
StorageRootResolver.ResolveAssessmentDefaultRoot(null, null, cwd)
.Should().Be(Path.Combine(cwd, "storage", "assessment"));
}

[Fact] // FindGitRoot:起点本身含.git
public void FindGitRoot_Self()
{
var repo = MkDir("selfrepo");
Directory.CreateDirectory(Path.Combine(repo, ".git"));
StorageRootResolver.FindGitRoot(repo).Should().Be(repo);
}

[Fact] // FindGitRoot:找不到返回 null
public void FindGitRoot_None()
{
StorageRootResolver.FindGitRoot(MkDir("none", "a", "b")).Should().BeNull();
}

[Fact] // 配置键名 Storage:RootDirectory 原样生效
public void AssessmentRoot_ConfigurationOverload_UsesStorageRootDirectoryKey()
{
var cfg = new ConfigurationBuilder().AddInMemoryCollection(
new Dictionary<string, string?> { ["Storage:RootDirectory"] = "/from/config"}).Build();
StorageRootResolver.ResolveAssessmentDefaultRoot(cfg).Should().Be("/from/config");
}

// ---------------- Interviews 根链 ----------------

[Fact] // 专用配置 > 专用环境变量 > 共享根派生 > 仓库根 > cwd
public void InterviewsRoot_DedicatedWins()
{
StorageRootResolver.ResolveInterviewsRoot("/dedicated", "/env", "/shared", _base, _base)
.Should().Be("/dedicated");
}

[Fact] // 环境变量第二;专用配置空白下探
public void InterviewsRoot_EnvSecond()
{
StorageRootResolver.ResolveInterviewsRoot(" ", "/env", "/shared", _base, _base)
.Should().Be("/env");
}

[Fact] // 共享根派生 {Storage:RootDirectory}/interviews
public void InterviewsRoot_SharedRootDerivation()
{
StorageRootResolver.ResolveInterviewsRoot(null, null, "/shared", _base, _base)
.Should().Be(Path.Combine("/shared", "interviews"));
}

[Fact] // base 上层有 docker-compose.yml → {仓库根}/storage/interviews
public void InterviewsRoot_SolutionFallback()
{
var repo = MkDir("iv-repo");
File.WriteAllText(Path.Combine(repo, "docker-compose.yml"), "services: {}");
var bin = MkDir("iv-repo", "src", "bin");

StorageRootResolver.ResolveInterviewsRoot(null, null, null, bin, MkDir("elsewhere"))
.Should().Be(Path.Combine(repo, "storage", "interviews"));
}

[Fact] // 全链无命中 → cwd/storage/interviews
public void InterviewsRoot_CwdFallback()
{
var cwd = MkDir("iv-cwd");
StorageRootResolver.ResolveInterviewsRoot(null, null, null, MkDir("iv-base"), cwd)
.Should().Be(Path.Combine(cwd, "storage", "interviews"));
}

[Fact] // 专用键 Storage:InterviewsRoot 原样生效
public void InterviewsRoot_ConfigurationOverload_UsesDedicatedKey()
{
var cfg = new ConfigurationBuilder().AddInMemoryCollection(
new Dictionary<string, string?> { ["Storage:InterviewsRoot"] = "/iv/config"}).Build();
StorageRootResolver.ResolveInterviewsRoot(cfg).Should().Be("/iv/config");
}

// ---------------- FindSolutionRoot ----------------

[Fact] // compose 优先于 sln —— 哪怕 sln 离起点更近
public void FindSolutionRoot_ComposePreferredOverNearerSln()
{
var top = MkDir("sol-top");
File.WriteAllText(Path.Combine(top, "docker-compose.yml"), "x");
var mid = MkDir("sol-top", "mid");
File.WriteAllText(Path.Combine(mid, "app.sln"), "x");
var leaf = MkDir("sol-top", "mid", "leaf");

StorageRootResolver.FindSolutionRoot(leaf).Should().Be(top);
}

[Fact] // 同标记多层命中取最高层(向上遍历后写覆盖)
public void FindSolutionRoot_HighestComposeWins()
{
var top = MkDir("sol-high");
File.WriteAllText(Path.Combine(top, "docker-compose.yml"), "x");
var low = MkDir("sol-high", "low");
File.WriteAllText(Path.Combine(low, "docker-compose.yml"), "x");
var leaf = MkDir("sol-high", "low", "leaf");

StorageRootResolver.FindSolutionRoot(leaf).Should().Be(top);
}

[Fact] // 只有 sln 时退回 sln 所在目录
public void FindSolutionRoot_SlnOnly()
{
var dir = MkDir("sol-sln");
File.WriteAllText(Path.Combine(dir, "app.sln"), "x");
StorageRootResolver.FindSolutionRoot(MkDir("sol-sln", "sub")).Should().Be(dir);
}

[Fact] // 都没有 → null
public void FindSolutionRoot_None()
{
StorageRootResolver.FindSolutionRoot(MkDir("sol-none", "a")).Should().BeNull();
}

[Fact] // compose 在起点向上第 7 层祖先(检查窗口最后一格)→ 命中
public void FindSolutionRoot_ComposeAtSeventhAncestor_Found()
{
var top = MkDir("sol-7");
File.WriteAllText(Path.Combine(top, "docker-compose.yml"), "x");
var leaf = top;
for (var i = 0; i < 7; i++) { leaf = Path.Combine(leaf, "d" + i); Directory.CreateDirectory(leaf); }
// leaf 距 top 7 层:窗口检查 leaf..top 共 8 个目录
StorageRootResolver.FindSolutionRoot(leaf).Should().Be(top);
}

[Fact] // compose 在第 8 层祖先(窗口外)→ 找不到
public void FindSolutionRoot_ComposeAtEighthAncestor_NotFound()
{
var top = MkDir("sol-8");
File.WriteAllText(Path.Combine(top, "docker-compose.yml"), "x");
var cur = top;
for (var i = 0; i < 8; i++)
{
cur = Path.Combine(cur, "d" + i);
Directory.CreateDirectory(cur);
}
StorageRootResolver.FindSolutionRoot(cur).Should().BeNull();
}

// ---------------- ResolveGuarded(Interviews 读路径口径) ----------------

private const string Root = "/data/interviews";

[Theory] // 一律拒绝
[InlineData(null)]
[InlineData("")]
[InlineData(" ")]
public void Guarded_EmptyRejected(string? rel)
{
StorageRelativePaths.ResolveGuarded(Root, rel).Should().BeNull();
}

[Fact] // 正常相对路径组合到根下(正斜杠转 OS 分隔符)
public void Guarded_NormalRelative()
{
StorageRelativePaths.ResolveGuarded(Root, "entry-1/uuid.m4a")
.Should().Be(Path.Combine(Root, "entry-1", "uuid.m4a"));
}

[Fact] // 反斜杠输入同样归一后组合(Windows 侧写出的路径)
public void Guarded_BackslashNormalized()
{
StorageRelativePaths.ResolveGuarded(Root, "entry-1\\uuid.m4a")
.Should().Be(Path.Combine(Root, "entry-1", "uuid.m4a"));
}

[Theory] // ".." 任意位置一律拒绝
[InlineData("../escape.mp3")]
[InlineData("a/../../escape.mp3")]
[InlineData("..")]
[InlineData("a/..")]
public void Guarded_TraversalRejected(string rel)
{
StorageRelativePaths.ResolveGuarded(Root, rel).Should().BeNull();
}

[Fact] // 文件名里含 ".." 子串(如 a..b)也被拒 —— 既有子串判定口径锁定
public void Guarded_DotDotSubstringInFileName_AlsoRejected_Characterization()
{
StorageRelativePaths.ResolveGuarded(Root, "entry/a..b.mp3").Should().BeNull();
}

[Fact] // rooted 输入拒绝
public void Guarded_RootedRejected()
{
StorageRelativePaths.ResolveGuarded(Root, "/etc/passwd").Should().BeNull();
}

[Fact] // 中文/emoji 文件名正常组合,不做任何转码
public void Guarded_UnicodeName()
{
StorageRelativePaths.ResolveGuarded(Root, "条目甲/录音-🎙.m4a")
.Should().Be(Path.Combine(Root, "条目甲", "录音-🎙.m4a"));
}

[Fact] // 300 字符文件名无长度守卫,原样组合(既有口径锁定)
public void Guarded_SuperLongName()
{
var name = new string('a', 300) + ".mp3";
StorageRelativePaths.ResolveGuarded(Root, "e/" + name)
.Should().Be(Path.Combine(Root, "e", name));
}

// ---------------- CombineUnderRoot(Assessment 读/删既有口径) ----------------

[Fact] // 正常相对路径组合到根下
public void Combine_NormalRelative()
{
StorageRelativePaths.CombineUnderRoot("/data/assessment", "abcd1234/2026/10/rec.mp3")
.Should().Be(Path.Combine("/data/assessment", "abcd1234", "2026", "10", "rec.mp3"));
}

[Fact] // ".." 不拦截、组合结果出根 —— Assessment 既有行为锁定,本票不改安全口径
public void Combine_TraversalNotGuarded_Characterization()
{
var combined = StorageRelativePaths.CombineUnderRoot("/data/assessment", "../escape.mp3");
Path.GetFullPath(combined).Should().Be(Path.GetFullPath("/data/escape.mp3"));
}

[Fact] // rooted 输入按 Path.Combine 语义直接胜出 —— 同上,既有行为锁定
public void Combine_RootedWins_Characterization()
{
StorageRelativePaths.CombineUnderRoot("/data/assessment", "/abs/file.mp3")
.Should().Be("/abs/file.mp3");
}

// ---------------- ToPortableRelative ----------------

[Theory] // 入库形态归一:反斜杠→正斜杠;已归一/空串不变
[InlineData("a\\b\\c.mp3", "a/b/c.mp3")]
[InlineData("a/b/c.mp3", "a/b/c.mp3")]
[InlineData("", "")]
public void Portable_Conversions(string input, string expected)
{
StorageRelativePaths.ToPortableRelative(input).Should().Be(expected);
}

[Fact] // 归一只动分隔符,Unicode 原样
public void Portable_UnicodePreserved()
{
StorageRelativePaths.ToPortableRelative("条目\\录音.m4a").Should().Be("条目/录音.m4a");
}

// ---------------- LocalFileStorage.WriteAtomicallyAsync ----------------

[Fact] // 原子写:目录自动建、内容完整、无.tmp 残留
public async Task AtomicWrite_CreatesDirsAndLeavesNoTmp()
{
var target = Path.Combine(_base, "w", "sub", "a.bin");
await LocalFileStorage.WriteAtomicallyAsync(target,
fs => fs.WriteAsync(new byte[] { 1, 2, 3}).AsTask(), CancellationToken.None);

File.ReadAllBytes(target).Should().Equal(1, 2, 3);
File.Exists(target + ".tmp").Should().BeFalse();
}

[Fact] // 原子写:覆盖已有文件(overwrite 语义)
public async Task AtomicWrite_Overwrites()
{
var target = Path.Combine(_base, "w2", "a.bin");
Directory.CreateDirectory(Path.GetDirectoryName(target)!);
File.WriteAllText(target, "old-content-longer");

await LocalFileStorage.WriteAtomicallyAsync(target,
fs => fs.WriteAsync(new byte[] { 9}).AsTask(), CancellationToken.None);

File.ReadAllBytes(target).Should().Equal(9);
}

[Fact] // 空内容写出 0 字节文件(不报错、不跳过)
public async Task AtomicWrite_ZeroBytes()
{
var target = Path.Combine(_base, "w3", "empty.bin");
await LocalFileStorage.WriteAtomicallyAsync(target,
_ => Task.CompletedTask, CancellationToken.None);

File.Exists(target).Should().BeTrue();
new FileInfo(target).Length.Should().Be(0);
}

[Fact] // Unicode 目录 + 长文件名(组件字节上限内)可正常落盘读回
public async Task AtomicWrite_UnicodeAndLongName()
{
var name = "录音" + new string('长', 60) + ".mp3";   // 60 字=180 字节,组件上限 255 字节内
var target = Path.Combine(_base, "w4", name);
await LocalFileStorage.WriteAtomicallyAsync(target,
fs => fs.WriteAsync(new byte[] { 7, 7}).AsTask(), CancellationToken.None);

File.ReadAllBytes(target).Should().Equal(7, 7);
}
}
