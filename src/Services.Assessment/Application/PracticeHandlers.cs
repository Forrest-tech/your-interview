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
    string ReferenceText, DateTimeOffset AssessedAt);

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
public sealed record SaveMaterialTreeCommand(Guid UserId, IReadOnlyList<MaterialNodeIn> Nodes)
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

                // 前端传来的 id 可能是 "f_intro" 这类非 GUID 种子串 → 一律当新建
                PracticeMaterial? entity = null;
                if (Guid.TryParse(n.Id, out var gid)) byId.TryGetValue(gid, out entity);

                if (entity is null)
                {
                    entity = new PracticeMaterial(r.UserId, parentId, kind, n.Name, n.Content, n.SortOrder);
                    db.Materials.Add(entity);
                }
                else
                {
                    entity.Rename(n.Name);
                    if (kind == MaterialKind.Folder) entity.SetContent(null);
                    else entity.SetContent(n.Content);
                    entity.MoveTo(parentId, n.SortOrder);
                    entity.SetExpanded(n.Expanded);
                    touched.Add(entity);
                }

                incoming.Add(entity.Id);
                if (n.Children is { Count: > 0 }) Walk(n.Children, entity.Id);
            }
        }

        Walk(r.Nodes, null);

        // 前端没再提交的旧节点 = 已被删除。
        // 走软/硬删都行,这里硬删 —— 树是整树提交,没提交就是用户真的删了它。
        // 级联会把子树一起带走(见 DbContext 的自引用 Cascade 配置)。
        var removed = existing.Where(x => !incoming.Contains(x.Id)).ToList();
        if (removed.Count > 0) db.Materials.RemoveRange(removed);

        await db.SaveChangesAsync(ct);
        return Result.Success(incoming.Count);
    }
}

// ============================================================
// 录音:保存 + 评分缓存
// ============================================================

public sealed record ListRecordingsQuery(Guid UserId, Guid MaterialId)
    : MediatR.IRequest<IReadOnlyList<RecordingDto>>;

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

/// <summary>删除一条录音(软删元数据 + 尽力删文件)。</summary>
public sealed record DeleteRecordingCommand(Guid UserId, Guid RecordingId) : MediatR.IRequest<Result>;

public sealed class DeleteRecordingCommandHandler(AssessmentDbContext db, IAudioStore store)
    : MediatR.IRequestHandler<DeleteRecordingCommand, Result>
{
    public async Task<Result> Handle(DeleteRecordingCommand r, CancellationToken ct)
    {
        var rec = await db.Recordings
            .FirstOrDefaultAsync(x => x.Id == r.RecordingId && x.UserId == r.UserId, ct);
        if (rec is null) return Result.Failure(Error.NotFound("录音"));

        rec.MarkDeleted();
        await db.SaveChangesAsync(ct);

        // 文件删不掉也不回滚 —— 元数据已隐藏,用户视角已删除;
        // 残留文件由后台清理任务扫(比让删除请求失败更友好)。
        await store.DeleteAsync(rec.StoragePath, ct);
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
    string RecognizedText, string? WordsJson, string AzureRegion, string ReferenceText)
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
            score.ReferenceText, score.AssessedAt));
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
            score.ReferenceText, score.AssessedAt));
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
