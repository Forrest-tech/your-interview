using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// 录音:保存 + 评分缓存
// ============================================================

public sealed record ListRecordingsQuery(Guid UserId, Guid MaterialId)
    : MediatR.IRequest<IReadOnlyList<RecordingDto>>;

/// <summary>
/// ★ 第三十九轮:列出当前用户的**全部录音**(不按素材过滤)。
/// 数据安全兼底 —— 素材被删/ID 变动也不会让录音在前端"消失"。
/// </summary>
public sealed record ListAllRecordingsQuery(Guid UserId)
    : MediatR.IRequest<IReadOnlyList<RecordingDto>>;

public sealed class ListAllRecordingsQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<ListAllRecordingsQuery, IReadOnlyList<RecordingDto>>
{
    public async Task<IReadOnlyList<RecordingDto>> Handle(ListAllRecordingsQuery r, CancellationToken ct)
    {
        var rows = await db.Recordings.AsNoTracking()
            .Include(x => x.Score)
            .Where(x => x.UserId == r.UserId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var versions = await ListRecordingsQueryHandler.ScoreVersionsAsync(db, rows, ct);
        var recVersions = await ListRecordingsQueryHandler.RecordingVersionsAsync(db, rows, ct);
        return rows.Select(x => ListRecordingsQueryHandler.ToDto(x, versions, recVersions)).ToList();
    }
}

public sealed class ListRecordingsQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<ListRecordingsQuery, IReadOnlyList<RecordingDto>>
{
    public async Task<IReadOnlyList<RecordingDto>> Handle(ListRecordingsQuery r, CancellationToken ct)
    {
        var rows = await db.Recordings.AsNoTracking()
            .Include(x => x.Score)
            .Where(x => x.UserId == r.UserId && x.MaterialId == r.MaterialId && !x.IsDeleted)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var versions = await ScoreVersionsAsync(db, rows, ct);
        var recVersions = await RecordingVersionsAsync(db, rows, ct);
        return rows.Select(x => ToDto(x, versions, recVersions)).ToList();
    }

    /// <summary>票108 A10:批量取评分行读时 xmin(影子属性须投影读,AsNoTracking 实体上取不到)。</summary>
    internal static async Task<IReadOnlyDictionary<Guid, uint>> ScoreVersionsAsync(
        AssessmentDbContext db, IReadOnlyList<PracticeRecording> rows, CancellationToken ct)
    {
        var ids = rows.Where(x => x.Score is not null).Select(x => x.Id).ToList();
        if (ids.Count == 0) return new Dictionary<Guid, uint>();
        var vs = await db.RecordingScores.AsNoTracking()
            .Where(s => ids.Contains(s.RecordingId))
            .Select(s => new { s.RecordingId, V = EF.Property<uint>(s, "xmin") })
            .ToListAsync(ct);
        return vs.ToDictionary(x => x.RecordingId, x => x.V);
    }

