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

    /// <summary>
    /// ★ 票111 段3(If-Match 甲案):解析 DELETE/替换端点的可选 If-Match 头。
    /// 形态:"<version>"(version = xmin 的既有前端表示 = 十进制 uint,与 DTO 回传
    /// Version 同编码);容忍弱验证前缀 W/ 与无引号。头缺失 → null(不钉版,兼容 LWW);
    /// 头存在但不可解析 → 返回 uint.MaxValue 哨兵(xmin 永不取此值,钉版必不符 → 409,
    /// 而不是静默放行一个无法验证的前置条件)。
    /// </summary>
    public static uint? ParseIfMatch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        if (v.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) v = v[2..].Trim();
        v = v.Trim('"');
        return uint.TryParse(v, out var n) ? n : uint.MaxValue;
    }
}
