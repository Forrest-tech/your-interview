using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Analytics.Domain;

namespace YourInterview.Services.Analytics.Infrastructure.Persistence;

/// <summary>
/// Analytics 读模型库(独占 schema "analytics")。
///
/// 注意这张库里没有任何业务实体 —— 只有汇总快照与幂等台账。
/// 这让 Analytics 可以随时"删库重建":重放事件流即可恢复,
/// 而不会影响任何业务真相。
/// </summary>
public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options)
    : DbContext(options)
{
    public const string Schema = "analytics";

    public DbSet<AbilitySnapshot> AbilitySnapshots => Set<AbilitySnapshot>();
    public DbSet<PipelineSnapshot> PipelineSnapshots => Set<PipelineSnapshot>();
    public DbSet<MasteryBreakdown> MasteryBreakdowns => Set<MasteryBreakdown>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<AbilitySnapshot>(e =>
        {
            e.ToTable("ability_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Dimension).HasMaxLength(50).IsRequired();
            e.Property(x => x.Source).HasMaxLength(30).IsRequired();

            // 同一用户 + 同一天 + 同一维度 + 同一来源 只能有一条 —— 重复事件走 Update 而不是插新行
            e.HasIndex(x => new { x.UserId, x.Date, x.Dimension, x.Source }).IsUnique();
        });

        b.Entity<PipelineSnapshot>(e =>
        {
            e.ToTable("pipeline_snapshots");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Date }).IsUnique();
        });

        b.Entity<MasteryBreakdown>(e =>
        {
            e.ToTable("mastery_breakdowns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Topic).HasMaxLength(200).IsRequired();
            e.HasIndex(x => new { x.UserId, x.Date, x.Topic }).IsUnique();
        });

        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("processed_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(200).IsRequired();
            // EventId 唯一 —— 数据库层的最后一道幂等防线。
            // 即使应用层判断被并发绕过,唯一索引也会拦住重复插入。
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => x.ProcessedAt);
        });
    }
}
