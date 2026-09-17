using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Interviews.Infrastructure.Persistence;

/// <summary>
/// Interviews 数据库启动任务。
///
/// ★ 2026-09-17 数据规范修订(Forrest 要求):
///   重要业务数据**只能来自数据库**,不再从代码里的硬编码种子写入。
///   因此本类**只负责建表(EF 迁移)**,不写入任何面试记录。
///   历史上这里曾内置"Forrest 真实面试场次"的种子(Gateway/CIBC/Rentsync/
///   Pack-Smart/Geotab),已全部移除:
///     · 含真实面试官姓名、公司、评分与复盘结论 —— 属个人隐私数据,不应进代码仓库;
///     · 违反"重要数据放数据库"的原则;
///     · 需要这些资料时,请用界面录入或直接导入数据库(memory/ 与 面试/ 有原文归档)。
///   空库启动 → 面试表保持为空,由用户自己录入。
/// </summary>
public static class InterviewsDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewsDbContext>();

        await db.Database.MigrateAsync();

        logger.LogInformation("Interviews 数据库就绪(仅建表,不写入任何种子数据)");
    }
}
