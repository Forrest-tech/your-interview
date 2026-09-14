using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Analytics.Domain;

namespace YourInterview.Services.Analytics.Infrastructure.Persistence;

/// <summary>
/// Analytics 种子数据。
///
/// 这里的种子有一点特殊:它**演示读模型的价值**——
/// 造一条 30 天的能力提升曲线与漏斗趋势,让用户一打开仪表盘
/// 就能看懂"这个页面在讲什么"。
///
/// ⚠️ 纪律:曲线本身是**示意性的**趋势数据(用于展示图表与洞察逻辑),
/// 但每一个维度的起止值都刻意对齐 Forrest 的真实基线 ——
/// 发音高(93)、结构低(45)、技术深度低(60)。
/// 这样示意曲线不会给出与实际相反的印象。
/// </summary>
public static class AnalyticsDbSeeder
{
    private static async Task<Guid?> ResolveSeedUserIdAsync(AnalyticsDbContext db,
        IConfiguration config, ILogger logger)
    {
        var email = config["Seed:OwnerEmail"] ?? "admin@your-interview.local";
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ""Id"" FROM identity.users WHERE ""Email"" = @e LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "@e";
            p.Value = email;
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync();
            if (result is Guid g) return g;
            if (result is string s && Guid.TryParse(s, out var parsed)) return parsed;
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询 Identity 用户失败 —— 跳过 Analytics 种子");
            return null;
        }
    }

    public static async Task MigrateAndSeedAsync(IServiceProvider services, IConfiguration config,
        ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>();

        await db.Database.MigrateAsync();

        if (!(config["Seed:CreateDemoData"] ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Analytics 数据库就绪(未写入演示数据)");
            return;
        }

        if (await db.AbilitySnapshots.AnyAsync())
        {
            logger.LogInformation("Analytics 演示数据已存在,跳过");
            return;
        }

        var userId = await ResolveSeedUserIdAsync(db, config, logger);
        if (userId is null) return;
        var uid = userId.Value;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // ---------- 30 天能力曲线 ----------
        // (维度, 起始值, 结束值, 来源)
        // 起止值对齐真实基线:发音强、结构与技术深度弱。
        var curves = new (string Dim, int Start, int End, string Source)[]
        {
            ("pronunciation", 91, 93, "interview"),
            ("fluency", 52, 61, "interview"),
            ("sentenceIntegrity", 54, 63, "interview"),
            ("structure", 38, 52, "interview"),
            ("technicalDepth", 48, 58, "knowledge"),
            ("relevance", 66, 72, "interview")
        };

        var rnd = new Random(20260913); // 固定种子 → 每次生成同样的示意曲线(可复现)

        for (var dayOffset = 29; dayOffset >= 0; dayOffset -= 2)
        {
            var date = today.AddDays(-dayOffset);
            var progress = (29 - dayOffset) / 29.0; // 0 → 1

            foreach (var (dim, start, end, source) in curves)
            {
                var baseValue = start + (end - start) * progress;
                var jitter = rnd.Next(-2, 3); // ±2 的自然波动
                var score = (int)Math.Round(baseValue) + jitter;
                db.AbilitySnapshots.Add(new AbilitySnapshot(uid, date, dim, score, source));
            }
        }

        // ---------- 求职漏斗趋势(每 3 天一个快照) ----------
        for (var dayOffset = 29; dayOffset >= 0; dayOffset -= 3)
        {
            var date = today.AddDays(-dayOffset);
            var progress = (29 - dayOffset) / 29.0;

            var applied = (int)Math.Round(12 + 38 * progress);      // 12 → 50
            var screening = (int)Math.Round(3 + 11 * progress);     // 3 → 14
            var interviewing = (int)Math.Round(1 + 6 * progress);   // 1 → 7
            var offered = progress > 0.85 ? 1 : 0;

            db.PipelineSnapshots.Add(new PipelineSnapshot(uid, date,
                saved: applied + 8, applied: applied, screening: screening,
                interviewing: interviewing, offered: offered,
                rejected: (int)Math.Round(4 + 9 * progress)));
        }

        // ---------- 技术栈掌握分布 ----------
        var topics = new (string Topic, int Total, int Mastered, int Learning, int Fresh)[]
        {
            ("架构", 7, 2, 4, 1),
            ("云原生", 5, 1, 3, 1),
            ("C#/.NET", 5, 2, 2, 1),
            ("安全", 4, 1, 2, 1),
            ("API", 4, 2, 2, 0),
            ("前端", 3, 0, 1, 2),
            ("软技能", 3, 1, 1, 1),
            ("数据库", 2, 0, 2, 0)
        };

        foreach (var (topic, total, mastered, learning, fresh) in topics)
            db.MasteryBreakdowns.Add(new MasteryBreakdown(uid, today, topic, total, mastered,
                learning, fresh));

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Analytics 演示数据已写入:{A} 条能力快照、{P} 条漏斗快照、{M} 条掌握分布",
            await db.AbilitySnapshots.CountAsync(),
            await db.PipelineSnapshots.CountAsync(),
            await db.MasteryBreakdowns.CountAsync());
    }
}
