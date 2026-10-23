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
    // ★ 2026-09-27(Forrest):用户在"我的账户"里自选的本机录音存储根目录。
    public DbSet<UserStorageSetting> StorageSettings => Set<UserStorageSetting>();
    // ★ 2026-09-25(Forrest):练习类别 —— 素材树按场景过滤的下拉数据源。
    public DbSet<PracticeCategory> Categories => Set<PracticeCategory>();

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

            // ★ 修复票 66:软删除过滤器 —— 与 PracticeMaterial(下方)同口径。
            //   MarkDeleted() 只置 IsDeleted,此前本实体漏配过滤器,已删会话仍能被
            //   GET/列表/统计读到、删后写路径也照常成功。配上后所有查询默认只看
            //   未删会话(写路径先查会话→查不到即 404);排查/恢复时 IgnoreQueryFilters()。
            e.HasQueryFilter(x => !x.IsDeleted);

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
            // ★ 票108 A09:题目行补 xmin 并发令牌(PG 系统列,零迁移;票101
            //   Interviews 题目行同先例)—— answer/score/skip 钉版防串行双写覆盖。
            e.UseXminAsConcurrencyToken();
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

            // ★ 第三十九轮:软删除过滤器 —— 所有查询默认只看未删节点。
            //   配合实体上的 MarkDeleted(),整树保存不再硬删数据。
            //   排查/恢复时可 IgnoreQueryFilters() 看到全部行。
            e.HasQueryFilter(x => !x.IsDeleted);

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

        // ★ 2026-09-25(Forrest):练习类别。
        //   素材挂类别用 SetNull:删类别只摘标签,素材一行不丢(自动回"未分类")。
        b.Entity<PracticeCategory>(e =>
        {
            e.ToTable("practice_categories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            // ★ 票87 D2 对齐:快照/存量库该列为 varchar(20),模型此前无长度配置(约定 text)
            //   致 has-pending 恒报漂移。补 HasMaxLength(20) 使模型=快照=存量库,零 DB 变更;
            //   取值域封闭(null 或 "techstack",领域层 SetLinkedSource 硬校验),20 永不触顶。
            e.Property(x => x.LinkedSource).HasMaxLength(20);

            // 同一用户内类别名唯一(忽略大小写靠应用层归一化,这里保证精确唯一)
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.SortOrder });
            e.UseXminAsConcurrencyToken();
        });

        b.Entity<PracticeMaterial>()
            .HasOne<PracticeCategory>()
            .WithMany()
            .HasForeignKey(x => x.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        b.Entity<PracticeRecording>(e =>
        {
            e.ToTable("practice_recordings");
            e.HasKey(x => x.Id);
            e.Property(x => x.StoragePath).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.SourceLanguage).HasMaxLength(10).IsRequired();

            // ★ 第五十二轮(2026-09-17,Forrest 明确要求):录音改为**硬删除** ——
            //   删一条录音 = 数据库行消失 + 磁盘音频文件消失,不留痕。
            //   见 DeleteRecordingCommandHandler(先删文件,后删行)。
            //   录音是练习素材不是业务凭证;保留孤儿文件只会占盘并让用户困惑。
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

            // ★ 票108 A10:评分行补 xmin 并发令牌(PG 系统列,零迁移;
            //   票101 J19/I06 子行先例)—— 重评分钉版防串行双写静默覆盖。
            e.UseXminAsConcurrencyToken();

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

        // ★ 2026-09-27(Forrest):本机录音存储根目录(一人一条)
        b.Entity<UserStorageSetting>(e =>
        {
            e.ToTable("practice_storage_settings");
            e.HasKey(x => x.UserId);
            // 路径用 1000 上限:macOS 的 PATH_MAX 是 1024,深目录也要装得下
            e.Property(x => x.RootPath).HasMaxLength(1000).IsRequired();
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
        // ★ 票69:先 DetectChanges 让子实体状态物化 —— 本钩子跑在 base.SaveChanges
        //   的变更检测之前,未经检测时新子实体可能尚未被跟踪/状态未定,下面的
        //   Detached / Modified 修正都会漏掉它(原 Detached 分支失效的原因之一)。
        //   DetectChanges 在保存流程里本就会执行且幂等,提前跑不改变语义。
        ChangeTracker.DetectChanges();

        // 只处理"聚合内新建的子实体":它们的 Id 是新建的、但导航属性让 EF 认为是已存在。
        // 这里按类型精确处理,比通用反射更可预测(通用方案容易误标 Unchanged 的实体)。
        foreach (var entry in ChangeTracker.Entries<MockQuestion>().ToList())
            if (entry.State == EntityState.Detached) entry.State = EntityState.Added;

        foreach (var entry in ChangeTracker.Entries<DimensionIssue>().ToList())
            if (entry.State == EntityState.Detached) entry.State = EntityState.Added;

        // ★ 票69:MockQuestion / DimensionIssue 的 Guid 主键在构造函数里预赋值,
        //   EF 在「已跟踪的父聚合上新增子实体」时还会把它们误判成 Modified
        //   (实测状态转储:全新会话加题,两道新题均为 Modified)—— 生成 UPDATE
        //   命中 0 行 → DbUpdateConcurrencyException → 被全局映射成 409,
        //   于是「建会话后首加题」恒回并发冲突。上面的 Detached 分支抓不到这种。
        //   判定口径与 BuildingBlocks.NewChildEntityFixer 一致:直接查库,行不存在
        //   才是真新建 → 标 Added;行已存在 = 真改字段,保持 Modified(xmin 真冲突
        //   仍走 UPDATE 0 行 → 409,票07 口径不回退)。只限这两类子实体,
        //   不套用共享 fixer 的全上下文版 —— PracticeMaterial 等其他实体的
        //   保存行为不在本票改动面内。
        FlipModifiedNewChild<MockQuestion>();
        FlipModifiedNewChild<DimensionIssue>();

        // 2026-09-15 第十七轮:PatchScore() 只加子实体而不改聚合根状态,
        // 不显式标 Added 就会被 EF 当成"已存在",不写 INSERT —— 这是本项目踩过的坑。
        foreach (var entry in ChangeTracker.Entries<PracticeRecordingScore>().ToList())
            if (entry.State == EntityState.Detached) entry.State = EntityState.Added;
    }

    /// <summary>
    /// 票69:把「被 EF 误判成 Modified 的新子实体」标回 Added。
    /// 只翻库里确实没有这一行的条目;真被修改的既有实体(RowExists=true)保持 Modified。
    /// </summary>
    private void FlipModifiedNewChild<TEntity>() where TEntity : class
    {
        foreach (var entry in ChangeTracker.Entries<TEntity>().ToList())
        {
            if (entry.State != EntityState.Modified) continue;
            if (entry.Metadata.FindPrimaryKey() is null) continue;

            var keyProps = entry.Metadata.FindPrimaryKey()!.Properties;
            if (keyProps.Count != 1 || keyProps[0].ClrType != typeof(Guid)) continue;

            var id = (Guid)entry.Property(keyProps[0].Name).CurrentValue!;
            if (id == Guid.Empty) continue;

            if (!RowExists(entry.Metadata.GetTableName(),
                    entry.Metadata.GetSchema() ?? Schema, keyProps[0].Name, id))
                entry.State = EntityState.Added;
        }
    }

    /// <summary>
    /// 原生 SQL 查行存在性(不经 EF 跟踪,避免递归)。查询失败时按「已存在」处理:
    /// 宁可维持原状态报并发错,也不要错误地重复 INSERT(同 NewChildEntityFixer 口径)。
    /// </summary>
    private bool RowExists(string? table, string schema, string keyColumn, Guid id)
    {
        if (table is null) return true;
        try
        {
            var conn = Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT 1 FROM \"{schema}\".\"{table}\" WHERE \"{keyColumn}\" = @id LIMIT 1";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = id;
            cmd.Parameters.Add(p);

            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return true;
        }
    }
}
