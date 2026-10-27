using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>审批门结果(控制器据此定 2xx/5xx,扫表据此写审计)。</summary>
public enum LogtoGateOutcome
{
    /// <summary>映射表里 approval=migrated = 迁移用户,原样放行。</summary>
    PassedThroughMigrated,
    /// <summary>映射表里 approval=approved 的用户收到 User.Created 重发:已批过,不重复动作。</summary>
    PassedThroughApproved,
    /// <summary>已停用且已打 pending,重复投递不重复动作(但仍会补写镜像行)。</summary>
    AlreadyGated,
    /// <summary>本次置停用 + 打 pending + 镜像写三步成功。</summary>
    Gated,
    /// <summary>待审批用户被取消停用且邮箱已验证,标记置 approved。</summary>
    Approved,
    /// <summary>取消停用但邮箱未验证:镜像保持 pending,等验证完成后再翻转(见数据更新事件)。</summary>
    UnverifiedHeld,
    /// <summary>验证完成事件/扫表把 pending 翻成 approved(管理员早已取消停用,无需第二次操作)。</summary>
    VerificationApproved,
    /// <summary>镜像按 Logto 现态同步,审批标记不动(停用事件方向)。</summary>
    MirrorSynced,
    /// <summary>延迟/重发的旧事件被排序水位挡下,无动作。</summary>
    Ignored,
    /// <summary>无动作(非待审批用户的取消停用、已删用户的事件等)。</summary>
    NoAction
}

/// <summary>映射表镜像行(审批门/扫表/管理端共用;写路径见 LogtoMappingStore)。</summary>
public sealed record LogtoMirrorRow(
    string LogtoUserId,
    Guid LegacyUserId,
    string Email,
    string Approval,
    bool IsSuspended,
    bool SuspendedByGate,
    DateTimeOffset? LastEventAt);

/// <summary>旧侧用户状态(null = 不存在或已删除)。</summary>
public sealed record OldUserState(bool Exists, bool IsActive);

/// <summary>映射表访问(审批门、扫表、管理端;读 + 镜像写 + 旧侧 IsActive 读写)。</summary>
public interface ILogtoMappingStore
{
    Task<LogtoMirrorRow?> GetRowByLogtoIdAsync(string logtoUserId, CancellationToken ct);
    Task<LogtoMirrorRow?> GetRowByLegacyIdAsync(Guid legacyUserId, CancellationToken ct);
    Task<IReadOnlyList<LogtoMirrorRow>> GetAllRowsAsync(CancellationToken ct);
    Task UpsertRowAsync(LogtoMirrorRow row, CancellationToken ct);
    Task<OldUserState?> GetOldUserByIdAsync(Guid legacyUserId, CancellationToken ct);
    Task<Guid?> GetOldUserByEmailAsync(string email, CancellationToken ct);
    Task SetOldUserActiveAsync(Guid legacyUserId, bool active, CancellationToken ct);
}

/// <summary>扫表/审批审计写入口(实现写 identity.audit_logs,见 Infrastructure)。</summary>
public interface ILogtoSweepAudit
{
    Task WriteAsync(string action, string logtoUserId, string detail, CancellationToken ct);
}

