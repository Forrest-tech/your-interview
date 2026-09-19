using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Domain;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.AiGateway.Domain;

namespace YourInterview.Services.AiGateway.Infrastructure.Persistence;

/// <summary>
/// AiGateway 数据库上下文 —— 独占 schema "aigateway"。
///
/// 只存一类东西:用户级 LLM 连接配置。
/// 不做别的 —— AI 网关的职责边界就是"凭据保管 + 转发补全请求"。
/// </summary>
public sealed class AiGatewayDbContext(DbContextOptions<AiGatewayDbContext> options)
    : DbContext(options)
{
    public const string Schema = "aigateway";

    public DbSet<AiSetting> AiSettings => Set<AiSetting>();
    public DbSet<AiCallLog> CallLogs => Set<AiCallLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<AiSetting>(e =>
        {
            e.ToTable("ai_settings");
            e.HasKey(x => x.UserId);
            e.Property(x => x.Protocol).HasMaxLength(50).IsRequired();
            // key 用 text:不同厂商 key 长度差异大(有的带长前缀),
            // 限长会造成"保存失败但不知道为什么"。
            e.Property(x => x.ApiKey).HasColumnType("text").IsRequired();
            e.Property(x => x.BaseUrl).HasMaxLength(1024);
            e.Property(x => x.Model).HasMaxLength(200).IsRequired();
            e.Property(x => x.Endpoint).HasMaxLength(1024);
            e.Property(x => x.ApiVersion).HasMaxLength(50);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });

        // 调用日志:只记元数据,绝不记 prompt 全文与 key。
        // 用途:排查"哪个用户在什么时候调了多少次",以及 OpenAI 式的用量审计。
        b.Entity<AiCallLog>(e =>
        {
            e.ToTable("call_logs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.Property(x => x.Purpose).HasMaxLength(100).IsRequired();
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
        });
    }
}

/// <summary>
/// 一次 LLM 调用记录(成功或失败)。
///
/// ⚠️ 隐私红线:绝不存 prompt 正文与 API key。
///    只记"谁、什么用途、哪个模型、多少 token、耗时、成功与否"。
///    原因:prompt 里会带简历/JD 等内容,存下来等于把用户资料再复制一份到日志表。
/// </summary>
public sealed class AiCallLog
{
    private AiCallLog() { }

    public static AiCallLog Success(Guid userId, string purpose, string model,
        int? promptTokens, int? completionTokens, TimeSpan? latency)
    {
        return new AiCallLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            Model = model,
            Succeeded = true,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            LatencyMs = latency is null ? null : (int)latency.Value.TotalMilliseconds,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static AiCallLog Failure(Guid userId, string purpose, string model, string errorMessage)
    {
        return new AiCallLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Purpose = purpose,
            Model = model,
            Succeeded = false,
            ErrorMessage = errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>用途标签(cover-letter / prep-pack / knowledge-digest…)。用于按功能统计用量。</summary>
    public string Purpose { get; private set; } = string.Empty;

    public string? Model { get; private set; }
    public bool Succeeded { get; private set; }
    public int? PromptTokens { get; private set; }
    public int? CompletionTokens { get; private set; }
    public int? LatencyMs { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
