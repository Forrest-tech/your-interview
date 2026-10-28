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
/// 票260 S3（非 1x 行上服务端：两步创建 + 行 kind 派生 + 评分隔离 + 混音 404）。
/// 纯映射/构造校验无需库；命令级测试用沙盒库门控（库不可用时跳过，
/// 与票111/108/258/259 先例同款），测试用户数据在 Dispose 时清理。
/// 行 kind 派生（前后端同一公式）：IsShadow=true 且 StoragePath 非空 → 跟读-混音行；
/// IsShadow=true 且 StoragePath 为空 且 VoiceStoragePath 非空 → 跟读-only 行；
/// IsShadow=true 且两路径皆空 → 未完成行；其余 → 普通行。
/// </summary>
public sealed class ShadowCrossDeviceS3Tests : IAsyncLifetime
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

    // ---------- 纯映射：四态 kind 派生 ----------

    [Fact]
    public void ToDto_OrdinaryRow_MapsHasMixTrackTrue()
    {
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "u/r.webm", "audio/webm", 10, 1.5, "en-US", false);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsShadow.Should().BeFalse();
        dto.HasMixTrack.Should().BeTrue();
        dto.HasVoiceTrack.Should().BeFalse();
    }

    [Fact]
    public void ToDto_ShadowMixRow_MapsBothTracksTrue()
    {
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "u/r.webm", "audio/webm", 10, 1.5, "en-US", true);
        rec.AttachVoiceTrack("u/r-voice.webm", "audio/webm", 42, 1.5, 0.25);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsShadow.Should().BeTrue();
        dto.HasMixTrack.Should().BeTrue();
        dto.HasVoiceTrack.Should().BeTrue();
        dto.TakeRate.Should().Be(1.5);
        dto.LaneShiftSec.Should().Be(0.25);
    }

    [Fact]
    public void ToDto_ShadowOnlyRow_MapsMixFalseVoiceTrue()
    {
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, "audio/webm", 0, 2.0, "en-US", true);
        rec.SetAlignment(1.5, 0.3);
        rec.AttachVoiceTrack("u/r-voice.webm", "audio/webm", 42, 1.5, 0.3);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsShadow.Should().BeTrue();
        dto.HasMixTrack.Should().BeFalse();
        dto.HasVoiceTrack.Should().BeTrue();
    }

    [Fact]
    public void ToDto_UnfinishedRow_MapsBothTracksFalse()
    {
        var rec = new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, "audio/webm", 0, 2.0, "en-US", true);
        rec.SetAlignment(1.5, 0.3);
        var dto = ListRecordingsQueryHandler.ToDto(rec);
        dto.IsShadow.Should().BeTrue();
        dto.HasMixTrack.Should().BeFalse();
        dto.HasVoiceTrack.Should().BeFalse();
        dto.TakeRate.Should().Be(1.5);
        dto.LaneShiftSec.Should().Be(0.3);
    }

    [Fact]
    public void Ctor_OrdinaryRowNullStoragePath_Throws()
    {
        var act = () => new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, "audio/webm", 0, 1.0, "en-US", false);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_NullIsShadowNullStoragePath_Throws()
    {
        // IsShadow 未传（历史行语义）仍按普通行处理：空 StoragePath 不合法。
        var act = () => new PracticeRecording(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, "audio/webm", 0, 1.0, "en-US");
        act.Should().Throw<ArgumentException>();
    }

    // ---------- 两步创建：校验矩阵 ----------

    [Theory]
    [InlineData(0.0, 1.5, 0.3, "ShadowRow.Duration")]
    [InlineData(-1.0, 1.5, 0.3, "ShadowRow.Duration")]
    [InlineData(2.0, 0.0, 0.3, "ShadowRow.TakeRate")]
    [InlineData(2.0, 2.5, 0.3, "ShadowRow.TakeRate")]
    [InlineData(2.0, -0.5, 0.3, "ShadowRow.TakeRate")]
    public async Task CreateShadowRow_InvalidValues_ReturnsValidation(
        double duration, double rate, double shift, string code)
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);

        await using var db = NewDb();
        var result = await new CreateShadowRowCommandHandler(db)
            .Handle(new CreateShadowRowCommand(user, matId, duration, rate, shift), default);
        result.IsSuccess.Should().BeFalse();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Code.Should().Be(code);
    }

    [Fact]
    public async Task CreateShadowRow_NonFiniteValues_ReturnsValidation()
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);

        await using var db = NewDb();
        var nanRate = await new CreateShadowRowCommandHandler(db)
            .Handle(new CreateShadowRowCommand(user, matId, 2.0, double.NaN, 0.3), default);
        nanRate.Error.Type.Should().Be(ErrorType.Validation);
        nanRate.Error.Code.Should().Be("ShadowRow.TakeRate");

        var infRate = await new CreateShadowRowCommandHandler(db)
            .Handle(new CreateShadowRowCommand(user, matId, 2.0, double.PositiveInfinity, 0.3), default);
        infRate.Error.Type.Should().Be(ErrorType.Validation);

        var nanShift = await new CreateShadowRowCommandHandler(db)
            .Handle(new CreateShadowRowCommand(user, matId, 2.0, 1.5, double.NaN), default);
        nanShift.Error.Type.Should().Be(ErrorType.Validation);
        nanShift.Error.Code.Should().Be("ShadowRow.LaneShift");
    }

    [Fact]
    public async Task CreateShadowRow_MaterialNotOwned_ReturnsNotFound()
    {
        if (!_available) return;
        var user = NewUser();
        var other = NewUser();
        var matId = await SeedMaterialAsync(other);

        await using var db = NewDb();
        var result = await new CreateShadowRowCommandHandler(db)
            .Handle(new CreateShadowRowCommand(user, matId, 2.0, 1.5, 0.3), default);
        result.IsSuccess.Should().BeFalse();
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    // ---------- 两步创建：建行 → 传人声 → 完整 ----------

    [Fact]
    public async Task CreateShadowRow_Valid_CreatesUnfinishedRowWithNullStoragePath()
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);

        Guid rowId;
        await using (var db = NewDb())
        {
            var result = await new CreateShadowRowCommandHandler(db)
                .Handle(new CreateShadowRowCommand(user, matId, 2.5, 1.5, -0.2), default);
            result.IsSuccess.Should().BeTrue(result.Error.Description);
            result.Value.IsShadow.Should().BeTrue();
            result.Value.HasMixTrack.Should().BeFalse();
            result.Value.HasVoiceTrack.Should().BeFalse();
            result.Value.TakeRate.Should().Be(1.5);
            result.Value.LaneShiftSec.Should().Be(-0.2);
            result.Value.DurationSeconds.Should().Be(2.5);
            rowId = result.Value.Id;
        }

        await using var check = NewDb();
        var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == rowId);
        row.StoragePath.Should().BeNull();
        row.VoiceStoragePath.Should().BeNull();
    }

    [Fact]
    public async Task TwoStepFlow_CreateThenVoiceTrack_RowBecomesCompleteAndMixStill404()
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);
        var store = new MemoryAudioStore();

        Guid rowId;
        await using (var db = NewDb())
        {
            var created = await new CreateShadowRowCommandHandler(db)
                .Handle(new CreateShadowRowCommand(user, matId, 2.5, 1.5, 0.3), default);
            created.IsSuccess.Should().BeTrue(created.Error.Description);
            rowId = created.Value.Id;
        }

        // 第二步：人声轨上传（复用 S2 端点），成功后行 = 完整跟读行。
        var voice = new byte[] { 11, 22, 33 };
        await using (var db = NewDb())
        {
            var uploaded = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(user, rowId, "voice.webm", "audio/webm",
                    new MemoryStream(voice), null, null), default);
            uploaded.IsSuccess.Should().BeTrue(uploaded.Error.Description);
        }

        await using (var check = NewDb())
        {
            var row = await check.Recordings.AsNoTracking().SingleAsync(r => r.Id == rowId);
            row.VoiceStoragePath.Should().NotBeNullOrWhiteSpace();
            // 对齐参数由第一步写入，第二步不带参数时保留（不抹掉）。
            row.TakeRate.Should().Be(1.5);
            row.LaneShiftSec.Should().Be(0.3);

            var dto = ListRecordingsQueryHandler.ToDto(row);
            dto.HasMixTrack.Should().BeFalse();
            dto.HasVoiceTrack.Should().BeTrue();

            // 混音端点对跟读-only 行 404（绝不把人声轨当混音回流）。
            var mix = await new GetRecordingAudioQueryHandler(check, store)
                .Handle(new GetRecordingAudioQuery(user, rowId), default);
            mix.IsSuccess.Should().BeFalse();
            mix.Error.Type.Should().Be(ErrorType.NotFound);

            // 人声轨端点回读字节一致。
            var get = await new GetVoiceTrackQueryHandler(check, store)
                .Handle(new GetVoiceTrackQuery(user, rowId), default);
            get.IsSuccess.Should().BeTrue(get.Error.Description);
            var (voiceStream, _, _) = get.Value;
            await using (voiceStream)
            {
                var back = new MemoryStream();
                await voiceStream.CopyToAsync(back);
                back.ToArray().Should().Equal(voice);
            }
        }
    }

    // ---------- 混音端点：普通行与跟读-混音行仍 200 ----------

    [Fact]
    public async Task GetRecordingAudio_OrdinaryAndShadowMixRows_ReturnStream()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();

        var ordinaryId = await SeedOrdinaryRecordingAsync(user, store, isShadow: false);
        var mixId = await SeedOrdinaryRecordingAsync(user, store, isShadow: true);

        await using var check = NewDb();
        foreach (var id in new[] { ordinaryId, mixId })
        {
            var get = await new GetRecordingAudioQueryHandler(check, store)
                .Handle(new GetRecordingAudioQuery(user, id), default);
            get.IsSuccess.Should().BeTrue(get.Error.Description);
            var (mixStream, _, _) = get.Value;
            await using (mixStream)
            {
                var back = new MemoryStream();
                await mixStream.CopyToAsync(back);
                back.ToArray().Should().Equal(new byte[] { 1, 2, 3 });
            }
        }
    }

    // ---------- 评分隔离：三态拒绝 + 普通行放行 ----------

    [Fact]
    public async Task SaveRecordingScore_ShadowRows_ReturnValidationOrdinarySucceeds()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();

        var ordinaryId = await SeedOrdinaryRecordingAsync(user, store, isShadow: false);
        var mixId = await SeedOrdinaryRecordingAsync(user, store, isShadow: true);
        var onlyId = await SeedShadowOnlyRowAsync(user, store);
        var unfinishedId = await SeedUnfinishedRowAsync(user);

        await using var db = NewDb();
        foreach (var id in new[] { mixId, onlyId, unfinishedId })
        {
            var denied = await new SaveRecordingScoreCommandHandler(db)
                .Handle(ScoreCmd(user, id), default);
            denied.IsSuccess.Should().BeFalse();
            denied.Error.Type.Should().Be(ErrorType.Validation);
            denied.Error.Code.Should().Be("Recording.ShadowNotScored");
        }

        var ok = await new SaveRecordingScoreCommandHandler(db)
            .Handle(ScoreCmd(user, ordinaryId), default);
        ok.IsSuccess.Should().BeTrue(ok.Error.Description);
    }

    // ---------- 删除：跟读-only 行只删人声文件 ----------

    [Fact]
    public async Task DeleteRecording_ShadowOnlyRow_DeletesVoiceFileOnlyAndRow()
    {
        if (!_available) return;
        var user = NewUser();
        var store = new MemoryAudioStore();
        var rowId = await SeedShadowOnlyRowAsync(user, store);

        string? voicePath;
        await using (var db = NewDb())
        {
            voicePath = (await db.Recordings.AsNoTracking().SingleAsync(r => r.Id == rowId)).VoiceStoragePath;
        }
        voicePath.Should().NotBeNullOrWhiteSpace();
        store.Contains(voicePath!).Should().BeTrue();

        await using (var db = NewDb())
        {
            var del = await new DeleteRecordingCommandHandler(db, store,
                    NullLogger<DeleteRecordingCommandHandler>.Instance)
                .Handle(new DeleteRecordingCommand(user, rowId), default);
            del.IsSuccess.Should().BeTrue(del.Error.Description);
        }

        store.Contains(voicePath!).Should().BeFalse();
        await using var check = NewDb();
        (await check.Recordings.AnyAsync(r => r.Id == rowId)).Should().BeFalse();
    }

    // ---------- 普通上传 file 必填口径不变 ----------

    [Fact]
    public async Task SaveRecording_EmptyStream_StillValidation()
    {
        if (!_available) return;
        var user = NewUser();
        var matId = await SeedMaterialAsync(user);
        var store = new MemoryAudioStore();

        await using var db = NewDb();
        var result = await new SaveRecordingCommandHandler(db, store).Handle(
            new SaveRecordingCommand(user, matId, "empty.webm", "audio/webm",
                1.0, "en-US", new MemoryStream([])), default);
        result.IsSuccess.Should().BeFalse();
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    // ---------- 种子与辅助 ----------

    private async Task<Guid> SeedMaterialAsync(Guid user)
    {
        await using var db = NewDb();
        var mat = new PracticeMaterial(user, null, MaterialKind.File, "s3-material");
        db.Materials.Add(mat);
        await db.SaveChangesAsync();
        return mat.Id;
    }

    private async Task<Guid> SeedOrdinaryRecordingAsync(Guid user, MemoryAudioStore store, bool isShadow)
    {
        var matId = await SeedMaterialAsync(user);
        await using var db = NewDb();
        var result = await new SaveRecordingCommandHandler(db, store).Handle(
            new SaveRecordingCommand(user, matId, "r.webm", "audio/webm",
                1.5, "en-US", new MemoryStream(new byte[] { 1, 2, 3 }), IsShadow: isShadow),
            default);
        result.IsSuccess.Should().BeTrue(result.Error.Description);
        return result.Value.Id;
    }

    private async Task<Guid> SeedShadowOnlyRowAsync(Guid user, MemoryAudioStore store)
    {
        var matId = await SeedMaterialAsync(user);
        Guid rowId;
        await using (var db = NewDb())
        {
            var created = await new CreateShadowRowCommandHandler(db)
                .Handle(new CreateShadowRowCommand(user, matId, 2.5, 1.5, 0.3), default);
            created.IsSuccess.Should().BeTrue(created.Error.Description);
            rowId = created.Value.Id;
        }
        await using (var db = NewDb())
        {
            var uploaded = await new UploadVoiceTrackCommandHandler(db, store,
                    NullLogger<UploadVoiceTrackCommandHandler>.Instance)
                .Handle(new UploadVoiceTrackCommand(user, rowId, "voice.webm", "audio/webm",
                    new MemoryStream(new byte[] { 9, 9, 9 }), null, null), default);
            uploaded.IsSuccess.Should().BeTrue(uploaded.Error.Description);
        }
        return rowId;
    }

    private async Task<Guid> SeedUnfinishedRowAsync(Guid user)
    {
        var matId = await SeedMaterialAsync(user);
        await using var db = NewDb();
        var created = await new CreateShadowRowCommandHandler(db)
            .Handle(new CreateShadowRowCommand(user, matId, 2.5, 1.5, 0.3), default);
        created.IsSuccess.Should().BeTrue(created.Error.Description);
        return created.Value.Id;
    }

    private static SaveRecordingScoreCommand ScoreCmd(Guid user, Guid recId) =>
        new(user, recId, 80, 80, null, null, null, "recognized", null, "canadacentral", "reference");

    /// <summary>内存音频仓：字节真实往返（与 S2 测试同一口径的精简版）。</summary>
    private sealed class MemoryAudioStore : IAudioStore
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public bool IsConfigured => true;
        public string DefaultRoot => "/tmp/s3-mem-audio";
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

        public bool Contains(string path) => _files.ContainsKey(path);

        private static byte[] ReadAll(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
