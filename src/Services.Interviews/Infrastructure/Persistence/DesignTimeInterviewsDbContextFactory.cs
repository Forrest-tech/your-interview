using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace YourInterview.Services.Interviews.Infrastructure.Persistence;

/// <summary>
/// 票 240：仅供 dotnet-ef 脚手架迁移的设计时工厂（本项目用最小托管模型，
///
/// 无 CreateHostBuilder 入口，工具无法从 Program 反推上下文）。
/// 只建模型、不连库：连接串是占位串，migrations add 全程不打开连接；
/// 运行时仍走 Program.cs 的 AddDbContext（拦截器 + 当前用户过滤器），与本工厂无关。
/// </summary>
public sealed class DesignTimeInterviewsDbContextFactory : IDesignTimeDbContextFactory<InterviewsDbContext>
{
    public InterviewsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=5432;Database=yourinterview;Username=postgres;Password=design-time-only",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", InterviewsDbContext.Schema))
            .Options;
        return new InterviewsDbContext(options, currentUser: null);
    }
}
