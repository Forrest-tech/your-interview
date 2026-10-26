using YourInterview.Services.Assessment.Domain;

namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// 票 63:会话模式字符串 → <see cref="SessionMode"/> 的唯一解析口径(纯函数)。
///
/// 规则:入参 trim 后必须逐字等于某个枚举名(不区分大小写),其余一律失败 ——
/// 含旧前端误传的 FullMock、任务口径里不存在的 WeaknessDrill、纯数字串
/// ("0"/"3"/"99"/"-1";Enum.TryParse 对数字与逗号组合也会返回 true,故不用它)、
/// null / 空串 / 纯空白、带标点或内部空格的串。
///
/// 失败时 <paramref name="mode"/> 输出 TopicDrill 仅作占位:调用方必须先判返回值,
/// 不得使用失败时的 mode —— 旧实现正是「TryParse 失败静默用 TopicDrill」,
/// 让用户选的「完整模拟」实际建成主题专项会话且无任何提示。
/// </summary>
public static class SessionModeParsing
{
    public static bool TryParse(string? raw, out SessionMode mode)
    {
        mode = SessionMode.TopicDrill;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var name = raw.Trim();
        foreach (var candidate in Enum.GetNames<SessionMode>())
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                mode = Enum.Parse<SessionMode>(candidate);
                return true;
            }
        }
        return false;
    }

    /// <summary>合法值清单(报错文案用;顺序与枚举定义一致)。</summary>
    public static string ValidNames => string.Join(" / ", Enum.GetNames<SessionMode>());
}
