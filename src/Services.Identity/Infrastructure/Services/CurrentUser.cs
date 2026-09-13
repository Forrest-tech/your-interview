using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using YourInterview.Services.Identity.Application.Abstractions;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Infrastructure.Services;

/// <summary>ICurrentUser 的 HttpContext 实现。</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private HttpContext? Ctx => accessor.HttpContext;

    public Guid? UserId
    {
        get
        {
            var raw = Ctx?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? Ctx?.User.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? Email => Ctx?.User.FindFirstValue(ClaimTypes.Email) ?? Ctx?.User.FindFirstValue("email");
    public bool IsAuthenticated => Ctx?.User.Identity?.IsAuthenticated ?? false;
    public string? IpAddress => Ctx?.Connection.RemoteIpAddress?.ToString()
        ?? Ctx?.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    public string? UserAgent => Ctx?.Request.Headers.UserAgent.FirstOrDefault();
}

/// <summary>
/// 审计拦截器:自动把"写了 users/roles"的操作记进 audit_logs。
/// 设计取舍:审计写在与业务同一个 DbContext + 同一个事务里 ——
/// 宁可业务失败也不产生"业务成功但审计缺失"的合规漏洞。
/// </summary>
public sealed class AuditSaveChangesInterceptor(
    ICurrentUser currentUser,
    IHttpContextAccessor accessor,
    ILogger<AuditSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    private static readonly HashSet<string> AuditedEntities = ["AppUser", "AppRole", "RefreshToken"];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        WriteAuditEntries(eventData.Context);
        return await base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
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

            var resourceId = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey())?.CurrentValue?.ToString()
                             ?? entry.Properties.FirstOrDefault(p => p.Metadata.Name == "UserId")?.CurrentValue?.ToString();

            // 只记录字段名,不记录值 —— 避免把密码哈希/token 写进审计日志
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
