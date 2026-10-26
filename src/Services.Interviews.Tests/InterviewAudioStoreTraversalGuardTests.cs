using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Services.Interviews.Infrastructure.Storage;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票94 §7 集成层:Interviews <see cref="LocalInterviewAudioStore"/> 真实临时根目录端到端 ——
/// 守卫已从 ".." 子串粗判改为 BuildingBlocks 规范判定(adapter <c>ResolveGuarded</c>):
/// 既有已存文件(SaveAsync 生成的 {entryId}/{guid}.m4a 形态)零回退读回;a..b 合法名放行;
/// 穿越(../、URL 编码、绝对路径)在 Store 层即被拒(读不到、删不掉根外文件)。
/// </summary>
public sealed class InterviewAudioStoreTraversalGuardTests : IDisposable
{
    private readonly string _base = Path.Combine("/tmp", "t94iv-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly LocalInterviewAudioStore _store;

    public InterviewAudioStoreTraversalGuardTests()
    {
        _root = Path.Combine(_base, "interviews");
        Directory.CreateDirectory(_root);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Storage:InterviewsRoot"] = _root }).Build();
        _store = new LocalInterviewAudioStore(cfg, NullLogger<LocalInterviewAudioStore>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_base, recursive: true); } catch { /* 清理失败不影响结论 */ } }

    private string WriteOutsideSecret()
    {
        var secret = Path.Combine(_base, "secret.m4a");
        File.WriteAllBytes(secret, new byte[] { 9, 9, 9 });
        return secret;
    }

    [Fact] // 回归:SaveAsync 落盘的既有形态文件可原样读回(票39 样本形态 {entryId}/{uuid}.m4a)
    public async Task SaveThenOpenRead_RoundTrips()
    {
        var entryId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var stored = await _store.SaveAsync(entryId, "sample.m4a",
            new MemoryStream(bytes), CancellationToken.None);

        stored.RelativePath.Should().StartWith(entryId + "/");
        await using var s = await _store.OpenReadAsync(stored.RelativePath, CancellationToken.None);
        s.Should().NotBeNull();
        using var ms = new MemoryStream();
        await s!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(bytes);
        _store.FileExists(stored.RelativePath).Should().BeTrue();
    }

    [Theory] // boundary: 穿越形态在 Store 层被拒 —— 根外文件真实存在也读不到
    [InlineData("../secret.m4a")]
    [InlineData("..\\secret.m4a")]
    [InlineData("%2e%2e%2fsecret.m4a")]
    [InlineData("entry/../../secret.m4a")]
    public async Task OpenRead_Traversal_ReturnsNull(string rel)
    {
        WriteOutsideSecret();
        (await _store.OpenReadAsync(rel, CancellationToken.None)).Should().BeNull();
        _store.FileExists(rel).Should().BeFalse();
    }

    [Fact] // boundary: 绝对路径(指向根外真实文件)被拒
    public async Task OpenRead_AbsolutePath_ReturnsNull()
    {
        var secret = WriteOutsideSecret();
        (await _store.OpenReadAsync(secret, CancellationToken.None)).Should().BeNull();
    }

    [Fact] // 票94 行为变化:a..b 合法名改前被子串粗判拒绝,改后可正常读回
    public async Task OpenRead_DotDotSubstringName_Allowed_Ticket94()
    {
        var dir = Path.Combine(_root, "entry-1");
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "a..b.m4a");
        await File.WriteAllBytesAsync(p, new byte[] { 7, 7 });

        await using var s = await _store.OpenReadAsync("entry-1/a..b.m4a", CancellationToken.None);
        s.Should().NotBeNull();
        using var ms = new MemoryStream();
        await s!.CopyToAsync(ms);
        ms.ToArray().Should().Equal(7, 7);
    }

    [Fact] // 删除面:穿越删除不得伤及根外文件(返回幂等成功,但文件必须还在)
    public async Task Delete_Traversal_DoesNotTouchOutsideFile()
    {
        var secret = WriteOutsideSecret();
        (await _store.DeleteAsync("../secret.m4a", CancellationToken.None)).Should().BeTrue();
        File.Exists(secret).Should().BeTrue();
    }
}
