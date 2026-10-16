using FluentAssertions;
using Xunit;
using YourInterview.Analysis.Worker.Analysis;

namespace YourInterview.Analysis.Worker.Tests;

/// <summary>
/// SpeakerLabelStage 单元测试 —— 覆盖 marker 解析、继承、降级与回归。
/// 直接调 SpeakerLabelStage.Label(PipelineState),不碰日志与计时。
/// </summary>
public sealed class SpeakerLabelStageTests
{
    private static PipelineState StateOf(params (string Text, string Speaker, double Confidence)[] segs)
    {
        var state = new PipelineState();
        var i = 0;
        foreach (var (text, speaker, conf) in segs)
            state.Segments.Add(new TranscriptSegment(i++, i, i + 1, text, speaker, conf));
        return state;
    }

    private static PipelineState UnknownStateOf(params string[] texts) =>
        StateOf(texts.Select(t => (t, "UNKNOWN", 1.0)).ToArray());

    // ---------- 1. 中文冒号 marker ----------

    [Fact]
    public void ChineseColonMarkers_AreParsedAndStripped()
    {
        var state = UnknownStateOf("面试官：请介绍一下你自己", "我：我叫Forrest，做了十年开发");

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be("INTERVIEWER");
        state.Segments[0].Text.Should().Be("请介绍一下你自己");
        state.Segments[0].SpeakerConfidence.Should().Be(1.0);
        state.Segments[1].Speaker.Should().Be("CANDIDATE");
        state.Segments[1].Text.Should().Be("我叫Forrest，做了十年开发");
    }

    [Fact]
    public void ChineseHalfWidthColon_IsAccepted()
    {
        var state = UnknownStateOf("面试官:你好", "候选人:你好，我是候选人");

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be("INTERVIEWER");
        state.Segments[1].Speaker.Should().Be("CANDIDATE");
    }

    // ---------- 2. 英文 marker(大小写不敏感) ----------

    [Theory]
    [InlineData("Interviewer: tell me about yourself", "INTERVIEWER", "tell me about yourself")]
    [InlineData("INTERVIEWER: tell me about yourself", "INTERVIEWER", "tell me about yourself")]
    [InlineData("Me: I am Forrest", "CANDIDATE", "I am Forrest")]
    [InlineData("Candidate: I am Forrest", "CANDIDATE", "I am Forrest")]
    public void EnglishMarkers_AreParsedCaseInsensitively(string text, string expected, string expectedText)
    {
        var state = UnknownStateOf(text);

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be(expected);
        state.Segments[0].Text.Should().Be(expectedText);
    }

    // ---------- 3. 方括号标签 ----------

    [Theory]
    [InlineData("[面试官] 你好", "INTERVIEWER", "你好")]
    [InlineData("[我] 你好，我是候选人", "CANDIDATE", "你好，我是候选人")]
    [InlineData("[Interviewer] hi there", "INTERVIEWER", "hi there")]
    [InlineData("[Candidate] I am ready", "CANDIDATE", "I am ready")]
    public void BracketMarkers_AreParsedAndStripped(string text, string expected, string expectedText)
    {
        var state = UnknownStateOf(text);

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be(expected);
        state.Segments[0].Text.Should().Be(expectedText);
    }

    // ---------- 4. marker 继承 ----------

    [Fact]
    public void SpeakerInherits_UntilNextMarker()
    {
        var state = UnknownStateOf(
            "面试官：先做个自我介绍",
            "越详细越好",                 // 无 marker → 继承面试官
            "我：我叫Forrest",            // marker → 切换候选人
            "做了十年后端开发",           // 无 marker → 继承候选人
            "面试官：讲讲你最难的项目",   // marker → 切换面试官
            "技术上最挑战的是哪部分");   // 无 marker → 继承面试官

        SpeakerLabelStage.Label(state);

        state.Segments.Select(s => s.Speaker).Should().Equal(
            "INTERVIEWER", "INTERVIEWER", "CANDIDATE", "CANDIDATE", "INTERVIEWER", "INTERVIEWER");
    }

    [Fact]
    public void SegmentsBeforeFirstMarker_FallBackToCandidate()
    {
        var state = UnknownStateOf(
            "嗯，好的",                  // 第一个 marker 之前 → 候选人默认
            "面试官：请开始");

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be("CANDIDATE");
        state.Segments[1].Speaker.Should().Be("INTERVIEWER");
    }

