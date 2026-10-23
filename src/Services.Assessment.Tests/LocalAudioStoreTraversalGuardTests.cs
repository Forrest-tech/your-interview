using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票94 §7 集成层:Assessment <see cref="LocalAudioStore"/> 真实临时根目录端到端 ——
/// 读/删路径自票94 起走 BuildingBlocks 规范守卫(adapter <c>CombineUnderRoot</c>),
/// 改前无守卫:`../` 可出根读/删、rooted 输入直接胜出;改后一律按「无此文件」处理。
/// 用 Guid.Empty 用户:DesiredRootAsync 对空用户直接返回 null(不查库),根目录退回配置默认根,
/// 故无需 DbContext 即可测 Store 本体(scope factory 替身若被调用即抛,反证此前提)。
/// </summary>
public sealed class LocalAudioStoreTraversalGuardTests : IDisposable
{
    private readonly string _base = Path.Combine("/tmp", "t94asm-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly LocalAudioStore _store;

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("Guid.Empty 用户不应触达 scope factory");
    }

    public LocalAudioStoreTraversalGuardTests()
    {
        _root = Path.Combine(_base, "assessment");
        Directory.CreateDirectory(_root);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:RootDirectory"] = _root }).Build();
        _store = new LocalAudioStore(cfg, new UnusedScopeFactory(),
            NullLogger<LocalAudioStore>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch { /* 清理失败不影响结论 */ } }

    private async Task<string> WriteInside(string rel, byte[] bytes)
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllBytesAsync(p, bytes);
        return p;
    }

    private string WriteOutsideSecret()
    {
        var secret = Path.Combine(_base, "secret.webm");
        File.WriteAllBytes(secret, new byte[] { 9, 9, 9 });
        return secret;
    }

    [Fact] // 回归:既有落盘形态 {user8}/{yyyy}/{MM}/{id}.webm 可原样读回(票39 样本形态)
    public async Task OpenRead_ExistingFile_RoundTrips()
    {
        await WriteInside("abcd1234/2026/10/rec.webm", new byte[] { 1, 2, 3 });
        await using var s = await _store.OpenReadAsync(
            Guid.Empty, "abcd1234/2026/10/rec.webm", CancellationToken.None);
        s.Should().NotBeNull();
        using var ms = new MemoryStream();
        await s!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(1, 2, 3);
    }

    [Theory] // boundary: 穿越形态全谱 —— 改前可出根读到根外文件,改后一律 null
    [InlineData("../secret.webm")]
    [InlineData("..\\secret.webm")]
    [InlineData("%2e%2e%2fsecret.webm")]
    [InlineData("abcd1234/../../secret.webm")]
    public async Task OpenRead_Traversal_ReturnsNull(string rel)
    {
        WriteOutsideSecret();
        (await _store.OpenReadAsync(Guid.Empty, rel, CancellationToken.None)).Should().BeNull();
    }

    [Fact] // boundary: rooted 输入改前按 Path.Combine 语义胜出,改后拒绝
    public async Task OpenRead_AbsolutePath_ReturnsNull()
    {
        var secret = WriteOutsideSecret();
        (await _store.OpenReadAsync(Guid.Empty, secret, CancellationToken.None)).Should().BeNull();
    }

    [Fact] // 票94 口径对齐 Interviews:a..b 合法名(规范化后在根内)可读
    public async Task OpenRead_DotDotSubstringName_Allowed()
    {
        await WriteInside("abcd1234/a..b.webm", new byte[] { 8 });
        await using var s = await _store.OpenReadAsync(
            Guid.Empty, "abcd1234/a..b.webm", CancellationToken.None);
        s.Should().NotBeNull();
    }

    [Fact] // 删除面:穿越删除不得伤及根外文件(幂等返回 true,文件必须还在)
    public async Task Delete_Traversal_DoesNotTouchOutsideFile()
    {
        var secret = WriteOutsideSecret();
        (await _store.DeleteAsync(Guid.Empty, "../secret.webm", CancellationToken.None))
            .Should().BeTrue();
        File.Exists(secret).Should().BeTrue();
    }

    [Fact] // 回归:根内文件删除照常生效
    public async Task Delete_InsideFile_StillWorks()
    {
        var p = await WriteInside("abcd1234/2026/10/del.webm", new byte[] { 1 });
        (await _store.DeleteAsync(Guid.Empty, "abcd1234/2026/10/del.webm", CancellationToken.None))
            .Should().BeTrue();
        File.Exists(p).Should().BeFalse();
    }
}
