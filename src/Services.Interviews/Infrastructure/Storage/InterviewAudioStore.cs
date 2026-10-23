using System.Security.Cryptography;
using YourInterview.BuildingBlocks.Storage;

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
    /// 存储根目录 —— 解析链已收编 BuildingBlocks adapter(ARC-12/票39),
    /// 本服务与分析 Worker 共用同一实现,禁止再另写一份:
    /// 见 <see cref="StorageRootResolver.ResolveInterviewsRoot(IConfiguration)"/>。
    /// </summary>
    private readonly string _root = StorageRootResolver.ResolveInterviewsRoot(config);

    /// <summary>
    /// 允许的文件扩展名。白名单拒绝 —— 别让 .exe 伪装成材料混进存储区。
    /// 音频是录音本体;图片是统一入口的截图附件(与录音共用同一存储/取回管线,
    /// 靠材料 meta 的 screenshotAssetId 引用),2026-10-21 补入。
    /// </summary>
    public static readonly string[] AllowedExtensions = [
        ".m4a", ".mp3", ".wav", ".webm", ".ogg",
        ".png", ".jpg", ".jpeg", ".gif", ".webp"
    ];

    /// <summary>单文件上限 200MB:约两小时 m4a,足够覆盖一场马拉松面试,同时挡住误传。</summary>
    public const long MaxFileBytes = 200 * 1024 * 1024;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_root);

    public async Task<StoredAudioFile> SaveAsync(Guid entryId, string fileName, Stream content,
        CancellationToken ct)
    {
        var ext = NormalizeExtension(Path.GetExtension(fileName));

        var relDir = entryId.ToString();
        var fileNameOnDisk = Guid.NewGuid().ToString("N") + ext;
        var relative = $"{relDir}/{fileNameOnDisk}";

        var absolute = Path.Combine(_root, relDir, fileNameOnDisk);

        // 先写临时文件再原子改名(机制已收编 BuildingBlocks adapter,ARC-12/票39);
        // 同时流式计算 SHA-256 —— 只读一遍流。
        long bytes = 0;
        string sha256 = string.Empty;   // 实际值在原子写回调内赋值(回调必然先于下方日志执行)
        await LocalFileStorage.WriteAtomicallyAsync(absolute, async fs =>
        {
            using var hasher = SHA256.Create();
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
        }, ct);

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
    /// 相对路径 → 绝对路径 —— 守卫口径已收编 BuildingBlocks adapter(ARC-12/票39):
    /// 见 <see cref="StorageRelativePaths.ResolveGuarded"/>。
    /// </summary>
    private string? ResolveAbsolute(string? relativePath)
        => StorageRelativePaths.ResolveGuarded(_root, relativePath);

    /// <summary>扩展名归一化 + 白名单校验。不在白名单直接抛(由 Handler 转 400)。</summary>
    private static string NormalizeExtension(string ext)
    {
        var e = ext?.ToLowerInvariant() ?? string.Empty;
        if (!AllowedExtensions.Contains(e))
            throw new InvalidOperationException(
                $"不支持的文件格式 {e}。允许:{string.Join("/", AllowedExtensions)}");
        return e;
    }
}
