using FluentAssertions;
using YourInterview.Services.Assessment.Application;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票116 F-H2:Mock 编排链(generate/score-ai)的 AiGateway 失败对外口径。
/// 改前 MapAiError 把 AiGatewayException.Message 直传 502 detail —— 连接
/// 异常原文(含代理/连接细节)外泄给终端用户(真人化验收实测)。口径按
/// 票47 定版:固定脱敏 detail + code 区分失败类,原文只进服务端日志。
/// 本测试钉映射纯逻辑:只看 IsConfigurationError 与失败类,绝不读 Message。
/// 不适用类别:日期时区/文件——本逻辑无此类入参。
/// </summary>
public sealed class MockAiFailureTests
{
    // ---------- 常规:非配置错误 → 502 + 按失败类区分 code/detail ----------

    [Fact]
    public void Generation_upstream_failure_maps_to_502_fixed_detail()
    {
        var ex = new AiGatewayException("无法连接 AiGateway:Connection refused (127.0.0.1:5268)", false);
        var p = MockAiFailure.For(ex, scoring: false);
        p.StatusCode.Should().Be(502);
        p.Code.Should().Be("Mock.AiGenerationFailed");
        p.Title.Should().Be("AI 出题失败");
        p.Detail.Should().Be(MockAiFailure.GenerationFailedDetail);
    }

    [Fact]
    public void Scoring_upstream_failure_maps_to_502_scoring_code()
    {
        var ex = new AiGatewayException("无法连接 AiGateway:Connection refused", false);
        var p = MockAiFailure.For(ex, scoring: true);
        p.StatusCode.Should().Be(502);
        p.Code.Should().Be("Mock.AiScoringFailed");
        p.Title.Should().Be("AI 评分失败");
        p.Detail.Should().Be(MockAiFailure.ScoringFailedDetail);
    }

    // ---------- 常规:配置错误 → 503 口径逐字守恒(改前定版文案) ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Configuration_error_maps_to_503_regardless_of_chain(bool scoring)
    {
        var ex = new AiGatewayException("未配置 LLM 凭据", isConfigurationError: true);
        var p = MockAiFailure.For(ex, scoring);
        p.StatusCode.Should().Be(503);
        p.Code.Should().Be("Mock.AiNotConfigured");
        p.Title.Should().Be("AI 未配置");
        p.Detail.Should().Be("AI 服务尚未配置凭据,请先在设置里保存 LLM 密钥后再试。");
    }

    // ---------- 防外泄锁:detail 绝不包含异常原文(任意形态) ----------

    [Theory]
    // 验收实测形态:沙盒代理原文
    [InlineData("muse: Other TCP connections is turned off in the sandbox environment")]
    // 连接细节形态:地址/端口/内部主机名
    [InlineData("无法连接 AiGateway:HttpRequestException: Connection refused (ai.internal:5268)")]
    // boundary-特殊字符/Unicode:中文+符号+换行
    [InlineData("连接失败\n\t密钥=sk-SECRET 代理 http://proxy:8080")]
    // boundary-空消息
    [InlineData("")]
    public void Detail_never_contains_exception_message(string rawMessage)
    {
        var ex = new AiGatewayException(rawMessage, false);
        foreach (var scoring in new[] { false, true })
        {
            var p = MockAiFailure.For(ex, scoring);
            if (rawMessage.Length > 0)
                p.Detail.Should().NotContain(rawMessage);
            p.Detail.Should().Be(scoring
                ? MockAiFailure.ScoringFailedDetail
                : MockAiFailure.GenerationFailedDetail);
            p.Detail.Length.Should().BeLessThan(200, "detail 是定长定稿文案");
        }
    }

    [Fact]
    public void Detail_never_contains_exception_message_very_long()
    {
        // boundary-超长:10000 字符原文 → detail 仍定长定稿
        var ex = new AiGatewayException(new string('x', 10000), false);
        var p = MockAiFailure.For(ex, scoring: false);
        p.Detail.Should().Be(MockAiFailure.GenerationFailedDetail);
        p.Detail.Should().NotContain("xxxx");
    }

    // ---------- 常量锁:三 code 互异、状态码口径 ----------

    [Fact]
    public void Constants_are_locked()
    {
        MockAiFailure.UpstreamStatusCode.Should().Be(502);
        MockAiFailure.NotConfiguredStatusCode.Should().Be(503);
        new[]
        {
            MockAiFailure.NotConfiguredCode,
            MockAiFailure.GenerationFailedCode,
            MockAiFailure.ScoringFailedCode
        }.Should().OnlyHaveUniqueItems();
        MockAiFailure.GenerationFailedDetail.Should().NotBe(MockAiFailure.ScoringFailedDetail);
    }
}
