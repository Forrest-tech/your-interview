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

        return rows.Select(ListRecordingsQueryHandler.ToDto).ToList();
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

        return rows.Select(ToDto).ToList();
    }

    internal static RecordingDto ToDto(PracticeRecording x) => new(
        x.Id, x.MaterialId, x.DurationSeconds, x.SizeBytes, x.ContentType, x.CreatedAt,
        x.Score is null ? null : new RecordingScoreDto(
            x.Score.PronScore, x.Score.AccuracyScore, x.Score.FluencyScore,
            x.Score.CompletenessScore, x.Score.ProsodyScore,
            x.Score.RecognizedText,
            DeserializeWords(x.Score.WordsJson),
            x.Score.ReferenceText, x.Score.AssessedAt));

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
    string ContentType, double DurationSeconds, string SourceLanguage, Stream Audio)
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
            buffered.Length, r.DurationSeconds, r.SourceLanguage);

        db.Recordings.Add(rec);
        await db.SaveChangesAsync(ct);

        return Result.Success(ListRecordingsQueryHandler.ToDto(rec));
    }

    /// <summary>
    /// 从 MIME 猜扩展名。
    /// 浏览器录的是 audio/webm;opus,但也要能吃下别处传来的 wav/mp3。
    /// </summary>
    private static string GuessExtension(string? contentType, string? fileName)
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
public sealed record DeleteRecordingCommand(Guid UserId, Guid RecordingId) : MediatR.IRequest<Result>;

public sealed class DeleteRecordingCommandHandler(AssessmentDbContext db, IAudioStore store,
    ILogger<DeleteRecordingCommandHandler> logger)
    : MediatR.IRequestHandler<DeleteRecordingCommand, Result>
{
    public async Task<Result> Handle(DeleteRecordingCommand r, CancellationToken ct)
    {
        var rec = await db.Recordings
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId, ct);
        if (rec is null) return Result.Failure(Error.NotFound("录音"));

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

        // ② 再硬删数据库行(不走 MarkDeleted,直接 Remove)
        db.Recordings.Remove(rec);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("录音 {RecordingId} 已硬删除(文件={FileStatus})",
            r.RecordingId, fileGone ? "已删" : "删除失败/孤儿");

        return Result.Success();
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
    double? BilledSeconds = null, int? BilledBytes = null)
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
        }

        await db.SaveChangesAsync(ct);

        return Result.Success(new RecordingScoreDto(
            score.PronScore, score.AccuracyScore, score.FluencyScore,
            score.CompletenessScore, score.ProsodyScore,
            score.RecognizedText,
            ListRecordingsQueryHandler.DeserializeWords(score.WordsJson),
            score.ReferenceText, score.AssessedAt,
            score.BilledSeconds, score.BilledBytes));
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

        return Result.Success(new RecordingScoreDto(
            score.PronScore, score.AccuracyScore, score.FluencyScore,
            score.CompletenessScore, score.ProsodyScore,
            score.RecognizedText,
            ListRecordingsQueryHandler.DeserializeWords(score.WordsJson),
            score.ReferenceText, score.AssessedAt,
            score.BilledSeconds, score.BilledBytes));
    }
}

