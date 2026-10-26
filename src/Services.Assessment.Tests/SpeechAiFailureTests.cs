using FluentAssertions;
using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票117:Assessment speech/TTS 面 Azure 失败对外口径。改前控制器三处 catch
/// (pronunciation/assess、speech/test、tts)把 AzureAuthException /
/// InvalidOperationException 的 Message 直传 502 detail,而消息拼有 Azure
/// 上游响应 body 或底层异常原文;PracticeSettingsHandlers 两处同病
/// (Description 塞原文经控制器 detail 下发)。口径按票47/116 定版:
/// 固定脱敏 detail + title/code 沿用现值,原文只进服务端日志。
/// 本测试钉映射纯逻辑 SpeechAiFailure:只按失败类取口径,签名上就不
/// 接受异常对象 —— 结构上不可能读到 ex.Message。
/// 不适用类别:日期时区——本逻辑无此类入参;文件类见
/// StorageErrorWordingTests(TryEnsureWritable 脱敏)。
/// </summary>
public sealed class SpeechAiFailureTests
{
    // ---------- 常规:三失败类 → 502 + 沿用 title + 固定 detail ----------

    [Fact]
    public void InvalidCredential_maps_to_502_fixed_detail()
    {
        var p = SpeechAiFailure.For(SpeechAiFailure.Kind.InvalidCredential);
        p.StatusCode.Should().Be(502);
        p.Title.Should().Be("密钥或区域无效");
        p.Detail.Should().Be(SpeechAiFailure.InvalidCredentialDetail);
    }

    [Fact]
    public void Pronunciation_maps_to_502_fixed_detail()
    {
        var p = SpeechAiFailure.For(SpeechAiFailure.Kind.Pronunciation);
        p.StatusCode.Should().Be(502);
        p.Title.Should().Be("发音评估失败");
        p.Detail.Should().Be(SpeechAiFailure.PronunciationFailedDetail);
    }

    [Fact]
    public void Synthesis_maps_to_502_fixed_detail()
    {
        var p = SpeechAiFailure.For(SpeechAiFailure.Kind.Synthesis);
        p.StatusCode.Should().Be(502);
        p.Title.Should().Be("语音合成失败");
        p.Detail.Should().Be(SpeechAiFailure.SynthesisFailedDetail);
    }

    // ---------- handler 侧 code/文案锁(B1/B2:控制器据 code 分流、detail 用 Description) ----------

    [Fact]
    public void Handler_codes_keep_existing_values()
    {
        // 改前现值逐字守恒:TestSpeechCredential 的 Speech.InvalidCredential、
        // SaveSpeechSetting 的 Speech.CredentialRejected(控制器 502 分支据此判定)
        SpeechAiFailure.InvalidCredentialCode.Should().Be("Speech.InvalidCredential");
        SpeechAiFailure.CredentialRejectedCode.Should().Be("Speech.CredentialRejected");
        SpeechAiFailure.InvalidCredentialCode.Should().NotBe(SpeechAiFailure.CredentialRejectedCode);
    }

    [Fact]
    public void CredentialRejected_detail_is_fixed_wording_without_raw_suffix()
    {
        // 改前形态 = 自有前缀 + ":" + ex.Message(Azure body);改后只剩定稿文案
        SpeechAiFailure.CredentialRejectedDetail.Should().Be("Azure 拒绝了该密钥或区域,凭据未保存。");
        SpeechAiFailure.CredentialRejectedDetail.Should().NotContain(":");
        SpeechAiFailure.CredentialRejectedDetail.Should().NotContain("：");
    }

    // ---------- 防外泄锁:三类 detail 绝不含异常原文形态 ----------

    [Theory]
    // Azure 上游响应 body 形态(PingWith/Synthesize 拼入异常的原文)
    [InlineData("{\"error\":{\"code\":\"401\",\"message\":\"Access denied\"}}")]
    // 连接细节形态
    [InlineData("Connection refused (127.0.0.1:443)")]
    // boundary-特殊字符/Unicode:中文+密钥样串+换行
    [InlineData("失败\n\tkey=sk-SECRET http://proxy:8080")]
    // 堆栈样文本
    [InlineData("at Microsoft.CognitiveServices.Speech")]
    public void Details_never_contain_raw_fragments(string rawFragment)
    {
        foreach (var kind in Enum.GetValues<SpeechAiFailure.Kind>())
        {
            var p = SpeechAiFailure.For(kind);
            p.Detail.Should().NotContain(rawFragment);
            p.Detail.Length.Should().BeLessThan(200, "detail 是定长定稿文案");
        }
        SpeechAiFailure.CredentialRejectedDetail.Should().NotContain(rawFragment);
    }

    [Fact]
    public void Details_are_fixed_regardless_of_kind_repetition()
    {
        // boundary-空/重复调用:口径是常量,不随任何输入漂移
        SpeechAiFailure.For(SpeechAiFailure.Kind.InvalidCredential)
            .Should().Be(SpeechAiFailure.InvalidCredentialProblem);
        SpeechAiFailure.For(SpeechAiFailure.Kind.Pronunciation)
            .Should().Be(SpeechAiFailure.PronunciationFailedProblem);
        SpeechAiFailure.For(SpeechAiFailure.Kind.Synthesis)
            .Should().Be(SpeechAiFailure.SynthesisFailedProblem);
    }

    // ---------- 常量锁:状态码与三 detail 互异 ----------

    [Fact]
    public void Constants_are_locked()
    {
        SpeechAiFailure.UpstreamStatusCode.Should().Be(502);
        new[]
        {
            SpeechAiFailure.InvalidCredentialDetail,
            SpeechAiFailure.PronunciationFailedDetail,
            SpeechAiFailure.SynthesisFailedDetail,
            SpeechAiFailure.CredentialRejectedDetail,
        }.Should().OnlyHaveUniqueItems();
    }
}
