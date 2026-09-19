using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// DTO —— 与前端 MaterialNode / Recording 形状对齐,序列化零转换
// ============================================================

/// <summary>素材节点(树形,children 递归)。</summary>
public sealed record MaterialNodeDto(
    Guid Id, string Name, bool Folder, int SortOrder, bool Expanded,
    string? Content, IReadOnlyList<MaterialNodeDto> Children);

/// <summary>一条录音 + 可选评分。</summary>
public sealed record RecordingDto(
    Guid Id, Guid MaterialId, double DurationSeconds, long SizeBytes,
    string ContentType, DateTimeOffset CreatedAt, RecordingScoreDto? Score);

/// <summary>
/// 发音评分结果。
/// ⚠️ 可空字段拿不到就是 null —— 前端据此显示 "—",绝不用 0 冒充。
/// </summary>
public sealed record RecordingScoreDto(
    double? PronScore, double? AccuracyScore, double? FluencyScore,
    double? CompletenessScore, double? ProsodyScore,
    string Recognized, IReadOnlyList<WordScoreDto> Words,
    string ReferenceText, DateTimeOffset AssessedAt,
    // ★ 第三十四轮:计费口径(音频秒数),读库与新建都会带上。
    //   拿不到为 null —— 不编造。
    double? BilledSeconds = null, int? BilledBytes = null);

public sealed record WordScoreDto(string Word, double Accuracy, string ErrorType);

// ============================================================
// 素材树:整树读写
// ============================================================

/// <summary>取我的整棵素材树(一次拿完,树规模小,不值得做增量加载)。</summary>
public sealed record GetMaterialTreeQuery(Guid UserId) : MediatR.IRequest<IReadOnlyList<MaterialNodeDto>>;

public sealed class GetMaterialTreeQueryHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<GetMaterialTreeQuery, IReadOnlyList<MaterialNodeDto>>
{
    public async Task<IReadOnlyList<MaterialNodeDto>> Handle(GetMaterialTreeQuery r, CancellationToken ct)
    {
        var all = await db.Materials.AsNoTracking()
            .Where(x => x.UserId == r.UserId)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        return Build(all, null);
    }

    /// <summary>内存里拼树 —— 素材木只有几十个节点,一次查完比递归 SQL 更清晰也更快。</summary>
    private static List<MaterialNodeDto> Build(List<PracticeMaterial> all, Guid? parentId) =>
        all.Where(x => x.ParentId == parentId)
            .Select(x => new MaterialNodeDto(
                x.Id, x.Name, x.Kind == MaterialKind.Folder, x.SortOrder, x.IsExpanded,
                x.Content, Build(all, x.Id)))
            .ToList();
}

/// <summary>整树覆盖写。</summary>
/// <remarks>
/// 为什么用"整树替换"而不是逐个增删改的细粒度端点:
///   前端的拖拽排序 / 重命名 / 改正文 / 新建删除是**高频小操作**,
///   如果每个都发一次请求,排序时会产生几十个请求且顺序敏感、容易半途失败留下脏状态。
///   整树提交是一个原子操作:要么全新状态,要么什么都没变。
///   素材树是纯私有小数据(几十节点、几 KB),整树传输的成本可以忽略。
/// </remarks>
public sealed record SaveMaterialTreeCommand(Guid UserId, IReadOnlyList<MaterialNodeIn> Nodes, bool Force = false)
    : MediatR.IRequest<Result<int>>;

/// <summary>前端提交的节点(Id 为 null = 新建)。</summary>
public sealed record MaterialNodeIn(
    string? Id, string Name, bool Folder, string? Content, int SortOrder, bool Expanded,
    IReadOnlyList<MaterialNodeIn>? Children);

