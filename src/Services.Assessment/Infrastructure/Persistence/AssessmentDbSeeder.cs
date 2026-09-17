using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Assessment.Infrastructure.Persistence;

/// <summary>
/// Assessment 数据库启动任务。
///
/// ★ 2026-09-17 数据规范修订(Forrest 要求):
///   重要业务数据**只能来自数据库**,不再从代码里的硬编码种子写入。
///   因此本类**只负责建表(EF 迁移)**,不写入任何模拟练习会话/评分。
///   历史上这里曾内置 Forrest 的真实 mock session(Gateway system design、
///   发音练习等,含六维评分与弱点结论),已全部移除:
///     · 属个人练习数据 + 复盘结论,应保存在数据库并由用户自己产生;
///     · 种子依赖"查 Identity 用户表拿 UserId"的跨 schema 逻辑,本身就是
///       早期架构的妥协产物。
///   空库启动 → 练习数据保持为空,由用户在 /practice 页面自己录音与评分。
/// </summary>
public static class AssessmentDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AssessmentDbContext>();

        await db.Database.MigrateAsync();

        logger.LogInformation("Assessment 数据库就绪(仅建表,不写入任何种子数据)");
    }
}