    /// <summary>票111 A11:批量取录音行自身读时 xmin(影子属性须投影读)。</summary>
    internal static async Task<IReadOnlyDictionary<Guid, uint>> RecordingVersionsAsync(
        AssessmentDbContext db, IReadOnlyList<PracticeRecording> rows, CancellationToken ct)
    {
        var ids = rows.Select(x => x.Id).ToList();
        if (ids.Count == 0) return new Dictionary<Guid, uint>();
        var vs = await db.Recordings.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, V = EF.Property<uint>(x, "xmin") })
            .ToListAsync(ct);
        return vs.ToDictionary(x => x.Id, x => x.V);
    }

    internal static RecordingDto ToDto(PracticeRecording x,
        IReadOnlyDictionary<Guid, uint>? scoreVersions = null,
        IReadOnlyDictionary<Guid, uint>? recordingVersions = null) => new(
        x.Id, x.MaterialId, x.DurationSeconds, x.SizeBytes, x.ContentType, x.CreatedAt,
        x.Score is null ? null : new RecordingScoreDto(
            x.Score.PronScore, x.Score.AccuracyScore, x.Score.FluencyScore,
            x.Score.CompletenessScore, x.Score.ProsodyScore,
            x.Score.RecognizedText,
            DeserializeWords(x.Score.WordsJson),
            x.Score.ReferenceText, x.Score.AssessedAt,
            // 计费两字段列表口径逐字不变(原实现未回填,本票不顺带改),只补 Version。
            null, null,
            scoreVersions is not null && scoreVersions.TryGetValue(x.Id, out var v) ? v : null),
        recordingVersions is not null && recordingVersions.TryGetValue(x.Id, out var rv) ? rv : null,
        // ★ 票258 S1:NULL(历史行)映射为 false,前端只见 bool。
        x.IsShadow ?? false,
        // ★ 票259 S2:人声轨存在 = VoiceStoragePath 非空；对齐参数原样透传（NULL 保持 NULL）。
        !string.IsNullOrWhiteSpace(x.VoiceStoragePath),
        // ★ 票260 S3:混音存在 = StoragePath 非空；与 HasVoiceTrack 一起构成行 kind 派生。
        !string.IsNullOrWhiteSpace(x.StoragePath),
        x.TakeRate,
        x.LaneShiftSec);

    internal static IReadOnlyList<WordScoreDto> DeserializeWords(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<List<WordScoreDto>>(json, JsonOpts) ?? [];
        }
        catch
        {
            // 历史脏数据不该让整个列表接口 500 —— 降级成空明细,页面仍可用
            return [];
        }
    }

    internal static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// 保存一次录音 = 音频写盘 + 元数据入库,一步完成,返回可用的 RecordingDto。
/// </summary>
/// <remarks>
/// 为什么合成一个命令而不是"先建元数据再上传文件"两步:
///   两步制会在中间留下**孤儿元数据**(记录了路径但文件没传成功),
///   列表里就会出现一条点了播不出来的录音 —— 用户会以为功能坏了。
///   先落盘再写库:盘上有文件、库里没记录最多浪费几 MB,
///   反过来则是用户可见的坏数据。宁可浪费空间,不给用户看坏数据。
/// </remarks>
public sealed record SaveRecordingCommand(Guid UserId, Guid MaterialId, string? FileName,
    string ContentType, double DurationSeconds, string SourceLanguage, Stream Audio,
    // ★ 票258 S1:跟读上传传 true;普通上传不传(缺省 false),行为一字不变。
    bool IsShadow = false)
    : MediatR.IRequest<Result<RecordingDto>>;

