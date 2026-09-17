using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Analytics.Infrastructure.Persistence;

/// <summary>
/// Analytics 数据库启动任务。
///
/// ★ 2026-09-17 数据规范修订(Forrest 要求):
///   重要业务数据**只能来自数据库**,不再从代码里的硬编码种子写入。
///   因此本类**只负责建表(EF 迁移)**,不写入任何统计快照。
///   历史上这里曾内置"随机生成的示意曲线"(能力曲线/管线快照/主题掌握度),
///   已全部移除:
///     · 分析数据应由真实业务事件聚合产生,而不是代码里编造的假曲线;
///     · 假数据会让用户误以为系统"已有分析",实际是幻觉。
///   空库启动 → 统计表保持为空,随用户真实使用而自然积累。
/// </summary>
public static class AnalyticsDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();

        await db.Database.MigrateAsync();

        logger.LogInformation("Analytics 数据库就绪(仅建表,不写入任何种子数据)");
    }
}
