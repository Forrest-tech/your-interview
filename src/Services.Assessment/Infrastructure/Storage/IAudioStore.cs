using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Infrastructure.Persistence;

namespace YourInterview.Services.Assessment.Infrastructure.Storage;

/// <summary>
/// 音频存储抽象。
///
/// 为什么要有这层抽象(2026-09-15 方案决策):
///   录音字节**不进 Postgres** —— 二进制大对象会把库撑大、拖慢备份、
///   让 pg_dump 体积失控。所以走文件系统。
///   但"文件系统"只是**当前**选择:以后单人用久了想上对象存储
///   (S3 / Azure Blob)只需要新增一个实现类,Application 层一行都不用改。
///
/// 只存**相对路径**在数据库里:这样整个 storage 目录可以整体搬走/挂载,
/// 换机器不用改数据。
/// </summary>
public interface IAudioStore
{
    /// <summary>把音频写盘,返回**相对**当前生效根目录的路径。</summary>
    Task<string> SaveAsync(Guid userId, Guid recordingId, string extension,
        Stream content, CancellationToken ct);

    /// <summary>打开音频读取流。文件不存在返回 null(不抛)。</summary>
    Task<Stream?> OpenReadAsync(Guid userId, string relativePath, CancellationToken ct);

    /// <summary>删除音频文件。文件已不在也视为成功(幂等)。</summary>
    Task<bool> DeleteAsync(Guid userId, string relativePath, CancellationToken ct);

    /// <summary>是否已配置存储根目录。</summary>
    bool IsConfigured { get; }

    /// <summary>部署层给的默认根目录(配置/环境变量/兜底) —— 设置页展示用。</summary>
    string DefaultRoot { get; }

    /// <summary>
    /// 解析某用户**当前生效**的根目录。
    /// 用户自选目录不可用(未挂载/没权限)时退回默认目录 —— 宁可换位置,
    /// 也绝不让录音写进一个本机看不到的地方。
    /// </summary>
    Task<StorageRoots> ResolveRootsAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// 根目录三元组:默认(部署) / 用户自选 / 当前实际生效。
/// 设置页据此告诉用户"你现在到底存到哪了",避免界面与落盘分叉。
/// </summary>
public sealed record StorageRoots(
    string DefaultRoot,
    string? DesiredRoot,
    string ActiveRoot,
    bool DesiredActive,
    string? Reason);

/// <summary>
/// 本地文件系统实现(当前默认)。
///
/// 目录结构:`{Root}/{userId 前8位}/{yyyy}/{MM}/{recordingId}.{ext}`
///   · 按用户 + 年月分目录:单个目录不会堆几十万文件(某些文件系统会因此变慢);
///   · 用 GUID 当文件名:天然防冲突,也不会把用户素材名泄漏到文件系统上。
///
/// 其中 `{Root}` = 用户在"我的账户"里自选的本机目录 + 服务子目录 assessment
/// (未设置或不可用时 = 部署默认目录,见 <see cref="StorageRoots"/>)。
/// </summary>
public sealed class LocalAudioStore(IConfiguration config, IServiceScopeFactory scopeFactory,
    ILogger<LocalAudioStore> logger) : IAudioStore
{
    /// <summary>
    /// 部署层给的默认根目录。
    /// 优先级:Storage:RootDirectory 配置 > PRACTICE_STORAGE_DIR 环境变量 > 仓库**同级**的 recordings/。
    /// 未显式配置时落到一个**可预测**的目录,而不是随机临时目录 —— 否则重启后找不到文件。
    /// </summary>
    private readonly string _defaultRoot = ResolveRoot(config);

    public string DefaultRoot => _defaultRoot;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_defaultRoot);

