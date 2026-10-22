using System.Globalization;
using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票 28(ARC-08a):SmartParseRules.InvitePasteParser 特征测试(与 JdPasteParser 同文件同批锁定)。
/// 时间断言只比日期/时刻分量,不比 UTC 偏移——解析器用 AssumeLocal,偏移随运行机时区变,
/// 断偏移会把测试写成环境相关的 flaky(总纲第七节质量口径)。
/// 中文相对日期(10月5日)用当年年份推算,同理只断月/日/时刻。
/// </summary>
public sealed class SmartParseRulesInviteTests
{
    private static void AssertTime(DateTimeOffset? actual, int y, int mo, int d, int h, int mi)
    {
        Assert.NotNull(actual);
        var t = actual!.Value;
        Assert.Equal((y, mo, d, h, mi), (t.Year, t.Month, t.Day, t.Hour, t.Minute));
    }

    [Fact]
    public void Parse_ChineseInvite_RoundStageTimeFormatExtracted()
    {
        var d = InvitePasteParser.Parse("您好，邀请您参加第三轮系统设计面试，时间：10月5日 14:30，公司： 环球支付，职位： 高级工程师，面试官： 王五，形式为视频面试。");
        Assert.Equal(3, d.RoundNo);
        Assert.Equal("SystemDesign", d.Stage);
        Assert.Equal("Video", d.Format);
        Assert.NotNull(d.ScheduledAt);
        // 中文日期无年份:取当年(跨年兜底可能 +1),只断月/日/时刻分量
        Assert.Equal((10, 5, 14, 30), (d.ScheduledAt!.Value.Month, d.ScheduledAt.Value.Day, d.ScheduledAt.Value.Hour, d.ScheduledAt.Value.Minute));
        // 现状特征:单行多标签时 (.+) 贪婪到行尾,公司/职位/面试官互相吞尾——特征锁定
        Assert.Equal("环球支付，职位： 高级工程师，面试官： 王五，形式为视频面试。", d.Company);
        Assert.Equal("高级工程师，面试官： 王五，形式为视频面试。", d.Role);
        Assert.Equal("王五，形式为视频面试。", d.Interviewer);
        Assert.Equal(5, InvitePasteParser.CountFilled(d));
    }

    [Fact]
    public void Parse_FinalOnsiteInvite_AddressAsLocation()
    {
        var d = InvitePasteParser.Parse("Final round onsite interview at our office: 123 King Street, Toronto. Hiring manager interview on 2026-10-20 10:00. Company: Globex");
        Assert.Equal("Final", d.Stage);
        Assert.Equal("Onsite", d.Format);
        Assert.Equal("123 King Street", d.Location);
        Assert.Equal("Globex", d.Company);
        AssertTime(d.ScheduledAt, 2026, 10, 20, 10, 0);
    }

    [Fact]
    public void Parse_FirstRoundChineseInvite_PhoneScreen()
    {
        var d = InvitePasteParser.Parse("第一轮面试邀请：电话面试，时间 2026-11-01 09:30");
        Assert.Equal(1, d.RoundNo);
        Assert.Equal("Screen", d.Stage);
        Assert.Equal("Phone", d.Format);
        AssertTime(d.ScheduledAt, 2026, 11, 1, 9, 30);
    }

    [Fact] // 公司名兜底:无标签时取发件邮箱域名(非免费邮箱)
    public void Parse_InviteCompanyFromEmailDomain()
    {
        var d = InvitePasteParser.Parse("Interview invitation: technical coding interview, round 2, via Zoom. Contact: bob@acme.com");
        Assert.Equal("Acme", d.Company);
        Assert.Equal(2, d.RoundNo);
        Assert.Equal("Technical", d.Stage);
        Assert.Equal("Video", d.Format);
    }

    [Fact] // 免费邮箱域名不冒充公司名
    public void Parse_InviteFreeMailDomain_NotUsedAsCompany()
    {
        var d = InvitePasteParser.Parse("Interview invitation - contact: someone@gmail.com - behavioral interview");
        Assert.Null(d.Company);
        Assert.Equal("Behavioral", d.Stage);
    }

    [Fact] // 公司名兜底:"interview at X" 形态
    public void Parse_InviteCompanyAfterAt()
    {
        var d = InvitePasteParser.Parse("You have an interview at Shopify for the designer role on 2026-11-03 15:00 via Google Meet");
        Assert.Equal("Shopify", d.Company);
        Assert.Equal("Video", d.Format);
        AssertTime(d.ScheduledAt, 2026, 11, 3, 15, 0);
    }