public sealed class SaveRecordingCommandHandler(AssessmentDbContext db, IAudioStore store)
    : MediatR.IRequestHandler<SaveRecordingCommand, Result<RecordingDto>>
{
    /// <summary>单条录音大小上限。练习跟读是短音频;超过这个数说明传错了文件。</summary>
    private const long MaxBytes = 25 * 1024 * 1024;

    public async Task<Result<RecordingDto>> Handle(SaveRecordingCommand r, CancellationToken ct)
    {
        if (!store.IsConfigured)
            return Result.Failure<RecordingDto>(Error.Unexpected(
                "音频存储未配置(Storage:RootDirectory)。"));

        // 素材必须属于我 —— 否则就是越权往别人素材里塞录音
        var owns = await db.Materials.AnyAsync(
            x => x.Id == r.MaterialId && x.UserId == r.UserId, ct);
        if (!owns)
            return Result.Failure<RecordingDto>(Error.NotFound("素材"));

        var recordingId = Guid.NewGuid();
        var ext = GuessExtension(r.ContentType, r.FileName);

        // 先数一遍大小再决定是否落盘 —— 避免被超大上传塞满磁盘
        var buffered = new MemoryStream();
        await r.Audio.CopyToAsync(buffered, ct);
        if (buffered.Length == 0)
            return Result.Failure<RecordingDto>(Error.Validation("Recording.Empty", "录音内容为空。"));
        if (buffered.Length > MaxBytes)
            return Result.Failure<RecordingDto>(Error.Validation("Recording.TooLarge",
                $"录音 {buffered.Length / 1024.0 / 1024.0:F1}MB 超过 {MaxBytes / 1024 / 1024}MB 上限。"));

        buffered.Position = 0;
        var relativePath = await store.SaveAsync(r.UserId, recordingId, ext, buffered, ct);

        // id 显式传给实体 —— 与上面命名磁盘文件用的 recordingId 是同一个值,
        // 保证"录音 id → 文件路径"这条链路闭合(不再用反射硬改属性,那是脏写法)。
        var rec = new PracticeRecording(recordingId, r.UserId, r.MaterialId, relativePath,
            string.IsNullOrWhiteSpace(r.ContentType) ? "audio/webm" : r.ContentType,
            buffered.Length, r.DurationSeconds, r.SourceLanguage, r.IsShadow);

        db.Recordings.Add(rec);
        await db.SaveChangesAsync(ct);

        return Result.Success(ListRecordingsQueryHandler.ToDto(rec));
    }

    /// <summary>
    /// 从 MIME 猜扩展名。
    /// 浏览器录的是 audio/webm;opus,但也要能吃下别处传来的 wav/mp3。
    /// </summary>
    /// 票 259(S3-S2)：人声轨上传复用同一 MIME→扩展名映射（分叉映射会让
    /// 混音与人声轨的扩展名口径不一致）。
    internal static string GuessExtension(string? contentType, string? fileName)
    {
        var ct = (contentType ?? string.Empty).ToLowerInvariant();
        if (ct.Contains("webm")) return "webm";
        if (ct.Contains("ogg")) return "ogg";
        if (ct.Contains("wav")) return "wav";
        if (ct.Contains("mpeg") || ct.Contains("mp3")) return "mp3";
        if (ct.Contains("mp4") || ct.Contains("m4a")) return "m4a";

        var ext = Path.GetExtension(fileName ?? string.Empty).TrimStart('.');
        return string.IsNullOrWhiteSpace(ext) ? "webm" : ext.ToLowerInvariant();
    }
}

/// <summary>
/// 票260 S3：创建跟读-only 行（两步创建的第一步，纯元数据，无文件）。
/// 普通录音上传流程一字不动 —— 本命令是 P29b 修订后非 1x 行上服务端的唯一合法入口。
/// 行 kind 派生（前后端同一公式）：IsShadow=true 且 StoragePath 非空 → 跟读-混音行；
/// IsShadow=true 且 StoragePath 为空 且 VoiceStoragePath 非空 → 跟读-only 行；
/// IsShadow=true 且两路径皆空 → 未完成行；其余 → 普通行。
/// 建出的行即未完成行（无混音、无人声轨）；第二步经 S2 人声轨端点传人声轨后才完整。
/// 未完成行在别设备不可见（前端 shadow 列表按 HasVoiceTrack + 本地链接过滤），
/// 录制设备可见（本地人声 blob 还在，显示上传中/失败、可重试）。
/// </summary>
public sealed record CreateShadowRowCommand(Guid UserId, Guid MaterialId,
    double DurationSeconds, double TakeRate, double LaneShiftSec)
    : MediatR.IRequest<Result<RecordingDto>>;

