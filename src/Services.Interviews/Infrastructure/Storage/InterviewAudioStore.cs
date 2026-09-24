using System.Security.Cryptography;

namespace YourInterview.Services.Interviews.Infrastructure.Storage;

/// <summary>
/// 实战机经录音存储抽象。
///
/// 与 Assessment 的 IAudioStore 同一套设计决策(2026-09-23 M1 地基修复):
///   · 录音字节不进 Postgres —— 文件系统 + 数据库只存相对路径;
///   · 先写 .tmp 再原子改名 —— 中途崩溃不会留下"半截录音被当成完整文件";
///   · 上传时同步算 SHA-256 —— M1.3 的完整性巡检靠它发现"文件被换过/损坏"。
///
/// 目录结构:`{Root}/{entryId}/{uuid}.{ext}`
///   按"一场面试一个目录"组织:同一场的材料聚在一起,
///   分析 Worker 也按这个约定找文件(事件里带真实路径,不再猜)。
/// </summary>
public interface IInterviewAudioStore
{
    /// <summary>
    /// 把录音写盘,返回 (相对路径, SHA-256, 实际字节数)。
    /// 文件名由存储层生成(Guid),调用方不需要先造好 AssetId。
    /// </summary>
    Task<StoredAudioFile> SaveAsync(Guid entryId, string fileName, Stream content,
        CancellationToken ct);

    /// <summary>打开音频读取流。文件不存在返回 null(不抛)。</summary>
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct);

    /// <summary>删除文件。文件已不在也视为成功(幂等)。</summary>
    Task<bool> DeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>校验相对路径里的文件是否还在(完整性巡检用)。</summary>
    bool FileExists(string relativePath);
}

/// <summary>落盘结果:相对路径 + 内容摘要 + 实际大小。</summary>
public sealed record StoredAudioFile(string RelativePath, string Sha256, long SizeBytes);