    private static string ResolveRoot(IConfiguration config)
    {
        var configured = config["Storage:RootDirectory"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var env = Environment.GetEnvironmentVariable("PRACTICE_STORAGE_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        // 兜底(2026-09-27,Forrest 要求):录音**不能放进仓库里面**(防止误提交 GitHub),
        //   统一放「仓库的同级 recordings/assessment/」—— 仓库在 ~/dev/your-interview,
        //   录音在 ~/dev/recordings/assessment。再加一层 `assessment` 子目录,
        //   是为了让 ../recordings 下能按服务分目录(interviews 的视频将来放 ../recordings/interviews …)。
        //   向上找 .git 定位仓库根;找不到 .git(罕见)才退回旧位置。
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        return dir is null
            ? Path.Combine(Directory.GetCurrentDirectory(), "storage", "assessment")
            : Path.GetFullPath(Path.Combine(dir.FullName, "..", "recordings", "assessment"));
    }

    // ---------- 根目录解析 ----------

    /// <summary>用户在"我的账户"里保存的本机目录(未设置返回 null)。</summary>
    private async Task<string?> DesiredRootAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) return null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AssessmentDbContext>();
            var row = await db.StorageSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return string.IsNullOrWhiteSpace(row?.RootPath) ? null : row!.RootPath;
        }
        catch (Exception ex)
        {
            // 查库失败不该让录音功能整体挂掉 —— 退回默认目录,但如实记录
            logger.LogWarning(ex, "读取用户自定义存储目录失败,退回部署默认目录");
            return null;
        }
    }

    public async Task<StorageRoots> ResolveRootsAsync(Guid userId, CancellationToken ct)
    {
        var raw = await DesiredRootAsync(userId, ct);
        if (string.IsNullOrWhiteSpace(raw))
            return new StorageRoots(_defaultRoot, null, _defaultRoot, false, null);

        var desired = LocalStorageRules.Normalize(raw);
        var audioRoot = LocalStorageRules.AudioRootFor(desired);

        // ① 路径在服务**自己眼里**就可用(原生运行时 / 用户填的就是容器内已挂载的路径)
        var (usable, reason) = LocalStorageRules.Evaluate(audioRoot);
        if (usable)
            return new StorageRoots(_defaultRoot, desired, audioRoot, true, null);

        // ② 容器里的**典型情形**:用户填的是 Mac 上的路径(`/Users/…/Documents/…`),
        //    容器里当然看不到它 —— 真正的对应关系是"部署时把哪个本机目录挂了进来"
        //    (compose 注入的 Storage:HostDirectory)。两者一致 = 已经生效。
        var hostDirRaw = config["Storage:HostDirectory"];
        if (!string.IsNullOrWhiteSpace(hostDirRaw))
        {
            var hostDir = LocalStorageRules.Normalize(hostDirRaw);
            if (string.Equals(hostDir, desired, StringComparison.Ordinal))
                return new StorageRoots(_defaultRoot, desired, _defaultRoot, true, null);

            return new StorageRoots(_defaultRoot, desired, _defaultRoot, false,
                $"容器当前挂载的是 {hostDirRaw},还不是你选的这个目录 —— 按 .env 配置改好后重建 assessment 容器即可。");
        }

        return new StorageRoots(_defaultRoot, desired, _defaultRoot, false, reason);
    }

    public async Task<string> SaveAsync(Guid userId, Guid recordingId, string extension,
        Stream content, CancellationToken ct)
    {
        var roots = await ResolveRootsAsync(userId, ct);
        if (!roots.DesiredActive && roots.DesiredRoot is not null)
            logger.LogWarning("用户自定义存储目录 {Desired} 当前不可用({Reason}),本次录音仍写入默认目录 {Root}",
                roots.DesiredRoot, roots.Reason, roots.ActiveRoot);

        var now = DateTimeOffset.UtcNow;
        var userPart = userId.ToString("N")[..8];
        var relDir = Path.Combine(userPart, now.Year.ToString("D4"), now.Month.ToString("D2"));
        var fileName = recordingId.ToString("N") + "." + extension.TrimStart('.');
        var relative = Path.Combine(relDir, fileName);

        var absolute = Path.Combine(roots.ActiveRoot, relative);
        var dir = Path.GetDirectoryName(absolute)!;
        Directory.CreateDirectory(dir);

        // 先写临时文件再原子改名:中途崩了也不会留下半截录音被当成完整文件
        var tmp = absolute + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fs, ct);
        }
        File.Move(tmp, absolute, overwrite: true);

        logger.LogInformation("录音已保存 {Relative} → {Root}({Bytes} 字节)",
            relative, roots.ActiveRoot, new FileInfo(absolute).Length);
        return relative.Replace('\\', '/');   // 统一成正斜杠,跨平台可移植
    }

    public async Task<Stream?> OpenReadAsync(Guid userId, string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;

        // 依次在「当前生效根目录 → 默认根目录」里找:
        // 用户换了存储位置后,**旧位置的历史录音仍然要能播** ——
        // 相对路径是一样的,所以换根目录不等于换路径,两边都找一遍即可。
        var roots = await ResolveRootsAsync(userId, ct);
        foreach (var root in CandidateRoots(roots))
        {
            var absolute = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute)) continue;

            Stream s = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
            return s;
        }
        return null;
    }

    public async Task<bool> DeleteAsync(Guid userId, string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;

        // 删除要**一处不留**:新旧根目录里都可能有(换过目录/迁移半途),
        // 只删当前生效的那一份就会留下孤儿文件。
        var roots = await ResolveRootsAsync(userId, ct);
        var allGone = true;

        foreach (var root in CandidateRoots(roots))
        {
            var absolute = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absolute)) continue;

            try
            {
                File.Delete(absolute);
            }
            catch (Exception ex)
            {
                // 删不掉不该让整个删除请求失败 —— 元数据已硬删,文件留待人工清理
                logger.LogWarning(ex, "删除录音文件失败 {Relative}", relativePath);
                allGone = false;
            }
        }

        // 文件压根不在(两端都没有)也视为成功 —— 幂等
        return allGone;
    }

    /// <summary>候选根目录(去重、保序):当前生效的在前,默认目录兜底。</summary>
    private static IEnumerable<string> CandidateRoots(StorageRoots roots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in new[] { roots.ActiveRoot, roots.DefaultRoot })
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var normalized = LocalStorageRules.Normalize(r);
            if (seen.Add(normalized)) yield return normalized;
        }
    }
}
