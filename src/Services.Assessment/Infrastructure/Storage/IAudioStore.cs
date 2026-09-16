using System.Security.Cryptography;

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
    /// <summary>把音频写盘,返回**相对** storage 根目录的路径。</summary>
    Task<string> SaveAsync(Guid userId, Guid recordingId, string extension,
        Stream content, CancellationToken ct);

    /// <summary>打开音频读取流。文件不存在返回 null(不抛)。</summary>
    Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct);

    /// <summary>删除音频文件。文件已不在也视为成功(幂等)。</summary>
    Task<bool> DeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>是否已配置存储根目录。</summary>
    bool IsConfigured { get; }
}

/// <summary>
/// 本地文件系统实现(当前默认)。
///
/// 目录结构:`{Root}/{userId 前8位}/{yyyy}/{MM}/{recordingId}.{ext}`
///   · 按用户 + 年月分目录:单个目录不会堆几十万文件(某些文件系统会因此变慢);
///   · 用 GUID 当文件名:天然防冲突,也不会把用户素材名泄漏到文件系统上。
/// </summary>
public sealed class LocalAudioStore(IConfiguration config, ILogger<LocalAudioStore> logger) : IAudioStore
{
    /// <summary>
    /// 存储根目录。
    /// 优先级:Storage:RootDirectory 配置 > AZURE_STORAGE_DIR 环境变量 > 仓库旁的 storage/recordings。
    /// 未显式配置时落到一个**可预测**的目录,而不是随机临时目录 —— 否则重启后找不到文件。
    /// </summary>
    private readonly string _root = ResolveRoot(config);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_root);

    private static string ResolveRoot(IConfiguration config)
    {
        var configured = config["Storage:RootDirectory"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var env = Environment.GetEnvironmentVariable("PRACTICE_STORAGE_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        // 兜底:当前工作目录下的 storage/recordings
        return Path.Combine(Directory.GetCurrentDirectory(), "storage", "recordings");
    }

    public async Task<string> SaveAsync(Guid userId, Guid recordingId, string extension,
        Stream content, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var userPart = userId.ToString("N")[..8];
        var relDir = Path.Combine(userPart, now.Year.ToString("D4"), now.Month.ToString("D2"));
        var fileName = recordingId.ToString("N") + "." + extension.TrimStart('.');
        var relative = Path.Combine(relDir, fileName);

        var absolute = Path.Combine(_root, relative);
        var dir = Path.GetDirectoryName(absolute)!;
        Directory.CreateDirectory(dir);

        // 先写临时文件再原子改名:中途崩了也不会留下半截录音被当成完整文件
        var tmp = absolute + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fs, ct);
        }
        File.Move(tmp, absolute, overwrite: true);

        logger.LogInformation("录音已保存 {Relative} ({Bytes} 字节)", relative, new FileInfo(absolute).Length);
        return relative.Replace('\\', '/');   // 统一成正斜杠,跨平台可移植
    }

    public Task<Stream?> OpenReadAsync(string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Task.FromResult<Stream?>(null);

        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute)) return Task.FromResult<Stream?>(null);

        Stream s = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(s);
    }

    public Task<bool> DeleteAsync(string relativePath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return Task.FromResult(true);

        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute)) return Task.FromResult(true);   // 幂等

        try
        {
            File.Delete(absolute);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            // 删不掉不该让整个删除请求失败 —— 元数据已软删,文件留待后台清理
            logger.LogWarning(ex, "删除录音文件失败 {Relative}", relativePath);
            return Task.FromResult(false);
        }
    }
}
