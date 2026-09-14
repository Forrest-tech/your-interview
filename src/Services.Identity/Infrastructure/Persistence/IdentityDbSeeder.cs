using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Identity.Domain;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Infrastructure.Persistence;

/// <summary>
/// 数据库迁移 + 种子数据。生产用 EF Migrations;这里同时提供
/// "启动时 EnsureCreated + 幂等种子"的兜底,保证容器/全新环境一键起来就有管理员账号。
/// </summary>
public static class IdentityDbSeeder
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        // 统一用 EF 迁移建表。
        // 不用 EnsureCreated:它只要发现「库里已有同名 schema」就静默跳过,
        // 结果就是表没建、启动不报错、第一个请求才炸(踩过这个坑)。
        await db.Database.MigrateAsync();

        // 1) 角色 + 默认权限(幂等 upsert)
        foreach (var roleName in Roles.All)
        {
            var role = await db.Roles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Name == roleName);

            if (role is null)
            {
                role = new AppRole(roleName, Describe(roleName), isSystemRole: true);
                db.Roles.Add(role);
            }

            var defaults = Roles.DefaultPermissions[roleName];
            var current = await db.RolePermissions.Where(p => p.RoleId == role.Id)
                .Select(p => p.Permission).ToListAsync();

            foreach (var missing in defaults.Except(current))
                db.RolePermissions.Add(new RolePermission(role.Id, missing));

            var obsolete = current.Except(defaults).ToList();
            if (obsolete.Count > 0)
            {
                var toRemove = await db.RolePermissions
                    .Where(p => p.RoleId == role.Id && obsolete.Contains(p.Permission)).ToListAsync();
                db.RolePermissions.RemoveRange(toRemove);
            }
        }
        await db.SaveChangesAsync();

        // 2) 初始管理员
        var adminEmail = config["Seed:AdminEmail"] ?? "admin@your-interview.local";
        var adminPassword = config["Seed:AdminPassword"];
        var normalized = AppUser.Normalize(adminEmail);

        var admin = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == normalized);
        if (admin is null && !string.IsNullOrWhiteSpace(adminPassword))
        {
            var hasher = scope.ServiceProvider.GetRequiredService<Infrastructure.Security.IPasswordHasher>();
            admin = new AppUser(normalized, "System Administrator", hasher.Hash(adminPassword));
            admin.RequirePasswordChange();

            var adminRole = await db.Roles.FirstAsync(r => r.Name == Roles.Admin);
            admin.AssignRole(adminRole.Id);
            db.Users.Add(admin);
            await db.SaveChangesAsync();
            logger.LogWarning("已创建初始管理员 {Email} —— 请尽快登录并修改密码", normalized);
        }

        // 3) 演示账号(仅开发环境)
        if ((config["Seed:CreateDemoData"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            await SeedDemoAsync(db, scope.ServiceProvider, config, logger);
        }

        logger.LogInformation("Identity 数据库就绪:角色 {Roles} 个,用户 {Users} 个",
            await db.Roles.CountAsync(), await db.Users.CountAsync());
    }

    private static async Task SeedDemoAsync(IdentityDbContext db, IServiceProvider sp, IConfiguration config, ILogger logger)
    {
        var hasher = sp.GetRequiredService<Infrastructure.Security.IPasswordHasher>();
        var demoEmail = "demo@your-interview.local";

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == demoEmail)) return;

        // 演示用户密码从配置读(Seed:DemoPassword), 不硬编码。
        // 未配置则跳过演示数据播种 —— 只影响本地演示, 不影响正常流程。
        var demoPassword = config["Seed:DemoPassword"];
        if (string.IsNullOrWhiteSpace(demoPassword))
        {
            logger.LogInformation("未配置 Seed:DemoPassword, 跳过演示数据播种");
            return;
        }

        var demo = new AppUser(demoEmail, "Forrest (Demo)", hasher.Hash(demoPassword));
        var role = await db.Roles.FirstAsync(r => r.Name == Roles.PowerUser);
        demo.AssignRole(role.Id);
        db.Users.Add(demo);

        // 造一批用户用于后台演示
        var rnd = new Random(42);
        var userRole = await db.Roles.FirstAsync(r => r.Name == Roles.User);
        var viewerRole = await db.Roles.FirstAsync(r => r.Name == Roles.Viewer);

        for (var i = 1; i <= 12; i++)
        {
            var u = new AppUser($"user{i:00}@example.com", $"Test User {i:00}", hasher.Hash(demoPassword));
            u.AssignRole(i % 4 == 0 ? viewerRole.Id : userRole.Id);
            if (i % 5 == 0) u.Deactivate();
            db.Users.Add(u);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("已写入演示数据(1 个 PowerUser + 12 个普通用户)");
    }

    private static string Describe(string role) => role switch
    {
        Roles.Admin => "系统管理员:全部权限,含用户/角色/审计/系统配置",
        Roles.PowerUser => "高级用户:业务全权限 + 审计只读,无用户管理",
        Roles.User => "普通用户:自己的求职/机经/知识库/模拟数据",
        Roles.Viewer => "只读用户:仅可查看",
        _ => role
    };
}
