using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Infrastructure.Persistence;

namespace YourInterview.Services.Assessment.Infrastructure.Storage;

/// <summary>
/// 语音密钥的解析入口 —— 一处定优先级,全局一致。
/// </summary>
/// <remarks>
/// 为什么需要这层(2026-09-15 第十七轮发现的设计漏洞):
///   PronunciationAssessor 是**单例**,而 EF 的 DbContext 是 **Scoped**。
///   单例直接依赖 Scoped 会抛出 "Cannot consume scoped service from singleton",
///   也会把 DbContext 长期持有导致连接不释放 / 缓存脏数据。
///
///   但 key 又必须支持"界面上保存后立即生效" —— 所以要能查到数据库。
///
///   解法:单例只依赖这个**窄接口**,实现内部用 IServiceScopeFactory
///   每次调用开一个短命 scope 查库,查完即释放。
///   这是"单例需要读 Scoped 数据"的标准做法。
///
/// 优先级(与 GetSpeechSettingQuery 保持一致,别分叉):
///   数据库(界面配的) > 环境变量 > appsettings(部署时注入的)
/// </remarks>
public interface ISpeechKeyProvider
{
    Task<SpeechKeySnapshot> ResolveAsync(Guid userId, CancellationToken ct);
}

public sealed record SpeechKeySnapshot(string? Key, string Region, string? Endpoint)
{
    public bool HasKey => !string.IsNullOrWhiteSpace(Key);
}

public sealed class SpeechKeyProvider(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<SpeechKeyProvider> logger) : ISpeechKeyProvider
{
    public async Task<SpeechKeySnapshot> ResolveAsync(Guid userId, CancellationToken ct)
    {
        var fallbackRegion = config["AzureSpeech:Region"];
        if (string.IsNullOrWhiteSpace(fallbackRegion)) fallbackRegion = "canadacentral";
        fallbackRegion = fallbackRegion.Trim();
        // ⚠️ 默认用 **区域 STT 主机** —— 与 PronunciationAssessor.ResolveSttBase 保持一致。
        //    旧代码给的是 api.cognitive.microsoft.com(旧主机),与新的 .stt. 主机不同;
        //    而且空串不会被 `??` 拦住,会抛出相对地址异常。这里一律产出完整绝对地址。
        var fallbackEndpoint = config["AzureSpeech:Endpoint"];
        if (string.IsNullOrWhiteSpace(fallbackEndpoint))
            fallbackEndpoint = $"https://{fallbackRegion}.stt.speech.microsoft.com";

        // 1) 数据库里界面保存的配置优先
        if (userId != Guid.Empty)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AssessmentDbContext>();
                var row = await db.SpeechSettings.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.UserId == userId, ct);

                if (row is not null && !string.IsNullOrWhiteSpace(row.Key))
                {
                    // 空 Endpoint 不能原样传出 —— 下游会拼成相对地址。
                    var ep = string.IsNullOrWhiteSpace(row.Endpoint)
                        ? $"https://{row.Region}.stt.speech.microsoft.com"
                        : row.Endpoint.Trim();
                    return new SpeechKeySnapshot(row.Key, row.Region, ep);
                }
            }
            catch (Exception ex)
            {
                // 查库失败不该让评分功能整体挂掉 —— 降级到配置来源,但如实记录
                logger.LogWarning(ex, "读取数据库中的语音设置失败,降级到配置文件");
            }
        }

        // 2) 环境变量(容器/CI 注入)
        var envKey = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return new SpeechKeySnapshot(envKey,
                Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? fallbackRegion,
                fallbackEndpoint);
        }

        // 3) appsettings(本地开发用 appsettings.Development.json)
        var cfgKey = config["AzureSpeech:Key"];
        return new SpeechKeySnapshot(cfgKey, fallbackRegion, fallbackEndpoint);
    }
}
