using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Infrastructure.Persistence;

/// <summary>
/// Jobs 数据库启动任务。
///
/// ★ 2026-09-17 数据规范修订(Forrest 要求):
///   重要业务数据**只能来自数据库**,不再从代码里的硬编码种子写入。
///   因此本类**只负责建表(EF 迁移)**,不写入任何投递记录/公司数据。
///   历史上这里曾内置"Forrest 真实投递历史"的演示种子,已全部移除:
///     · 硬编码的真实业务数据既违反"数据在库里"的原则,又存在隐私风险;
///     · 需要演示数据时,请用 tools/ 下的导入脚本或直接 SQL 写入数据库。
///   空库启动 → 业务表保持为空,由用户在界面里自己录入。
/// </summary>
public static class JobsDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobsDbContext>();

        // 统一用 EF 迁移建表(而非 EnsureCreated —— 后者一旦库中存在同名 schema 就会静默跳过)
        await db.Database.MigrateAsync();

        logger.LogInformation("Jobs 数据库就绪(仅建表,不写入任何种子数据)");
    }
}
