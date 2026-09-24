using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Storage;

namespace YourInterview.Services.Interviews.Infrastructure.Services;

/// <summary>
/// 录音文件完整性巡检(M1.3)—— 后台定期把每个文件型材料"体检"一遍:
///   文件在吗 → 字节数对吗 → SHA-256 还是上传时那份吗?
///
/// 结论(AssetIntegrity)连同时间戳回写,前端据此提示:
///   Missing → 「文件丢失」(卷被误清/人工误删);
///   Size/HashMismatch → 「文件损坏」(被截断/被同名替换)。
///
/// 为什么扫盘而不是"信任上传成功就完了":录音是这个产品最不可再生的数据
/// (面试结束了没法重录),磁盘静默损坏、运维误清目录、卷挂载漂移都不会
/// 有任何报错 —— 只有定期实打实地读一遍文件才能发现。
/// 摘要逐文件重算:单文件上限 200MB,个人规模(几百个文件)夜里一轮跑得完。
///
/// 配置(可覆盖,沙盒测试用秒级):
///   Storage:IntegritySweepInitialDelay —— 启动后首巡延迟(默认 30s)
///   Storage:IntegritySweepInterval    —— 巡检间隔(默认 6 小时)
/// </summary>
public sealed class AudioIntegritySweepService(
    IServiceScopeFactory scopeFactory,
    IInterviewAudioStore store,
    IConfiguration config,
    ILogger<AudioIntegritySweepService> logger) : BackgroundService
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>单批处理条数 —— 分批保存,避免长事务长时间占着连接。</summary>
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = ParseTimeSpan(config["Storage:IntegritySweepInitialDelay"], DefaultInitialDelay);
        var interval = ParseTimeSpan(config["Storage:IntegritySweepInterval"], DefaultInterval);
        logger.LogInformation("录音完整性巡检已启动(首巡延迟 {Initial},间隔 {Interval})",
            initialDelay, interval);

        try { await Task.Delay(initialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // 单轮失败不退出:巡检是长驻的自愈组件,下一轮接着来
                logger.LogWarning(ex, "完整性巡检一轮失败,下一轮重试");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>巡一轮:全量文件型材料逐个体检,分批回写。返回处理的文件数。</summary>
    private async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewsDbContext>();

        // 先只取 Id 列表(轻量、无跟踪),再按批加载实体 —— 巡检是后台任务,
        // 不与请求路径争长事务。Guid 上做 keyset 分页在 LINQ 里不好翻,
        // 个人规模的材料表全量 Id 一次拉完全全够。
        var ids = await db.Assets.AsNoTracking()
            .Where(a => a.StoragePath != null)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (ids.Count == 0) return 0;
        logger.LogInformation("完整性巡检开始:共 {Count} 个文件", ids.Count);

        var ok = 0; var missing = 0; var sizeMismatch = 0; var hashMismatch = 0;

        foreach (var chunk in ids.Chunk(BatchSize))
        {
            var batch = await db.Assets.Where(a => chunk.Contains(a.Id)).ToListAsync(ct);
            foreach (var asset in batch)
            {
                var result = await VerifyAsync(asset, ct);
                asset.RecordIntegrity(result);
                switch (result)
                {
                    case AssetIntegrity.Ok: ok++; break;
                    case AssetIntegrity.Missing: missing++; break;
                    case AssetIntegrity.SizeMismatch: sizeMismatch++; break;
                    case AssetIntegrity.HashMismatch: hashMismatch++; break;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "完整性巡检完成:{Total} 个文件 —— 正常 {Ok},丢失 {Missing},大小不符 {Size},摘要不符 {Hash}",
            ids.Count, ok, missing, sizeMismatch, hashMismatch);
        return ids.Count;
    }

    /// <summary>
    /// 单文件体检:一次顺序读同时拿字节数与 SHA-256。
    /// 结论优先级:不在 → 大小不符 → 摘要不符 → Ok。
    /// 历史(无 Sha256 的)资产退化成只比大小。
    /// </summary>
    private async Task<AssetIntegrity> VerifyAsync(InterviewAsset asset, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(asset.StoragePath!, ct);
        if (stream is null) return AssetIntegrity.Missing;

        long bytes;
        string sha;
        using (var hasher = SHA256.Create())
        {
            bytes = 0;
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                hasher.TransformBlock(buffer, 0, read, null, 0);
                bytes += read;
            }
            hasher.TransformFinalBlock([], 0, 0);
            sha = Convert.ToHexString(hasher.Hash!);
        }

        if (asset.SizeBytes != bytes) return AssetIntegrity.SizeMismatch;
        if (asset.Sha256 is not null
            && !string.Equals(asset.Sha256, sha, StringComparison.OrdinalIgnoreCase))
            return AssetIntegrity.HashMismatch;
        return AssetIntegrity.Ok;
    }

    private static TimeSpan ParseTimeSpan(string? raw, TimeSpan fallback)
        => TimeSpan.TryParse(raw, out var v) && v > TimeSpan.Zero ? v : fallback;
}
