using Microsoft.EntityFrameworkCore;

namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// 票108(承票101/70)乐观并发钉版助手:
/// 客户端回传读时 xmin 时,把被跟踪实体的 xmin OriginalValue 钉回该版本 ——
/// SaveChanges 的 UPDATE 即带 xmin 谓词,版本已推进时影响 0 行,
/// EF 抛 DbUpdateConcurrencyException,由全局异常映射转 409 concurrency_conflict。
/// Version 为 null(旧调用方未回传)时跳过,保持兼容。
/// </summary>
public static class ConcurrencyPin
{
    public static void Pin(DbContext db, object entity, uint? version)
    {
        if (version is { } v)
            db.Entry(entity).Property("xmin").OriginalValue = v;
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