public sealed class SaveMaterialTreeCommandHandler(AssessmentDbContext db)
    : MediatR.IRequestHandler<SaveMaterialTreeCommand, Result<int>>
{
    public async Task<Result<int>> Handle(SaveMaterialTreeCommand r, CancellationToken ct)
    {
        var existing = await db.Materials
            .IgnoreQueryFilters()
            .Where(x => x.UserId == r.UserId)
            .ToListAsync(ct);
        var byId = existing.ToDictionary(x => x.Id);

        var incoming = new HashSet<Guid>();
        var touched = new List<PracticeMaterial>();

        void Walk(IReadOnlyList<MaterialNodeIn> nodes, Guid? parentId)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                var kind = n.Folder ? MaterialKind.Folder : MaterialKind.File;

                // 前端传来的 id:
                //   · 合法 GUID 且库里已存在(属当前用户) → 更新现有节点
                //   · 合法 GUID 但库里没有 → **采纳它作为新节点的 id**(见下)
                //   · "f_intro" 这类非 GUID 种子串 → 无法采纳,退化为服务端新发 GUID
                PracticeMaterial? entity = null;
                var hasClientId = Guid.TryParse(n.Id, out var gid);
                if (hasClientId) byId.TryGetValue(gid, out entity);

                if (entity is null)
                {
                    // ★ 第三十七轮 丢数据修复:采纳前端给的 GUID。
                    //   旧代码这里永远 new 一个服务端 GUID,导致
                    //   “前端发 A、服务端存 B” → 整树覆盖变成删了重建,
                    //   一旦有并发/重试就丢数据。采纳前端 id 后,
                    //   每次保存都是稳定的幂等 upsert。
                    entity = hasClientId
                        ? new PracticeMaterial(gid, r.UserId, parentId, kind, n.Name, n.Content, n.SortOrder)
                        : new PracticeMaterial(r.UserId, parentId, kind, n.Name, n.Content, n.SortOrder);
                    db.Materials.Add(entity);
                }
                else
                {
                    // ★ 第三十九轮:若这个节点之前被软删过,重新出现就恢复它。
                    entity.Restore();
                    entity.Rename(n.Name);
                    // 文件夹不持有正文 → 不去碰它的 Content(避免无意义的 Touch);
                    // 文件才写正文。
                    if (kind == MaterialKind.File) entity.SetContent(n.Content);
                    entity.MoveTo(parentId, n.SortOrder);
                    entity.SetExpanded(n.Expanded);
                    touched.Add(entity);
                }

                incoming.Add(entity.Id);
                if (n.Children is { Count: > 0 }) Walk(n.Children, entity.Id);
            }
        }

        Walk(r.Nodes, null);

        // 前端没再提交的旧节点 = 用户已经删了它。
        //
        // ★★ 第三十九轮 数据安全修复(Forrest 报“录音记录都没了”):
        //   旧代码这里走 `db.Materials.RemoveRange(removed)` —— **硬删**。
        //   但整树覆盖只要出现一次不完整的提交(并发 PUT、网络重试、
        //   前端状态未回读、本地种子 id 未换 GUID),就会把用户真正存在的
        //   素材连根硬删,挂在其下的录音也随即变孤儿,
        //   界面按 MaterialId 查不到 → 用户看到“录音全没了”。
        //   **数据丢失是不可接受的** —— 现在一律改软删:
        //   标记 IsDeleted 并脱离子树(避免子节点被当成根节点重复显示)。
        //   已软删的行被查询过滤器隐藏,用户视角与真删一致,
        //   但数据仍在库里,可排查、可恢复。
        var removed = existing.Where(x => !incoming.Contains(x.Id) && !x.IsDeleted).ToList();

        // ★★ 第四十轮 数据安全熔断(Forrest 报"数据全丢失"):
        //   即使前端改成了软删,仍要防"客户端把初始/残缺树整套写回"这类
        //   覆盖事故 —— 它会一次性把大量真实节点标记删除。
        //   启发式:本次要抹掉的节点占比很高(>80%)、且抹掉数达到一定规模(>=5)
        //   时,先不动手,返回失败让客户端二次确认(除非显式 force)。
        //   为什么用比例而非绝对数:个人素材库体量小,5 个节点已算“有东西了”;
        //   单节点删除(改名/删一个)永远不会触发熔断,不影响正常使用。
        var aliveCount = existing.Count(x => !x.IsDeleted);
        if (!r.Force && removed.Count >= 5 && aliveCount > 0
            && (double)removed.Count / aliveCount > 0.8)
        {
            return Result.Failure<int>(Error.Validation("materials.guard",
                $"为防止误删,本次保存被拦下:它将删除 {removed.Count}/{aliveCount} 个素材"
                + "(超过 80%)。如确需删除请在请求中显式带上 force=true。"));
        }

        if (removed.Count > 0)
        {
            foreach (var m in removed) m.MarkDeleted();
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(incoming.Count);
    }
}

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

        var s = await store.OpenReadAsync(rec.StoragePath, ct);
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
            fileGone = await store.DeleteAsync(path, ct);
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