public sealed class LocalInterviewAudioStore(IConfiguration config,
    ILogger<LocalInterviewAudioStore> logger) : IInterviewAudioStore
{
    /// <summary>
    /// 存储根目录 —— 解析链与分析 Worker 的 InterviewsRoot **完全一致**
    /// (两处必须同步改,相对路径拼出来才是同一个文件):
    ///   Storage:InterviewsRoot 配置 > INTERVIEW_STORAGE_DIR 环境变量 >
    ///   {Storage:RootDirectory}/interviews > 仓库根/storage/interviews > cwd 兜底。
    ///
    /// docker-compose 里 interviews 与 analysis-worker 挂**同一个卷**
    /// 到 /app/storage/interviews —— 上传方写、分析方读,路径天然一致。
    /// </summary>
    private readonly string _root = ResolveRoot(config);

    /// <summary>允许的音频扩展名。白名单拒绝 —— 别让 .exe 伪装成材料混进存储区。</summary>
    public static readonly string[] AllowedExtensions = [".m4a", ".mp3", ".wav", ".webm", ".ogg"];

    /// <summary>单文件上限 200MB:约两小时 m4a,足够覆盖一场马拉松面试,同时挡住误传。</summary>
    public const long MaxFileBytes = 200 * 1024 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_root);

    private static string ResolveRoot(IConfiguration config)
    {
        // 1. 专用配置/环境变量( docker-compose 里 interviews 与 worker 共卷,两边都设它 )
        var dedicated = config["Storage:InterviewsRoot"];
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;

        var env = Environment.GetEnvironmentVariable("INTERVIEW_STORAGE_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        // 2. 沙盒/统一 Storage 根派生:{Storage:RootDirectory}/interviews ——
        //    和 Assessment 的 {root}/{user}/… 同一层级,一个根管所有录音。
        var rootDir = config["Storage:RootDirectory"];
        if (!string.IsNullOrWhiteSpace(rootDir)) return Path.Combine(rootDir, "interviews");

        // 3. 本地裸跑(dotnet run):锚定到**仓库根**而不是进程 cwd ——
        //    interviews 与 analysis-worker 的 cwd 不同,按 cwd 会指向两个地方。
        var solutionRoot = FindSolutionRoot();
        if (solutionRoot is not null) return Path.Combine(solutionRoot, "storage", "interviews");

        return Path.Combine(Directory.GetCurrentDirectory(), "storage", "interviews");
    }

    /// <summary>
    /// 从程序集位置向上找仓库根。优先含 docker-compose.yml 的目录(仓库根,
    /// .sln 常在 src/ 子目录里,只认 sln 会锚错一层)。找不到 compose 再退回 sln。
    /// 最多向上 8 层,找不到返回 null —— 只作兜底,不影响显式配置。
    /// </summary>
    private static string? FindSolutionRoot()
    {
        DirectoryInfo? withCompose = null, withSln = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.GetFiles("docker-compose.yml").Length > 0) withCompose = dir;
            if (dir.GetFiles("*.sln").Length > 0) withSln = dir;
        }
        return (withCompose ?? withSln)?.FullName;
    }

    public async Task<StoredAudioFile> SaveAsync(Guid entryId, string fileName, Stream content,
        CancellationToken ct)
    {
        var ext = NormalizeExtension(Path.GetExtension(fileName));

        var relDir = entryId.ToString();
        var fileNameOnDisk = Guid.NewGuid().ToString("N") + ext;
        var relative = $"{relDir}/{fileNameOnDisk}";

        var absolute = Path.Combine(_root, relDir, fileNameOnDisk);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        // 先写临时文件再原子改名;同时流式计算 SHA-256 —— 只读一遍流。
        var tmp = absolute + ".tmp";
        long bytes;
        string sha256;
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var hasher = SHA256.Create())
        {
            // CopyToAsync 之后 hasher 里已有完整摘要,但要拿 hash 就不能再用 fs 的写入位置
            bytes = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                hasher.TransformBlock(buffer, 0, read, null, 0);
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                bytes += read;
            }
            hasher.TransformFinalBlock([], 0, 0);
            sha256 = Convert.ToHexString(hasher.Hash!);
        }
        File.Move(tmp, absolute, overwrite: true);

        logger.LogInformation("面试录音已保存 {Relative} ({Bytes} 字节, SHA256={Sha}…)",
            relative, bytes, sha256[..8]);
        return new StoredAudioFile(relative, sha256, bytes);
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        var absolute = ResolveAbsolute(relativePath);
        if (absolute is null || !File.Exists(absolute)) return Task.FromResult<Stream?>(null);

        Stream s = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(s);
    }

    public Task<bool> DeleteAsync(string relativePath, CancellationToken ct)
    {
        var absolute = ResolveAbsolute(relativePath);
        if (absolute is null || !File.Exists(absolute)) return Task.FromResult(true);   // 幂等

        try
        {
            File.Delete(absolute);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            // 删不掉不该让删除请求失败 —— 元数据已软删,文件留待后台清理
            logger.LogWarning(ex, "删除面试录音失败 {Relative}", relativePath);
            return Task.FromResult(false);
        }
    }

    public bool FileExists(string relativePath)
        => ResolveAbsolute(relativePath) is { } absolute && File.Exists(absolute);

    /// <summary>
    /// 相对路径 → 绝对路径。含路径穿越("..")或 rooted 的输入直接拒绝 ——
    /// StoragePath 会进事件、跨服务传给 Worker,这里把它钉死在根目录内。
    /// </summary>
    private string? ResolveAbsolute(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains("..")) return null;

        return Path.Combine(_root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>扩展名归一化 + 白名单校验。不在白名单直接抛(由 Handler 转 400)。</summary>
    private static string NormalizeExtension(string ext)
    {
        var e = ext?.ToLowerInvariant() ?? string.Empty;
        if (!AllowedExtensions.Contains(e))
            throw new InvalidOperationException(
                $"不支持的音频格式 {e}。允许:{string.Join("/", AllowedExtensions)}");
        return e;
    }
}
