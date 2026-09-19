using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.AiGateway.Infrastructure.Persistence;

/// <summary>
/// AiGateway 数据库启动任务 —— 只建表,不写任何种子数据。
///
/// ★ 遵循 2026-09-17 数据规范(Forrest 要求):
///   重要业务数据只能来自数据库,不从代码里的硬编码种子写入。
///   凭据(ai_settings)必须由用户在设置页自己填 ——
///   代码里预置一条"演示 key"既无意义(无真 key)也不安全。
/// </summary>
public static class AiGatewayDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AiGatewayDbContext>();

        await db.Database.MigrateAsync();

        logger.LogInformation("AiGateway 数据库就绪(仅建表,不写入任何种子数据)");
    }
}
