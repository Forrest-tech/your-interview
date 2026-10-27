using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票258 S1（跨设备跟读标识）：服务端 IsShadow 列 + DTO + 上传字段。
/// 纯映射测试不依赖数据库；handler round-trip 用沙盒库门控（库不可用时跳过，
/// 与票111/108 先例同款），测试用户数据在 Dispose 时清理。
/// </summary>
public sealed class ShadowCrossDeviceS1Tests : IAsyncLifetime
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
        var recs = await db.Recordings.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)).ToListAsync();
        db.Recordings.RemoveRange(recs);
        var mats = await db.Materials.Where(m => _users.Contains(m.UserId)).ToListAsync();
        db.Materials.RemoveRange(mats);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private static PracticeRecording NewEntity(bool? isShadow) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "u/r.webm",
            "audio/webm", 10, 1.5, "en-US", isShadow);

    // ---------- 纯映射：DTO 只见 bool，NULL → false ----------

    [Fact]
    public void ToDto_HistoricalRow_NullMapsToFalse()
    {
        var dto = ListRecordingsQueryHandler.ToDto(NewEntity(null));
        dto.IsShadow.Should().BeFalse();
    }

    [Fact]
    public void ToDto_DefaultCtor_NullMapsToFalse()
    {
        // 不传 isShadow 的旧调用点（实体缺省 null）同样映射为 false。
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "u/r.webm", "audio/webm", 10, 1.5);
        ListRecordingsQueryHandler.ToDto(rec).IsShadow.Should().BeFalse();
    }

    [Fact]
    public void ToDto_ShadowRow_TrueMapsToTrue()
    {
        ListRecordingsQueryHandler.ToDto(NewEntity(true)).IsShadow.Should().BeTrue();
    }

    [Fact]
    public void ToDto_OrdinaryRow_FalseMapsToFalse()
    {
        ListRecordingsQueryHandler.ToDto(NewEntity(false)).IsShadow.Should().BeFalse();
    }

    // ---------- handler round-trip（库门控） ----------

    [Fact]
    public async Task SaveRecording_IsShadowTrue_RoundTrips()
    {
        if (!_available) return;
        var user = NewUser();
        var materialId = await CreateMaterialAsync(user);
        var store = new FakeAudioStore();

        await using var db = NewDb();
        var result = await new SaveRecordingCommandHandler(db, store).Handle(
            new SaveRecordingCommand(user, materialId, "shadow.webm", "audio/webm",
                1.5, "en-US", new MemoryStream(new byte[] { 1, 2, 3 }), IsShadow: true),
            default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        result.Value.IsShadow.Should().BeTrue();

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking()
            .SingleAsync(r => r.Id == result.Value.Id);
        row.IsShadow.Should().BeTrue();
    }

    [Fact]
    public async Task SaveRecording_IsShadowAbsent_DefaultsFalse()
    {
        if (!_available) return;
        var user = NewUser();
        var materialId = await CreateMaterialAsync(user);
        var store = new FakeAudioStore();

        await using var db = NewDb();
        // 普通上传不传 IsShadow（命令缺省 false）——行为与 S1 之前一致，
        // 只是行上多了 false 标识（历史 NULL 行不受影响，见下）。
        var result = await new SaveRecordingCommandHandler(db, store).Handle(
            new SaveRecordingCommand(user, materialId, "plain.webm", "audio/webm",
                1.5, "en-US", new MemoryStream(new byte[] { 1, 2, 3 })),
            default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        result.Value.IsShadow.Should().BeFalse();
    }

    [Fact]
    public async Task HistoricalRow_PersistedNull_ListsAsNonShadow()
    {
        if (!_available) return;
        var user = NewUser();
        var materialId = await CreateMaterialAsync(user);

        // S1 之前录的历史行：实体不传 isShadow → 库里存 NULL。
        var rec = new PracticeRecording(Guid.NewGuid(), user, materialId, "u/hist.webm",
            "audio/webm", 10, 1.5);
        await using (var db = NewDb())
        {
            db.Recordings.Add(rec);
            await db.SaveChangesAsync();
        }

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == rec.Id);
        row.IsShadow.Should().BeNull();
        ListRecordingsQueryHandler.ToDto(row).IsShadow.Should().BeFalse();
    }

    private async Task<Guid> CreateMaterialAsync(Guid user)
    {
        await using var db = NewDb();
        var mat = new PracticeMaterial(user, null, MaterialKind.File, "s1-material");
        db.Materials.Add(mat);
        await db.SaveChangesAsync();
        return mat.Id;
    }

    /// <summary>内存假存储：只记相对路径，不碰磁盘（handler 只用 SaveAsync + IsConfigured）。</summary>
    private sealed class FakeAudioStore : IAudioStore
    {
        public bool IsConfigured => true;
        public string DefaultRoot => "/tmp/s1-fake-audio";
        public Task<StorageRoots> ResolveRootsAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(new StorageRoots(DefaultRoot, null, DefaultRoot, false, null));
        public Task<string> SaveAsync(Guid userId, Guid recordingId, string extension,
            Stream content, CancellationToken ct)
            => Task.FromResult($"{userId}/{recordingId}.{extension}");
        public Task<Stream?> OpenReadAsync(Guid userId, string relativePath, CancellationToken ct)
            => Task.FromResult<Stream?>(null);
        public Task<bool> DeleteAsync(Guid userId, string relativePath, CancellationToken ct)
            => Task.FromResult(true);
    }
}
