using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Domain;

namespace YourInterview.Services.Assessment.Infrastructure.Persistence;

/// <summary>
/// Assessment 服务的数据库上下文(独占 PostgreSQL schema "assessment")。
///
/// 为什么每服务一个 schema:跨服务直接查表是最常见的架构腐化路径。
/// 把边界做进数据库层,想越界就得显式改连接字符串 —— 让违规变难,而不是靠自觉。
/// </summary>
public sealed class AssessmentDbContext(DbContextOptions<AssessmentDbContext> options)
    : DbContext(options)
{
    public const string Schema = "assessment";

    public DbSet<MockSession> Sessions => Set<MockSession>();
    public DbSet<MockQuestion> Questions => Set<MockQuestion>();
    public DbSet<DimensionIssue> Issues => Set<DimensionIssue>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<MockSession>(e =>
        {
            e.ToTable("mock_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.Topic).HasMaxLength(200);
            e.Property(x => x.CompanyStyle).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(10).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.OverallSummary).HasColumnType("text");
            e.Property(x => x.PriorityAction).HasColumnType("text");

            // 一个会话下的题目:cascade 删除 —— 题目不可能脱离会话独立存在
            e.HasMany(x => x.Questions)
                .WithOne()
                .HasForeignKey(q => q.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.UserId, x.StartedAt });
            e.HasIndex(x => x.Status);

            e.UseXminAsConcurrencyToken();
        });

        b.Entity<MockQuestion>(e =>
        {
            e.ToTable("mock_questions");
            e.HasKey(x => x.Id);
            e.Property(x => x.QuestionText).HasColumnType("text").IsRequired();
            e.Property(x => x.ExpectedPointsJson).HasColumnType("jsonb");
            e.Property(x => x.AnswerText).HasColumnType("text");
            e.Property(x => x.AnswerTranscriptJson).HasColumnType("jsonb");
            e.Property(x => x.AnswerAudioPath).HasMaxLength(1000);
            e.Property(x => x.Comment).HasColumnType("text");
            e.Property(x => x.RecommendedAnswer).HasColumnType("text");
            e.Property(x => x.BetterStructure).HasColumnType("text");
            e.Property(x => x.FillerWordsJson).HasColumnType("jsonb");
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);

            // 六维评分作为 owned type 内联进同一张表 ——
            // 它没有独立生命周期,也不跨题复用,单独建表只会增加 join 成本。
            e.OwnsOne(x => x.Scores, s =>
            {
                s.Property(p => p.Pronunciation).HasColumnName("score_pronunciation");
                s.Property(p => p.Fluency).HasColumnName("score_fluency");
                s.Property(p => p.SentenceIntegrity).HasColumnName("score_sentence_integrity");
                s.Property(p => p.Structure).HasColumnName("score_structure");
                s.Property(p => p.TechnicalDepth).HasColumnName("score_technical_depth");
                s.Property(p => p.Relevance).HasColumnName("score_relevance");
            });

            e.HasMany(x => x.Issues)
                .WithOne()
                .HasForeignKey(i => i.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.SessionId, x.Sequence });
        });

        b.Entity<DimensionIssue>(e =>
        {
            e.ToTable("dimension_issues");
            e.HasKey(x => x.Id);
            e.Property(x => x.Dimension).HasMaxLength(50).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.Detail).HasColumnType("text");
            e.Property(x => x.Evidence).HasColumnType("text");
            e.Property(x => x.Suggestion).HasColumnType("text");
            e.HasIndex(x => x.Dimension);
        });
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // jsonb 里的中文不转义 —— 数据库里直接可读,排查问题时省一步
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
        base.OnConfiguring(optionsBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // 新增的子实体在聚合根状态为 Unchanged 时必须手动标 Added,
        // 否则 EF 会当作"已存在的实体"而不写 INSERT。
        MarkNewChildrenAsAdded();
        return base.SaveChangesAsync(ct);
    }

    private void MarkNewChildrenAsAdded()
    {
        // 只处理"聚合内新建的子实体":它们的 Id 是新建的、但导航属性让 EF 认为是已存在。
        // 这里按类型精确处理,比通用反射更可预测(通用方案容易误标 Unchanged 的实体)。
        foreach (var entry in ChangeTracker.Entries<MockQuestion>().ToList())
            if (entry.State == EntityState.Detached) entry.State = EntityState.Added;

        foreach (var entry in ChangeTracker.Entries<DimensionIssue>().ToList())
            if (entry.State == EntityState.Detached) entry.State = EntityState.Added;
    }
}
