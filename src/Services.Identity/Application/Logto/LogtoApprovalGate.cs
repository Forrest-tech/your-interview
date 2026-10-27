using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>审批门结果(控制器据此定 2xx/5xx,扫表据此写审计)。</summary>
public enum LogtoGateOutcome
{
    /// <summary>在映射表里(或 customData 已打 migrated)= 迁移用户,原样放行。</summary>
    PassedThroughMigrated,
    /// <summary>已停用且已打 pending,重复投递不重复动作。</summary>
    AlreadyGated,
    /// <summary>本次置停用 + 打 pending 成功。</summary>
    Gated,
    /// <summary>待审批用户被取消停用,标记置 approved。</summary>
    Approved,
    /// <summary>无动作(例如停用事件本身、非待审批用户的取消停用)。</summary>
    NoAction
}

/// <summary>Guid ↔ Logto ID 映射表访问(审批门与扫表只读;写只由迁移脚本做)。</summary>
public interface ILogtoMappingStore
{
    Task<bool> IsMigratedAsync(string logtoUserId, CancellationToken ct);
    Task<HashSet<string>> GetAllMigratedIdsAsync(CancellationToken ct);
}

/// <summary>扫表审计写入口(实现写 identity.audit_logs,见 Infrastructure)。</summary>
public interface ILogtoSweepAudit
{
    Task WriteAsync(string action, string logtoUserId, string detail, CancellationToken ct);
}