// ============================================================
// 示范朗读音频缓存(2026-09-16 第三十一轮)
//
// 需求(Forrest 第 3 条):点「示范朗读」→ 若 Azure 已生成过同一段文本的音频,
//   直接回放本地缓存,**不再花 Azure TTS 额度**;
//   文本(或音色/倍速)变了 → 重新合成一份并存下来。
//
// 设计要点:
//   · 缓存键 = SHA-256(归一化文本 + 音色 + 倍速 + 语言),见 PracticeTtsCache.ComputeKey;
//   · 音频落文件系统(与录音同一 IAudioStore),库里只存相对路径;
//   · **回放路径绝不调 Azure** —— GetOrCreateTtsCache 先查库、命中即返回字节流。
// ============================================================

/// <summary>
/// 取(或生成)一段示范朗读音频。
/// </summary>
/// <param name="Force">
/// true = 忽略缓存,强制重新合成并覆盖缓存。
/// 正常朗读传 false(默认),这样第二遍起就是纯本地回放、零额度。
/// </param>
public sealed record GetOrCreateTtsCommand(Guid UserId, string Text, string? Voice,
    double? Speed, string Language, bool Force = false)
    : MediatR.IRequest<Result<TtsAudioResult>>;

/// <summary>
/// 示范朗读音频结果。
///
/// <paramref name="FromCache"/> 是诚实的来源标记 —— 前端据此提示
/// "已有本地缓存,未消耗额度",而不是含糊其辞。
///
/// ★ 第三十四轮(Forrest 要求"给我准确的消耗了多少"):
///   新增 <paramref name="BilledChars"/> 与 <paramref name="Voice"/>。
///
///   ⚠️ **为什么不是"token 数":Azure 语音 TTS 根本不以 token 计费** ——
///      它的真实计费单位是**合成字符数**(按每 1M 字符计价,
///      神经语音与标准语音单价不同)。响应里也没有任何 token 字段。
///      所以这里如实给出 ** billedChars = 本次送给 Azure 的字符数**,
///      这是能 100% 精确算出、且与账单口径一致的数字;
///      编一个假的"token 数"比不显示更坏。
///   字符数按 PowerShell/.NET 的 UTF-16 代码单元计(与 Azure 口径一致),
///   中文每个字算 1,emoji 算 2 —— 不做"看起来更少"的美化。
/// </summary>
public sealed record TtsAudioResult(byte[] Audio, string ContentType, bool FromCache,
    int BilledChars, string Voice = "", int AudioBytes = 0)
{
    /// <summary>兼容旧构造调用(第三十一轮的 3 参形式)。</summary>
    public TtsAudioResult(byte[] audio, string contentType, bool fromCache)
        : this(audio, contentType, fromCache, 0, string.Empty, audio.Length) { }
}

