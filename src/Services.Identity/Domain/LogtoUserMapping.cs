namespace YourInterview.Services.Identity.Domain;

/// <summary>
/// 票241:原 Identity 用户 Guid ↔ Logto 用户 ID 的迁移映射表 + 审批门镜像。
/// 位置已定:现有 yourinterview 库 identity schema 内新表 logto_user_mapping。
/// 角色(PLAN §4.2a/§4.4,round-3 决议):
///   · User.Created 接收端守卫:行里 approval=migrated = 迁移用户,直接放行;
///   · 审批门镜像:新注册用户由接收端写入行(approval=pending),置停用 + 打标 +
///     镜像写三步全部成功才回 2xx(任一步失败 → 5xx → 投递方重发);
///   · claims 层共享门:每请求读本表(approval + IsSuspended),未批/停用一律零权限;
///   · 扫表:按 Logto 现态同步本表(approval/IsSuspended),纠一切漂移;
///   · 回滚:Logto-only 用户的业务 Guid linkage 就存在本行(LegacyUserId),
///     回迁时按此 Guid 重建旧账号,业务数据不断链。
/// 普通类(不继承 Entity):ChangeTracker 的 Entity 拦截器与审计拦截器只认
/// AppUser/AppRole/RefreshToken,映射行不进领域事件与自动审计。
/// </summary>
public sealed class LogtoUserMapping
{
    private LogtoUserMapping() { } // EF Core

    public LogtoUserMapping(string logtoUserId, Guid legacyUserId, string email, string approval,
        bool isSuspended, bool suspendedByGate = false, DateTimeOffset? lastEventAt = null)
    {
        LogtoUserId = logtoUserId;
        LegacyUserId = legacyUserId;
        Email = email;
        Approval = approval;
        IsSuspended = isSuspended;
        SuspendedByGate = suspendedByGate;
        LastEventAt = lastEventAt;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Logto 用户 ID(12 字符短 ID),主键。</summary>
    public string LogtoUserId { get; private set; } = string.Empty;

    /// <summary>原 Identity AppUser.Id;新注册用户由审批门分配(见 LogtoApprovalGate)。</summary>
    public Guid LegacyUserId { get; private set; }

    /// <summary>迁移/注册时的邮箱(对账用,不作唯一键:邮箱可改,主键是 LogtoUserId)。</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>审批镜像:pending / approved / migrated 三态(PLAN §4.2a)。</summary>
    public string Approval { get; private set; } = string.Empty;

    /// <summary>停用态镜像:接收端 + 扫表按 Logto 现态同步;claims 层据此拒停用账号。</summary>
    public bool IsSuspended { get; private set; }

    /// <summary>
    /// 停用来源标记(round-4 决议):true = 本次停用由审批门/扫表执行(导入窗口误停的候选),
    /// false = 在 Logto 侧观察到的停用(管理员在 Console 的操作)。扫表“已映射但 pending”
    /// 纠正规则只对 SuspendedByGate=true 的行解除停用;管理员的停用绝不由扫表碰。
    /// Logto 不暴露停用时间戳,updatedAt 又会被每次 PATCH 改动,所以来源不能靠时间推断,
    /// 必须显式记录——这是本列存在的唯一原因。
    /// </summary>
    public bool SuspendedByGate { get; private set; }

    /// <summary>
    /// 悬停事件排序水位(round-4 决议):上次处理过的 Logto 用户 updatedAt。
    /// 悬停事件处理器先重查 Logto 现态,只有 fresh updatedAt &gt; 本列时才应用;
    /// 延迟/重发的旧取消停用事件(updatedAt 未变或更旧)一律忽略,绝不冲掉更新的停用。
    /// </summary>
    public DateTimeOffset? LastEventAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>镜像同步(接收端/扫表/管理端共用):只改镜像列,不碰主键与邮箱。</summary>
    public void Sync(string approval, bool isSuspended, bool suspendedByGate, DateTimeOffset? lastEventAt)
    {
        Approval = approval;
        IsSuspended = isSuspended;
        SuspendedByGate = suspendedByGate;
        if (lastEventAt is not null) LastEventAt = lastEventAt;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
