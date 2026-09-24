using System.Linq;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Infrastructure.Persistence;

/// <summary>
/// 实战机经的持久化上下文。
/// 每个微服务独占一个 PostgreSQL schema("interviews"),服务间不共享表 —— 微服务的核心纪律。
/// </summary>
public sealed class InterviewsDbContext(DbContextOptions<InterviewsDbContext> options) : DbContext(options)
{
    public const string Schema = "interviews";

    public DbSet<InterviewEntry> Entries => Set<InterviewEntry>();
    public DbSet<InterviewAsset> Assets => Set<InterviewAsset>();
    public DbSet<InterviewQuestion> Questions => Set<InterviewQuestion>();
    public DbSet<InterviewWeakness> Weaknesses => Set<InterviewWeakness>();

    /// <summary>分析任务台账(发件箱)。派发器与回写端点共用。</summary>
    public DbSet<AnalysisJob> AnalysisJobs => Set<AnalysisJob>();

    /// <summary>
    /// 给该条目所有未关单的任务排定终态(只改跟踪状态,不落库 ——
    /// 由调用方随后的 SaveChangesAsync 与业务变更**同一事务**提交)。
    ///
    /// 调用点:转写稿回写/分析结果回写(→ Succeeded)、失败上报(→ Failed)。
    /// 已关单的任务不动(幂等):首次结论不被重复回写覆盖。
    /// </summary>
    public async Task StageCloseOpenJobsAsync(Guid entryId, AnalysisJobStatus status,
        string? reason, CancellationToken ct = default)
    {
        var open = await AnalysisJobs
            .Where(j => j.InterviewEntryId == entryId)
            .Where(AnalysisJob.IsOpenFilter)   // ⚠️ 计算属性 IsOpen 进不了查询
            .ToListAsync(ct);
        foreach (var job in open)
            job.Close(status, reason);
    }

    /// <summary>
    /// 修正「新增子实体被 EF 误判为 Modified」—— 实现已提取到共享的
    /// BuildingBlocks.Persistence.NewChildEntityFixer(查库判存在,M1.5),
    /// 与 Jobs 服务同一份逻辑,不再各写一套。
    /// </summary>
    public override int SaveChanges()
    {
        this.MarkNewChildrenAsAdded(Schema);
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        this.MarkNewChildrenAsAdded(Schema);
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<InterviewEntry>(e =>
        {
            e.ToTable("entries");
            e.HasKey(x => x.Id);

            e.Property(x => x.CompanyName).HasMaxLength(300).IsRequired();
            e.Property(x => x.Role).HasMaxLength(300).IsRequired();
            e.Property(x => x.CompanyProfile).HasMaxLength(20000);
            e.Property(x => x.JdText).HasMaxLength(60000);
            e.Property(x => x.JdSummary).HasMaxLength(8000);
            e.Property(x => x.InterviewFormat).HasMaxLength(50);
            e.Property(x => x.Interviewers).HasMaxLength(1000);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Result).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(20000);
            e.Property(x => x.FailureReason).HasMaxLength(4000);
            e.Property(x => x.AnalysisSummary).HasMaxLength(20000);

            // 枚举存字符串:数据库可读性远高于魔法数字,加新枚举值也不会错位
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);

            e.HasIndex(x => x.CompanyId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.InterviewDate);
            e.HasIndex(x => x.CompanyName);

            // PostgreSQL 原生 xmin 做乐观并发令牌,不需要额外 rowversion 列
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);

            e.HasMany(x => x.Assets).WithOne().HasForeignKey(a => a.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(q => q.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Weaknesses).WithOne().HasForeignKey(w => w.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            // 集合用字段访问模式 + 只读暴露,保证外部不能绕过领域方法直接改集合(封装不变量)
            e.Metadata.FindNavigation(nameof(InterviewEntry.Assets))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Questions))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Weaknesses))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<InterviewAsset>(e =>
        {
            e.ToTable("assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(500).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.StoragePath).HasMaxLength(2048);
            e.Property(x => x.BlobUrl).HasMaxLength(2048);
            e.Property(x => x.Sha256).HasMaxLength(64);          // 十六进制 SHA-256 正好 64 字符
            e.Property(x => x.IntegrityStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SourceLanguage).HasMaxLength(20);
            e.Property(x => x.TranscriptText).HasMaxLength(500000);
            e.Property(x => x.TranscriptSegmentsJson).HasColumnType("text");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => x.StoragePath);                       // 巡检按路径反查;软删条目清理孤儿文件也走它
        });

        b.Entity<InterviewQuestion>(e =>
        {
            e.ToTable("questions");
            e.HasKey(x => x.Id);
            e.Property(x => x.QuestionText).HasMaxLength(8000).IsRequired();
            e.Property(x => x.MyAnswerText).HasMaxLength(60000);
            e.Property(x => x.Assessment).HasMaxLength(8000);
            e.Property(x => x.StuckReason).HasMaxLength(2000);
            e.Property(x => x.RecommendedAnswer).HasMaxLength(20000);
            e.Property(x => x.WeaknessTagsJson).HasMaxLength(2000);
            e.Property(x => x.FollowUpQuestionsJson).HasMaxLength(8000);
            e.Property(x => x.MissedPointsJson).HasMaxLength(8000);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => new { x.InterviewEntryId, x.Sequence });
        });

        b.Entity<InterviewWeakness>(e =>
        {
            e.ToTable("weaknesses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(8000);
            e.Property(x => x.Evidence).HasMaxLength(8000);
            e.Property(x => x.Suggestion).HasMaxLength(8000);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => x.Category);
        });

        b.Entity<AnalysisJob>(e =>
        {
            e.ToTable("analysis_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.PayloadJson).HasMaxLength(4000).IsRequired();
            e.Property(x => x.FailureReason).HasMaxLength(4000);
            e.Property(x => x.LastError).HasMaxLength(4000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.JobType).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => x.Status);

            // ★ 单飞约束:同一幂等键最多一个未关单任务(Pending/Dispatched)。
            // 部分唯一索引:关单(Succeeded/Failed/Dead)的历史行不占坑,
            // 重跑转写会新建任务行,历史与单飞两全。
            e.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"Status\" IN ('Pending', 'Dispatched')");
        });
    }
}