/// <summary>
/// 票241:管理员审批门(PLAN §4.2a,接收端与扫表共用同一规则)。
/// 核心不变式 —— **一次 webhook 丢失不得 fail-open**:
///   1. 映射表 guard(强制):User.Created 先查映射表,在表里 = 迁移用户,
///      直接放行,绝不置停用、绝不打 pending。没有这道 guard,批量导入
///      会触发接收端把刚导入的 admin 自己停用。
///   2. 投递语义:只有置停用 + 打 pending 两步都成功才算成功;任一步失败
///      抛异常 → 控制器回 5xx → 投递方重发。重发幂等:已停用且已打 pending
///      直接回 AlreadyGated;只完成一半(已停用未打标)则补打标,不重复停用。
///   3. 区分标记:Logto 用户 customData 的 approval 字段只有三态
///      pending / approved / migrated。其他原因的停用不带 approval 标记,
///      扫表一律不动(见 ReconcileAsync)。
///   4. 新注册用户的 legacyUserId 在门里分配(Guid):写进 customData,供
///      JWT customizer 打成 legacy_user_id 声明,业务服务按 Guid 过滤的
///      旧逻辑无需改动。门不往映射表写新注册用户的行 —— “不在映射表里”
///      正是扫表识别丢事件新用户的依据;门若先写行、后置停用失败,
///      重发会被 guard 放行,门就 fail-open 了,所以顺序绝不能反。
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

    public async Task<LogtoGateOutcome> HandleUserCreatedAsync(string logtoUserId, CancellationToken ct)
    {
        // Guard(强制):迁移用户直接放行。
        if (await mappings.IsMigratedAsync(logtoUserId, ct))
        {
            logger.LogInformation("Logto User.Created {UserId} 在映射表里=迁移用户,放行", logtoUserId);
            return LogtoGateOutcome.PassedThroughMigrated;
        }

        var user = await client.GetUserAsync(logtoUserId, ct)
            ?? throw new InvalidOperationException($"Logto 用户 {logtoUserId} 不存在,无法执行审批门(投递方重发时若用户已删,仍会 5xx,属预期:删号事件不走本门)");

        var approval = GetApproval(user);
        // 第二层:导入时 customData 已打 migrated(映射行写入有延迟时仍可防误删)。
        if (approval == "migrated") return LogtoGateOutcome.PassedThroughMigrated;
        if (user.IsSuspended && approval == "pending") return LogtoGateOutcome.AlreadyGated;

        // 分步执行,任一步抛异常 → 控制器 5xx → 重发时按当前状态续做,不重复动作。
        if (!user.IsSuspended) await client.SuspendUserAsync(logtoUserId, true, ct);
        var customData = Clone(user.CustomData);
        customData[ApprovalField] = "pending";
        customData[LegacyIdField] ??= Guid.NewGuid().ToString();
        await client.SetCustomDataAsync(logtoUserId, customData, ct);
        logger.LogInformation("Logto User.Created {UserId} 已置停用并打 pending", logtoUserId);
        return LogtoGateOutcome.Gated;
    }

    public async Task<LogtoGateOutcome> HandleSuspensionStatusUpdatedAsync(
        string logtoUserId, bool isSuspended, CancellationToken ct)
    {
        // 停用事件本身不需要改标记;批准 = 取消停用 + 待审批标记 → approved。
        if (isSuspended) return LogtoGateOutcome.NoAction;
        var user = await client.GetUserAsync(logtoUserId, ct)
            ?? throw new InvalidOperationException($"Logto 用户 {logtoUserId} 不存在,无法处理取消停用事件");
        if (GetApproval(user) != "pending") return LogtoGateOutcome.NoAction;
        var customData = Clone(user.CustomData);
        customData[ApprovalField] = "approved";
        await client.SetCustomDataAsync(logtoUserId, customData, ct);
        logger.LogInformation("Logto 用户 {UserId} 被取消停用,pending → approved", logtoUserId);
        return LogtoGateOutcome.Approved;
    }

    /// <summary>
    /// 对账扫表(每 15 分钟,PLAN §4.2a 最后一道):分页扫上线后创建的用户。
    ///   · approved / migrated → 不动;
    ///   · pending 但未停用 → fail-closed 重新置停用 + 审计(批准事件丢了或停用被异常解除;
    ///     管理员在 Console 再批准一次即恢复,不丢数据);
    ///   · 标记缺失且未停用且不在映射表 → 丢了注册事件,置停用 + 打 pending + 审计;
    ///   · 标记缺失且已停用 → 其他原因的停用,扫表一律不动。
    /// 返回本次采取的动作描述(供扫表服务记日志;审计行本身写 identity.audit_logs)。
    /// </summary>
    public async Task<IReadOnlyList<string>> ReconcileAsync(CancellationToken ct)
    {
        var actions = new List<string>();
        var migrated = await mappings.GetAllMigratedIdsAsync(ct);
        var launchedAt = DateTimeOffset.TryParse(options.Value.GateLaunchedAt,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : DateTimeOffset.MinValue;

        var page = 1;
        const int pageSize = 100;
        while (true)
        {
            var users = await client.ListUsersAsync(page, pageSize, ct);
            foreach (var user in users)
            {
                if (user.CreatedAt < launchedAt) continue;
                var approval = GetApproval(user);
                if (approval is "approved" or "migrated") continue;

                if (approval == "pending")
                {
                    if (user.IsSuspended) continue;
                    await client.SuspendUserAsync(user.Id, true, ct);
                    await audit.WriteAsync("logto.approval.sweep.resuspend", user.Id,
                        "扫表:pending 标记但未停用,fail-closed 重新置停用", ct);
                    actions.Add($"resuspend-pending:{user.Id}");
                    continue;
                }

                // 标记缺失。
                if (user.IsSuspended) continue; // 其他原因的停用,扫表不动。
                if (migrated.Contains(user.Id)) continue; // 迁移用户无标记的兜底 guard。
                await client.SuspendUserAsync(user.Id, true, ct);
                var customData = Clone(user.CustomData);
                customData[ApprovalField] = "pending";
                customData[LegacyIdField] ??= Guid.NewGuid().ToString();
                await client.SetCustomDataAsync(user.Id, customData, ct);
                await audit.WriteAsync("logto.approval.sweep.gate", user.Id,
                    "扫表:标记缺失且未停用且不在映射表,丢注册事件,置停用并打 pending", ct);
                actions.Add($"gate-unmarked:{user.Id}");
            }
            if (users.Count < pageSize) break;
            page++;
        }
        return actions;
    }

    private static string? GetApproval(LogtoUserDto user) =>
        user.CustomData.TryGetPropertyValue(ApprovalField, out var v) ? v?.GetValue<string>() : null;

    private static JsonObject Clone(JsonObject source) =>
        JsonNode.Parse(source.ToJsonString())!.AsObject();
}