    [Fact] // "终面"不给具体轮次:猜错轮次比留空更糟(代码注释口径)
    public void Parse_FinalRoundChinese_NoRoundNumber()
    {
        var d = InvitePasteParser.Parse("终面邀请：2026-12-01 10:00 现场面试");
        Assert.Null(d.RoundNo);
        Assert.Equal("Final", d.Stage);
        Assert.Equal("Onsite", d.Format);
        AssertTime(d.ScheduledAt, 2026, 12, 1, 10, 0);
    }

    [Fact]
    public void Parse_RoundHashForm_RoundExtracted()
    {
        var d = InvitePasteParser.Parse("Round #3 coding interview invitation");
        Assert.Equal(3, d.RoundNo);
        Assert.Equal("Technical", d.Stage);
    }

    [Fact] // 现状特征:英文轮次正则只认一位数字,"Round 10" 解成第 1 轮——特征锁定,报告观察项
    public void Parse_RoundTen_ReadAsRoundOne()
    {
        var d = InvitePasteParser.Parse("Round 10 coding interview invitation");
        Assert.Equal(1, d.RoundNo);
    }

    [Fact]
    public void Parse_HrScreenInvite_StageAndFormat()
    {
        var d = InvitePasteParser.Parse("HR screen invitation: we will call you for a phone screen next week");
        Assert.Equal("Screen", d.Stage);
        Assert.Equal("Phone", d.Format);
    }

    [Fact] // boundary: 空输入——状态恒 Partial,字段全空
    public void Parse_Empty_BoundaryEmpty_ReturnsPartialAllNull()
    {
        var d = InvitePasteParser.Parse("");
        Assert.Equal(ParseOutcome.Partial, d.Status);
        Assert.Null(d.Company);
        Assert.Null(d.RoundNo);
        Assert.Null(d.ScheduledAt);
        Assert.Equal(0, InvitePasteParser.CountFilled(d));
    }

    [Fact] // boundary: null 输入——现状为抛 NullReferenceException
    public void Parse_Null_BoundaryNull_ThrowsNullReference()
        => Assert.Throws<NullReferenceException>(() => InvitePasteParser.Parse(null!));

    // known-gap(非票 29 范围,报告已上报协调者另立票):
    // 英文月份名日期分支(Oct 5, 2026 / 10/05/2026)走 GetCultureInfo("en-US")。
    // 行为随**宿主进程**的 globalization 模式分裂:
    //  - 生产/常规测试宿主:仓库全局 InvariantGlobalization=true(Directory.Build.props,
    //    服务与 testhost 的 runtimeconfig 均为 invariant)→ GetCultureInfo 抛
    //    CultureNotFoundException,整条解析抛错(探针 evidence-28/probe-output.txt 实证)。
    //  - 沙盒定版的 xunit console runner 宿主:其自身 runtimeconfig 未开 invariant,
    //    文化可用 → 正常解出时间(本套件在此通道实测)。
    // 两种宿主下分别断言各自的确定行为,不是放宽断言:分裂本身就是要钉住的现状。
    private static bool HostIsInvariant
    {
        get
        {
            try { _ = CultureInfo.GetCultureInfo("en-US"); return false; }
            catch (CultureNotFoundException) { return true; }
        }
    }

    [Fact]
    public void Parse_EnglishMonthNameDate_KnownGap_BehaviorDependsOnHostGlobalization()
    {
        const string raw = "Round 2 will be on Oct 5, 2026 at 2:30 PM via Zoom. Interviewer: Jane Smith.";
        if (HostIsInvariant)
        {
            Assert.Throws<CultureNotFoundException>(() => InvitePasteParser.Parse(raw));
            return;
        }
        var d = InvitePasteParser.Parse(raw);
        Assert.Equal(2, d.RoundNo);
        Assert.Equal("Video", d.Format);
        Assert.Equal("Jane Smith.", d.Interviewer); // 现状:标签值 (.+) 贪婪到行尾,句末句号一并吞入
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 30);
    }

    [Fact]
    public void Parse_SlashDateUsForm_KnownGap_BehaviorDependsOnHostGlobalization()
    {
        const string raw = "Phone screen on 10/05/2026 2:00pm with the recruiter.";
        if (HostIsInvariant)
        {
            Assert.Throws<CultureNotFoundException>(() => InvitePasteParser.Parse(raw));
            return;
        }
        var d = InvitePasteParser.Parse(raw);
        Assert.Equal("Screen", d.Stage);
        Assert.Equal("Phone", d.Format);
        AssertTime(d.ScheduledAt, 2026, 10, 5, 14, 0);
    }
}
