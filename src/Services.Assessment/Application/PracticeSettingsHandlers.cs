using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

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

// ============================================================
// 本机录音存储目录(★ 2026-09-27 Forrest:在"我的账户"里自选)
// ============================================================

/// <summary>存储目录状态 —— 设置页据此说清"我现在到底存到哪了"。</summary>
public sealed record StorageSettingDto(
    /// <summary>用户保存的本机目录(null = 未设置,用部署默认)。</summary>
    string? DesiredPath,
    /// <summary>容器内实际写盘的根目录。</summary>
    string EffectiveRoot,
    /// <summary>部署时挂载进容器的**本机**目录(由 compose 注入,用于界面回显)。</summary>
    string? HostDirectory,
    /// <summary>Default / CustomActive / CustomPendingMount / CustomUnusable。</summary>
    string Status,
    bool Writable,
    /// <summary>不可用时的人话原因。可用时为 null。</summary>
    string? Message,
    long FileCount,
    long TotalBytes,
    /// <summary>目录结构示例,让用户一眼看懂文件会落在哪。</summary>
    string LayoutExample,
    bool InContainer,
    /// <summary>是否可以把已有录音搬到新目录(新目录已生效且旧目录里还有文件)。</summary>
    bool CanMigrate);

/// <summary>迁移结果。</summary>
public sealed record StorageMigrationDto(int Moved, int Skipped, long Bytes, string TargetRoot);

/// <summary>
/// 查询当前存储目录状态。
///
/// ⚠️ 与"实际写盘"走**同一份**判定(<see cref="LocalStorageRules"/>):
///   界面说"已生效"就必须真的生效,绝不能出现界面绿了、文件却去了别处 ——
///   那正是 2026-09-27 "录音录完找不到"事故的样子。
/// </summary>
public sealed record GetStorageSettingQuery(Guid UserId) : MediatR.IRequest<StorageSettingDto>;

public sealed class GetStorageSettingQueryHandler(IAudioStore store, IConfiguration config)
    : MediatR.IRequestHandler<GetStorageSettingQuery, StorageSettingDto>
{
    public async Task<StorageSettingDto> Handle(GetStorageSettingQuery r, CancellationToken ct)
    {
        var roots = await store.ResolveRootsAsync(r.UserId, ct);
        var hostDir = config["Storage:HostDirectory"];

        string status;
        string? message = null;

        if (roots.DesiredRoot is null)
        {
            status = "Default";
        }
        else if (roots.DesiredActive)
        {
            status = "CustomActive";
        }
        else
        {
            // 容器里"路径存在但不在挂载点下"= 需要用户去 compose 里挂一下;
            // 其它情况(没权限等)才是真不可用。
            status = LocalStorageRules.RunningInContainer ? "CustomPendingMount" : "CustomUnusable";
            message = roots.Reason;
        }

        var (files, bytes) = LocalStorageRules.Measure(roots.ActiveRoot);

        // 展示用的"用户视角根目录":优先用户所选,其次是挂载进来的本机目录
        var displayBase = roots.DesiredRoot
            ?? (string.IsNullOrWhiteSpace(hostDir) ? LocalStorageRules.TrimServiceFolder(roots.DefaultRoot) : hostDir);

        var userPart = r.UserId.ToString("N")[..8];
        // ⚠️ 格式串里的 / 要加单引号转义:否则 .NET 会把它当"日期分隔符"，
        //    某些区域设置下会渲染成 2026.09 或 2026-09。
        var layout = $"{displayBase}/{LocalStorageRules.ServiceFolder}/{userPart}/" +
                     $"{DateTimeOffset.UtcNow:yyyy'/'MM}/{{录音ID}}.webm";

        var canMigrate = roots.DesiredActive
            && !string.Equals(roots.ActiveRoot, roots.DefaultRoot, StringComparison.Ordinal)
            && Directory.Exists(Path.Combine(roots.DefaultRoot, userPart));

        return new StorageSettingDto(
            roots.DesiredRoot, roots.ActiveRoot,
            string.IsNullOrWhiteSpace(hostDir) ? null : hostDir,
            status, message is null, message,
            files, bytes, layout,
            LocalStorageRules.RunningInContainer, canMigrate);
    }
}

/// <summary>
/// 保存用户自选的本机存储目录。
///
/// ★ 关键取舍:**路径当前不可用也照样保存**,只是状态如实标成
///   "待挂载"。用户在容器里换目录本来就需要两步(改设置 + 改挂载后重启),
///   若因为"现在还看不见"就拒绝保存,用户永远走不完这个流程 ——
///   而真正危险的是"保存了但静默写进容器内部",那由
///   <see cref="LocalStorageRules.Evaluate"/> 在每次落盘时拦住,不是靠界面。
/// </summary>
public sealed record SaveStorageSettingCommand(Guid UserId, string? RootPath)
    : MediatR.IRequest<Result<StorageSettingDto>>;