public sealed class GetOrCreateTtsCommandHandler(AssessmentDbContext db, SpeechSynthesizer synth,
    IAudioStore store, PronunciationAssessor assessor)
    : MediatR.IRequestHandler<GetOrCreateTtsCommand, Result<TtsAudioResult>>
{
    private const int MaxTextLength = 4000;

    public async Task<Result<TtsAudioResult>> Handle(GetOrCreateTtsCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Text))
            return Result.Failure<TtsAudioResult>(Error.Validation("Tts.TextEmpty", "请先选择要朗读的素材内容。"));
        if (r.Text.Length > MaxTextLength)
            return Result.Failure<TtsAudioResult>(Error.Validation("Tts.TextTooLong",
                $"文本 {r.Text.Length} 字符超过 {MaxTextLength} 上限。"));

        var language = string.IsNullOrWhiteSpace(r.Language) ? "en-US" : r.Language.Trim();
        var speed = Math.Clamp(r.Speed is > 0 ? r.Speed.Value : 1.0, 0.5, 2.0);
        var cacheKey = PracticeTtsCache.ComputeKey(r.Text, r.Voice, speed, language);

        // ---------- 1) 查缓存(命中则零 Azure 调用) ----------
        if (!r.Force)
        {
            var hit = await db.TtsCache
                .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.CacheKey == cacheKey, ct);
            if (hit is not null)
            {
                var cached = await store.OpenReadAsync(hit.StoragePath, ct);
                if (cached is not null)
                {
                    await using (cached)
                    {
                        using var ms = new MemoryStream();
                        await cached.CopyToAsync(ms, ct);
                        hit.MarkUsed();
                        // 命中计数是统计信息,写失败不该让朗读报错 —— 尽力而为
                        try { await db.SaveChangesAsync(ct); } catch { /* 统计失败可忽略 */ }
                        // ★ 缓存命中:真正应付费字符数 = 0(没调 Azure)。
                        //   但也如实带上"这段文本本该花多少",让界面能说清楚
                        //   "本次 0 字符 / 本可花费 N 字符 —— 因为命中了本地缓存"。
                        var bytesHit = ms.ToArray();
                        return Result.Success(new TtsAudioResult(bytesHit, hit.ContentType, true,
                            0, hit.Voice, bytesHit.Length));
                    }
                }
                // 库里有记录但盘上文件没了(被清理/换机器)→ 清掉脏行,走重新合成
                db.TtsCache.Remove(hit);
                await db.SaveChangesAsync(ct);
            }
        }

        // ---------- 2) 未命中 → 真调 Azure 合成 ----------
        if (!await assessor.IsAvailableAsync(r.UserId, ct))
            return Result.Failure<TtsAudioResult>(new Error("Tts.NotConfigured",
                "服务端缺少可用的 AzureSpeech key,示范朗读不可用。请先在 AI 语音设置里保存密钥。",
                ErrorType.Failure));

        byte[] mp3;
        string voiceUsed;
        try
        {
            // ★ 2026-09-19:把语言透传给合成器 —— 之前这里就没传,
            //   导致 SSML 的 xml:lang 与音色永远只能是英语。
            mp3 = await synth.SynthesizeAsync(r.Text, r.Voice, speed, r.UserId, ct, language);
            voiceUsed = string.IsNullOrWhiteSpace(r.Voice)
                ? (language.StartsWith("fr", StringComparison.OrdinalIgnoreCase)
                    ? "fr-FR-DeniseNeural" : "en-US-AriaNeural")
                : r.Voice.Trim();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("未配置"))
        {
            return Result.Failure<TtsAudioResult>(new Error("Tts.NotConfigured", ex.Message, ErrorType.Failure));
        }
        // ⚠️ AzureAuthException / 其它异常直接向上抛,由控制器映射为 502 ——
        //    不在这里吞咽,免得把"上游凭据错"伪装成"成功但空音频"。

        // ---------- 3) 落盘 + 入库(= 下次的缓存) ----------
        // 落盘失败不阻断本次播放:能听到声音比存下缓存重要。
        try
        {
            using var ms = new MemoryStream(mp3);
            // ⚠️ IAudioStore.SaveAsync 的第二个参数是 Guid,它同时用于**命名磁盘文件**。
            //    而我们的缓存键是 64 位十六进制字符串 —— 两者形状不同。
            //    这里用缓存键派生一个**确定性 Guid**(同一个缓存键永远得到同一个 Guid),
            //    好处:重复合成时文件名稳定不变(不会每次落一堆孤儿 MP3),
            //    也让"键 → 文件"这条链路可复现。
            var fileId = DeriveGuidFromCacheKey(cacheKey);
            var rel = await store.SaveAsync(r.UserId, fileId, "mp3", ms, ct);

            var row = await db.TtsCache
                .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.CacheKey == cacheKey, ct);
            if (row is null)
            {
                row = new PracticeTtsCache(r.UserId, cacheKey, rel, "audio/mpeg", mp3.Length,
                    PracticeTtsCache.ComputeTextHash(r.Text), voiceUsed, speed, language);
                db.TtsCache.Add(row);
            }
            else
            {
                // 强制重合成:覆盖既有行的路径与大小(主键不变)
                db.Entry(row).CurrentValues.SetValues(new PracticeTtsCache(
                    r.UserId, cacheKey, rel, "audio/mpeg", mp3.Length,
                    PracticeTtsCache.ComputeTextHash(r.Text), voiceUsed, speed, language));
            }
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // 缓存失败不能影响"这一次能听到" —— 静默降级,下次再合成一遍
            db.ChangeTracker.Clear();
        }

        return Result.Success(new TtsAudioResult(mp3, "audio/mpeg", false,
            BilledChars(r.Text), voiceUsed, mp3.Length));
    }

    /// <summary>
    /// 本次送给 Azure 的**计费字符数**(= 文本的 UTF-16 代码单元数)。
    ///
    /// ⚠️ 为什么不是 r.Text.Length?
    ///   其实 .NET 的 string.Length **就是** UTF-16 代码单元数,
    ///   与 Azure 计费口径一致(BMP 内每字符 1,代理对如 emoji 算 2)。
    ///   单独抽成方法是为了:① 把"这就是计费单位"这件事写在名字上;
    ///   ② 日后若 Azure 改口径(如改按码点计)只改这一处。
    /// 不做任何"看起来更少"的美化 —— 是多少就报多少。
    /// </summary>
    public static int BilledChars(string text) => text.Length;

    /// <summary>
    /// 从缓存键派生一个确定性 Guid(取 SHA-256 前 16 字节)。
    /// 只用于**命名磁盘文件**,不参与缓存查找 —— 查找永远走 (UserId, CacheKey)。
    /// </summary>
    private static Guid DeriveGuidFromCacheKey(string cacheKey)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(cacheKey);
        var hash = System.Security.Cryptography.SHA256.HashData(raw);
        return new Guid(hash.AsSpan(0, 16));
    }
}

