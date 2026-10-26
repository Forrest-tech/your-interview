using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// CONF-CAL-2 / CAL2-T1:提醒排期纯逻辑。口径 = 已删 .ics 导出的历史定版
/// (开场 = 日期 + 时间,读不出回落 10:00;提醒 = 开场 −1 天 / −1 小时;
/// 无日期不生成)。断言比墙钟分量(RemindAt.DateTime),与运行环境时区无关。
/// 每例注释标注 boundary 类别(总纲第七节)。
/// </summary>
public sealed class ReminderScheduleTests
{
    private static (DateTime DayBefore, DateTime HourBefore) Walls(DateOnly? date, string? time)
    {
        var r = ReminderSchedule.Compute(date, time);
        r.Should().HaveCount(2);
        r[0].Kind.Should().Be(RoundReminderKind.DayBefore);
        r[1].Kind.Should().Be(RoundReminderKind.HourBefore);
        return (r[0].RemindAt.DateTime, r[1].RemindAt.DateTime);
    }

    [Fact] // boundary: 空/null —— 无排期日期不生成(同 .ics 先例)
    public void Compute_NoDate_ReturnsEmpty()
    {
        ReminderSchedule.Compute(null, "14:00").Should().BeEmpty();
        ReminderSchedule.Compute(null, null).Should().BeEmpty();
    }

    [Fact] // 常规:24h 时间直读
    public void Compute_WithTime_ComputesDayAndHourBefore()
    {
        var (day, hour) = Walls(new DateOnly(2026, 10, 27), "14:30");
        day.Should().Be(new DateTime(2026, 10, 26, 14, 30, 0));
        hour.Should().Be(new DateTime(2026, 10, 27, 13, 30, 0));
    }

    [Fact] // boundary: 空/null 时间 —— 回落 10:00(.ics 先例)
    public void Compute_MissingTime_FallsBackTo1000()
    {
        var (dayNull, hourNull) = Walls(new DateOnly(2026, 10, 27), null);
        dayNull.Should().Be(new DateTime(2026, 10, 26, 10, 0, 0));
        hourNull.Should().Be(new DateTime(2026, 10, 27, 9, 0, 0));

        var (dayEmpty, _) = Walls(new DateOnly(2026, 10, 27), "  ");
        dayEmpty.Should().Be(new DateTime(2026, 10, 26, 10, 0, 0));
    }

    [Fact] // boundary: 特殊字符/乱码时间 —— 读不出即回落,不抛
    public void Compute_GarbageTime_FallsBackTo1000()
    {
        var (day, hour) = Walls(new DateOnly(2026, 10, 27), "下午两点");
        day.Should().Be(new DateTime(2026, 10, 26, 10, 0, 0));
        hour.Should().Be(new DateTime(2026, 10, 27, 9, 0, 0));

        var (day2, _) = Walls(new DateOnly(2026, 10, 27), "ten thirty");
        day2.Should().Be(new DateTime(2026, 10, 26, 10, 0, 0));
    }

    [Fact] // boundary: 非法时刻(25:00 / 14:60)—— 回落 10:00
    public void Compute_InvalidTime_FallsBackTo1000()
    {
        var (day, _) = Walls(new DateOnly(2026, 10, 27), "25:00");
        day.Should().Be(new DateTime(2026, 10, 26, 10, 0, 0));

        var (day2, _) = Walls(new DateOnly(2026, 10, 27), "14:60");
        day2.Should().Be(new DateTime(2026, 10, 26, 10, 0, 0));
    }

    [Fact] // 英文邀约形态:AM/PM 必须读出(第二道 invariant 精确格式)
    public void Compute_AmPmTime_Parses()
    {
        var (day, hour) = Walls(new DateOnly(2026, 10, 27), "2:00 PM");
        day.Should().Be(new DateTime(2026, 10, 26, 14, 0, 0));
        hour.Should().Be(new DateTime(2026, 10, 27, 13, 0, 0));

        var (dayAm, _) = Walls(new DateOnly(2026, 10, 27), "9:05 AM");
        dayAm.Should().Be(new DateTime(2026, 10, 26, 9, 5, 0));
    }

    [Fact] // boundary: 一位小时 + 带秒形态
    public void Compute_SingleDigitHour_AndSeconds_Parse()
    {
        var (day, _) = Walls(new DateOnly(2026, 10, 27), "9:05");
        day.Should().Be(new DateTime(2026, 10, 26, 9, 5, 0));

        var (day2, _) = Walls(new DateOnly(2026, 10, 27), "14:30:00");
        day2.Should().Be(new DateTime(2026, 10, 26, 14, 30, 0));
    }

    [Fact] // boundary: 日期临界 —— −1 小时跨日、−1 天跨月(非闰年 2 月 28 天)
    public void Compute_MonthBoundary_CrossesCorrectly()
    {
        var (day, hour) = Walls(new DateOnly(2026, 3, 1), "00:30");
        day.Should().Be(new DateTime(2026, 2, 28, 0, 30, 0));
        hour.Should().Be(new DateTime(2026, 2, 28, 23, 30, 0));
    }

    [Fact] // boundary: 日期临界 —— 闰年 2/29 与跨年
    public void Compute_LeapDay_AndYearBoundary()
    {
        var (day, _) = Walls(new DateOnly(2028, 2, 29), "10:00");
        day.Should().Be(new DateTime(2028, 2, 28, 10, 0, 0));

        var (dayY, hourY) = Walls(new DateOnly(2027, 1, 1), "00:15");
        dayY.Should().Be(new DateTime(2026, 12, 31, 0, 15, 0));
        hourY.Should().Be(new DateTime(2026, 12, 31, 23, 15, 0));
    }

    [Fact] // 开场时刻本身:ComputeStart 与 Compute 口径一致
    public void ComputeStart_MatchesComputeAnchor()
    {
        var start = ReminderSchedule.ComputeStart(new DateOnly(2026, 10, 27), "14:30");
        start.DateTime.Should().Be(new DateTime(2026, 10, 27, 14, 30, 0));
    }
}
