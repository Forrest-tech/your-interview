using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Identity.Application.Abstractions;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Infrastructure.Services;

/// <summary>Unit of Work 实现:聚合 DbContext 的 SaveChanges,便于应用层不直接依赖 EF。</summary>
public sealed class IdentityUnitOfWork(IdentityDbContext db) : IIdentityUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);
}
