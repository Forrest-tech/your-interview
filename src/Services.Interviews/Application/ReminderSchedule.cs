using System.Globalization;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// CONF-CAL-2 提醒排期纯逻辑:由轮次排期算两条提醒的触发时刻。
///
/// 时间口径 = 已删 .ics 导出的历史定版(票110 按定稿裁决删的是「导出到系统日历」
/// 功能本身,时间口径保留为本功能的定版):
///   开场 = ScheduledDate + ScheduledTime;ScheduledTime 用 TimeOnly.TryParse
///   解析,读不出(缺失/空/乱码/非法)一律回落 10:00;
///   提醒 = 开场 −1 天(DayBefore)/ 开场 −1 小时(HourBefore);
///   无 ScheduledDate → 不生成任何提醒(同 .ics 先例「尚未安排日期」)。
/// 墙钟时间按服务器本地时区解释为时刻(SPEC 设计决定 1:与 .ics 浮动时间被
/// 用户日历按本地时区解释等价;本应用无用户时区建模)。
/// </summary>
public static class ReminderSchedule
{
    /// <summary>时间缺失/不可解析时的开场时刻(.ics 先例定版)。</summary>
    public static readonly TimeOnly DefaultStartTime = new(10, 0);

    // 英文邀约邮件解析出的 "2:00 PM" 等形态在非 en 文化下裸 TryParse 读不出,
    // 补一组 invariant 精确格式作第二道(只放宽解析,不改变读出后的口径)。
    private static readonly string[] ExactFormats =
        ["H:mm", "HH:mm", "h:mm tt", "hh:mm tt", "H:mm:ss", "HH:mm:ss"];

    public static TimeOnly ParseStartTime(string? scheduledTime)
    {
        if (!string.IsNullOrWhiteSpace(scheduledTime))
        {
            var s = scheduledTime.Trim();
            if (TimeOnly.TryParse(s, out var t)) return t;
            if (TimeOnly.TryParseExact(s, ExactFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var te)) return te;
        }
        return DefaultStartTime;
    }

    /// <summary>开场时刻(墙钟 → 服务器本地时区时刻)。</summary>
    public static DateTimeOffset ComputeStart(DateOnly date, string? scheduledTime)
    {
        var wall = date.ToDateTime(ParseStartTime(scheduledTime), DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    /// <summary>两条提醒的 (种类, 触发时刻);无排期日期 → 空集。</summary>
    public static IReadOnlyList<(RoundReminderKind Kind, DateTimeOffset RemindAt)> Compute(
        DateOnly? date, string? scheduledTime)
    {
        if (date is null) return [];
        var start = ComputeStart(date.Value, scheduledTime);
        return
        [
            (RoundReminderKind.DayBefore, start.AddDays(-1)),
            (RoundReminderKind.HourBefore, start.AddHours(-1)),
        ];
    }
}