public sealed class CreateShadowRowCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<CreateShadowRowCommand, Result<RecordingDto>>
{
    public async Task<Result<RecordingDto>> Handle(CreateShadowRowCommand r, CancellationToken ct)
    {
        // 素材必须属于我 —— 与普通上传同一口径，非本人素材 = 不存在（404）。
        var owns = await db.Materials.AnyAsync(
            x => x.Id == r.MaterialId && x.UserId == r.UserId, ct);
        if (!owns)
            return Result.Failure<RecordingDto>(Error.NotFound("素材"));

        // 校验矩阵（缺一不可；duration 与现有 durationSeconds 表单字段同一信任模型，
        // 即客户端按 §5 联合时长公式算、服务端如实存，但仍挡非法值）。
        if (!(r.DurationSeconds > 0))
            return Result.Failure<RecordingDto>(Error.Validation("ShadowRow.Duration",
                "行时长必须大于 0。"));
        if (!double.IsFinite(r.TakeRate) || r.TakeRate <= 0 || r.TakeRate > 2)
            return Result.Failure<RecordingDto>(Error.Validation("ShadowRow.TakeRate",
                "跟读倍速必须在 (0, 2] 区间内。"));
        if (!double.IsFinite(r.LaneShiftSec))
            return Result.Failure<RecordingDto>(Error.Validation("ShadowRow.LaneShift",
                "laneShift 必须是有限数值。"));

        var rec = new PracticeRecording(Guid.NewGuid(), r.UserId, r.MaterialId,
            storagePath: null, contentType: "audio/webm", sizeBytes: 0,
            durationSeconds: r.DurationSeconds, sourceLanguage: "en-US", isShadow: true);
        // 对齐参数随行落库（两步创建的第一步即写，不等第二步人声轨）；
        // 人声轨端点重传不带参数时保留既有值（AttachVoiceTrack 语义）。
        // VoiceStoragePath 保持 NULL（≠ 空串）⇒ HasVoiceTrack=false ⇒ 未完成行。
        rec.SetAlignment(r.TakeRate, r.LaneShiftSec);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync(ct);

        return Result.Success(ListRecordingsQueryHandler.ToDto(rec));
    }
}

/// <summary>取一条录音的音频流(用于回放)。</summary>
public sealed record GetRecordingAudioQuery(Guid UserId, Guid RecordingId)
    : MediatR.IRequest<Result<(Stream Audio, string ContentType, string FileName)>>;

public sealed class GetRecordingAudioQueryHandler(AssessmentDbContext db, IAudioStore store)
    : MediatR.IRequestHandler<GetRecordingAudioQuery, Result<(Stream, string, string)>>
{
    public async Task<Result<(Stream, string, string)>> Handle(GetRecordingAudioQuery r, CancellationToken ct)
    {
        var rec = await db.Recordings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId && !x.IsDeleted, ct);
        if (rec is null) return Result.Failure<(Stream, string, string)>(Error.NotFound("录音"));

        // ★ 票260 S3：跟读-only 行无混音文件 —— 混音端点对它 404，绝不把人声轨当混音回流。
        //   普通行与跟读-混音行的 StoragePath 由构造器保证非空，此处只挡空路径行。
        if (string.IsNullOrWhiteSpace(rec.StoragePath))
            return Result.Failure<(Stream, string, string)>(Error.NotFound("录音文件"));

        var s = await store.OpenReadAsync(r.UserId, rec.StoragePath, ct);
        if (s is null)
            return Result.Failure<(Stream, string, string)>(Error.NotFound("录音文件"));

        var ext = Path.GetExtension(rec.StoragePath).TrimStart('.');
        return Result.Success((s, rec.ContentType, $"recording-{rec.Id:N}.{ext}"));
    }
}

/// <summary>
/// 删除一条录音(**硬删数据库行 + 硬删磁盘音频文件**)。
/// </summary>
/// <remarks>
/// ★ 第五十二轮(2026-09-17,Forrest 明确要求):
///   "这个录音的数据库和本地录音都要一起被硬删除"。
///
///   与第三十九轮相比,语义**反转**:
///     · 第三十九轮:软删元数据行 + 保留磁盘文件(防误删不可恢复)。
///     · 现在      :彻底删除 —— 库里行消失 + 磁盘文件消失,不留痕。
///
///   为什么反转是安全的(第三十九轮担心的"可恢复"已不再需要):
///     · 录音是**练习素材**,不是业务凭证,用户点删除就是真的不要了;
///     · 保留孤儿文件反而让用户困惑(界面没了、磁盘还在占);
///     · 这个项目是单用户自用,没有"误删要审计追回"的合规需求。
///
///   执行顺序(重要):**先删文件,后删行**。
///     若反过来(先删行):行没了 → StoragePath 也没了 → 文件永远成孤儿,
///     再没有任何线索能找到它。所以必须趁行还在时把路径取出来删文件,
///     成功后再删行。
///
///   ⚠️ 文件删除失败怎么办:仍继续删行(用户意图是"删除"必须成立),
///     但记 Warning 日志 —— 此时会留下一个孤儿文件,需要人工/后台清理。
///     这是"宁可留孤儿文件,也不能让用户删不掉"的取舍。
/// </remarks>
public sealed record DeleteRecordingCommand(Guid UserId, Guid RecordingId, uint? Version = null) : MediatR.IRequest<Result>; // 票111段3:If-Match 钉版

