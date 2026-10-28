using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票269 一句话模式（M-A：后端 IsSentence + SentenceText 可空列，照抄票 258 范式）。
/// 纯映射测试无需库；命令级 round-trip 测试用沙盒库门控（库不可用时跳过，
/// 与票111/108/258/259/260 先例同款），测试用户数据在 Dispose 时清理。
/// </summary>
public sealed class SentenceModeTests : IAsyncLifetime
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

    // ---------- 纯映射 ----------

    [Fact]
    public void ToDto_SentenceRow_MapsIsSentenceAndText()
    {
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "u/r.webm", "audio/webm", 10, 1.5, "en-US", false,
            true, "one two three four five six seven eight nine ten");
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsSentence.Should().BeTrue();
        dto.SentenceText.Should().Be("one two three four five six seven eight nine ten");
        dto.IsShadow.Should().BeFalse();
    }

    [Fact]
    public void ToDto_OrdinaryRow_MapsSentenceFalseNull()
    {
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "u/r.webm", "audio/webm", 10, 1.5, "en-US", false);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsSentence.Should().BeFalse();
        dto.SentenceText.Should().BeNull();
    }

    [Fact]
    public void ToDto_HistoryRowNullSentence_MapsFalseNull()
    {
        // 269 之前录的历史行：IsSentence/SentenceText 均为 NULL → 按普通 take 处理。
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "u/r.webm", "audio/webm", 10, 1.5, "en-US", null, null, null);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsSentence.Should().BeFalse();
        dto.SentenceText.Should().BeNull();
    }

    // ---------- 命令级 round-trip（沙盒库门控） ----------

    [Fact]
    public async Task SaveRecording_SentenceFields_RoundTrip()
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);
        var store = new MemoryAudioStore();
        const string sentence = "one two three four five six seven eight nine ten";

        Guid rowId;
        await using (var db = NewDb())
        {
            var result = await new SaveRecordingCommandHandler(db, store).Handle(
                new SaveRecordingCommand(user, matId, "r.webm", "audio/webm",
                    1.5, "en-US", new MemoryStream(new byte[] { 1, 2, 3 }),
                    IsSentence: true, SentenceText: sentence),
                default);
            result.IsSuccess.Should().BeTrue(result.Error.Description);
            result.Value.IsSentence.Should().BeTrue();
            result.Value.SentenceText.Should().Be(sentence);
            rowId = result.Value.Id;
        }

        // 换一个 DbContext 重读：字段已落库，不是仅内存透传。
        await using (var db = NewDb())
        {
            var row = await db.Recordings.IgnoreQueryFilters().SingleAsync(r => r.Id == rowId);
            row.IsSentence.Should().BeTrue();
            row.SentenceText.Should().Be(sentence);
            var dto = ListRecordingsQueryHandler.ToDto(row);
            dto.IsSentence.Should().BeTrue();
            dto.SentenceText.Should().Be(sentence);
        }
    }

    [Fact]
    public async Task SaveRecording_OrdinaryUpload_SentenceDefaults()
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);
        var store = new MemoryAudioStore();

        await using var db = NewDb();
        var result = await new SaveRecordingCommandHandler(db, store).Handle(
            new SaveRecordingCommand(user, matId, "r.webm", "audio/webm",
                1.5, "en-US", new MemoryStream(new byte[] { 1, 2, 3 })),
            default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        result.Value.IsSentence.Should().BeFalse();
        result.Value.SentenceText.Should().BeNull();
    }

    // ---------- 种子 ----------

    private async Task<Guid> SeedMaterialAsync(Guid user)
    {
        await using var db = NewDb();
        var mat = new PracticeMaterial(user, null, MaterialKind.File, "sentence-material");
        db.Materials.Add(mat);
        await db.SaveChangesAsync();
        return mat.Id;
    }

    // ---------- 内存音频仓（与票 260 S3 测试的同名测试替身同款，测试文件内私有） ----------

    private sealed class MemoryAudioStore : IAudioStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public bool IsConfigured => true;
        public string DefaultRoot => "/tmp/sentence-mem-audio";
        public Task<StorageRoots> ResolveRootsAsync(Guid userId, CancellationToken ct)
            => Task.FromResult(new StorageRoots(DefaultRoot, null, DefaultRoot, false, null));

        public Task<string> SaveAsync(Guid userId, Guid recordingId, string extension,
            Stream content, CancellationToken ct)
        {
            var path = $"{userId}/{recordingId}.{extension}";
            _files[path] = ReadAll(content);
            return Task.FromResult(path);
        }

        public Task<string> SaveVoiceTrackAsync(Guid userId, Guid recordingId, string extension,
            Stream content, CancellationToken ct)
        {
            var path = $"{userId}/{recordingId}-voice.{extension}";
            _files[path] = ReadAll(content);
            return Task.FromResult(path);
        }

        public Task<Stream?> OpenReadAsync(Guid userId, string relativePath, CancellationToken ct)
            => Task.FromResult<Stream?>(_files.TryGetValue(relativePath, out var bytes)
                ? new MemoryStream(bytes) : null);

        public Task<bool> DeleteAsync(Guid userId, string relativePath, CancellationToken ct)
        {
            _files.Remove(relativePath);
            return Task.FromResult(true);
        }

        private static byte[] ReadAll(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