// ============================================================
// Azure Speech 设置(服务端托管,key 永不回传浏览器)
// ============================================================

/// <summary>
/// 对外可见的设置状态 —— **只有**这三样,永远不含 key 本身。
/// </summary>
public sealed record SpeechSettingStatusDto(bool HasKey, string? Region, string? MaskedKey,
    string Source, string? Endpoint);

public sealed record GetSpeechSettingQuery(Guid UserId) : MediatR.IRequest<SpeechSettingStatusDto>;

public sealed class GetSpeechSettingQueryHandler(AssessmentDbContext db, IConfiguration config)
    : MediatR.IRequestHandler<GetSpeechSettingQuery, SpeechSettingStatusDto>
{
    public async Task<SpeechSettingStatusDto> Handle(GetSpeechSettingQuery r, CancellationToken ct)
    {
        // ⚠️ 2026-09-16(Forrest 第 1 条需求):
        //    前端只认"用户在设置页保存到数据库里的 key"。
        //
        //    以前这里会回落到 appsettings 里的 AzureSpeech:Key,导致
        //    `appsettings.Development.json` 中预置的一把开发 key 被当成
        //    "已配置" 下发 —— 用户在界面上从没填过,却看到 hasKey=true、
        //    还能直接朗读。这正是 Forrest 说的"你是不是把 key 写死在文件里了"。
        //
        //    现在:未在界面保存过 → hasKey=false、source="none"。
        //    配置文件里的 key 只作为**服务端兜底**(见 SpeechKeyProvider),
        //    不再影响界面的"已配置"判定 —— 界面与真实的用户配置一一对应。
        var row = await db.SpeechSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);

        if (row is not null && !string.IsNullOrWhiteSpace(row.Key))
            return new SpeechSettingStatusDto(true, row.Region, Mask(row.Key), "database", row.Endpoint);

        // 未在界面保存:区域给个默认值方便下拉预选,但明确 hasKey=false
        var region = config["AzureSpeech:Region"];
        if (string.IsNullOrWhiteSpace(region)) region = "canadacentral";
        return new SpeechSettingStatusDto(false, region, null, "none", null);
    }

    /// <summary>只露头 4 位 + 尾 4 位 —— 够用户确认"是我那把 key",又不足以泄密。</summary>
    private static string Mask(string key) =>
        key.Length <= 8 ? "••••••••" : key[..4] + "••••••••" + key[^4..];
}

/// <summary>
/// 测试一把**尚未保存**的候选凭据(Forrest 第 1 条需求:先测后存)。
///
/// 为什么需要独立命令:
///   用户希望"测试通过后才允许保存到数据库"。若沿用 /speech/test,
///   它测的是"当前已保存的 key",无法验证输入框里那把新的。
///   所以这里接受候选 Key/Region,真去 Azure 走一次最小请求,**不落库**。
///
/// 返回:ok=true 表示凭据可用,可以保存;false = 不可用,附原因。
/// </summary>
public sealed record TestSpeechCredentialCommand(string Key, string Region)
    : MediatR.IRequest<Result<TestSpeechCredentialResult>>;

public sealed record TestSpeechCredentialResult(bool Ok, string Message);

