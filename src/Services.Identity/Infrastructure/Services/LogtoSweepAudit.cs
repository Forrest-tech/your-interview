using YourInterview.Services.Identity.Application.Logto;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Infrastructure.Services;

/// <summary>
/// 扫表审计写实现:每条扫表动作写 identity.audit_logs 一行(PLAN §4.2a)。
/// UserId 留空(Logto 用户未必有本地 Guid 行;ResourceId 记 Logto 用户 ID,
/// 审计查询按 ResourceId 可追踪)。每次动作单独 SaveChanges:扫表半途崩了,
/// 已做的动作审计不丢。
/// </summary>
public sealed class LogtoSweepAudit(IdentityDbContext db) : ILogtoSweepAudit
{
    public async Task WriteAsync(string action, string logtoUserId, string detail, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(null, action, "LogtoUser", logtoUserId, detail, null, null, true));
        await db.SaveChangesAsync(ct);
    }
}
