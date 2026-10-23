using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Interviews.Domain;

/// <summary>提醒种类(CONF-CAL-2 定稿口径:开场前 1 天 / 开场前 1 小时)。</summary>
public enum RoundReminderKind
{
    DayBefore,
    HourBefore,
}

/// <summary>提醒状态:待触发 → 已确认(用户在应用内确认)/ 已取消(改期重算、终态、删轮次)。</summary>
public enum RoundReminderStatus
{
    Pending,
    Acknowledged,
    Cancelled,
}

/// <summary>
/// 轮次提醒(CONF-CAL-2,定稿 §二「应用内日历 + 提醒(提前 1 天 + 提前 1 小时)」的后端落点)。
///
/// 派生数据而非用户资产:由轮次排期经 ReminderSchedule 计算生成,唯一键
/// (RoundId, Kind);同步规则见 ReminderSync —— 排期变则待触发行重算,
/// 轮次出终态/被删则取消,无排期日期不生成。
/// 归属(票03 口径):带 UserId,查询恒带归属谓词 + 全局过滤器。
/// 送达仅应用内(SPEC 设计决定 2):到期与否按 RemindAt ≤ now 在读取时判定,
/// 用户确认走 Acknowledge —— 无外部推送通道,不新造调度器。
/// </summary>
public sealed class RoundReminder : AuditableAggregateRoot
{
    private RoundReminder() { }

    public RoundReminder(Guid userId, Guid entryId, Guid roundId, RoundReminderKind kind, DateTimeOffset remindAt)
    {
        UserId = userId;
        EntryId = entryId;
        RoundId = roundId;
        Kind = kind;
        RemindAt = remindAt;
        Status = RoundReminderStatus.Pending;
    }

    public Guid UserId { get; private set; }

    /// <summary>所属条目(展示联表与条目删除级联定位用)。</summary>
    public Guid EntryId { get; private set; }

    /// <summary>所属轮次。</summary>
    public Guid RoundId { get; private set; }

    public RoundReminderKind Kind { get; private set; }

    /// <summary>触发时刻(开场时刻 −1 天 / −1 小时,口径见 ReminderSchedule)。</summary>
    public DateTimeOffset RemindAt { get; private set; }

    public RoundReminderStatus Status { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    /// <summary>
    /// 改期重算:更新触发时刻并回到待触发 —— 时刻已变,旧确认对新时刻无意义。
    /// 已取消的行不复活(取消原因若消失,由同步层新建行)。
    /// </summary>
    public void Reschedule(DateTimeOffset remindAt)
    {
        if (Status == RoundReminderStatus.Cancelled) return;
        RemindAt = remindAt;
        Status = RoundReminderStatus.Pending;
        AcknowledgedAt = null;
        Touch();
    }

    /// <summary>用户在应用内确认(点「知道了」)。重复确认幂等。</summary>
    public void Acknowledge(DateTimeOffset at)
    {
        if (Status != RoundReminderStatus.Pending) return;
        Status = RoundReminderStatus.Acknowledged;
        AcknowledgedAt = at;
        Touch();
    }

    /// <summary>取消(轮次终态/删除/条目删除)。重复取消幂等。</summary>
    public void Cancel()
    {
        if (Status == RoundReminderStatus.Cancelled) return;
        Status = RoundReminderStatus.Cancelled;
        Touch();
    }
}
