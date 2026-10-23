using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Jobs.Infrastructure.Persistence;

namespace YourInterview.Services.Jobs.Application;

/// <summary>
/// 票101:乐观并发钉版(口径同票70 Knowledge)。详情 DTO 暴露读时 xmin(Version),
/// 更新命令回传后在改字段前把 OriginalValue 钉回读时值 —— 若期间已被他人改过,
/// SaveChanges 的 UPDATE 影响 0 行 → DbUpdateConcurrencyException → 全局映射 409
/// (concurrency_conflict)。Version=null = 旧调用方,不钉版,行为与票101 前一致。
/// </summary>
internal static class ConcurrencyPin
{
    public static void Pin<T>(JobsDbContext db, T entity, uint? version) where T : class
    {
        if (version is { } v)
            db.Entry(entity).Property<uint>("xmin").OriginalValue = v;
    }
}
