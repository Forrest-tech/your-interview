using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Assessment.Infrastructure.Storage;

/// <summary>
/// LLM 凭据解析入口 —— 一处定优先级,全局一致。
///
/// 优先级(与 SpeechKeyProvider 严格同构,别分叉):
///   数据库(用户在设置页保存的) &gt; 环境变量 &gt; appsettings
///
/// 为什么用 IServiceScopeFactory 而不是直接依赖 DbContext:
///   调用方(PronunciationAssessor 式的单例、以及后台生成器)可能是单例,
///   而 EF DbContext 是 Scoped —— 单例依赖 Scoped 会被容器拒绝,
///   直接持有还会导致连接不释放。这里每次调用开一个短命 scope,查完即释放。
/// </summary>
public interface IAiKeyProvider
{
    /// <summary>解析出可用于建客户端的连接参数。查不到 key 时返回 null(LlmConnection 为空)。</summary>
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
                var db = scope.ServiceProvider.GetRequiredService<AssessmentDbContext>();
                var row = await db.AiSettings.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.UserId == userId, ct);

                if (row is not null && !string.IsNullOrWhiteSpace(row.ApiKey))
                {
                    return new LlmConnection(row.Protocol, row.ApiKey, row.BaseUrl, row.Model,
                        row.Endpoint, row.ApiVersion);
                }
            }
            catch (Exception ex)
            {
                // 查库失败不该让整个功能挂掉 —— 降级到配置来源,但如实记录。
                logger.LogWarning(ex, "读取数据库中的 AI 设置失败,降级到配置文件");
            }
        }

        // 2) 环境变量(容器/CI 注入)。名称与主流 SDK 习惯保持一致。
        var envKey = Environment.GetEnvironmentVariable("LLM_API_KEY")
                     ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            var protocol = Environment.GetEnvironmentVariable("LLM_PROTOCOL") ?? LlmProtocols.OpenAiCompatible;
            var baseUrl = Environment.GetEnvironmentVariable("LLM_BASE_URL");
            var model = Environment.GetEnvironmentVariable("LLM_MODEL");
            if (!string.IsNullOrWhiteSpace(model))
            {
                return new LlmConnection(protocol, envKey, baseUrl, model,
                    Environment.GetEnvironmentVariable("LLM_ENDPOINT"),
                    Environment.GetEnvironmentVariable("LLM_API_VERSION"));
            }
        }

        // 3) appsettings(本地开发用 appsettings.Development.json)
        var cfgKey = config["Llm:ApiKey"];
        if (!string.IsNullOrWhiteSpace(cfgKey))
        {
            return new LlmConnection(
                config["Llm:Protocol"] ?? LlmProtocols.OpenAiCompatible,
                cfgKey,
                config["Llm:BaseUrl"],
                config["Llm:Model"] ?? string.Empty,
                config["Llm:Endpoint"],
                config["Llm:ApiVersion"]);
        }

        return null;
    }

    public async Task<ILlmClient?> CreateClientAsync(Guid userId, CancellationToken ct)
    {
        var conn = await ResolveAsync(userId, ct);
        if (conn is null || !conn.HasKey || !conn.HasModel) return null;
        return clientFactory.Create(conn);
    }
}
