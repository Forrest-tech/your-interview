using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票259 S2（跨设备跟读：人声跟人走）—— 7 例生命周期矩阵 + DTO/实体映射。
/// handler 级测试用内存音频仓（真实字节往返，含旧文件删除断言）；
/// DB round-trip 用沙盒库门控（库不可用时跳过，与票111/108/258 先例同款），
/// 测试用户数据在 Dispose 时清理。
/// </summary>
public sealed class ShadowCrossDeviceS2Tests : IAsyncLifetime
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

    private static PracticeRecording NewEntity() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "u/r.webm",
            "audio/webm", 10, 1.5, "en-US", true);

    // ---------- 纯映射：DTO 只见 HasVoiceTrack/TakeRate/LaneShiftSec ----------

    [Fact]
    public void ToDto_NoVoiceTrack_MapsFalseAndNulls()
    {
        var dto = ListRecordingsQueryHandler.ToDto(NewEntity());
        dto.HasVoiceTrack.Should().BeFalse();
        dto.TakeRate.Should().BeNull();
        dto.LaneShiftSec.Should().BeNull();
    }

    [Fact]
    public void ToDto_WithVoiceTrack_MapsTrueAndParams()
    {
        var rec = NewEntity();
        rec.AttachVoiceTrack("u/r-voice.webm", "audio/webm", 42, 1.5, 0.25);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.HasVoiceTrack.Should().BeTrue();
        dto.TakeRate.Should().Be(1.5);
        dto.LaneShiftSec.Should().Be(0.25);
    }

    [Fact]
    public void AttachVoiceTrack_AbsentParams_KeepsExisting()
    {
        var rec = NewEntity();
        rec.AttachVoiceTrack("u/r-voice.webm", "audio/webm", 42, 1.5, 0.25);
        // 重传不带对齐参数：路径/类型/字节覆盖，对齐参数保留。
        rec.AttachVoiceTrack("u/r-voice.mp3", "audio/mp3", 99, null, null);
        rec.VoiceStoragePath.Should().Be("u/r-voice.mp3");
        rec.VoiceContentType.Should().Be("audio/mp3");
        rec.VoiceSizeBytes.Should().Be(99);
        rec.TakeRate.Should().Be(1.5);
        rec.LaneShiftSec.Should().Be(0.25);
    }

    // ---------- 7 例生命周期矩阵（库门控） ----------

    // ① 首次上传：三元组落库 + GET 回读字节一致 + 文件名带 -voice。
    [Fact]
    public async Task UploadVoiceTrack_FirstUpload_PersistsTripleAndGetReturnsBytes()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        var voice = new byte[] { 10, 20, 30, 40 };
        await using (var db = NewDb())
        {
            var result = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(user, recId, "voice.webm", "audio/webm",
                    new MemoryStream(voice), 1.5, 0.3), default);
            result.IsSuccess.Should().BeTrue(result.Error.Description);
            result.Value.ContentType.Should().Be("audio/webm");
            result.Value.SizeBytes.Should().Be(4);
            result.Value.TakeRate.Should().Be(1.5);
            result.Value.LaneShiftSec.Should().Be(0.3);
        }

        await using (var check = NewDb())
        {
            var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
            row.VoiceStoragePath.Should().Contain("-voice.webm");
            row.VoiceContentType.Should().Be("audio/webm");
            row.VoiceSizeBytes.Should().Be(4);
            row.TakeRate.Should().Be(1.5);
            row.LaneShiftSec.Should().Be(0.3);

            var get = await new GetVoiceTrackQueryHandler(check, store)
                .Handle(new GetVoiceTrackQuery(user, recId), default);
            get.IsSuccess.Should().BeTrue(get.Error.Description);
            var (audioStream, voiceContentType, voiceFileName) = get.Value;
            voiceContentType.Should().Be("audio/webm");
            voiceFileName.Should().Contain("-voice.webm");
            await using var s = audioStream;
            var back = new MemoryStream();
            await s.CopyToAsync(back);
            back.ToArray().Should().Equal(voice);
        }
    }

    // ②a 重传（换扩展名）：旧文件删除、新文件生效，仓里无旧文件残留。
    [Fact]
    public async Task UploadVoiceTrack_ReuploadDifferentExt_OldFileDeletedNoResidue()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        var first = await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 1, 1, 1 });
        first.Should().NotBeNullOrWhiteSpace();
        store.Contains(first!).Should().BeTrue();

        var second = await UploadVoiceAsync(user, recId, store, "voice.mp3", "audio/mp3",
            new byte[] { 2, 2, 2, 2 });
        second.Should().NotBeNullOrWhiteSpace();
        second.Should().NotBe(first);
        store.Contains(first!).Should().BeFalse("旧扩展名的人声文件必须被删掉");
        store.Read(second!).Should().Equal(new byte[] { 2, 2, 2, 2 });

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
        row.VoiceStoragePath.Should().Be(second);
        row.VoiceContentType.Should().Be("audio/mp3");
        row.VoiceSizeBytes.Should().Be(4);
    }

    // ②b 重传（同扩展名 = 原地覆盖）：新文件绝不能被「删旧」误删。
    [Fact]
    public async Task UploadVoiceTrack_ReuploadSameExt_NewFileSurvivesOldDeleteSkip()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        var first = await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 1, 1, 1 });
        var second = await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 9, 9 });
        second.Should().Be(first, "同月同扩展名 → 同一路径，原地覆盖");
        store.Read(second!).Should().Equal(new byte[] { 9, 9 });

        // GET 必须读到新 blob（若删旧逻辑误伤，GET 会 404）。
        await using var check = NewDb();
        var get = await new GetVoiceTrackQueryHandler(check, store)
            .Handle(new GetVoiceTrackQuery(user, recId), default);
        get.IsSuccess.Should().BeTrue(get.Error.Description);
        var (audioStream2, _, _) = get.Value;
        await using var s = audioStream2;
        var back = new MemoryStream();
        await s.CopyToAsync(back);
        back.ToArray().Should().Equal(new byte[] { 9, 9 });
    }

    // ②c 重传不带对齐参数：保留既有对齐参数（只换 blob）。
    [Fact]
    public async Task UploadVoiceTrack_ReuploadWithoutParams_KeepsAlignment()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 1 }, 1.5, 0.3);
        await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 2, 2 });

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
        row.TakeRate.Should().Be(1.5);
        row.LaneShiftSec.Should().Be(0.3);
        row.VoiceSizeBytes.Should().Be(2);
    }

    // ③ 删除：混音 + 人声双删、行删；人声删失败 → Warning 语义下行仍删。
    [Fact]
    public async Task DeleteRecording_DeletesBothFilesAndRow()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);
        var voicePath = await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 7, 7, 7 });

        await using var db = NewDb();
        var mixPath = (await db.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId)).StoragePath;
        store.Contains(mixPath).Should().BeTrue();
        store.Contains(voicePath!).Should().BeTrue();

        var del = await new DeleteRecordingCommandHandler(db, store,
                NullLogger<DeleteRecordingCommandHandler>.Instance)
            .Handle(new DeleteRecordingCommand(user, recId), default);
        del.IsSuccess.Should().BeTrue(del.Error.Description);

        store.Contains(mixPath).Should().BeFalse();
        store.Contains(voicePath!).Should().BeFalse();
        await using var check = NewDb();
        (await check.Recordings.AnyAsync(r => r.Id == recId)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteRecording_VoiceDeleteFails_RowStillDeleted()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);
        var voicePath = await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 7, 7, 7 });
        store.FailDeletePaths.Add(voicePath!);

        await using var db = NewDb();
        var del = await new DeleteRecordingCommandHandler(db, store,
                NullLogger<DeleteRecordingCommandHandler>.Instance)
            .Handle(new DeleteRecordingCommand(user, recId), default);
        del.IsSuccess.Should().BeTrue(del.Error.Description);

        await using var check = NewDb();
        (await check.Recordings.AnyAsync(r => r.Id == recId)).Should().BeFalse();
        store.Contains(voicePath!).Should().BeTrue("删失败的人声文件成孤儿（Warning 语义），但行必须已删");
    }

    // ④ 旧行（无声轨）：GET 人声轨 404，不 500。
    [Fact]
    public async Task GetVoiceTrack_LegacyRow_ReturnsNotFound()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        await using var check = NewDb();
        var get = await new GetVoiceTrackQueryHandler(check, store)
            .Handle(new GetVoiceTrackQuery(user, recId), default);
        get.IsSuccess.Should().BeFalse();
        get.Error.Type.Should().Be(ErrorType.NotFound);
    }

    // ⑤ 非本人：POST/GET 均为 404（NotFound），绝不 403；行一字不动。
    [Fact]
    public async Task VoiceTrack_NonOwner_PostAndGetReturnNotFoundRowUnchanged()
    {
        if (!_available) return;
        var user = NewUser();
        var other = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        await using (var db = NewDb())
        {
            var post = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(other, recId, "voice.webm", "audio/webm",
                    new MemoryStream(new byte[] { 1, 2 }), 1.0, 0.1), default);
            post.IsSuccess.Should().BeFalse();
            post.Error.Type.Should().Be(ErrorType.NotFound);
        }

        await using (var db = NewDb())
        {
            var get = await new GetVoiceTrackQueryHandler(db, store)
                .Handle(new GetVoiceTrackQuery(other, recId), default);
            get.IsSuccess.Should().BeFalse();
            get.Error.Type.Should().Be(ErrorType.NotFound);

            var row = await db.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
            row.VoiceStoragePath.Should().BeNull();
            row.VoiceSizeBytes.Should().BeNull();
        }
    }

    // ⑥ 非音频 ContentType → 400（Validation），行一字不动。
    [Fact]
    public async Task UploadVoiceTrack_NonAudio_ReturnsValidationRowUnchanged()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        await using (var db = NewDb())
        {
            var result = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(user, recId, "voice.txt", "text/plain",
                    new MemoryStream(new byte[] { 1, 2, 3 })), default);
            result.IsSuccess.Should().BeFalse();
            result.Error.Type.Should().Be(ErrorType.Validation);
            result.Error.Code.Should().Be("Recording.NotAudio");
        }

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
        row.VoiceStoragePath.Should().BeNull();
    }

    // ⑦ 超 25MB → 400（Validation），行一字不动。
    [Fact]
    public async Task UploadVoiceTrack_Over25MB_ReturnsValidationRowUnchanged()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);

        await using (var db = NewDb())
        {
            var big = new MemoryStream(new byte[25 * 1024 * 1024 + 1]);
            var result = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(user, recId, "voice.webm", "audio/webm", big),
                    default);
            result.IsSuccess.Should().BeFalse();
            result.Error.Type.Should().Be(ErrorType.Validation);
            result.Error.Code.Should().Be("Recording.TooLarge");
        }

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
        row.VoiceStoragePath.Should().BeNull();
    }

    // ⑧ 安全顺序：新文件回读校验失败 → 直接失败，旧轨与行一字不动。
    //   （重传用不同扩展名，使新路径 ≠ 旧路径：同路径原地覆盖时，
    //    新写成功本身就是旧字节的合法替换，校验失败只影响「是否认领」，
    //    行仍指向该路径上的新 blob，重试即可认领 —— 不属本例断言范围。）
    [Fact]
    public async Task UploadVoiceTrack_VerifyFails_OldTrackAndRowUnchanged()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var recId = await SeedShadowRecordingAsync(user, store);
        var oldPath = await UploadVoiceAsync(user, recId, store, "voice.webm", "audio/webm",
            new byte[] { 5, 5, 5 });

        store.FailVoiceReads = true;
        await using (var db = NewDb())
        {
            var result = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(user, recId, "voice.mp3", "audio/mp3",
                    new MemoryStream(new byte[] { 6, 6 })), default);
            result.IsSuccess.Should().BeFalse();
            result.Error.Type.Should().Be(ErrorType.Unexpected);
        }

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId);
        row.VoiceStoragePath.Should().Be(oldPath);
        row.VoiceSizeBytes.Should().Be(3);
        store.FailVoiceReads = false;
        store.Read(oldPath!).Should().Equal(new byte[] { 5, 5, 5 });
    }

    // ---------- 种子与辅助 ----------

    private async Task<Guid> SeedShadowRecordingAsync(Guid user, MemoryAudioStore store)
    {
        await using (var db = NewDb())
        {
            var mat = new PracticeMaterial(user, null, MaterialKind.File, "s2-material");
            db.Materials.Add(mat);
            await db.SaveChangesAsync();

            var result = await new SaveRecordingCommandHandler(db, store).Handle(
                new SaveRecordingCommand(user, mat.Id, "shadow.webm", "audio/webm",
                    1.5, "en-US", new MemoryStream(new byte[] { 1, 2, 3 }), IsShadow: true),
                default);
            result.IsSuccess.Should().BeTrue(result.Error.Description);
            return result.Value.Id;
        }
    }

    private async Task<string?> UploadVoiceAsync(Guid user, Guid recId, MemoryAudioStore store,
        string fileName, string contentType, byte[] bytes,
        double? takeRate = null, double? laneShiftSec = null)
    {
        await using var db = NewDb();
        var result = await new UploadVoiceTrackCommandHandler(db, store,
                NullLogger<UploadVoiceTrackCommandHandler>.Instance)
            .Handle(new UploadVoiceTrackCommand(user, recId, fileName, contentType,
                new MemoryStream(bytes), takeRate, laneShiftSec), default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        await using var check = NewDb();
        return (await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == recId)).VoiceStoragePath;
    }

    /// <summary>内存音频仓：字节真实往返；FailDeletePaths/FailVoiceReads 模拟删/读失败。</summary>
    private sealed class MemoryAudioStore : IAudioStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public HashSet<string> FailDeletePaths { get; } = new(StringComparer.Ordinal);
        public bool FailVoiceReads { get; set; }

        public bool IsConfigured => true;
        public string DefaultRoot => "/tmp/s2-mem-audio";
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
        {
            if (FailVoiceReads && relativePath.Contains("-voice.", StringComparison.Ordinal))
                return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(_files.TryGetValue(relativePath, out var bytes)
                ? new MemoryStream(bytes) : null);
        }

        public Task<bool> DeleteAsync(Guid userId, string relativePath, CancellationToken ct)
        {
            if (FailDeletePaths.Contains(relativePath)) return Task.FromResult(false);
            _files.Remove(relativePath);
            return Task.FromResult(true);
        }

        public bool Contains(string path) => _files.ContainsKey(path);
        public byte[]? Read(string path) => _files.TryGetValue(path, out var b) ? b : null;

        private static byte[] ReadAll(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