/// <summary>
/// 票241:管理员审批门(PLAN §4.2a + round-3~9 决议,接收端与扫表共用同一规则)。
/// 核心不变式 —— **一次 webhook 丢失不得 fail-open**,分层实现:
///   1. 映射表 guard(强制):User.Created 先查映射表,approval=migrated/approved
///      直接放行,绝不置停用、绝不打 pending。没有这道 guard,批量导入会把
///      刚导入的 admin 自己停用。导入时暂停 webhook 订阅是第二层,不是替代品。
///   2. 投递语义:置停用 + 打 pending + 镜像写三步全部成功才算成功;任一步失败
///      抛异常 → 控制器回 5xx → 投递方重发。重发幂等:已停用且已打 pending
///      直接回 AlreadyGated(仍补写镜像);只完成一半则续做,不重复动作。
///      门不先写镜像行再置停用 —— 行先写、停用失败,重发会被 guard 放行,
///      门就 fail-open 了,所以镜像写永远是最后一步。
///   3. 区分标记:Logto 用户 customData 的 approval 字段只有三态
///      pending / approved / migrated;镜像另有本地态 "deleted"(Logto 侧已删,
///      customData 早已不存在,故不污染三态)。其他原因的停用不带 approval
///      标记,扫表按 provenance(见 SuspendedByGate)区别对待。
///   4. 悬停事件排序:处理器先重查 Logto 现态(fresh GET 赢过事件 payload),
///      只有 fresh updatedAt &gt; 镜像 LastEventAt 时才应用;延迟/重发的旧
///      取消停用绝不冲掉更新的停用。Logto 不暴露停用时间戳,updatedAt 又
///      会被每次 PATCH 改动,所以停用来源不能靠时间推断,必须用
///      SuspendedByGate 显式记录(true = 门/扫表执行的停用,false = 观察到的管理员停用)。
///   5. 归因规则:扫表无法从 Logto 审计日志查到 Console 操作(审计日志只覆盖
///      最终用户登录体验活动,不含 Console 管理动作),所以“无法归因的操作”
///      一律视为无管理员记录 —— fail-closed 重新置停用 + 告警(ERROR 日志)。
///      例外只有一种:镜像已记录了该次取消停用(镜像 IsSuspended=false),
///      说明接收端见过这次解封事件,扫表才敢把它当作管理员批准。
///   6. 验证翻转分两条路,语义不同(不要合并):
///      · 事件路(User.Data.Updated):pending + 现态已验证 + 现态未停用 →
///        直接翻 approved —— 事件本身就是“验证刚完成”的证据,且现态未停用
///        即管理员早已批准,验证是唯一 blocker(即 round-7 的 out-of-order 分支)。
///      · 扫表路:只有镜像已记录取消停用(IsSuspended=false)且现态已验证 +
///        未停用时才翻;镜像仍显示停用 = 解封事件根本没到,无法归因,
///        走 fail-closed 重置停用(管理员再点一次批准即恢复)。
///   7. Pass 1 与 Pass 2 看似相反实则集合不交:Pass 1 管未映射新用户
///      (pending 未停用 → 重置停用);Pass 2 管已映射的 legacy 用户
///      (pending 行纠成 migrated,SuspendedByGate=true 的导入窗口误停才解封,
///      管理员停用只纠标记不动停用)。注释里写清集合,后人不要“统一”它们。
/// </summary>
public sealed class LogtoApprovalGate(
    ILogtoManagementClient client,
    ILogtoMappingStore mappings,
    ILogtoSweepAudit audit,
    IOptions<LogtoManagementOptions> options,
    ILogger<LogtoApprovalGate> logger)
{
    private const string ApprovalField = "approval";
    private const string LegacyIdField = "legacyUserId";

    /// <summary>
    /// round-13:悬停事件按用户串行化。门实例是 scoped(每请求一个),实例锁跨请求无效,
    /// 故用进程级静态字典锁:同一用户的 suspend/un-suspend 事件绝不同时跑 ——
    /// 否则两者可能都通过 updatedAt 水位检查,最后写者胜出,把镜像定在错误态。
    /// 锁只包悬停处理器(User.Created 与 Data.Updated 按现态幂等,无此风险)。
    /// 条目不回收(用户量有界、单条目极小);锁内绝不调扫表,只做一次事件处理。
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SuspensionLocks = new(StringComparer.Ordinal);

    // ================= 接收端:User.Created =================

    public async Task<LogtoGateOutcome> HandleUserCreatedAsync(string logtoUserId, CancellationToken ct)
    {
        // Guard(强制):映射表里的已批/已迁移用户直接放行。
        var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
        if (row?.Approval == "migrated") return LogtoGateOutcome.PassedThroughMigrated;
        if (row?.Approval == "approved") return LogtoGateOutcome.PassedThroughApproved;

        var user = await client.GetUserAsync(logtoUserId, ct)
            ?? throw new InvalidOperationException($"Logto 用户 {logtoUserId} 不存在,无法执行审批门");

        var approval = GetApproval(user);
        // 第二层:导入时 customData 已打 migrated(映射行写入有延迟时仍可防误删)。
        // round-12:放行前若镜像行缺失则补行(导入写 customData 成功、写行失败的
        // 残态)—— 否则该用户在 claims 门里无行 = 零权限,只能等扫表兜底。
        // migrated 是终态标记,补行不做任何审批决定,故安全。
        if (approval == "migrated")
        {
            if (row is null)
                await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId,
                    ParseLegacyId(user) ?? Guid.NewGuid(),
                    user.PrimaryEmail ?? string.Empty,
                    "migrated", user.IsSuspended, false, null), ct);
            return LogtoGateOutcome.PassedThroughMigrated;
        }
        if (user.IsSuspended && approval == "pending")
        {
            // 幂等重发:仍补写镜像行(镜像写失败 = 投递失败,控制器回 5xx 重发)。
            await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId,
                row?.LegacyUserId ?? ParseLegacyId(user) ?? Guid.NewGuid(),
                user.PrimaryEmail ?? row?.Email ?? string.Empty,
                "pending", true, true, row?.LastEventAt), ct);
            return LogtoGateOutcome.AlreadyGated;
        }

        // 碰撞可见性:新 Logto 账号的邮箱撞上未迁移旧账号(灰度期本应由 blocklist 挡住,
        // 这里是漏网时的审计网):只写审计行,仍走标准门流程;恢复 = 管理员删 Logto 侧
        // 账号后重跑导入(删 + 重导,绝不合并)。
        if (user.PrimaryEmail is not null)
        {
            var oldId = await mappings.GetOldUserByEmailAsync(user.PrimaryEmail, ct);
            if (oldId is not null && row is null
                && await mappings.GetRowByLegacyIdAsync(oldId.Value, ct) is null)
                await audit.WriteAsync("logto.approval.collision", logtoUserId,
                    $"新注册邮箱撞上未迁移旧账号 {user.PrimaryEmail},走标准待审批流程,恢复需删号重导", ct);
        }

        var legacyId = row?.LegacyUserId ?? ParseLegacyId(user) ?? Guid.NewGuid();
        // 分步执行,任一步抛异常 → 控制器 5xx → 重发时按当前状态续做,不重复动作。
        if (!user.IsSuspended) await client.SuspendUserAsync(logtoUserId, true, ct);
        var customData = Clone(user.CustomData);
        customData[ApprovalField] = "pending";
        customData[LegacyIdField] ??= legacyId.ToString();
        await client.SetCustomDataAsync(logtoUserId, customData, ct);
        // 镜像写永远最后一步(见类注释第 2 条)。
        await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId, legacyId,
            user.PrimaryEmail ?? row?.Email ?? string.Empty,
            "pending", true, true, row?.LastEventAt), ct);
        logger.LogInformation("Logto User.Created {UserId} 已置停用并打 pending", logtoUserId);
        return LogtoGateOutcome.Gated;
    }

    // ================= 接收端:User.SuspensionStatus.Updated =================

    public async Task<LogtoGateOutcome> HandleSuspensionStatusUpdatedAsync(
        string logtoUserId, bool isSuspendedPayload, CancellationToken ct)
    {
        // 按用户串行化(见 SuspensionLocks 注释):第二个并发事件必须在第一个
        // 写完镜像(含水位)之后才读行、查 fresh,否则两者都通过水位检查。
        var userLock = SuspensionLocks.GetOrAdd(logtoUserId, static _ => new SemaphoreSlim(1, 1));
        await userLock.WaitAsync(ct);
        try { return await HandleSuspensionStatusUpdatedCoreAsync(logtoUserId, isSuspendedPayload, ct); }
        finally { userLock.Release(); }
    }

    private async Task<LogtoGateOutcome> HandleSuspensionStatusUpdatedCoreAsync(
        string logtoUserId, bool isSuspendedPayload, CancellationToken ct)
    {
        // 注意:payload 的 isSuspended 值只作控制器层面的 presence 校验,处理器
        // 一律以 fresh GET 为准 —— 延迟/重发的旧事件靠 fresh 现态 + 排序水位挡下。
        _ = isSuspendedPayload;
        var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
        var user = await client.GetUserAsync(logtoUserId, ct);
        if (user is null)
        {
            // 用户已删:不抛(否则删号事件会毒化重试队列);删号传播走删除事件/扫表存在检查。
            logger.LogWarning("Logto 悬停事件用户 {UserId} 已不存在,忽略(删号传播走删除路径)", logtoUserId);
            return LogtoGateOutcome.NoAction;
        }
        // 排序水位:只有更新的事件才应用。
        if (row?.LastEventAt is not null && user.UpdatedAt <= row.LastEventAt.Value)
            return LogtoGateOutcome.Ignored;

        var legacyId = row?.LegacyUserId ?? ParseLegacyId(user) ?? Guid.NewGuid();
        var approval = GetApproval(user) ?? row?.Approval ?? string.Empty;

        if (user.IsSuspended)
        {
            // 停用方向:镜像置停用,审批标记不动(与取消停用方向对称)。
            await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId, legacyId,
                user.PrimaryEmail ?? row?.Email ?? string.Empty,
                approval, true, false, user.UpdatedAt), ct);
            return LogtoGateOutcome.MirrorSynced;
        }

        // 取消停用方向:先按审批规则定标记,再做解锁双写。
        LogtoGateOutcome outcome;
        if (approval == "pending")
        {
            if (!user.EmailVerified)
            {
                await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId, legacyId,
                    user.PrimaryEmail ?? row?.Email ?? string.Empty,
                    "pending", false, false, user.UpdatedAt), ct);
                await audit.WriteAsync("logto.approval.unsuspend.unverified", logtoUserId,
                    "管理员取消停用但邮箱未验证,镜像保持 pending,验证完成后自动翻 approved", ct);
                outcome = LogtoGateOutcome.UnverifiedHeld;
            }
            else
            {
                var customData = Clone(user.CustomData);
                customData[ApprovalField] = "approved";
                customData[LegacyIdField] ??= legacyId.ToString();
                await client.SetCustomDataAsync(logtoUserId, customData, ct);
                await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId, legacyId,
                    user.PrimaryEmail ?? row?.Email ?? string.Empty,
                    "approved", false, false, user.UpdatedAt), ct);
                logger.LogInformation("Logto 用户 {UserId} 被取消停用,pending → approved", logtoUserId);
                outcome = LogtoGateOutcome.Approved;
            }
        }
        else
        {
            await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId, legacyId,
                user.PrimaryEmail ?? row?.Email ?? string.Empty,
                approval, false, false, user.UpdatedAt), ct);
            outcome = LogtoGateOutcome.MirrorSynced;
        }

        // 解锁双写(round-7):Console 取消停用经本接收端同步把旧侧 IsActive 拉回启用,
        // 两侧同时启用,扫表 fail-closed 对账就看不到漂移、不会把合法解锁又翻回去。
        // (注意:这是 webhook 路径,不是旧 /api/admin 路径;旧管理端对已映射用户的
        // 启用/停用/删除/重置一律拒绝,见 AdminHandlers —— 两条路不要混淆。)
        if (row is not null)
        {
            var old = await mappings.GetOldUserByIdAsync(row.LegacyUserId, ct);
            if (old is { Exists: true, IsActive: false })
            {
                await mappings.SetOldUserActiveAsync(row.LegacyUserId, true, ct);
                await audit.WriteAsync("logto.approval.unlock.dualwrite", logtoUserId,
                    $"Console 取消停用同步启用旧侧账号 {row.LegacyUserId}", ct);
            }
        }
        return outcome;
    }

    // ================= 接收端:User.Data.Updated(验证完成翻转) =================

    public async Task<LogtoGateOutcome> HandleUserDataUpdatedAsync(string logtoUserId, CancellationToken ct)
    {
        var user = await client.GetUserAsync(logtoUserId, ct);
        if (user is null) return LogtoGateOutcome.NoAction;
        // 事件路翻转条件(见类注释第 6 条):pending + 现态已验证 + 现态未停用。
        // 现态未停用即管理员早已批准,验证是唯一 blocker —— 无需第二次管理员操作。
        if (GetApproval(user) != "pending" || !user.EmailVerified || user.IsSuspended)
            return LogtoGateOutcome.NoAction;
        var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
        var legacyId = row?.LegacyUserId ?? ParseLegacyId(user) ?? Guid.NewGuid();
        var customData = Clone(user.CustomData);
        customData[ApprovalField] = "approved";
        customData[LegacyIdField] ??= legacyId.ToString();
        await client.SetCustomDataAsync(logtoUserId, customData, ct);
        await mappings.UpsertRowAsync(new LogtoMirrorRow(logtoUserId, legacyId,
            user.PrimaryEmail ?? row?.Email ?? string.Empty,
            "approved", false, false, user.UpdatedAt), ct);
        await audit.WriteAsync("logto.approval.data-updated.flip", logtoUserId,
            "验证完成事件:pending → approved(管理员早已取消停用)", ct);
        return LogtoGateOutcome.VerificationApproved;
    }

    // ================= 接收端:User.Deleted(删号快传;扫表存在检查是兜底) =================

    public async Task<LogtoGateOutcome> HandleUserDeletedAsync(string logtoUserId, CancellationToken ct)
    {
        var row = await mappings.GetRowByLogtoIdAsync(logtoUserId, ct);
        if (row is null) return LogtoGateOutcome.NoAction;
        await MarkDeletedAsync(row, "logto.approval.deleted", "Logto 删号事件", ct);
        return LogtoGateOutcome.MirrorSynced;
    }

    private async Task MarkDeletedAsync(LogtoMirrorRow row, string auditAction, string auditDetail, CancellationToken ct)
    {
        // 镜像本地态 "deleted"(不写回 Logto,用户已不存在);claims 门的严格 allowlist
        // 本来就拒绝非 approved/migrated,已发 token 在有效期内同样被拒。
        if (row.Approval != "deleted")
        {
            await mappings.UpsertRowAsync(row with { Approval = "deleted", IsSuspended = true }, ct);
            var old = await mappings.GetOldUserByIdAsync(row.LegacyUserId, ct);
            if (old is { Exists: true, IsActive: true })
                await mappings.SetOldUserActiveAsync(row.LegacyUserId, false, ct);
            await audit.WriteAsync(auditAction, row.LogtoUserId,
                $"{auditDetail}:镜像置 deleted,旧侧停用", ct);
        }
    }

    // ================= 扫表:三道 + 删号兜底 =================

    public async Task<IReadOnlyList<string>> ReconcileAsync(CancellationToken ct)
    {
        var actions = new List<string>();
        var acted = new HashSet<string>(StringComparer.Ordinal);
        var launchedAt = ParseLaunchedAt();
        var rows = await mappings.GetAllRowsAsync(ct);
        var rowsByLogto = rows.ToDictionary(r => r.LogtoUserId, StringComparer.Ordinal);

        // ---- Pass 1:审批标记道(只看上线后创建的用户)。集合 = 未映射新用户 + 丢事件用户。
        // round-12 镜像补行(不设上线时间限):Logto 侧有标记但无镜像行的用户,
        // 按 Logto 现态补行 —— 残态来源:首次投递在置停用 + 打标成功后、写行失败,
        // 或导入写 customData 成功、写行失败。补行本身不做审批决定。
        // 唯一起除外:pending + 未停用 + 无行 —— 该态按归因规则走 fail-closed
        // 重置停用(见下方 pending 分支),绝不能先补一行“已记录解封”再翻 approved,
        // 那会把无法归因的解封洗成已记录的解封。
        const int pageSize = 100;
        var page = 1;
        while (true)
        {
            var users = await client.ListUsersAsync(page, pageSize, ct);
            foreach (var user in users)
            {
                if (acted.Contains(user.Id)) continue;
                var approval = GetApproval(user);
                if (approval is not null && !rowsByLogto.ContainsKey(user.Id)
                    && (approval is "approved" or "migrated"
                        || (approval == "pending" && user.IsSuspended)))
                {
                    var backfill = new LogtoMirrorRow(user.Id,
                        ParseLegacyId(user) ?? Guid.NewGuid(),
                        user.PrimaryEmail ?? string.Empty,
                        approval, user.IsSuspended, approval == "pending", null);
                    await mappings.UpsertRowAsync(backfill, ct);
                    rowsByLogto[user.Id] = backfill;
                    await audit.WriteAsync("logto.approval.sweep.mirror.backfill", user.Id,
                        $"扫表:Logto 侧标记 {approval} 但无镜像行,按 Logto 现态补行", ct);
                    actions.Add($"mirror-backfill:{user.Id}");
                }

                if (user.CreatedAt < launchedAt) continue;
                if (approval is "approved" or "migrated") continue;

                if (approval == "pending")
                {
                    if (user.IsSuspended)
                    {
                        // 镜像漂移纠正(只在行状态与现态不一致时写,避免审计刷屏)。
                        if (rowsByLogto.TryGetValue(user.Id, out var prow)
                            && (prow.Approval != "pending" || !prow.IsSuspended))
                        {
                            await mappings.UpsertRowAsync(prow with
                                { Approval = "pending", IsSuspended = true, SuspendedByGate = true }, ct);
                            await audit.WriteAsync("logto.approval.sweep.mirror.sync", user.Id,
                                "扫表:pending 已停用但镜像漂移,同步镜像", ct);
                            actions.Add($"mirror-sync-pending:{user.Id}");
                            acted.Add(user.Id);
                        }
                        continue;
                    }
                    // pending 但未停用:先看镜像是否记录过这次解封。
                    if (rowsByLogto.TryGetValue(user.Id, out var mrow) && !mrow.IsSuspended
                        && user.EmailVerified)
                    {
                        // 镜像已记录取消停用 + 现态已验证 → 验证翻转(扫表路,见类注释第 6 条)。
                        await ApproveAsync(user, mrow, "logto.approval.sweep.verified-flip",
                            "扫表:pending 未停用且镜像已记录解封、现态已验证 → approved", ct);
                        actions.Add($"verified-flip:{user.Id}");
                        acted.Add(user.Id);
                        continue;
                    }
                    // 无法归因的解封(镜像仍显示停用或根本无行) → fail-closed 重置停用 + 告警。
                    var fresh = await client.GetUserAsync(user.Id, ct);
                    if (fresh is { IsSuspended: false } && GetApproval(fresh) == "pending")
                    {
                        await client.SuspendUserAsync(user.Id, true, ct);
                        var legacyId = rowsByLogto.TryGetValue(user.Id, out var r0)
                            ? r0.LegacyUserId : ParseLegacyId(fresh) ?? Guid.NewGuid();
                        await mappings.UpsertRowAsync(new LogtoMirrorRow(user.Id, legacyId,
                            fresh.PrimaryEmail ?? string.Empty, "pending", true, true, null), ct);
                        await audit.WriteAsync("logto.approval.sweep.resuspend.unattributed", user.Id,
                            "扫表:pending 未停用且无法归因管理员操作,fail-closed 重新置停用", ct);
                        logger.LogError("Logto 扫表 fail-closed 重置停用 {UserId}:pending 未停用且无法归因", user.Id);
                        actions.Add($"resuspend-pending:{user.Id}");
                        acted.Add(user.Id);
                    }
                    continue;
                }

                // 标记缺失。
                if (user.IsSuspended) continue; // 其他原因的停用,扫表不动。
                var row = rowsByLogto.TryGetValue(user.Id, out var er) ? er : null;
                var legacyFromData = ParseLegacyId(user);
                var isMappedLegacy = row is not null
                    || (legacyFromData is not null
                        && await mappings.GetOldUserByIdAsync(legacyFromData.Value, ct) is { Exists: true });
                if (isMappedLegacy)
                {
                    // 已映射用户丢了标记:只纠标记为 migrated,绝不动停用(见 Pass 2 规则)。
                    var legacyId = row?.LegacyUserId ?? legacyFromData!.Value;
                    var customData = Clone(user.CustomData);
                    customData[ApprovalField] = "migrated";
                    customData[LegacyIdField] ??= legacyId.ToString();
                    await client.SetCustomDataAsync(user.Id, customData, ct);
                    await mappings.UpsertRowAsync(new LogtoMirrorRow(user.Id, legacyId,
                        user.PrimaryEmail ?? row?.Email ?? string.Empty,
                        "migrated", user.IsSuspended, false, null), ct);
                    await audit.WriteAsync("logto.approval.sweep.mapped-mark-corrected", user.Id,
                        "扫表:已映射用户标记缺失,纠为 migrated(停用态不动)", ct);
                    actions.Add($"mapped-mark:{user.Id}");
                    acted.Add(user.Id);
                    continue;
                }
                // 真丢事件的新用户:重查一次再动手(list→act 竞态护栏)。
                var recheck = await client.GetUserAsync(user.Id, ct);
                if (recheck is { IsSuspended: false } && GetApproval(recheck) is null)
                {
                    await client.SuspendUserAsync(user.Id, true, ct);
                    var customData = Clone(recheck.CustomData);
                    customData[ApprovalField] = "pending";
                    var newLegacy = ParseLegacyId(recheck) ?? Guid.NewGuid();
                    customData[LegacyIdField] ??= newLegacy.ToString();
                    await client.SetCustomDataAsync(user.Id, customData, ct);
                    await mappings.UpsertRowAsync(new LogtoMirrorRow(user.Id, newLegacy,
                        recheck.PrimaryEmail ?? string.Empty, "pending", true, true, null), ct);
                    await audit.WriteAsync("logto.approval.sweep.gate", user.Id,
                        "扫表:标记缺失且未停用且不在映射表,丢注册事件,置停用并打 pending", ct);
                    actions.Add($"gate-unmarked:{user.Id}");
                    acted.Add(user.Id);
                }
            }
            if (users.Count < pageSize) break;
            page++;
        }

        // ---- Pass 2:已映射但 pending 纠正(集合 = 镜像行 pending 且 legacy 在旧库存在)。
        foreach (var row in rows)
        {
            if (row.Approval != "pending" || acted.Contains(row.LogtoUserId)) continue;
            var old = await mappings.GetOldUserByIdAsync(row.LegacyUserId, ct);
            if (old is not { Exists: true }) continue;
            var user = await client.GetUserAsync(row.LogtoUserId, ct);
            if (user is null) continue; // 删号走 Pass 3/4 的存在检查
            var customData = Clone(user.CustomData);
            customData[ApprovalField] = "migrated";
            customData[LegacyIdField] ??= row.LegacyUserId.ToString();
            await client.SetCustomDataAsync(row.LogtoUserId, customData, ct);
            if (row.SuspendedByGate)
            {
                // 导入窗口误停(停用发生在映射行写入之前):解封 + 审计。
                await client.SuspendUserAsync(row.LogtoUserId, false, ct);
                await mappings.UpsertRowAsync(row with
                    { Approval = "migrated", IsSuspended = false, SuspendedByGate = false }, ct);
                await audit.WriteAsync("logto.approval.sweep.mapped-pending-corrected", row.LogtoUserId,
                    "扫表:已映射 pending 行纠为 migrated,导入窗口误停已解封", ct);
            }
            else
            {
                // 映射行写入之后的停用 = 管理员动作:只纠标记,绝不动停用态。
                await mappings.UpsertRowAsync(row with
                    { Approval = "migrated", IsSuspended = user.IsSuspended, SuspendedByGate = false }, ct);
                await audit.WriteAsync("logto.approval.sweep.mapped-pending-corrected", row.LogtoUserId,
                    "扫表:已映射 pending 行纠为 migrated(管理员停用态不动)", ct);
            }
            actions.Add($"mapped-pending:{row.LogtoUserId}");
            acted.Add(row.LogtoUserId);
        }

        // ---- Pass 3/4:停用态 + 双写对账(集合 = 全部镜像行,含上线前用户,不受 GateLaunchedAt 限)。
        foreach (var row in rows)
        {
            if (acted.Contains(row.LogtoUserId)) continue;
            var user = await client.GetUserAsync(row.LogtoUserId, ct);
            if (user is null)
            {
                // 删号兜底:存在检查(删除事件丢了也由本道兜住)。
                await MarkDeletedAsync(row, "logto.approval.sweep.deleted", "扫表存在检查:Logto 用户已不存在", ct);
                actions.Add($"deleted:{row.LogtoUserId}");
                acted.Add(row.LogtoUserId);
                continue;
            }
            if (row.Approval == "deleted") continue; // 已标记,等 Logto 侧重建(不自动复活)

            // 扫表路验证翻转(镜像已记录解封 + 现态已验证 + 现态未停用)。
            if (row.Approval == "pending" && !row.IsSuspended && user.EmailVerified && !user.IsSuspended)
            {
                await ApproveAsync(user, row, "logto.approval.sweep.verified-flip",
                    "扫表:pending 镜像已记录解封、现态已验证 → approved", ct);
                actions.Add($"verified-flip:{row.LogtoUserId}");
                acted.Add(row.LogtoUserId);
                continue;
            }

            // 停用态同步:镜像跟随 Logto 现态(观察到的停用记 SuspendedByGate=false)。
            var synced = row;
            if (user.IsSuspended != row.IsSuspended)
            {
                synced = row with
                {
                    IsSuspended = user.IsSuspended,
                    SuspendedByGate = user.IsSuspended ? false : row.SuspendedByGate,
                    LastEventAt = user.UpdatedAt
                };
                await mappings.UpsertRowAsync(synced, ct);
                await audit.WriteAsync("logto.approval.sweep.suspension.sync", row.LogtoUserId,
                    $"扫表:镜像停用态同步为 Logto 现态 IsSuspended={user.IsSuspended}", ct);
                actions.Add($"suspension-sync:{row.LogtoUserId}");
            }

            // 双写对账 FAIL-CLOSED:任一侧停用 → 两侧都停用,绝不自动启用任一侧。
            // 特别注意方向:不是“Logto 跟随旧侧”—— 那会把每次正常的 Console 停用
            // 在 15 分钟内翻回去。Console 解锁的合法路径走 webhook 解锁双写(两侧同启用),
            // 所以对账看到漂移时,漂移本身就是异常,两侧停用 + 告警才是安全动作。
            var old = await mappings.GetOldUserByIdAsync(row.LegacyUserId, ct);
            if (old is not { Exists: true }) continue;
            if (old.IsActive && user.IsSuspended)
            {
                await mappings.SetOldUserActiveAsync(row.LegacyUserId, false, ct);
                await audit.WriteAsync("logto.approval.sweep.reconcile.disable-old", row.LogtoUserId,
                    "扫表:旧侧启用但 Logto 已停用,fail-closed 停用旧侧", ct);
                logger.LogError("Logto 扫表对账漂移 {UserId}:旧侧启用/Logto 停用,已停用旧侧", row.LogtoUserId);
                actions.Add($"reconcile-disable-old:{row.LogtoUserId}");
            }
            else if (!old.IsActive && !user.IsSuspended)
            {
                await client.SuspendUserAsync(row.LogtoUserId, true, ct);
                await mappings.UpsertRowAsync(synced with { IsSuspended = true, SuspendedByGate = false }, ct);
                await audit.WriteAsync("logto.approval.sweep.reconcile.suspend-logto", row.LogtoUserId,
                    "扫表:旧侧停用但 Logto 未停用,fail-closed 停用 Logto 侧", ct);
                logger.LogError("Logto 扫表对账漂移 {UserId}:旧侧停用/Logto 启用,已停用 Logto 侧", row.LogtoUserId);
                actions.Add($"reconcile-suspend-logto:{row.LogtoUserId}");
            }
        }
        return actions;
    }

    private async Task ApproveAsync(LogtoUserDto user, LogtoMirrorRow row, string auditAction, string auditDetail, CancellationToken ct)
    {
        var customData = Clone(user.CustomData);
        customData[ApprovalField] = "approved";
        customData[LegacyIdField] ??= row.LegacyUserId.ToString();
        await client.SetCustomDataAsync(user.Id, customData, ct);
        await mappings.UpsertRowAsync(row with { Approval = "approved", IsSuspended = false, SuspendedByGate = false }, ct);
        await audit.WriteAsync(auditAction, user.Id, auditDetail, ct);
    }

    private DateTimeOffset ParseLaunchedAt() =>
        DateTimeOffset.TryParse(options.Value.GateLaunchedAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : DateTimeOffset.MinValue;

    private static string? GetApproval(LogtoUserDto user) =>
        user.CustomData.TryGetPropertyValue(ApprovalField, out var v) ? v?.GetValue<string>() : null;

    private static Guid? ParseLegacyId(LogtoUserDto user) =>
        user.CustomData.TryGetPropertyValue(LegacyIdField, out var v)
        && Guid.TryParse(v?.GetValue<string>(), out var g) ? g : null;

    private static JsonObject Clone(JsonObject source) =>
        JsonNode.Parse(source.ToJsonString())!.AsObject();
}