public sealed class DeleteRecordingCommandHandler(AssessmentDbContext db, IAudioStore store,
    ILogger<DeleteRecordingCommandHandler> logger)
    : MediatR.IRequestHandler<DeleteRecordingCommand, Result>
{
    public async Task<Result> Handle(DeleteRecordingCommand r, CancellationToken ct)
    {
        var rec = await db.Recordings
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId, ct);
        if (rec is null) return Result.Failure(Error.NotFound("录音"));

        // ★ 票111 A11:钉版必须在删文件之前比对 —— 本 handler 先删文件后删行,
        //   若等 SaveChanges 才撞 xmin,文件已删而行还在,"409 且数据完好"不成立。
        //   故先比对被跟踪行的当前 xmin,不符 → 409 先行拒绝(文件/行都不动);
        //   再 Pin 让删行 UPDATE 自带 xmin 谓词兜住比对后的竞态。
        if (r.Version is { } want)
        {
            var current = db.Entry(rec).Property("xmin").CurrentValue;
            if (current is uint cur && cur != want)
                return Result.Failure(Error.Conflict("concurrency_conflict",
                    "录音已被其他页签修改,本次删除未生效,请刷新后重试"));
            ConcurrencyPin.Pin(db, rec, r.Version);
        }

        // ① 先删磁盘文件 —— 必须趁行还在(行一删,StoragePath 这条线索就断了)
        var path = rec.StoragePath;
        var fileGone = true;
        if (!string.IsNullOrWhiteSpace(path))
        {
            fileGone = await store.DeleteAsync(r.UserId, path, ct);
            if (!fileGone)
                logger.LogWarning("录音 {RecordingId} 的音频文件删除失败,留下孤儿文件 {Path};数据库行仍将删除",
                    rec.Id, path);
        }

        // ①b 票 259(S3-S2)：人声轨同删（混音先删、人声后删，任一删失败只记
        //     Warning、行仍删 —— 与混音既有口径一致，不立新口径）。
        var voicePath = rec.VoiceStoragePath;
        if (!string.IsNullOrWhiteSpace(voicePath))
        {
            var voiceGone = await store.DeleteAsync(r.UserId, voicePath, ct);
            if (!voiceGone)
                logger.LogWarning("录音 {RecordingId} 的人声轨删除失败,留下孤儿文件 {Path};数据库行仍将删除",
                    rec.Id, voicePath);
        }

        // ② 再硬删数据库行(不走 MarkDeleted,直接 Remove)
        db.Recordings.Remove(rec);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("录音 {RecordingId} 已硬删除(文件={FileStatus})",
            r.RecordingId, fileGone ? "已删" : "删除失败/孤儿");

        return Result.Success();
    }
}

/// <summary>
/// 票 259(S3-S2)：上传/覆盖一条录音的人声轨（跟读跨设备：人声跟人走）。
/// </summary>
/// <remarks>
/// 安全顺序（绑定，P18 copy-then-verify-then-delete）：
///   ① 新文件落盘（`{recordingId}-voice.{ext}`，与混音轨文件名区分）；
///   ② 回读校验新文件 size &gt; 0（校验失败 → 直接失败，旧文件与行一字不动）；
///   ③ 行挂接新人声轨并落库；
///   ④ 删旧文件（旧路径与新路径相同 = 同月同扩展名原地覆盖 → 不删，
///      否则会把刚写的新文件删掉）。删旧失败只记 Warning，行已是新轨，
///      旧文件成孤儿（与混音删文件的既有口径一致）。
/// 并发口径（Q5）：最后写入胜出 —— 整 blob 替换，无部分写入、无钉版。
///   重传不带 takeRate/laneShiftSec 时保留既有对齐参数（AttachVoiceTrack 语义）。
/// </remarks>
public sealed record UploadVoiceTrackCommand(Guid UserId, Guid RecordingId, string? FileName,
    string ContentType, Stream Audio, double? TakeRate = null, double? LaneShiftSec = null)
    : MediatR.IRequest<Result<VoiceTrackDto>>;

