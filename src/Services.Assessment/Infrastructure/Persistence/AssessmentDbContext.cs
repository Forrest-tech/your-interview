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

    // ---- /practice 页的数据(2026-09-15 第十七轮新增)----
    // 此前这些数据全在浏览器 localStorage 里,换机器就丢;现在落库。
    public DbSet<PracticeMaterial> Materials => Set<PracticeMaterial>();
    public DbSet<PracticeRecording> Recordings => Set<PracticeRecording>();
    public DbSet<PracticeRecordingScore> RecordingScores => Set<PracticeRecordingScore>();
    public DbSet<SpeechSetting> SpeechSettings => Set<SpeechSetting>();

    // ---- 示范朗读音频缓存(2026-09-16 第三十一轮)----
    // 文本没改就回放上次合成的 MP3,不再重复消耗 Azure TTS 额度。
    public DbSet<PracticeTtsCache> TtsCache => Set<PracticeTtsCache>();

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

        // ============================================================
        // /practice 页的四张表(2026-09-15 第十七轮)
        // ============================================================

        b.Entity<PracticeMaterial>(e =>
        {
            e.ToTable("practice_materials");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(10).IsRequired();
            e.Property(x => x.Content).HasColumnType("text");

            // 自引用邻接表:删父节点时级联删子节点。
            // 树是用户资产,删除必须是显式动作,但删了就该连根走 ——
            // 留一堆孤儿节点比删不干净更糟。
            e.HasOne<PracticeMaterial>()
                .WithMany()
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Cascade);

            // 列表查询永远是"我的树",所以 (UserId, ParentId, SortOrder) 是主查询路径
            e.HasIndex(x => new { x.UserId, x.ParentId, x.SortOrder });
            e.UseXminAsConcurrencyToken();
        });

        b.Entity<PracticeRecording>(e =>
        {
            e.ToTable("practice_recordings");
            e.HasKey(x => x.Id);
            e.Property(x => x.StoragePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceLanguage).HasMaxLength(10).IsRequired();

            // ⚠️ 只软删录音的元数据行;磁盘文件由后台清理(见 IAudioStore)。
            // 硬删会让"删错了想找回"变成不可能。
            e.HasIndex(x => new { x.UserId, x.MaterialId, x.CreatedAt });
            e.UseXminAsConcurrencyToken();

            // 1:1 → 一条录音最多一份评分。评分行随录音一起删。
            e.HasOne(x => x.Score)
                .WithOne()
                .HasForeignKey<PracticeRecordingScore>(x => x.RecordingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PracticeRecordingScore>(e =>
        {
            e.ToTable("practice_recording_scores");
            // 主键即外键:结构上就杜绝"一条录音两份评分"
            e.HasKey(x => x.RecordingId);
            e.Property(x => x.RecognizedText).HasColumnType("text").IsRequired();
            e.Property(x => x.WordsJson).HasColumnType("jsonb");
            e.Property(x => x.AzureRegion).HasMaxLength(50).IsRequired();
            e.Property(x => x.ReferenceText).HasColumnType("text").IsRequired();

            // 拿不到的维度就是 NULL —— 数据库层也不给它默认值,防止有人偷偷塞 0
            e.Property(x => x.PronScore);
            e.Property(x => x.AccuracyScore);
            e.Property(x => x.FluencyScore);
            e.Property(x => x.CompletenessScore);
            e.Property(x => x.ProsodyScore);
        });

        b.Entity<SpeechSetting>(e =>
        {
            e.ToTable("speech_settings");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Key).HasColumnType("text").IsRequired();
            e.Property(x => x.Region).HasMaxLength(50).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(500);
        });

        // 示范朗读音频缓存(2026-09-16 第三十一轮)
        b.Entity<PracticeTtsCache>(e =>
        {
            e.ToTable("practice_tts_cache");
            // ⚠️ 主键是 (UserId, CacheKey) 复合键 —— 不是单列。
            //    为什么:同一个文本在不同用户的 key/音色下可以各自缓存一份,
            //    单列主键会让用户 B 的命中覆盖用户 A 的音频文件。
            e.HasKey(x => new { x.UserId, x.CacheKey });
            e.Property(x => x.CacheKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.StoragePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.TextHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Voice).HasMaxLength(120).IsRequired();
            e.Property(x => x.Language).HasMaxLength(10).IsRequired();

            // 主查询路径:按用户 + 文本指纹找缓存(统计用)
            e.HasIndex(x => new { x.UserId, x.TextHash });
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

        // 2026-09-15 第十七轮:PatchScore() 只加子实体而不改聚合根状态,
        // 不显式标 Added 就会被 EF 当成"已存在",不写 INSERT —— 这是本项目踩过的坑。
        foreach (var entry in ChangeTracker.Entries<PracticeRecordingScore>().ToList())
            if (entry.State == EntityState.Detached) entry.State = EntityState.Added;
    }
}
