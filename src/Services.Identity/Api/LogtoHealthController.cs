using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Api;

/// <summary>
/// 票241:审批门健康端点(管理员可读)。只读快照,不触发任何动作;
/// 告警判断由部署侧的监控按返回的派生标志做(心跳缺失 &gt; 30 分钟、
/// 连续失败 ≥ 3 次、webhook 静默),本端点不直接发告警。
/// </summary>
[ApiController]
[Route("api/logto")]
[Authorize]
public sealed class LogtoHealthController(
    LogtoGateHealth health,
    IOptions<LogtoManagementOptions> options) : ControllerBase
{
    [HttpGet("health")]
    [Authorize(Policy = PermissionPolicy.Prefix + Permissions.AdminUsersRead)]
    public IActionResult Health()
    {
        var interval = options.Value.SweepIntervalMinutes > 0 ? options.Value.SweepIntervalMinutes : 15;
        var now = DateTimeOffset.UtcNow;
        var sweepStale = options.Value.ManagementConfigured
            && (health.LastSweepAt is null || health.LastSweepAt.Value < now.AddMinutes(-2 * interval));
        var webhookSilentHours = health.LastWebhookReceivedAt is null
            ? (double?)null
            : (now - health.LastWebhookReceivedAt.Value).TotalHours;
        return Ok(new
        {
            configured = options.Value.ManagementConfigured,
            webhookConfigured = options.Value.WebhookConfigured,
            lastWebhookReceivedAt = health.LastWebhookReceivedAt,
            webhookReceivedCount = health.WebhookReceivedCount,
            webhookSilentHours,
            lastSweepAt = health.LastSweepAt,
            lastSweepActions = health.LastSweepActions,
            lastSweepError = health.LastSweepError,
            consecutiveSweepFailures = health.ConsecutiveSweepFailures,
            sweepStale,
            sweepFailing = health.ConsecutiveSweepFailures >= 3,
            sweepIntervalMinutes = interval
        });
    }
}