public sealed class UploadVoiceTrackCommandHandler(AssessmentDbContext db, IAudioStore store,
    ILogger<UploadVoiceTrackCommandHandler> logger)
    : MediatR.IRequestHandler<UploadVoiceTrackCommand, Result<VoiceTrackDto>>
{
    // 与混音上传同一上限：不为新轨另立口径（计划 §3.1）。
    private const long MaxBytes = 25 * 1024 * 1024;

    public async Task<Result<VoiceTrackDto>> Handle(UploadVoiceTrackCommand r, CancellationToken ct)
    {
        if (!store.IsConfigured)
            return Result.Failure<VoiceTrackDto>(Error.Unexpected(
                "音频存储未配置(Storage:RootDirectory)。"));

        // 行必须属于我 —— 非本人行按「不存在」处理（与混音读/删同一口径：404，绝不 403）。
        var rec = await db.Recordings
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId && !x.IsDeleted, ct);
        if (rec is null) return Result.Failure<VoiceTrackDto>(Error.NotFound("录音"));

        var effectiveType = (r.ContentType ?? string.Empty).Trim();
        if (!effectiveType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<VoiceTrackDto>(Error.Validation("Recording.NotAudio",
                "人声轨必须是音频文件。"));

        var buffered = new MemoryStream();
        await r.Audio.CopyToAsync(buffered, ct);
        if (buffered.Length == 0)
            return Result.Failure<VoiceTrackDto>(Error.Validation("Recording.Empty", "录音内容为空。"));
        if (buffered.Length > MaxBytes)
            return Result.Failure<VoiceTrackDto>(Error.Validation("Recording.TooLarge",
                $"录音 {buffered.Length / 1024.0 / 1024.0:F1}MB 超过 {MaxBytes / 1024 / 1024}MB 上限。"));

        // ① 新文件落盘（与混音轨文件名区分，见 IAudioStore.SaveVoiceTrackAsync 注）。
        buffered.Position = 0;
        var ext = SaveRecordingCommandHandler.GuessExtension(effectiveType, r.FileName);
        var newPath = await store.SaveVoiceTrackAsync(r.UserId, rec.Id, ext, buffered, ct);

        // ② 回读校验：新文件必须真实存在且 size > 0，否则旧轨与行一字不动。
        var persistedOk = false;
        var verify = await store.OpenReadAsync(r.UserId, newPath, ct);
        if (verify is not null)
        {
            await using (verify)
            {
                if (verify.CanSeek) persistedOk = verify.Length > 0;
                else
                {
                    var drain = new MemoryStream();
                    await verify.CopyToAsync(drain, ct);
                    persistedOk = drain.Length > 0;
                }
            }
        }
        if (!persistedOk)
        {
            logger.LogWarning("人声轨 {RecordingId} 写入后回读校验失败（新路径 {Path}），旧轨未动",
                rec.Id, newPath);
            return Result.Failure<VoiceTrackDto>(Error.Unexpected("人声轨写入后校验失败，请重试。"));
        }

        // ③ 行挂接新人声轨并落库（对齐参数缺省则保留既有值）。
        var oldPath = rec.VoiceStoragePath;
        rec.AttachVoiceTrack(newPath, effectiveType, buffered.Length, r.TakeRate, r.LaneShiftSec);
        await db.SaveChangesAsync(ct);

        // ④ 删旧文件（路径相同 = 原地覆盖，不删；删失败只记 Warning，行已是新轨）。
        if (!string.IsNullOrWhiteSpace(oldPath)
            && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            var oldGone = await store.DeleteAsync(r.UserId, oldPath, ct);
            if (!oldGone)
                logger.LogWarning("录音 {RecordingId} 的旧人声轨删除失败，留下孤儿文件 {Path}；行已指向新轨",
                    rec.Id, oldPath);
        }

        return Result.Success(new VoiceTrackDto(rec.Id, effectiveType, buffered.Length,
            rec.TakeRate, rec.LaneShiftSec));
    }
}