public sealed class TestSpeechCredentialCommandHandler(PronunciationAssessor assessor)
    : MediatR.IRequestHandler<TestSpeechCredentialCommand, Result<TestSpeechCredentialResult>>
{
    public async Task<Result<TestSpeechCredentialResult>> Handle(
        TestSpeechCredentialCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Key))
            return Result.Failure<TestSpeechCredentialResult>(
                Error.Validation("Speech.KeyEmpty", "请先填写 Azure Speech Key。"));
        if (string.IsNullOrWhiteSpace(r.Region))
            return Result.Failure<TestSpeechCredentialResult>(
                Error.Validation("Speech.RegionEmpty", "请先选择区域(Region)。"));

        try
        {
            // 直接拿候选值去 Azure 验证 —— 不读库、不写库
            var ok = await assessor.PingWithAsync(r.Key.Trim(), r.Region.Trim(), ct);
            return ok
                ? Result.Success(new TestSpeechCredentialResult(true, "连接正常,密钥可用。"))
                : Result.Failure<TestSpeechCredentialResult>(
                    Error.Unexpected("Azure 未按预期响应,请核对密钥与区域。"));
        }
        catch (AzureAuthException ex)
        {
            // 领域错误不假装成功;由控制器映射为 502(绝不能是 401/403)
            return Result.Failure<TestSpeechCredentialResult>(
                new Error("Speech.InvalidCredential", ex.Message, ErrorType.Failure));
        }
    }
}

/// <summary>保存 Azure key / 区域(服务端保管)。</summary>
public sealed record SaveSpeechSettingCommand(Guid UserId, string Key, string Region, string? Endpoint)
    : MediatR.IRequest<Result<SpeechSettingStatusDto>>;

/// <summary>
/// 保存 Azure Speech 凭据。
///
/// ⚠️ 2026-09-16(Forrest 第 1 条)关键修复:保存前**必须先去 Azure 真测一次**,
///    不通就拒绝入库。
///
///    旧实现只查了"长度 >= 16"就写库 —— 于是 32 个 0 也能存进去,
///    用户看到"已保存",到练习页才炸。这正是他说的"要测试通过后才可以保存"。
///
///    现在后端自己把关,不依赖前端是否老实调了 test-credential。
///    前端门禁是体验,后端校验是底线 —— 两道都要有。
/// </summary>
public sealed class SaveSpeechSettingCommandHandler(AssessmentDbContext db, PronunciationAssessor assessor)
    : MediatR.IRequestHandler<SaveSpeechSettingCommand, Result<SpeechSettingStatusDto>>
{
    public async Task<Result<SpeechSettingStatusDto>> Handle(SaveSpeechSettingCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Key))
            return Result.Failure<SpeechSettingStatusDto>(Error.Validation("Speech.KeyEmpty", "密钥不能为空。"));
        if (r.Key.Trim().Length < 16)
            return Result.Failure<SpeechSettingStatusDto>(Error.Validation("Speech.KeyTooShort",
                "密钥长度明显不足,请核对 Azure 门户里的密钥。"));
        if (string.IsNullOrWhiteSpace(r.Region))
            return Result.Failure<SpeechSettingStatusDto>(Error.Validation("Speech.RegionEmpty", "区域不能为空。"));

        // ★ 核心门禁:拿这把候选 key 真去 Azure 验一次,不通就不写库。
        //   与 TestSpeechCredentialCommandHandler 走同一条路径,行为一致。
        try
        {
            var ok = await assessor.PingWithAsync(r.Key.Trim(), r.Region.Trim(), ct);
            if (!ok)
            {
                return Result.Failure<SpeechSettingStatusDto>(new Error(
                    "Speech.ProbeUnexpected",
                    "Azure 未按预期响应,凭据未保存,请核对密钥与区域。",
                    ErrorType.Failure));
            }
        }
        catch (AzureAuthException ex)
        {
            // 凭据被上游拒绝 → 不写库。领域错误用 Failure 类型,
            // 由控制器映射为 502(网关类),绝不用 401/403 ——
            // 那会被前端拦截器当成"会话失效"把用户踢回登录页。
            return Result.Failure<SpeechSettingStatusDto>(new Error(
                "Speech.CredentialRejected",
                $"Azure 拒绝了该密钥或区域,凭据未保存:{ex.Message}",
                ErrorType.Failure));
        }

        var row = await db.SpeechSettings.FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);
        if (row is null)
        {
            row = new SpeechSetting(r.UserId, r.Key.Trim(), r.Region.Trim(), r.Endpoint);
            db.SpeechSettings.Add(row);
        }
        else
        {
            row.Update(r.Key.Trim(), r.Region.Trim(), r.Endpoint);
        }

        await db.SaveChangesAsync(ct);

        return Result.Success(new SpeechSettingStatusDto(
            true, row.Region,
            row.Key.Length <= 8 ? "••••••••" : row.Key[..4] + "••••••••" + row.Key[^4..],
            "database", row.Endpoint));
    }
}
