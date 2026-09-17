using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence;

/// <summary>
/// Knowledge 数据库启动任务。
///
/// ★ 2026-09-17 数据规范修订(Forrest 要求):
///   重要业务数据**只能来自数据库**,不再从代码里的硬编码种子写入。
///   因此本类**只负责建表(EF 迁移)**,不写入任何知识库条目。
///   历史上这里曾内置 26 条概念百科 + 真实面试复盘条目(来源
///   workspace/NET技术知识库.md),已全部移除:
///     · 这些是"内容资产",属于业务数据,应由用户通过界面/导入维护;
///     · 硬编码在 C# 里无法被用户直接编辑,且随代码发布而固化。
///   空库启动 → 知识库保持为空,由用户自己录入或导入。
/// </summary>
public static class KnowledgeDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();

        await db.Database.MigrateAsync();

        logger.LogInformation("Knowledge 数据库就绪(仅建表,不写入任何种子数据)");
    }
}
