using FluentAssertions;
using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票47(COD-20):FetchLinkedIn 失败分类与 ProblemDetails 口径(纯逻辑 LinkedInFetchFailure)。
/// 锁三件事:①哨兵 needs_login 精确命中 → NeedsLogin;②其余一切原始 error 一律
/// FetchFailed;③产出的 title/detail/code/状态码为定稿口径,且 detail 绝不含
/// fetcher 原始 error(内部异常原文不外泄)。
/// </summary>
public sealed class LinkedInFetchFailureTests
{
    // 常规:fetcher 哨兵 needs_login(页面被重定向到登录/校验页)→ NeedsLogin 全口径
    [Fact]
    public void NeedsLogin_FullProfile()
    {
        var kind = LinkedInFetchFailure.Classify("needs_login");
        kind.Should().Be(LinkedInFetchFailure.Kind.NeedsLogin);
        LinkedInFetchFailure.CodeFor(kind).Should().Be("linkedin_needs_login");
        LinkedInFetchFailure.TitleFor(kind).Should().Be("LinkedIn login required");
        LinkedInFetchFailure.DetailFor(kind).Should().Contain("sign-in");
        LinkedInFetchFailure.StatusCode.Should().Be(502);
    }

    // 常规:fetcher 另一哨兵 empty(页面抓到但内容为空)→ 一般抓取失败
    [Fact]
    public void EmptySentinel_IsFetchFailed()
    {
        var kind = LinkedInFetchFailure.Classify("empty");
        kind.Should().Be(LinkedInFetchFailure.Kind.FetchFailed);
        LinkedInFetchFailure.CodeFor(kind).Should().Be("linkedin_fetch_failed");
        LinkedInFetchFailure.TitleFor(kind).Should().Be("LinkedIn fetch failed");
        LinkedInFetchFailure.StatusCode.Should().Be(502);
    }

    // 常规:fetcher catch 透传的异常原文(浏览器缺失)→ FetchFailed,且 detail 不含原文
    [Fact]
    public void RawExceptionMessage_IsFetchFailed_AndNotLeaked()
    {
        const string raw = "Playwright browser not installed. Rebuild Docker image.";
        var kind = LinkedInFetchFailure.Classify(raw);
        kind.Should().Be(LinkedInFetchFailure.Kind.FetchFailed);
        LinkedInFetchFailure.DetailFor(kind).Should().NotContain(raw);
        LinkedInFetchFailure.DetailFor(kind).Should().NotContain("Playwright");
    }

    // boundary(空/null):null、空串、纯空白 → FetchFailed(不误报需要登录)
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NullOrBlank_IsFetchFailed_BoundaryEmpty(string? raw)
    {
        LinkedInFetchFailure.Classify(raw).Should().Be(LinkedInFetchFailure.Kind.FetchFailed);
    }

    // boundary(精确匹配锁):大小写/前后空白变体不是哨兵 → FetchFailed
    // 分类器只认 fetcher 实际产出的逐字 "needs_login",不做模糊匹配
    [Theory]
    [InlineData("NEEDS_LOGIN")]
    [InlineData("Needs_Login")]
    [InlineData(" needs_login")]
    [InlineData("needs_login ")]
    [InlineData("needs_login\n")]
    public void SentinelVariants_AreFetchFailed_BoundaryExactMatch(string raw)
    {
        LinkedInFetchFailure.Classify(raw).Should().Be(LinkedInFetchFailure.Kind.FetchFailed);
    }

    // boundary(特殊字符/Unicode):含 Unicode、换行、堆栈样文本的任意原文 → FetchFailed 且不外泄
    [Theory]
    [InlineData("抓取超时:net::ERR_CONNECTION_TIMED_OUT at https://www.linkedin.com/jobs/view/1")]
    [InlineData("Timeout 30000ms exceeded.\n  at Playwright.Core.Page.GotoAsync(...)")]
    [InlineData("✓ checkpoint ✓ セッション切れ")]
    public void ArbitraryRawText_IsFetchFailed_AndNotLeaked_BoundaryUnicode(string raw)
    {
        var kind = LinkedInFetchFailure.Classify(raw);
        kind.Should().Be(LinkedInFetchFailure.Kind.FetchFailed);
        LinkedInFetchFailure.DetailFor(kind).Should().NotContain(raw);
    }

    // boundary(超长):10000 字符原文 → FetchFailed;detail 为定长定稿文案(长度与 FetchFailed 定稿一致)
    [Fact]
    public void VeryLongRaw_IsFetchFailed_BoundaryLength()
    {
        var raw = new string('x', 10_000);
        var kind = LinkedInFetchFailure.Classify(raw);
        kind.Should().Be(LinkedInFetchFailure.Kind.FetchFailed);
        LinkedInFetchFailure.DetailFor(kind).Length.Should().BeLessThan(300);
        LinkedInFetchFailure.DetailFor(kind).Should().NotContain("xxx");
    }

    // 口径常量锁:两类 code 不相等、不为空;400 侧 code 沿用改前匿名体的 error 值
    [Fact]
    public void Codes_AreDistinctAndStable()
    {
        LinkedInFetchFailure.NeedsLoginCode.Should().Be("linkedin_needs_login");
        LinkedInFetchFailure.FetchFailedCode.Should().Be("linkedin_fetch_failed");
        LinkedInFetchFailure.NotLinkedInJobUrlCode.Should().Be("not_linkedin_job_url");
        LinkedInFetchFailure.NeedsLoginCode.Should().NotBe(LinkedInFetchFailure.FetchFailedCode);
        LinkedInFetchFailure.NeedsLoginSentinel.Should().Be("needs_login");
    }
}
