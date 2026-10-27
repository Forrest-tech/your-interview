using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Infrastructure.Persistence;

/// <summary>映射表读实现(审批门与扫表只读;写只由迁移脚本做)。</summary>
public sealed class LogtoMappingStore(IdentityDbContext db) : ILogtoMappingStore
{
    public Task<bool> IsMigratedAsync(string logtoUserId, CancellationToken ct) =>
        db.LogtoUserMappings.AsNoTracking().AnyAsync(m => m.LogtoUserId == logtoUserId, ct);

    public async Task<HashSet<string>> GetAllMigratedIdsAsync(CancellationToken ct) =>
        (await db.LogtoUserMappings.AsNoTracking().Select(m => m.LogtoUserId).ToListAsync(ct))
        .ToHashSet(StringComparer.Ordinal);
}
