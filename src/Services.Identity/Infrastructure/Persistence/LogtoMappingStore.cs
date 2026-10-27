using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Infrastructure.Persistence;

/// <summary>
/// 映射表实现(审批门、扫表、管理端共用)。
/// Upsert 语义:按主键 LogtoUserId 查到则 Sync 镜像列,查不到则新增;
/// Sync 不碰主键与邮箱(邮箱只在新增时写,邮箱可改、主键才是身份)。
/// 旧侧查询走 db.Users(自带 !IsDeleted 全局过滤器):已删用户视为不存在。
/// </summary>
public sealed class LogtoMappingStore(IdentityDbContext db) : ILogtoMappingStore
{
    public async Task<LogtoMirrorRow?> GetRowByLogtoIdAsync(string logtoUserId, CancellationToken ct)
    {
        var m = await db.LogtoUserMappings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LogtoUserId == logtoUserId, ct);
        return m is null ? null : ToRow(m);
    }

    public async Task<LogtoMirrorRow?> GetRowByLegacyIdAsync(Guid legacyUserId, CancellationToken ct)
    {
        var m = await db.LogtoUserMappings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.LegacyUserId == legacyUserId, ct);
        return m is null ? null : ToRow(m);
    }

    public async Task<IReadOnlyList<LogtoMirrorRow>> GetAllRowsAsync(CancellationToken ct) =>
        (await db.LogtoUserMappings.AsNoTracking().ToListAsync(ct)).Select(ToRow).ToList();

    public async Task UpsertRowAsync(LogtoMirrorRow row, CancellationToken ct)
    {
        var existing = await db.LogtoUserMappings
            .FirstOrDefaultAsync(x => x.LogtoUserId == row.LogtoUserId, ct);
        if (existing is null)
        {
            db.LogtoUserMappings.Add(new LogtoUserMapping(row.LogtoUserId, row.LegacyUserId,
                row.Email, row.Approval, row.IsSuspended, row.SuspendedByGate, row.LastEventAt));
        }
        else
        {
            existing.Sync(row.Approval, row.IsSuspended, row.SuspendedByGate, row.LastEventAt);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<OldUserState?> GetOldUserByIdAsync(Guid legacyUserId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == legacyUserId, ct);
        return user is null ? null : new OldUserState(true, user.IsActive);
    }

    public async Task<Guid?> GetOldUserByEmailAsync(string email, CancellationToken ct)
    {
        var normalized = AppUser.Normalize(email);
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalized, ct);
        return user?.Id;
    }

    public async Task SetOldUserActiveAsync(Guid legacyUserId, bool active, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == legacyUserId, ct);
        if (user is null) return;
        if (active) user.Activate(); else user.Deactivate();
        await db.SaveChangesAsync(ct);
    }

    private static LogtoMirrorRow ToRow(LogtoUserMapping m) => new(
        m.LogtoUserId, m.LegacyUserId, m.Email, m.Approval,
        m.IsSuspended, m.SuspendedByGate, m.LastEventAt);
}