    // ---------- 5. 无 marker → 全文候选人 ----------

    [Fact]
    public void NoMarkers_AllSegmentsAreCandidate()
    {
        var state = UnknownStateOf(
            "我先介绍一下我的背景",
            "之前在花旗做了八年投资银行业务系统",
            "DealTrax 是我主导迁移的项目");

        SpeakerLabelStage.Label(state);

        state.Segments.Should().OnlyContain(s => s.Speaker == "CANDIDATE");
    }

    // ---------- 6. 已可信标签不被覆盖 ----------

    [Fact]
    public void TrustedLabels_AreKeptAndBecomeInheritanceBase()
    {
        var state = StateOf(
            ("请简单介绍一下你自己", "INTERVIEWER", 0.95), // 可信 → 保留
            ("我叫Forrest", "UNKNOWN", 1.0));              // 无 marker → 继承面试官

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be("INTERVIEWER");
        state.Segments[0].SpeakerConfidence.Should().Be(0.95);
        state.Segments[1].Speaker.Should().Be("INTERVIEWER");
    }

    [Fact]
    public void LowConfidenceLabels_AreReplacedByMarkers()
    {
        var state = StateOf(
            ("面试官：请介绍一下", "CANDIDATE", 0.3)); // 低置信度 → marker 覆盖

        SpeakerLabelStage.Label(state);

        state.Segments[0].Speaker.Should().Be("INTERVIEWER");
        state.Segments[0].SpeakerConfidence.Should().Be(1.0);
    }

    // ---------- 7. 回归:纯自己回答不被丢弃 ----------

    [Fact]
    public void Regression_OwnAnswersOnly_NothingIsDiscarded()
    {
        // 旧启发式:前半段 ≤12 词的分段标 INTERVIEWER → 这些话会从 CandidateTranscript 消失。
        // 用户只粘贴自己回答时,下面这些短句全部会被旧逻辑丢掉。
        var texts = new[]
        {
            "好的",                       // 1 词
            "我来介绍一下",               // 短句
            "我叫 Forrest",
            "之前在花旗银行做了八年",
            "主要是投资银行的交易系统",
            "好的，谢谢"
        };
        var state = UnknownStateOf(texts);

        SpeakerLabelStage.Label(state);

        state.Segments.Should().OnlyContain(s => s.Speaker == "CANDIDATE");
        var joined = string.Join(" ", state.Segments
            .Where(s => s.Speaker == "CANDIDATE").Select(s => s.Text));
        joined.Should().Be(string.Join(" ", texts), "纯自己回答的每一句都必须进入计分文本");
    }

    // ---------- 8. TryParseMarker 直接测试 ----------

    [Theory]
    [InlineData("面试官：你好", "INTERVIEWER")]
    [InlineData("面试官:你好", "INTERVIEWER")]
    [InlineData("我：你好", "CANDIDATE")]
    [InlineData("候选人：你好", "CANDIDATE")]
    [InlineData("Interviewer: hi", "INTERVIEWER")]
    [InlineData("Me: hi", "CANDIDATE")]
    [InlineData("[面试官] 你好", "INTERVIEWER")]
    [InlineData("[我] 你好", "CANDIDATE")]
    [InlineData("  面试官：你好", "INTERVIEWER")] // 前导空格容忍
    public void TryParseMarker_RecognizesAllForms(string text, string expected)
    {
        var (speaker, _) = SpeakerLabelStage.TryParseMarker(text);
        speaker.Should().Be(expected);
    }

    [Theory]
    [InlineData("你好，我是面试官：请介绍一下")] // marker 不在开头 → 不认
    [InlineData("今天天气不错")]
    [InlineData("")]
    public void TryParseMarker_NonLeadingMarker_IsIgnored(string text)
    {
        var (speaker, rest) = SpeakerLabelStage.TryParseMarker(text);
        speaker.Should().BeNull();
        rest.Should().Be(text);
    }

    [Fact]
    public void EmptySegments_DoNotThrow()
    {
        var state = new PipelineState();
        var act = () => SpeakerLabelStage.Label(state);
        act.Should().NotThrow();
    }
}