/// <summary>票 259(S3-S2)：取一条录音的人声轨音频流（另一设备打开时取回人声）。</summary>
/// <remarks>
/// 无人声轨（旧行 / 仅混音行）→ NotFound（404），调用方回落 S1 降级态，
/// 绝不 500。行归属口径与混音读一致：非本人行 = 不存在（404，不 403）。
/// </remarks>
public sealed record GetVoiceTrackQuery(Guid UserId, Guid RecordingId)
    : MediatR.IRequest<Result<(Stream Audio, string ContentType, string FileName)>>;

public sealed class GetVoiceTrackQueryHandler(AssessmentDbContext db, IAudioStore store)
    : MediatR.IRequestHandler<GetVoiceTrackQuery, Result<(Stream, string, string)>>
{
    public async Task<Result<(Stream, string, string)>> Handle(GetVoiceTrackQuery r, CancellationToken ct)
    {
        var rec = await db.Recordings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId && !x.IsDeleted, ct);
        if (rec is null) return Result.Failure<(Stream, string, string)>(Error.NotFound("录音"));

        if (string.IsNullOrWhiteSpace(rec.VoiceStoragePath))
            return Result.Failure<(Stream, string, string)>(Error.NotFound("人声轨"));

        var s = await store.OpenReadAsync(r.UserId, rec.VoiceStoragePath, ct);
        if (s is null)
            return Result.Failure<(Stream, string, string)>(Error.NotFound("人声轨文件"));

        var ext = Path.GetExtension(rec.VoiceStoragePath).TrimStart('.');
        var contentType = string.IsNullOrWhiteSpace(rec.VoiceContentType)
            ? "audio/webm" : rec.VoiceContentType;
        return Result.Success((s, contentType, $"recording-{rec.Id:N}-voice.{ext}"));
    }
}

/// <summary>
/// 写入一份发音评分结果(供 pronunciation/assess 端点落库复用)。
/// </summary>
/// <remarks>
/// ⚠️ 这是"评分缓存"的写入口(Forrest 需求第 4 条):
///    同一段音频 + 同一段参考文本只评一次,之后直接读库。
///    所以入库前先判断缓存是否仍然有效(参考文本没变)。
/// </remarks>
public sealed record SaveRecordingScoreCommand(Guid UserId, Guid RecordingId,
    double? PronScore, double? AccuracyScore, double? FluencyScore,
    double? CompletenessScore, double? ProsodyScore,
    string RecognizedText, string? WordsJson, string AzureRegion, string ReferenceText,
    // ★ 第三十四轮:评分计费口径(服务端精确算出的音频秒数)。
    double? BilledSeconds = null, int? BilledBytes = null,
    // ★ 票108 A10:既有评分行的读时 xmin(首次评分/旧调用方为 null = 不钉版兼容)。
    uint? Version = null)
    : MediatR.IRequest<Result<RecordingScoreDto>>;

public sealed class SaveRecordingScoreCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<SaveRecordingScoreCommand, Result<RecordingScoreDto>>
{
    public async Task<Result<RecordingScoreDto>> Handle(SaveRecordingScoreCommand r, CancellationToken ct)
    {
        var rec = await db.Recordings
            .Include(x => x.Score)
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId, ct);
        if (rec is null) return Result.Failure<RecordingScoreDto>(Error.NotFound("录音"));

        // ★ 票260 S3：评分隔离第二道防线（结构保证，不是约定）。
        //   行 kind 派生（前后端同一公式）：IsShadow=true 且 StoragePath 非空 → 跟读-混音行；
        //   IsShadow=true 且 StoragePath 为空 且 VoiceStoragePath 非空 → 跟读-only 行；
        //   IsShadow=true 且两路径皆空 → 未完成行；其余 → 普通行。
        //   三种跟读态一律拒绝入评分（400），普通行才放行；第一道防线是
        //   正常列表永不返回跟读行（P24 + S1 分类规则），本处防 crafted 请求。
        if (rec.IsShadow == true)
            return Result.Failure<RecordingScoreDto>(Error.Validation("Recording.ShadowNotScored",
                "跟读行不参与评分。"));

