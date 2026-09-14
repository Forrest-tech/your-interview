using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Infrastructure.Services;

/// <summary>
/// 审计拦截器:自动把"写了 users/roles/tokens"的操作记进 audit_logs。
///
/// 设计取舍:审计写在与业务**同一个 DbContext + 同一个事务**里 ——
/// 宁可业务失败,也不产生"业务成功但审计缺失"的合规漏洞。
///
/// 与 ICurrentUser 的关系:身份读取已提升到 BuildingBlocks.Security(全平台共用),
/// 这个拦截器留在 Identity 是因为它依赖 IdentityDbContext 与 AuditLog 领域类型。
/// </summary>
public sealed class AuditSaveChangesInterceptor(
    ICurrentUser currentUser,
    ILogger<AuditSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    private static readonly HashSet<string> AuditedEntities = ["AppUser", "AppRole", "RefreshToken"];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        WriteAuditEntries(eventData.Context);
        return await base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        WriteAuditEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void WriteAuditEntries(DbContext? context)
    {
        if (context is not IdentityDbContext db) return;

        var changed = db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                        && AuditedEntities.Contains(e.Entity.GetType().Name))
            .ToList();
        if (changed.Count == 0) return;

        foreach (var entry in changed)
        {
            var action = entry.State switch
            {
                EntityState.Added => $"{entry.Entity.GetType().Name.ToLowerInvariant()}.created",
                EntityState.Modified => $"{entry.Entity.GetType().Name.ToLowerInvariant()}.updated",
                _ => $"{entry.Entity.GetType().Name.ToLowerInvariant()}.deleted"
            };

            var resourceId = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())
                                 ?.CurrentValue?.ToString()
                             ?? entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UserId")
                                 ?.CurrentValue?.ToString();

            // 只记录字段名,不记录值 —— 避免把密码哈希 / token 写进审计日志
            var fields = entry.Properties.Where(p => p.IsModified)
                .Select(p => p.Metadata.Name).ToArray();
            var detail = fields.Length > 0 ? $"fields: {string.Join(", ", fields)}" : null;

            db.AuditLogs.Add(new AuditLog(
                currentUser.UserId, action, entry.Entity.GetType().Name, resourceId, detail,
                currentUser.IpAddress, currentUser.UserAgent, true));
        }

        logger.LogDebug("审计:记录 {Count} 条变更", changed.Count);
    }
}
