using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票 63:模式解析规则(SessionModeParsing)的回归钉。
/// 背景:旧实现 Enum.TryParse 失败静默回退 TopicDrill,前端误传 FullMock 时
/// 用户选的「完整模拟」实际建成主题专项会话且无任何提示。现口径:只有逐字
/// 等于枚举名(不区分大小写,允许首尾空白)才合法,其余一律失败,由控制器回 400。
/// 用例按 boundary 类别组织并逐类标注:
///   valid(合法名)/ case(大小写)/ 空-null / 旧误值-非法名 / 数字(0 与负数)/
///   特殊字符-Unicode / 超长 / 逗号组合(Enum.TryParse 的已知陷阱)。
/// 本组只测纯函数,无 DB。
/// </summary>
public sealed class SessionModeParsingTests
{
    // ---------- valid:四个合法枚举名逐字 → 对应枚举 ----------
    [Theory]
    [InlineData("TopicDrill", SessionMode.TopicDrill)]
    [InlineData("CompanyStyle", SessionMode.CompanyStyle)]
    [InlineData("WeaknessFocus", SessionMode.WeaknessFocus)]
    [InlineData("FullLoop", SessionMode.FullLoop)]
    public void ValidNames_ParseToTheirEnum(string raw, SessionMode expected)
    {
        Assert.True(SessionModeParsing.TryParse(raw, out var mode));
        Assert.Equal(expected, mode);
    }

    // ---------- case:大小写不敏感(与旧 TryParse ignoreCase 口径一致) ----------
    [Theory]
    [InlineData("fullloop")]
    [InlineData("FULLLOOP")]
    [InlineData("topicdrill")]
    public void CaseVariants_AreAccepted(string raw)
        => Assert.True(SessionModeParsing.TryParse(raw, out _));

    // ---------- boundary 空/null:一律失败 ----------
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EmptyOrNull_Fails(string? raw)
        => Assert.False(SessionModeParsing.TryParse(raw, out _));

    // ---------- boundary 旧误值/非法名:FullMock 是本票的 bug 本体 ----------
    [Theory]
    [InlineData("FullMock")]        // 旧前端提交值,后端从无此枚举
    [InlineData("WeaknessDrill")]   // 任务口径里出现过、后端不存在的值
    [InlineData("BogusMode")]
    [InlineData("FullLoop2")]
    [InlineData("Topic")]
    public void UnknownNames_Fail(string raw)
        => Assert.False(SessionModeParsing.TryParse(raw, out _));

    // ---------- boundary 数字(0 与负数):Enum.TryParse 对数字串会返回 true,必须挡 ----------
    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-1")]
    public void NumericStrings_Fail(string raw)
        => Assert.False(SessionModeParsing.TryParse(raw, out _));

    // ---------- boundary 特殊字符/Unicode:标点、内部空格、中文名、emoji 一律失败 ----------
    [Theory]
    [InlineData("FullLoop!")]
    [InlineData("Full Loop")]
    [InlineData("Full_Loop")]
    [InlineData("完整模拟")]
    [InlineData("FullLoop🎯")]
    [InlineData("FullLoop\0")]
    public void SpecialOrUnicode_Fails(string raw)
        => Assert.False(SessionModeParsing.TryParse(raw, out _));

    // ---------- boundary 逗号组合:Enum.TryParse 会把 "A,B" 按位或解析,必须挡 ----------
    [Theory]
    [InlineData("TopicDrill,CompanyStyle")]
    [InlineData("TopicDrill, CompanyStyle")]
    public void CommaCombinations_Fail(string raw)
        => Assert.False(SessionModeParsing.TryParse(raw, out _));

    // ---------- boundary 超长:超长串与「合法名+长后缀」一律失败 ----------
    [Fact]
    public void OverlongStrings_Fail()
    {
        Assert.False(SessionModeParsing.TryParse(new string('x', 10_000), out _));
        Assert.False(SessionModeParsing.TryParse("FullLoop" + new string('x', 1_000), out _));
    }

    // ---------- 首尾空白:trim 后合法即放行(容忍复制粘贴带空格) ----------
    [Fact]
    public void SurroundingWhitespace_IsTrimmed()
    {
        Assert.True(SessionModeParsing.TryParse("  FullLoop  ", out var mode));
        Assert.Equal(SessionMode.FullLoop, mode);
    }

    // ---------- 报错文案清单与枚举保持一致 ----------
    [Fact]
    public void ValidNames_ListsAllFourModes()
        => Assert.Equal("TopicDrill / CompanyStyle / WeaknessFocus / FullLoop",
            SessionModeParsing.ValidNames);
}
