using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Identity.Application;

/// <summary>
/// 票101 乐观并发钉版助手(口径同票70):客户端回传读时 xmin 时,把被跟踪
/// 实体的 xmin OriginalValue 钉回该版本 —— SaveChanges 的 UPDATE 带 xmin
/// 谓词,版本已推进时影响 0 行,EF 抛 DbUpdateConcurrencyException,
/// 由全局异常映射转 409 concurrency_conflict。Version 为 null 时跳过(兼容)。
/// </summary>
public static class ConcurrencyPin
{
    public static void Pin(DbContext db, object entity, uint? version)
    {
        if (version is { } v)
            db.Entry(entity).Property("xmin").OriginalValue = v;
    }
}