public sealed class SaveStorageSettingCommandHandler(AssessmentDbContext db, MediatR.ISender sender)
    : MediatR.IRequestHandler<SaveStorageSettingCommand, Result<StorageSettingDto>>
{
    public async Task<Result<StorageSettingDto>> Handle(SaveStorageSettingCommand r, CancellationToken ct)
    {
        var raw = (r.RootPath ?? string.Empty).Trim();

        // 空 = 回到部署默认(用户主动"恢复默认"的出口)
        if (raw.Length == 0)
        {
            var existing = await db.StorageSettings.FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);
            if (existing is not null)
            {
                db.StorageSettings.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            return Result.Success(await sender.Send(new GetStorageSettingQuery(r.UserId), ct));
        }

        // ⚠️ 容器里 `~` 要靠 HOST_HOME(compose 注入的**宿主机**家目录)才能还原成
        //    Mac 上的真实路径。没有它就展开成 /root/… —— 那是容器内部,写进去的
        //    录音 Mac 上根本看不到。所以这里不猜,直接让用户二选一:配 HOST_HOME 或填绝对路径。
        if (raw.StartsWith('~') && LocalStorageRules.RunningInContainer
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HOST_HOME")))
            return Result.Failure<StorageSettingDto>(Error.Validation("Storage.HomeShortcut",
                "容器里识别不了 ~:请在 .env 里加 HOST_HOME=/Users/你的用户名 后重建 assessment 容器,或直接填写完整绝对路径。"));

        var normalized = LocalStorageRules.Normalize(raw);

        if (!normalized.StartsWith('/'))
            return Result.Failure<StorageSettingDto>(Error.Validation("Storage.NotAbsolute",
                "请填写本机绝对路径,例如 /Users/你的用户名/Documents/your-interview/recordings"));
        if (normalized == "/" || normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            return Result.Failure<StorageSettingDto>(Error.Validation("Storage.TooShallow",
                "这个路径太浅(接近系统根目录),请指定一个专门的文件夹。"));
        if (normalized.Contains("..", StringComparison.Ordinal))
            return Result.Failure<StorageSettingDto>(Error.Validation("Storage.HasDotDot",
                "路径里不能包含 .. 。"));

        var row = await db.StorageSettings.FirstOrDefaultAsync(x => x.UserId == r.UserId, ct);
        if (row is null)
        {
            row = new UserStorageSetting(r.UserId, normalized);
            db.StorageSettings.Add(row);
        }
        else
        {
            row.Update(normalized);
        }

        await db.SaveChangesAsync(ct);

        return Result.Success(await sender.Send(new GetStorageSettingQuery(r.UserId), ct));
    }
}

/// <summary>
/// 把已有录音搬到用户自选的目录。
///
/// 为什么需要:换了根目录之后,旧目录里的录音虽然还能播(读取会两边都找),
///   但文件分散在两个地方 —— 正是 Forrest 最反感的"录音散落"。
///   由于数据库里存的是**相对路径**,搬文件**不需要改任何数据库记录**。
/// </summary>
public sealed record MigrateStorageCommand(Guid UserId) : MediatR.IRequest<Result<StorageMigrationDto>>;

public sealed class MigrateStorageCommandHandler(IAudioStore store,
    ILogger<MigrateStorageCommandHandler> logger)
    : MediatR.IRequestHandler<MigrateStorageCommand, Result<StorageMigrationDto>>
{
    public async Task<Result<StorageMigrationDto>> Handle(MigrateStorageCommand r, CancellationToken ct)
    {
        var roots = await store.ResolveRootsAsync(r.UserId, ct);
        if (!roots.DesiredActive)
            return Result.Failure<StorageMigrationDto>(Error.Validation("Storage.NotActive",
                "新目录当前不可用,先按提示挂载好再迁移。"));
        if (string.Equals(roots.ActiveRoot, roots.DefaultRoot, StringComparison.Ordinal))
            return Result.Failure<StorageMigrationDto>(Error.Validation("Storage.SameRoot",
                "当前生效的就是默认目录,无需迁移。"));

        var userPart = r.UserId.ToString("N")[..8];
        var sourceDir = Path.Combine(roots.DefaultRoot, userPart);
        if (!Directory.Exists(sourceDir))
            return Result.Success(new StorageMigrationDto(0, 0, 0, roots.ActiveRoot));

        var moved = 0;
        var skipped = 0;
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(roots.DefaultRoot, file);
            var target = Path.Combine(roots.ActiveRoot, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            var size = new FileInfo(file).Length;

            if (File.Exists(target))
            {
                skipped++;                       // 目标已有一份 —— 不覆盖,保守处理
                continue;
            }

            try
            {
                File.Move(file, target);
                moved++;
                bytes += size;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "迁移录音失败 {File} → {Target}", file, target);
                skipped++;
            }
        }

        return Result.Success(new StorageMigrationDto(moved, skipped, bytes, roots.ActiveRoot));
    }
}
