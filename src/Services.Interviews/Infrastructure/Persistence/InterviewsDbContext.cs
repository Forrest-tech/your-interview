using System.Linq;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Domain;
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

    /// <summary>
    /// 把"新加进聚合子集合、但 Id 由 C# 端生成"的子实体显式标成 Added。
    ///
    /// 背景:聚合根的 Guid 主键在构造函数里就赋值了。当父聚合被跟踪、
    /// 我们往它的子集合里 new 一个新子实体时,EF 的默认判断会出错:
    ///   - 判成 Modified → 生成 UPDATE,而行还不存在 → DbUpdateConcurrencyException(影响 0 行)
    ///   - 判成 Added 但行已存在 → PK 冲突(23505)
    ///
    /// ⚠️ 不要用启发式去猜(主键是否为 Empty、原值是否为空等) ——
    /// 这些在"已有实体被改字段"与"新实体刚加入"两种情况下长得一模一样,
    /// 猜错就是 500。唯一可靠的信号是 EF 自己的元数据:IsKeySet + 该类型是否
    /// 配置了数据库生成主键。我们的主键 100% 由 C# 生成(从不走高自增),
    /// 所以 "状态是 Modified 且 EF 认为主键未设" 才可能是新实体。
    ///
    /// 最终可靠做法:直接问数据库这行在不在。子实体数量极少,不会 N+1。
    /// </summary>
    private void MarkNewChildrenAsAdded()
    {
        foreach (var entry in ChangeTracker.Entries()
                     .Where(e => e.State == EntityState.Modified))
        {
            if (entry.Metadata.FindPrimaryKey() is null) continue;

            // 主键属性值
            var keyProps = entry.Metadata.FindPrimaryKey()!.Properties;
            if (keyProps.Count != 1) continue;
            if (keyProps[0].ClrType != typeof(Guid)) continue;

            var id = (Guid)entry.Property(keyProps[0].Name).CurrentValue!;
            if (id == Guid.Empty) continue;

            // 直接查库:这一行到底存不存在?
            var exists = QueryRawRowExists(entry, keyProps[0].Name, id);
            if (!exists)
            {
                // 库里没有这行 → 它是本次新建的(被 EF 误判成 Modified)
                entry.State = EntityState.Added;
            }
            // 库里已经有这行 → 真的是改字段,保持 Modified
        }
    }

    private bool QueryRawRowExists(EntityEntry entry, string keyName, Guid id)
    {
        var table = entry.Metadata.GetTableName();
        var schema = entry.Metadata.GetSchema() ?? Schema;
        if (table is null) return true;

        // 用原生 SQL 查存在性 —— 不经过 EF 实体验证,避免触发递归跟踪
        try
        {
            var conn = Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM \"{schema}\".\"{table}\" WHERE \"{keyName}\" = @id LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = id;
            cmd.Parameters.Add(p);

            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            // 查不到就当已存在(保持原状态)—— 宁可报并发错也不要错误地插重复行
            return true;
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
            e.Property(x => x.SourceLanguage).HasMaxLength(20);
            e.Property(x => x.TranscriptText).HasMaxLength(500000);
            e.Property(x => x.TranscriptSegmentsJson).HasColumnType("text");
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.InterviewEntryId);
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
    }
}
