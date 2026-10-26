using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Knowledge.Domain;

namespace YourInterview.Services.Knowledge.Infrastructure.Persistence;

/// <summary>
/// Knowledge 服务 DbContext。
/// 独立 schema "knowledge" —— 对应 database-per-service:服务之间不共享表,
/// 谁的表谁改 schema,不再出现"改一列全公司排队"的老共享库问题。
/// </summary>
public sealed class KnowledgeDbContext(
    DbContextOptions<KnowledgeDbContext> options,
    ICurrentUser? currentUser = null) : DbContext(options)
{
    public const string Schema = "knowledge";

    /// <summary>
    /// 当前请求用户(COD-01/票03 全局过滤器用)。无 HTTP 上下文时为 null,
    /// 过滤器 fail-closed 匹配零行 —— 非请求路径必须 IgnoreQueryFilters + 显式归属校验。
    /// </summary>
    public Guid? CurrentUserId { get; } = currentUser?.UserId;

    public DbSet<KnowledgeItem> Items => Set<KnowledgeItem>();
    public DbSet<KnowledgeReviewLog> ReviewLogs => Set<KnowledgeReviewLog>();
    public DbSet<KnowledgeRelation> Relations => Set<KnowledgeRelation>();

    // ---- COD-10c/票16:Tech Stack 本地数据(原 localStorage 孤岛上后端)----
    public DbSet<TechStackCategory> CustomCategories => Set<TechStackCategory>();
    public DbSet<ReviewActivityDay> ReviewActivities => Set<ReviewActivityDay>();

    /// <summary>
    /// 把"新加进聚合子集合、但 Id 由 C# 端生成"的子实体显式标成 Added。
    ///
    /// 踩过的坑(与 Jobs 服务同一个,务必照搬):EF 对「已跟踪父聚合上新增的子实体」,
    /// 会按主键是否已赋值来判断状态。我们的 Guid 主键在 C# 构造函数里就赋值了
    /// → EF 误判为 Modified → 生成 UPDATE,而该行其实不存在 → 影响 0 行
    /// → DbUpdateConcurrencyException。
    ///
    /// 判定依据:实体在数据库里"是否存在过"。凡是处于 Modified 却从未被真正加载过的子实体,
    /// 都应该是 Added。这里用 ChangeTracker 里子实体的状态做兜底修正。
    /// </summary>
    private void MarkNewChildrenAsAdded()
    {
        foreach (var entry in ChangeTracker.Entries<KnowledgeReviewLog>().ToList())
        {
            if (entry.State == EntityState.Modified) entry.State = EntityState.Added;
        }
        foreach (var entry in ChangeTracker.Entries<KnowledgeRelation>().ToList())
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

        b.Entity<KnowledgeItem>(e =>
        {
            e.ToTable("knowledge_items");
            e.HasKey(x => x.Id);

            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Topic).HasMaxLength(100).IsRequired();
            e.Property(x => x.SubTopic).HasMaxLength(200);
            e.Property(x => x.Question).HasMaxLength(8000);
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.SourceCompanyName).HasMaxLength(300);
            e.Property(x => x.SourceRoundStage).HasMaxLength(60);
            e.Property(x => x.ImportKey).HasMaxLength(200);

            // 反查索引:来源公司 / 来源机经条目 —— 需求 6.4.4 的几个入口都按它们过滤。

            // 幂等导入键:导入候选题时先按它查重,避免重复导入同一条。

            // 内容字段:面试原题/概念讲解可能是很长的 Markdown,给足空间。
            // 用 text 而不是 varchar:PG 里两者性能无差异,但 varchar 上限一旦撞到就丢数据。
            e.Property(x => x.ConceptExplanation).HasColumnType("text");
            e.Property(x => x.MyAnswer).HasColumnType("text");
            e.Property(x => x.BetterAnswer).HasColumnType("text");
            e.Property(x => x.KeyPointsJson).HasColumnType("text");
            e.Property(x => x.CommonMistakesJson).HasColumnType("text");
            e.Property(x => x.FollowUpsJson).HasColumnType("text");
            e.Property(x => x.ReferencesJson).HasColumnType("text");
            e.Property(x => x.SourcesJson).HasColumnType("text");
            e.Property(x => x.TagsJson).HasMaxLength(1000);

            e.Property(x => x.Mastery).HasConversion<string>().HasMaxLength(30);

            // 索引设计说明(面试可讲):
            //  1) (Topic)  —— 左侧目录树 + 按分类过滤
            //  2) (Mastery) —— 统计掌握度分布
            //  3) (NextReviewAt) —— "今天该复习哪些" 是这个服务被调用最频繁的查询,
            //     必须是索引列;配合 Mastery 做复合索引能覆盖"该复习 + 未掌握"的常见组合。
            //  4) (Source) —— 按来源过滤(只看实战机经汇入的题)
            e.HasIndex(x => x.Topic);
            e.HasIndex(x => x.Mastery);
            e.HasIndex(x => x.NextReviewAt);
            e.HasIndex(x => new { x.Mastery, x.NextReviewAt });
            e.HasIndex(x => x.Source);
            e.HasIndex(x => x.SourceInterviewEntryId);
            e.HasIndex(x => x.SourceCompanyName);
            e.HasIndex(x => x.ImportKey);

            e.HasIndex(x => x.UserId);
            e.Property(x => x.UserId).IsRequired();   // COD-01 contract:回填后收紧为非空
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);

            e.HasMany(x => x.ReviewLogs).WithOne().HasForeignKey(r => r.ItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Relations).WithOne().HasForeignKey(r => r.ItemId).OnDelete(DeleteBehavior.Cascade);

            // ⚠️ 子集合走 field 访问模式:不显式 Include 会得到空集合(踩过),详情查询必须 Include。
            e.Metadata.FindNavigation(nameof(KnowledgeItem.ReviewLogs))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(KnowledgeItem.Relations))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<KnowledgeReviewLog>(e =>
        {
            e.ToTable("knowledge_review_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Result).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.HasIndex(x => x.ItemId);
            // 进步曲线/本周复习数按时间倒序取 —— 复合索引让它走 Index Scan 而不是 Sort。
            e.HasIndex(x => new { x.ItemId, x.ReviewedAt });
            e.HasIndex(x => x.ReviewedAt);
        });

        // COD-10c/票16:自定义空分类(用户级占位行)。同一用户同名分类最多一行,
        // 导入/重复 PUT 靠库层兜底幂等;大小写不敏感查重在 Handler 层(存首现原文)。
        // 软删后重加走 Restore,不新建第二行。
        //
        // 票96:唯一语义必须是大小写不敏感 —— 原 (UserId, Name) 唯一索引在
        // Postgres 默认排序下大小写敏感,"React"/"react" 并发同发可双双落库(票92
        // 观察项)。模型层不再声明该大小写敏感唯一索引;真正的约束是迁移里
        // 的 lower(Name) 函数唯一索引(见 CaseInsensitiveCategoryNameUniqueIndex
        // 迁移 Up/Down),与 Handler 层 ToLower 回查同口径。
        b.Entity<TechStackCategory>(e =>
        {
            e.ToTable("tech_stack_categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
        });

        // COD-10c/票16:每日复习活动。(UserId, Date) 唯一 —— 一人一天一行,
        // Record 累加与导入合并都收敛到这一行。Date 为用户本地日历日期(逐字口径)。
        b.Entity<ReviewActivityDay>(e =>
        {
            e.ToTable("review_activities");
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Date).IsRequired();
            e.Property(x => x.Count).IsRequired();
            e.HasIndex(x => new { x.UserId, x.Date }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
        });

        b.Entity<KnowledgeRelation>(e =>
        {
            e.ToTable("knowledge_relations");
            e.HasKey(x => x.Id);
            e.Property(x => x.RelationType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasIndex(x => x.ItemId);
            e.HasIndex(x => x.RelatedItemId);
            // 同一条边只允许存在一次(聚合内也做了去重,这里是数据库层的最后一道闸)
            e.HasIndex(x => new { x.ItemId, x.RelatedItemId, x.RelationType }).IsUnique();
        });
    }
}
