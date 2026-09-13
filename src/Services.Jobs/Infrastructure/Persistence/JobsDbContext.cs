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
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            // 公司名唯一(同一用户视角;当前为单租户账号空间)
            e.HasIndex(x => x.Name).IsUnique();
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
    }
}
