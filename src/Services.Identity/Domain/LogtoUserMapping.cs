namespace YourInterview.Services.Identity.Domain;

/// <summary>
/// 票241:原 Identity 用户 Guid ↔ Logto 用户 ID 的迁移映射表。
/// 唯一用途是回答“这个 Logto 用户是不是迁移来的老用户”:
///   · User.Created 审批门收到事件时先查本表 —— 在表里 = 迁移用户,
///     直接放行,绝不置停用、绝不打 pending(没有这道 guard,批量导入会
///     触发接收端把刚导入的 admin 自己停用);
///   · 对账扫表用本表区分“丢了注册事件的新用户”(不在表里)与迁移用户。
/// 映射行只由迁移脚本写入(导入成功后),审批门与扫表只读本表、永不写入
/// 新注册用户的行 —— 新注册用户不在表里,正是扫表识别它们的依据。
/// 审批状态本身不在本表:它在 Logto 用户 customData 的 approval 字段
/// (pending / approved / migrated 三态,见 LogtoApprovalGate)。
/// 普通类(不继承 Entity):ChangeTracker 的 Entity 拦截器与审计拦截器
/// 只认 AppUser/AppRole/RefreshToken,映射行不进领域事件与自动审计。
/// </summary>
public sealed class LogtoUserMapping
{
    private LogtoUserMapping() { } // EF Core

    public LogtoUserMapping(string logtoUserId, Guid legacyUserId, string email)
    {
        LogtoUserId = logtoUserId;
        LegacyUserId = legacyUserId;
        Email = email;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Logto 用户 ID(12 字符短 ID),主键。</summary>
    public string LogtoUserId { get; private set; } = string.Empty;

    /// <summary>原 Identity AppUser.Id。</summary>
    public Guid LegacyUserId { get; private set; }

    /// <summary>迁移时的邮箱(对账用,不作唯一键:邮箱可改,主键是 LogtoUserId)。</summary>
    public string Email { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }
}
