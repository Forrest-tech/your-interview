using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace YourInterview.Services.Identity.Infrastructure.Persistence;

/// <summary>
/// 票241:供 dotnet-ef 脚手架迁移用的设计时工厂(仅生成迁移时用,不连真实库:
/// 生成迁移只需要模型快照对比,不需要数据库连接)。
/// 历史表名与 Program.cs 的 MigrationsHistoryTable 配置逐字一致,
/// 否则生成的迁移会写到默认历史表,与现行自定义名账本漂移。
/// </summary>
public sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=design-time-only",
                npg => npg.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema))
            .Options;
        return new IdentityDbContext(options);
    }
}
