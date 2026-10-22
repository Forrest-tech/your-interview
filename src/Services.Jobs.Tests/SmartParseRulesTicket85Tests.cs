using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 85:InvitePasteParser 英文日期分支去宿主 culture 依赖(显式格式表 + InvariantCulture)。
/// 期望口径 = 票 28 锁定的旧 en-US TryParse 行为,逐形态以探针双宿主对照实测
/// (evidence-85/probe-*.txt):含无后缀 24h 时刻、"14:30 PM" 怪形态能解出、
/// 纯小时无分钟解不出等旧口径细节。本文件用例在普通宿主与
/// DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 宿主下都必须全过——后者是本票的核心验收。
/// 时间断言只比日期/时刻分量(AssumeLocal 偏移随宿主时区,同票 28 口径)。
/// </summary>
public sealed class SmartParseRulesTicket85Tests
{
    private static void AssertTime(DateTimeOffset? actual, int y, int mo, int d, int h, int mi)
    {
        Assert.NotNull(actual);
        var t = actual!.Value;
        Assert.Equal((y, mo, d, h, mi), (t.Year, t.Month, t.Day, t.Hour, t.Minute));
    }

    // ---------- 英文月份名形态 ----------

    [Fact] // 全月名:正则 mon 组只取前三字母 + [a-z]* 吞余,构造串恒为缩写形态
    public void Parse_FullMonthName_Parses()
    {
        var d = InvitePasteParser.Parse("Interview scheduled: October 5, 2026 at 2:30 PM via Zoom");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 30);
    }

    [Fact] // 序数后缀 st/nd/rd/th 被正则剥除
    public void Parse_MonthNameOrdinalDay_Parses()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5th, 2026 2:30 PM");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 30);
    }

    [Fact] // 无时刻 → 默认 9:00 AM(旧口径)
    public void Parse_MonthNameNoTime_DefaultsToNineAm()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5, 2026 via Zoom");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 9, 0);
    }

    [Fact] // 小写 pm 紧贴时刻
    public void Parse_MonthNameLowercasePmAttached_Parses()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5, 2026 2:30pm");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 30);
    }

    [Fact] // 只有小时 + PM(无分钟)
    public void Parse_MonthNameHourOnlyWithPm_Parses()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5, 2026 9 PM");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 21, 0);
    }

    [Fact] // 无 AM/PM 后缀的 24h 时刻:旧 en-US 解析解出 14:30(H:mm 格式承接)
    public void Parse_MonthName24HourNoSuffix_Parses()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5, 2026 14:30");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 30);
    }

    [Fact] // 怪形态 "14:30 PM":旧 en-US 解析照样解出 14:30(H:mm tt 承接,探针实证)
    public void Parse_MonthName24HourWithPmSuffix_ParsesAsOldBehavior()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5, 2026 14:30 PM");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 30);
    }

    [Fact] // 12 小时制临界:12:00 AM = 00:00,12:00 PM = 12:00
    public void Parse_MonthNameMidnightAndNoon_Parses()
    {
        AssertTime(InvitePasteParser.Parse("Interview on Oct 5, 2026 12:00 AM").ScheduledAt, 2026, 10, 5, 0, 0);
        AssertTime(InvitePasteParser.Parse("Interview on Oct 5, 2026 12:00 PM").ScheduledAt, 2026, 10, 5, 12, 0);
    }

    [Fact] // boundary 日期/时区临界:闰年 2/29(月份名形态)解出
    public void Parse_MonthNameLeapDay_Parses()
    {
        var d = InvitePasteParser.Parse("Interview on Feb 29, 2028 10:00 AM");
        AssertTime(d.ScheduledAt, 2028, 2, 29, 10, 0);
    }

    [Fact] // boundary:非闰年 2/29 是非法日期 → 不解出、不抛异常
    public void Parse_MonthNameNonLeapFeb29_ReturnsNullNoThrow()
    {
        var d = InvitePasteParser.Parse("Interview on Feb 29, 2026 10:00 AM");
        Assert.Null(d.ScheduledAt);
    }

    [Fact] // boundary:非法日期字符串(日 32 / 日 99 / 时 99)→ 不解出、不抛异常
    public void Parse_MonthNameInvalidDates_ReturnNullNoThrow()
    {
        Assert.Null(InvitePasteParser.Parse("Interview on Oct 32, 2026 2:30 PM").ScheduledAt);
        Assert.Null(InvitePasteParser.Parse("Interview on Oct 99, 2026 2:30 PM").ScheduledAt);
        Assert.Null(InvitePasteParser.Parse("Interview on Oct 5, 2026 99:00 AM").ScheduledAt);
    }

    [Fact] // boundary 旧口径:纯小时无分钟无后缀("…2026 2")旧 en-US 解析解不出 → 保持 null
    public void Parse_MonthNameBareHourNoMinutes_ReturnsNullAsOldBehavior()
    {
        var d = InvitePasteParser.Parse("Interview on Oct 5, 2026 2");
        Assert.Null(d.ScheduledAt);
    }

    // ---------- 斜杠形态(MM/dd 现口径) ----------

    [Fact] // boundary 月日序歧义:按现口径 MM/dd 优先 —— 01/02 = 1 月 2 日(非 2 月 1 日)
    public void Parse_SlashAmbiguousDate_MonthFirstAsCurrentBehavior()
    {
        var d = InvitePasteParser.Parse("Phone screen on 01/02/2026 9:00 AM");
        AssertTime(d.ScheduledAt, 2026, 1, 2, 9, 0);
        var d2 = InvitePasteParser.Parse("Phone screen on 05/10/2026 9:00 AM");
        AssertTime(d2.ScheduledAt, 2026, 5, 10, 9, 0);
    }

    [Fact] // 无时刻 → 默认 9:00 AM
    public void Parse_SlashNoTime_DefaultsToNineAm()
    {
        var d = InvitePasteParser.Parse("Phone screen on 10/05/2026 with the recruiter");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 9, 0);
    }

    [Fact] // 无后缀 24h 时刻(斜杠形态)
    public void Parse_Slash24HourNoSuffix_Parses()
    {
        var d = InvitePasteParser.Parse("Phone screen on 10/05/2026 14:00");
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 0);
    }

    [Fact] // boundary:闰年 2/29(斜杠形态)解出
    public void Parse_SlashLeapDay_Parses()
    {
        var d = InvitePasteParser.Parse("Phone screen on 02/29/2028 9:00 AM");
        AssertTime(d.ScheduledAt, 2028, 2, 29, 9, 0);
    }

    [Fact] // boundary:非法日期(非闰 2/29、2/30、4/31)→ 不解出、不抛异常
    public void Parse_SlashInvalidDates_ReturnNullNoThrow()
    {
        Assert.Null(InvitePasteParser.Parse("Phone screen on 02/29/2026 9:00 AM").ScheduledAt);
        Assert.Null(InvitePasteParser.Parse("Phone screen on 02/30/2026 9:00 AM").ScheduledAt);
        Assert.Null(InvitePasteParser.Parse("Phone screen on 04/31/2026 9:00 AM").ScheduledAt);
    }

    [Fact] // boundary 旧口径:斜杠形态纯小时无分钟同样解不出
    public void Parse_SlashBareHourNoMinutes_ReturnsNullAsOldBehavior()
    {
        var d = InvitePasteParser.Parse("Phone screen on 10/05/2026 2");
        Assert.Null(d.ScheduledAt);
    }

    // ---------- 不变项守卫:中文分支与 ISO 分支在同批不被扰动 ----------

    [Fact] // 中文日期分支(不变项):行为与票 28 锁定一致
    public void Parse_ChineseDate_BranchUnchanged()
    {
        var d = InvitePasteParser.Parse("面试时间：10月5日 14:30，公司： 环球支付");
        Assert.NotNull(d.ScheduledAt);
        Assert.Equal((10, 5, 14, 30), (d.ScheduledAt!.Value.Month, d.ScheduledAt.Value.Day, d.ScheduledAt.Value.Hour, d.ScheduledAt.Value.Minute));
    }

    [Fact] // ISO 分支(不变项,本就 InvariantCulture):行为不变
    public void Parse_IsoDate_BranchUnchanged()
    {
        var d = InvitePasteParser.Parse("Interview on 2026-10-20 10:00 at office");
        AssertTime(d.ScheduledAt, 2026, 10, 20, 10, 0);
    }
}
