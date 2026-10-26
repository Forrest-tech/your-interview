using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Analytics.Application;

/// <summary>
/// 票56(解法A):ability 回写的归属判定 —— 纯逻辑,单测锁矩阵。
///
/// 口径:
/// - 调用方 JWT 不带 perm claim <see cref="Permissions.AnalyticsWriteback"/> → 一律落 Me,
///   ?ownerUserId= 忽略(票06 口径不变;注意仅有 analytics.write 的 PowerUser 同样不 honoured)。
/// - 带 writeback 且 ownerUserId 非空、非 Guid.Empty → honoured,以 ownerUserId 为归属。
/// - 带 writeback 但 ownerUserId 缺失/为空 Guid → 落 Me(服务账号自写场景,票面 AC③ 口径)。
/// </summary>
public static class AbilityOwnership
{
    public static Guid Resolve(Guid me, IEnumerable<string>? callerPermissions, Guid? ownerUserId)
    {
        var canWriteback = callerPermissions is not null
            && callerPermissions.Contains(Permissions.AnalyticsWriteback);

        if (!canWriteback) return me;
        if (ownerUserId is null || ownerUserId.Value == Guid.Empty) return me;
        return ownerUserId.Value;
    }
}
