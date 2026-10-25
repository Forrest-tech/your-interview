using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Jobs.Domain;

namespace YourInterview.Services.Jobs.Infrastructure.Persistence;

public sealed class JobsDbContext(
    DbContextOptions<JobsDbContext> options,
    ICurrentUser? currentUser = null) : DbContext(options)
{
    public const string Schema = "jobs";

    /// <summary>
    /// 当前请求用户(COD-01/票03 全局过滤器用)。EF 查询过滤器引用本属性:
    /// 无 HTTP 上下文(后台/消费者)时为 null,过滤器 fail-closed 匹配零行,
    /// 非请求路径必须 IgnoreQueryFilters + 事件 UserId 显式校验。
    /// </summary>
    public Guid? CurrentUserId { get; } = currentUser?.UserId;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<ApplicationStatusChange> StatusChanges => Set<ApplicationStatusChange>();
    public DbSet<InterviewRound> Rounds => Set<InterviewRound>();

    // ---- 用户简历正文(2026-09-18)----
    // 用户级资产(一份,跨所有岗位复用)—— 与 Company/JdText 粒度不同,故独立实体。
    public DbSet<UserResume> Resumes => Set<UserResume>();
    public DbSet<CoverLetter> CoverLetters => Set<CoverLetter>();

    // ---- 简历 PDF 文件(2026-10-19)----
    // 基础简历(ApplicationId null,一人一份)+ 各投递的 tailored 版本(一条投递一份)。
    public DbSet<ResumeFile> ResumeFiles => Set<ResumeFile>();

    // ---- M3:申请问答库(用户级)与沟通记录(投递级)----
    public DbSet<ApplicationAnswerTemplate> AnswerTemplates => Set<ApplicationAnswerTemplate>();
    public DbSet<ApplicationCommunication> Communications => Set<ApplicationCommunication>();

    // ---- COD-10a/票14:Tracker 收藏与按投递自备问题(原 localStorage 孤岛上后端)----
    public DbSet<ApplicationFavorite> Favorites => Set<ApplicationFavorite>();
    public DbSet<ApplicationQuestion> ApplicationQuestions => Set<ApplicationQuestion>();

    /// <summary>
    /// 修正「新增子实体被 EF 误判为 Modified」。共享实现见 NewChildEntityFixer ——
    /// 之前这里的私有版本是"状态 Modified 一律转 Added",把「修改既有轮次」也
    /// 误变成了 INSERT → 主键冲突(2026-09-24 M1.5 修复,详见共享类注释)。
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
            e.Property(x => x.UserId).IsRequired();
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
            // 公司名按用户唯一(COD-01 contract):同名公司在不同用户下各自一份
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
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
            // JD 摘要:text 列不限长 (票62,与 JdText 同口径)。SmartAdd 解析出的
            // 长摘要曾撞 varchar(20000) 整单 500 (Postgres 22001);长度口径改由
            // 应用层校验统一把关 (JdTextLimits.MaxLength,超限 400 而非落库 500)。
            e.Property(x => x.JdSummary).HasColumnType("text");
            // JD 全文:text 列不限长 (FUN-04)。真实 LinkedIn 长 JD 可达 44K+ 字符,
            // varchar 上限曾让 smart-add 整单 500 (Postgres 22001)。长度口径改由
            // 应用层校验统一把关 (JdTextLimits.MaxLength,超限 400 而非落库 500),
            // 口径与 UserResume.Content 的 text 先例一致。
            e.Property(x => x.JdText).HasColumnType("text");
            e.Property(x => x.JdSourceUrl).HasMaxLength(2048);
            // 公司介绍:text 列不限长 (票62,与 JdText 同口径,理由同 JdSummary)。
            e.Property(x => x.CompanyDescription).HasColumnType("text");
            // 工作身份要求:归一短文本,500 足够。
            e.Property(x => x.WorkAuthorization).HasMaxLength(500);
            // 岗位性质:归一短文本(Full-time/Part-time/Permanent/Contract/...),100 足够。
            e.Property(x => x.JobType).HasMaxLength(100);
            // 任职要求:text 列不限长 (票62,与 JdText 同口径,理由同 JdSummary)。
            e.Property(x => x.Requirements).HasColumnType("text");
            // 福利待遇:text 列不限长 (票62,与 JdText 同口径,理由同 JdSummary)。
            e.Property(x => x.Benefits).HasColumnType("text");
            // 票 154：JD 小节列表 JSON:text 列不限长，与 JdText 同口径；
            // 长度口径改由应用层校验统一把关（JdTextLimits.MaxLength，超限 400 而非落库 500）。
            e.Property(x => x.SectionsJson).HasColumnType("text");
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
            e.Property(x => x.UserId).IsRequired();
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
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
            // 票101:轮次行纳入 xmin 并发令牌(系统列,零迁移)——轮次详情编辑回传钉版。
            e.UseXminAsConcurrencyToken();
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

        // 简历 PDF 文件(2026-10-19)。ApplicationId null = 基础简历(一人一份),
        // 有值 = 该投递的 tailored 版本(一条投递一份)。
        b.Entity<ResumeFile>(e =>
        {
            e.ToTable("resume_files");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(300).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            // PDF 二进制:PostgreSQL bytea。10MB 上限在应用层校验,这里不设限。
            e.Property(x => x.Data).HasColumnType("bytea").IsRequired();
            // 提取文本:供 Resume Score 按投递版本算分,text 类型不设限。
            e.Property(x => x.TextContent).HasColumnType("text").IsRequired();
            // 基础简历一人一份(只对 ApplicationId IS NULL 的行唯一)
            e.HasIndex(x => x.UserId).IsUnique().HasFilter("\"ApplicationId\" IS NULL");
            // tailored 一条投递一份(只对 ApplicationId IS NOT NULL 的行唯一)
            e.HasIndex(x => x.ApplicationId).IsUnique().HasFilter("\"ApplicationId\" IS NOT NULL");
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne<JobApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        // M3:申请问答库(用户级,不挂投递,按 UserId 隔离)
        b.Entity<ApplicationAnswerTemplate>(e =>
        {
            e.ToTable("answer_templates");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Category).HasMaxLength(100).IsRequired();
            e.Property(x => x.Question).HasMaxLength(500).IsRequired();
            // Answer 是正文,给足空间但不设上限(用户答案可较长)
            e.Property(x => x.Answer).HasColumnType("text").IsRequired();
            e.Property(x => x.SortOrder).IsRequired();
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.Category });
        });

        // M3:沟通记录(投递级,挂在 ApplicationId 下)
        b.Entity<ApplicationCommunication>(e =>
        {
            e.ToTable("application_communications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(30).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(300);
            e.Property(x => x.Content).HasColumnType("text").IsRequired();
            e.Property(x => x.ContactName).HasMaxLength(300);
            e.Property(x => x.ContactEmail).HasMaxLength(300);
            // 2026-10-20 Playbook v2:解析元数据 + 安排处理状态。
            e.Property(x => x.MetaJson).HasColumnType("text");
            e.Property(x => x.HandlingStatus).HasMaxLength(20);
            e.Property(x => x.UserId).IsRequired();
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
            e.HasIndex(x => x.ApplicationId);
            e.HasOne<JobApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        // COD-10a/票14:Tracker 收藏(用户级关系行)。(UserId, ApplicationId) 唯一 ——
        // 同一用户对同一投递最多一条收藏,导入/重复 PUT 靠它在库层兜底幂等;
        // 软删后重加走 Restore(Handler 以 IgnoreQueryFilters 查含删行),不新建第二行。
        b.Entity<ApplicationFavorite>(e =>
        {
            e.ToTable("application_favorites");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.ApplicationId).IsRequired();
            e.HasIndex(x => new { x.UserId, x.ApplicationId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
            e.HasOne<JobApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        // COD-10a/票14:按投递自备问题。归属从所属投递盖章(同沟通记录口径),
        // 查询过滤器与 Handler 谓词双保险。
        b.Entity<ApplicationQuestion>(e =>
        {
            e.ToTable("application_questions");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.ApplicationId).IsRequired();
            e.Property(x => x.Text).HasMaxLength(4000).IsRequired();
            e.Property(x => x.SortOrder).IsRequired();
            e.HasIndex(x => new { x.UserId, x.ApplicationId });
            e.HasIndex(x => x.ApplicationId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
            e.HasOne<JobApplication>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
