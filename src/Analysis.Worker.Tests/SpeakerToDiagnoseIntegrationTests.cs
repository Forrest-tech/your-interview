using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Analysis.Worker.Analysis;
using Xunit;

namespace YourInterview.Analysis.Worker.Tests;

/// <summary>
/// 端到端（除 Azure 外）集成测试：用一段逼真的英文模拟面试文本，
/// 走 SpeakerLabel → Metrics → Diagnose 真实代码路径，验证
/// R06"只算候选人自己的发言"在整条链路上成立。
/// </summary>
public sealed class SpeakerToDiagnoseIntegrationTests
{
    private static List<TranscriptSegment> MixedInterviewSegments() => new()
    {
        // 转写阶段产出：标签全是 UNKNOWN，带时间戳
        new(0, 0, 8, "Interviewer: Let's start with a brief introduction of yourself.", "UNKNOWN"),
        new(1, 10, 70, "Me: I'm Forrest Lin, a backend developer with over ten years of experience, " +
            "mostly on C# and the .NET stack. I spent eight years at Citi building systems for the " +
            "investment bank, where I led a strangler-fig migration from a ten-year-old monolith to " +
            "microservices. It took about a year to move the core trading flow over, then we wound " +
            "the old system down.", "UNKNOWN"),
        new(2, 72, 80, "Interviewer: What was the hardest part of that migration?", "UNKNOWN"),
        new(3, 82, 160, "Me: I'd say the hardest part was drawing the data boundaries. First, we spent " +
            "two weeks mapping the domain model and sketching out bounded contexts for trading, " +
            "pricing and risk. Second, we split reads before writes on the database to avoid a " +
            "big-bang cutover. Finally, old and new ran side by side for three months, and we only " +
            "cut traffic after reconciliation passed. Um, we also hit one nasty issue with distributed " +
            "transactions, uh, and ended up going with eventual consistency.", "UNKNOWN"),
        new(4, 162, 170, "Interviewer: Got it, thanks.", "UNKNOWN"),
    };

    [Fact]
    public async Task FullChain_CandidateOnlyMetrics_And_SixDimensions()
    {
        var state = new PipelineState();
        state.Segments.AddRange(MixedInterviewSegments());
        var ctx = new PipelineContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/tmp/fake.wav", "/tmp/work");

        // 1) 标注
        var labeler = new SpeakerLabelStage(NullLogger<SpeakerLabelStage>.Instance);
        labeler.CanRun(ctx, state).Should().BeTrue();
        state = await labeler.ExecuteAsync(ctx, state, CancellationToken.None);

        var mine = state.Segments.Where(s => s.Speaker == "CANDIDATE").ToList();
        var theirs = state.Segments.Where(s => s.Speaker == "INTERVIEWER").ToList();
        mine.Should().HaveCount(2, "两段长的回答是候选人的");
        theirs.Should().HaveCount(3, "三段短问句是面试官的");

        // 2) 指标：只算候选人
        var metrics = SpeechMetricsCalculator.Calculate(state.Segments, [], durationSeconds: 170);
        state.Metrics = metrics;
        var candidateSeconds = 60 + 78; // 两段候选人分段时长
        var expectedWpm = metrics.WordCount / (candidateSeconds / 60.0);
        metrics.WordsPerMinute.Should().BeApproximately(expectedWpm, 0.5);
        // 旧算法用整段 170s 做分母：新值应为旧值的 170/138 ≈ 1.23 倍
        var oldWpm = metrics.WordCount / (170.0 / 60.0);
        (metrics.WordsPerMinute / oldWpm).Should().BeApproximately(170.0 / candidateSeconds, 0.05);
        // 填充词：候选人说了 um/uh，应被计入且区分犹豫/口语
        metrics.FillerWordCount.Should().BeGreaterThan(0);
        metrics.FillerWordBreakdown.Keys.Should().Contain(k => k.Contains("犹豫"));
        // 结构骨架：候选人用了 first/second/finally
        metrics.StructureMarkers.Should().NotBeEmpty();

        // 3) 诊断：六维 + 报告
        var diagnose = new DiagnoseStage(NullLogger<DiagnoseStage>.Instance);
        diagnose.CanRun(ctx, state).Should().BeTrue();
        state = await diagnose.ExecuteAsync(ctx, state, CancellationToken.None);

        state.ReportMarkdown.Should().NotBeNullOrWhiteSpace();
        state.ReportMarkdown.Should().Contain("发音");
        state.ReportMarkdown.Should().Contain("流利度");
        // 拼接文本由 Diagnose 产出：只含候选人发言
        state.CandidateTranscript.Should().Contain("strangler-fig");
        state.CandidateTranscript.Should().NotContain("brief introduction");
        state.InterviewerTranscript.Should().Contain("hardest part");
    }

    [Fact]
    public async Task FullChain_NoMarkers_AllCandidate_NothingDropped()
    {
        // 回归：用户只粘贴自己的回答（无 marker）时，旧启发式会丢掉前半段短句
        var state = new PipelineState();
        state.Segments.AddRange(new[]
        {
            new TranscriptSegment(0, 0, 5, "Alright, let me start.", "UNKNOWN"),
            new TranscriptSegment(1, 6, 90, "First, some background on the project. The system serves the " +
                "investment banking trading desk, handling hundreds of thousands of orders a day with " +
                "tight latency requirements. Our team had eight people across three cities.", "UNKNOWN"),
        });
        var ctx = new PipelineContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "/tmp/fake.wav", "/tmp/work");

        var labeler = new SpeakerLabelStage(NullLogger<SpeakerLabelStage>.Instance);
        state = await labeler.ExecuteAsync(ctx, state, CancellationToken.None);

        state.Segments.Should().OnlyContain(s => s.Speaker == "CANDIDATE");

        // 补上指标与诊断，走完拼接文本
        state.Metrics = SpeechMetricsCalculator.Calculate(state.Segments, [], durationSeconds: 90);
        var diagnose = new DiagnoseStage(NullLogger<DiagnoseStage>.Instance);
        state = await diagnose.ExecuteAsync(ctx, state, CancellationToken.None);

        state.CandidateTranscript.Should().Contain("Alright, let me start.",
            "第一句短回答不能被当成面试官丢掉——这是旧启发式的 bug");
    }
}
