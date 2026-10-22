using System.Linq;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Infrastructure.Persistence;

/// <summary>
/// 实战机经的持久化上下文。
/// 每个微服务独占一个 PostgreSQL schema("interviews"),服务间不共享表 —— 微服务的核心纪律。
/// </summary>
public sealed class InterviewsDbContext(
    DbContextOptions<InterviewsDbContext> options,
    ICurrentUser? currentUser = null) : DbContext(options)
{
    public const string Schema = "interviews";

    /// <summary>
    /// 当前请求用户(COD-01/票03 全局过滤器用)。无 HTTP 上下文(消费者/后台)时为 null,
    /// 过滤器 fail-closed 匹配零行 —— 非请求路径必须 IgnoreQueryFilters + 事件 UserId 显式校验。
    /// </summary>
    public Guid? CurrentUserId { get; } = currentUser?.UserId;

    public DbSet<InterviewEntry> Entries => Set<InterviewEntry>();
    public DbSet<InterviewAsset> Assets => Set<InterviewAsset>();
    public DbSet<InterviewQuestion> Questions => Set<InterviewQuestion>();
    public DbSet<InterviewWeakness> Weaknesses => Set<InterviewWeakness>();
    public DbSet<InterviewRound> Rounds => Set<InterviewRound>();
    public DbSet<GeneratedMaterial> GuidanceMaterials => Set<GeneratedMaterial>();

    /// <summary>待办事项(A2)。</summary>
    public DbSet<InterviewAction> Actions => Set<InterviewAction>();

    /// <summary>分析快照(A3):每次分析成功回写记一条。</summary>
    public DbSet<AnalysisSnapshot> AnalysisSnapshots => Set<AnalysisSnapshot>();

    /// <summary>分析任务台账(发件箱)。派发器与回写端点共用。</summary>
    public DbSet<AnalysisJob> AnalysisJobs => Set<AnalysisJob>();

    // ---- COD-10b/票15:Playbook 本地数据上后端(原 localStorage 孤岛)----
    /// <summary>STAR 故事库(用户级全局列表,原 pb.starStories)。</summary>
    public DbSet<StarStory> StarStories => Set<StarStory>();

    /// <summary>历史复盘孤儿键归档(原 pb.debrief.&lt;条目id&gt;,原始 JSON 逐字)。</summary>
    public DbSet<LegacyDebrief> LegacyDebriefs => Set<LegacyDebrief>();

    /// <summary>历史粘贴材料孤儿键归档(原 pb-pasted-&lt;条目id&gt;,原文逐字)。</summary>
    public DbSet<LegacyPastedMaterial> LegacyPastedMaterials => Set<LegacyPastedMaterial>();

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
            e.Property(x => x.SpeechMetricsJson).HasColumnType("text");
            e.Property(x => x.CandidateTranscript).HasColumnType("text");
            e.Property(x => x.QuestionsToAskJson).HasColumnType("text");

            // 枚举存字符串:数据库可读性远高于魔法数字,加新枚举值也不会错位
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);

            e.HasIndex(x => x.CompanyId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.InterviewDate);
            e.HasIndex(x => x.CompanyName);

            e.HasIndex(x => x.UserId);
            e.Property(x => x.UserId).IsRequired();   // COD-01 contract:回填后收紧为非空
            // PostgreSQL 原生 xmin 做乐观并发令牌,不需要额外 rowversion 列
            e.UseXminAsConcurrencyToken();
            // 票19 ARC-06:Json 大字段 backing field 存版本信封,EF 直读字段;属性 getter/setter 过 JsonFieldAdapter(列不变,无迁移)
            e.Metadata.FindProperty(nameof(InterviewEntry.DebriefJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewEntry.QuestionsToAskJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewEntry.SpeechMetricsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);

            e.HasMany(x => x.Assets).WithOne().HasForeignKey(a => a.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(q => q.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Weaknesses).WithOne().HasForeignKey(w => w.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Rounds).WithOne().HasForeignKey(r => r.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Materials).WithOne().HasForeignKey(m => m.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Actions).WithOne().HasForeignKey(a => a.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Snapshots).WithOne().HasForeignKey(s => s.InterviewEntryId)
                .OnDelete(DeleteBehavior.Cascade);

            // 集合用字段访问模式 + 只读暴露,保证外部不能绕过领域方法直接改集合(封装不变量)
            e.Metadata.FindNavigation(nameof(InterviewEntry.Assets))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Questions))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Weaknesses))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Rounds))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Materials))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Actions))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindNavigation(nameof(InterviewEntry.Snapshots))!
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
            // 2026-10-20 详情页 v2:材料元数据(解析字段/摘要/处理状态等),前端约定结构。
            e.Property(x => x.MetaJson).HasColumnType("text");
            // 票19 ARC-06:Json 大字段 backing field 存版本信封,EF 直读字段;属性 getter/setter 过 JsonFieldAdapter(列不变,无迁移)
            e.Metadata.FindProperty(nameof(InterviewAsset.TranscriptSegmentsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewAsset.MetaJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            // 2026-10-19:资产按 Round 管理。历史材料 RoundId/Phase 保持 null,不强制回填。
            e.Property(x => x.RoundId);
            e.Property(x => x.Phase).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => new { x.InterviewEntryId, x.RoundId });
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
            e.Property(x => x.Mastery).HasDefaultValue(0);
            // 2026-10-20 详情页 v2:题目归轮 + 提问人 + 逐题复盘 JSON。
            e.Property(x => x.RoundId);
            e.Property(x => x.AskedBy).HasMaxLength(200);
            e.Property(x => x.ReviewJson).HasColumnType("text");
            // 票19 ARC-06:Json 大字段 backing field 存版本信封,EF 直读字段;属性 getter/setter 过 JsonFieldAdapter(列不变,无迁移)
            e.Metadata.FindProperty(nameof(InterviewQuestion.ReviewJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewQuestion.WeaknessTagsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewQuestion.FollowUpQuestionsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewQuestion.MissedPointsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
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

        b.Entity<InterviewRound>(e =>
        {
            e.ToTable("rounds");
            e.HasKey(x => x.Id);
            e.Property(x => x.Stage).HasMaxLength(50).IsRequired();
            e.Property(x => x.Interviewers).HasMaxLength(1000);
            e.Property(x => x.Format).HasMaxLength(50);
            e.Property(x => x.Location).HasMaxLength(300);
            e.Property(x => x.Notes).HasMaxLength(20000);
            e.Property(x => x.Feedback).HasMaxLength(20000);
            e.Property(x => x.MeetingLink).HasMaxLength(2000);
            e.Property(x => x.ScheduledTime).HasMaxLength(10);
            e.Property(x => x.PrepQuestionsJson).HasColumnType("text");
            e.Property(x => x.EmailsJson).HasColumnType("text");
            e.Property(x => x.Transcript).HasColumnType("text");
            // 2026-10-20 详情页 v2:整轮复盘汇总 + 结构化备考报告。
            e.Property(x => x.ReviewJson).HasColumnType("text");
            e.Property(x => x.PrepReportJson).HasColumnType("text");
            // 票19 ARC-06:Json 大字段 backing field 存版本信封,EF 直读字段;属性 getter/setter 过 JsonFieldAdapter(列不变,无迁移)
            e.Metadata.FindProperty(nameof(InterviewRound.PrepQuestionsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewRound.EmailsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewRound.ChecklistJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewRound.ReviewJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(InterviewRound.PrepReportJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Property(x => x.RecordingUrl).HasMaxLength(2000);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => new { x.InterviewEntryId, x.Order }).IsUnique();
        });

        b.Entity<GeneratedMaterial>(e =>
        {
            e.ToTable("guidance_materials");
            e.HasKey(x => x.Id);
            e.Property(x => x.ContentMarkdown).HasColumnType("text").IsRequired();
            e.Property(x => x.Model).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => new { x.InterviewEntryId, x.Version }).IsUnique();
        });

        b.Entity<InterviewAction>(e =>
        {
            e.ToTable("actions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            e.Property(x => x.Detail).HasMaxLength(8000);
            e.HasIndex(x => x.InterviewEntryId);
        });

        b.Entity<AnalysisSnapshot>(e =>
        {
            e.ToTable("analysis_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.DimensionsJson).HasColumnType("text");
            e.Property(x => x.SummaryBulletsJson).HasColumnType("text");
            // 票19 ARC-06:Json 大字段 backing field 存版本信封,EF 直读字段;属性 getter/setter 过 JsonFieldAdapter(列不变,无迁移)
            e.Metadata.FindProperty(nameof(AnalysisSnapshot.DimensionsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.Metadata.FindProperty(nameof(AnalysisSnapshot.SummaryBulletsJson))!.SetPropertyAccessMode(PropertyAccessMode.Field);
            e.HasIndex(x => x.InterviewEntryId);
            e.HasIndex(x => new { x.InterviewEntryId, x.CreatedAt });
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

        // ---- COD-10b/票15:Playbook 本地数据(票03 归属口径与其它实体一致)----

        b.Entity<StarStory>(e =>
        {
            e.ToTable("star_stories");
            e.HasKey(x => x.Id);
            e.Property(x => x.ClientId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500).IsRequired();
            // S/T/A/R 是段落文本:text 列不限长,长度口径由应用层校验把关
            // (StarStoryLimits.FieldMax,超限 400 而非落库 500;同 JdText 先例)。
            e.Property(x => x.Situation).HasColumnType("text");
            e.Property(x => x.Task).HasColumnType("text");
            e.Property(x => x.Action).HasColumnType("text");
            e.Property(x => x.Result).HasColumnType("text");
            e.Property(x => x.Tags).HasMaxLength(1000);
            e.Property(x => x.UserId).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.ClientId }).IsUnique();
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
        });

        b.Entity<LegacyDebrief>(e =>
        {
            e.ToTable("legacy_debriefs");
            e.HasKey(x => x.Id);
            // 原始 JSON 逐字归档:text 列不限长,上限由导入校验把关。
            e.Property(x => x.PayloadJson).HasColumnType("text").IsRequired();
            e.Property(x => x.UserId).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.EntryId }).IsUnique();
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
        });

        b.Entity<LegacyPastedMaterial>(e =>
        {
            e.ToTable("legacy_pasted_materials");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).HasColumnType("text").IsRequired();
            e.Property(x => x.UserId).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.UserId, x.EntryId }).IsUnique();
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted && x.UserId == CurrentUserId);
        });
    }
}
