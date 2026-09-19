using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Domain;

namespace YourInterview.Services.Jobs.Infrastructure.Persistence;

public sealed class JobsDbContext(DbContextOptions<JobsDbContext> options) : DbContext(options)
{
    public const string Schema = "jobs";

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<ApplicationStatusChange> StatusChanges => Set<ApplicationStatusChange>();
    public DbSet<InterviewRound> Rounds => Set<InterviewRound>();

    // ---- 用户简历正文(2026-09-18)----
    // 用户级资产(一份,跨所有岗位复用)—— 与 Company/JdText 粒度不同,故独立实体。
    public DbSet<UserResume> Resumes => Set<UserResume>();
    public DbSet<CoverLetter> CoverLetters => Set<CoverLetter>();

    /// <summary>
    /// 把"新加进聚合子集合、但 Id 由 C# 端生成"的子实体显式标成 Added。
    ///
    /// 踩过的坑:EF 对「已跟踪父聚合上新增的子实体」会按主键是否已赋值来判断状态。
    /// 我们的 Guid 主键在 C# 构造函数里就赋值了 → EF 误判为 Modified → 生成 UPDATE,
    /// 而该行其实不存在 → 影响 0 行 → DbUpdateConcurrencyException。
    ///
    /// 判定依据:实体在数据库里"是否存在过"。
    ///   - 有主键但从未写入过(状态 Detached 或 被我方标记过) → Added
    ///   - 已从数据库加载过 → Unchanged/Modified,不能动
    /// 用 EF 的临时值机制最稳:子实体构造时把 Id 标成临时值,EF 就会按"新增"处理。
    /// </summary>
    private void MarkNewChildrenAsAdded()
    {
        foreach (var entry in ChangeTracker.Entries<ApplicationStatusChange>().ToList())
        {
            if (entry.State == EntityState.Modified) entry.State = EntityState.Added;
        }
        foreach (var entry in ChangeTracker.Entries<InterviewRound>().ToList())
        {
            if (entry.State == EntityState.Modified) entry.State = EntityState.Added;
        }
    }

    public override int SaveChanges()
    {
        MarkNewChildrenAsAdded();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        MarkNewChildrenAsAdded();
        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Company>(e =>
        {
            e.ToTable("companies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.Name);
            e.Property(x => x.Website).HasMaxLength(1024);
            e.Property(x => x.Industry).HasMaxLength(200);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.LogoUrl).HasMaxLength(1024);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.CompanyType).HasMaxLength(50);
            // 公司情报长文本:给足空间(粘贴整篇公司简介/技术博客摘要够用),
            // 但设上限避免无界增长拖垮行存储。
            e.Property(x => x.Profile).HasMaxLength(20000);
            e.Property(x => x.ProfileSourcesJson).HasMaxLength(4000);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            // 公司名唯一(同一用户视角;当前为单租户账号空间)
            e.HasIndex(x => x.Name).IsUnique();
        });

        // 用户简历正文(2026-09-18)
        b.Entity<UserResume>(e =>
        {
            e.ToTable("resumes");
            // 一人一份主版本 —— 用户级资产,不是每条投递一份
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            // Content 用 text:简历形态差异大(纯文本/Markdown/PDF 提取),
            // 限长只会造成"保存失败但不知道为什么"。
            e.Property(x => x.Content).HasColumnType("text").IsRequired();
            e.Property(x => x.Version).IsRequired();
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<JobApplication>(e =>
        {
            e.ToTable("applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(300).IsRequired();
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Link).HasMaxLength(2048);
            e.Property(x => x.Salary).HasMaxLength(200);
            e.Property(x => x.WorkMode).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(100);
            e.Property(x => x.JdSummary).HasMaxLength(20000);
            // JD 全文:与 JdSummary 同量级上限。取 40000 是因为真实 JD 常含
            // 岗位职责+任职要求+公司介绍三段,20000 在长 JD 上会截断。
            e.Property(x => x.JdText).HasMaxLength(40000);
            e.Property(x => x.JdSourceUrl).HasMaxLength(2048);
            e.Property(x => x.PassRateEstimate).HasMaxLength(1000);
            e.Property(x => x.MatchKeywords).HasMaxLength(4000);
            e.Property(x => x.Notes).HasMaxLength(8000);
            e.Property(x => x.PosterName).HasMaxLength(200);
            e.Property(x => x.OutreachMessage).HasMaxLength(4000);
            e.Property(x => x.RejectionReason).HasMaxLength(2000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Priority).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CompanyId);
            e.HasIndex(x => new { x.Status, x.Priority });
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.History).WithOne().HasForeignKey(h => h.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Rounds).WithOne().HasForeignKey(r => r.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.Metadata.FindNavigation(nameof(JobApplication.History))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(JobApplication.Rounds))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<ApplicationStatusChange>(e =>
        {
            e.ToTable("application_status_changes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => x.ApplicationId);
        });

        b.Entity<InterviewRound>(e =>
        {
            e.ToTable("interview_rounds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Stage).HasMaxLength(100);
            e.Property(x => x.Interviewer).HasMaxLength(300);
            e.Property(x => x.Format).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Feedback).HasMaxLength(4000);
            e.HasIndex(x => x.ApplicationId);
        });

        // 求职信(2026-09-18)。一条投递一封 —— ApplicationId 唯一。
        b.Entity<CoverLetter>(e =>
        {
            e.ToTable("cover_letters");
            e.HasKey(x => x.Id);
            // 一条投递只能有一封信:用唯一索引在数据库层兜住,
            // 而不是只靠应用层"先查再插" —— 并发下那套会插出两封。
            e.HasIndex(x => x.ApplicationId).IsUnique();
            e.HasIndex(x => x.UserId);
            // Content 用 text:求职信长短差异大,限长只造成"保存失败但不知道为什么"。
            e.Property(x => x.Content).HasColumnType("text").IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.GeneratedByModel).HasMaxLength(200);
            e.Property(x => x.LastPromptHint).HasMaxLength(4000);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne<JobApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