        var score = new PracticeRecordingScore(r.RecordingId, r.PronScore, r.AccuracyScore,
            r.FluencyScore, r.CompletenessScore, r.ProsodyScore,
            r.RecognizedText ?? string.Empty, r.WordsJson, r.AzureRegion, r.ReferenceText ?? string.Empty);
        // ★ 第三十四轮:把音频时长一并落库 —— 否则读库路径显示不出"花了多少"。
        score.SetBilling(r.BilledSeconds, r.BilledBytes);

        if (rec.Score is null)
        {
            rec.AttachScore(score);
            // 新增的 1:1 子实体必须显式标 Added(见 DbContext.MarkNewChildrenAsAdded)
            db.RecordingScores.Add(score);
        }
        else
        {
            // 重评分:直接覆盖既有行(主键就是 RecordingId)
            var existing = rec.Score;
            db.Entry(existing).CurrentValues.SetValues(score);
            rec.ReplaceScore(existing);
            score = existing;
            // ★ 票108 A10:回传了读时版本就钉住评分行 —— 期间已被他人重评分则
            //   UPDATE 带旧 xmin 谓词命中 0 行 → 409,不再静默覆盖(票101 子行模式)。
            ConcurrencyPin.Pin(db, existing, r.Version);
        }

        await db.SaveChangesAsync(ct);

        // 保存后回读新 xmin 供调用方续写(影子属性从投影读,口径确定)。
        var newVersion = await db.RecordingScores.AsNoTracking()
            .Where(x => x.RecordingId == r.RecordingId)
            .Select(x => EF.Property<uint>(x, "xmin"))
            .FirstAsync(ct);

        return Result.Success(new RecordingScoreDto(
            score.PronScore, score.AccuracyScore, score.FluencyScore,
            score.CompletenessScore, score.ProsodyScore,
            score.RecognizedText,
            ListRecordingsQueryHandler.DeserializeWords(score.WordsJson),
            score.ReferenceText, score.AssessedAt,
            score.BilledSeconds, score.BilledBytes, newVersion));
    }
}

/// <summary>读取已缓存的评分(不调 Azure,直接读库)。</summary>
public sealed record GetRecordingScoreQuery(Guid UserId, Guid RecordingId)
    : MediatR.IRequest<Result<RecordingScoreDto>>;

public sealed class GetRecordingScoreQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<GetRecordingScoreQuery, Result<RecordingScoreDto>>
{
    public async Task<Result<RecordingScoreDto>> Handle(GetRecordingScoreQuery r, CancellationToken ct)
    {
        var score = await db.RecordingScores.AsNoTracking()
            .Join(db.Recordings.Where(x => x.UserId == r.UserId && !x.IsDeleted),
                s => s.RecordingId, rec => rec.Id, (s, _) => s)
            .FirstOrDefaultAsync(x => x.RecordingId == r.RecordingId, ct);

        if (score is null) return Result.Failure<RecordingScoreDto>(Error.NotFound("评分"));

        // ★ 票108 A10:回传评分行读时 xmin,供重评分回传钉版。
        var version = await db.RecordingScores.AsNoTracking()
            .Where(x => x.RecordingId == r.RecordingId)
            .Select(x => EF.Property<uint>(x, "xmin"))
            .FirstAsync(ct);

        return Result.Success(new RecordingScoreDto(
            score.PronScore, score.AccuracyScore, score.FluencyScore,
            score.CompletenessScore, score.ProsodyScore,
            score.RecognizedText,
            ListRecordingsQueryHandler.DeserializeWords(score.WordsJson),
            score.ReferenceText, score.AssessedAt,
            score.BilledSeconds, score.BilledBytes, version));
    }
}

