using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票101:乐观并发钉版(口径同票70 Knowledge / 票101 Jobs)。详情 DTO 暴露读时
/// xmin(Version),更新命令回传后在改字段前把 OriginalValue 钉回读时值 ——
/// 过期即 DbUpdateConcurrencyException → 全局映射 409(concurrency_conflict)。
/// Version=null = 旧调用方,不钉版,行为与票101 前一致。
/// </summary>
internal static class ConcurrencyPin
{
    public static void Pin<T>(InterviewsDbContext db, T entity, uint? version) where T : class
    {
        if (version is { } v)
            db.Entry(entity).Property<uint>("xmin").OriginalValue = v;
    }
}
