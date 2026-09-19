using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YourInterview.Services.AiGateway.Domain;
using YourInterview.Services.AiGateway.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.AiGateway.Infrastructure.Storage;

/// <summary>
/// LLM 凭据解析入口 —— 一处定优先级,全局一致。
///
/// 优先级(与语音凭据解析严格同构,别分叉):
///   数据库(用户在设置页保存的) &gt; 环境变量 &gt; appsettings
///
/// 为什么用 IServiceScopeFactory 而不是直接依赖 DbContext:
///   调用方可能是单例,而 EF DbContext 是 Scoped —— 单例依赖 Scoped 会被容器拒绝,
///   直接持有还会导致连接不释放。这里每次调用开一个短命 scope,查完即释放。
///
/// 2026-09-18:从 Assessment 抽出为独立服务 —— 凭据只存一处,
/// 任何需要 LLM 的服务都通过本网关调用,不再各自解析凭据。
/// </summary>
public interface IAiKeyProvider
{
    /// <summary>解析出可用于建客户端的连接参数。查不到 key 时返回 null。</summary>
    Task<LlmConnection?> ResolveAsync(Guid userId, CancellationToken ct);

    /// <summary>
    /// 按给定连接参数构造客户端。用户未配置时返回 null —— 调用方必须处理 null,
    /// 不允许静默降级成"假成功"。
    /// </summary>
    Task<ILlmClient?> CreateClientAsync(Guid userId, CancellationToken ct);
}

public sealed class AiKeyProvider(
    IServiceScopeFactory scopeFactory,
    ILlmClientFactory clientFactory,
    IConfiguration config,
    ILogger<AiKeyProvider> logger) : IAiKeyProvider
{
    public async Task<LlmConnection?> ResolveAsync(Guid userId, CancellationToken ct)
    {
        // 1) 数据库里界面保存的配置优先
        if (userId != Guid.Empty)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AiGatewayDbContext>();
                var row = await db.AiSettings.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.UserId == userId, ct);

                if (row is not null && !string.IsNullOrWhiteSpace(row.ApiKey))
                    return row.ToConnection();
            }
            catch (Exception ex)
            {
                // 查库失败不该让整个功能挂掉 —— 降级到配置来源,但如实记录。
                logger.LogWarning(ex, "读取用户 {UserId} 的 LLM 配置失败,回退到环境变量/appsettings", userId);
            }
        }

        // 2) 环境变量
        var envKey = config["Ai:ApiKey"] ?? Environment.GetEnvironmentVariable("AI_API_KEY");
        var envModel = config["Ai:Model"] ?? Environment.GetEnvironmentVariable("AI_MODEL");
        var envBase = config["Ai:BaseUrl"] ?? Environment.GetEnvironmentVariable("AI_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return new LlmConnection(
                config["Ai:Protocol"] ?? LlmProviders.OpenAiCompatible,
                envKey, envBase, envModel ?? "deepseek-chat");
        }

        // 3) appsettings 兜底
        var cfgKey = config["Ai:ApiKey"];
        if (!string.IsNullOrWhiteSpace(cfgKey))
        {
            return new LlmConnection(
                config["Ai:Protocol"] ?? LlmProviders.OpenAiCompatible,
                cfgKey, config["Ai:BaseUrl"], config["Ai:Model"] ?? "deepseek-chat");
        }

        return null;
    }

    public async Task<ILlmClient?> CreateClientAsync(Guid userId, CancellationToken ct)
    {
        var conn = await ResolveAsync(userId, ct);
        return conn is null ? null : clientFactory.Create(conn);
    }
}
