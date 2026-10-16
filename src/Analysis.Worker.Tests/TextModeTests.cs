using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Analysis.Worker.Analysis;
using Xunit;

namespace YourInterview.Analysis.Worker.Tests;

/// <summary>
/// R01 文本模式(粘贴文本 → 跑分析)测试:
/// 切分/解析 → Measure(文本) → Diagnose(发音 N/A) → 端到端。
/// </summary>
public sealed class TextModeTests
{
    // ---------- TranscriptSegmentBuilder:切分 ----------

    [Fact]
    public void SplitText_Paragraphs_SynthesizesAccumulatingTimestamps()
    {
        var text = "Interviewer: Tell me about yourself.\n\nMe: I'm Forrest Lin, a backend developer " +
                   "with ten years of experience on the .NET stack.\n\nInterviewer: Thanks.";
        var segs = TranscriptSegmentBuilder.SplitText(text);

        segs.Should().HaveCount(3);
        segs.Should().OnlyContain(s => s.Speaker == "UNKNOWN");
        segs[0].StartSeconds.Should().Be(0);
        segs[0].EndSeconds.Should().BeGreaterThan(segs[0].StartSeconds);
        segs[1].StartSeconds.Should().Be(segs[0].EndSeconds, "时间戳累加");
        segs[2].StartSeconds.Should().Be(segs[1].EndSeconds);
        // marker 保留在文本里 —— 由 SpeakerLabelStage 解析剥离
        segs[0].Text.Should().StartWith("Interviewer:");
    }

    [Fact]
    public void SplitText_EmptyText_NoSegments()
    {
        TranscriptSegmentBuilder.SplitText("   \n  ").Should().BeEmpty();
        TranscriptSegmentBuilder.SplitText("").Should().BeEmpty();
    }

    [Fact]
    public void SplitText_LongParagraph_SplitsOnSentences()
    {
        var longPara = string.Concat(Enumerable.Repeat(
            "This is a fairly long sentence about the migration project. ", 30));
        var segs = TranscriptSegmentBuilder.SplitText(longPara);
        segs.Count.Should().BeGreaterThan(1, "超长段落应按句界切分");
    }

    // ---------- TranscriptSegmentBuilder:JSON 解析 ----------

    [Fact]
    public void TryParseSegmentsJson_ValidShape_Parses()
    {
        var json = """[{"index":0,"start":1.5,"end":4.0,"text":"hello world"}]""";
        var segs = TranscriptSegmentBuilder.TryParseSegmentsJson(json);

        segs.Should().NotBeNull();
        segs.Should().HaveCount(1);
        segs![0].StartSeconds.Should().Be(1.5);
        segs[0].EndSeconds.Should().Be(4.0);
        segs[0].Text.Should().Be("hello world");
        segs[0].Speaker.Should().Be("UNKNOWN");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"text\":\"\"}]")]
    public void TryParseSegmentsJson_InvalidOrEmpty_ReturnsNull(string? json)
    {
        TranscriptSegmentBuilder.TryParseSegmentsJson(json).Should().BeNull();
    }

    // ---------- Measure 文本模式 ----------

    [Fact]
    public async Task MeasureTextMode_SkipsPronunciation_TextMetricsComputed()
    {
        var seeds = TranscriptSegmentBuilder.SplitText(
            "Me: First, I mapped the domain model. Second, I split the database. " +
            "Finally, we cut over after reconciliation passed.");
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var ctx = new PipelineContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "/nonexistent.wav", workDir, SeedSegments: seeds);
        var state = new PipelineState();
        state.Segments.AddRange(seeds);

        // speech 传 null —— 文本模式绝不碰 Azure,传 null 即是断言
        var measure = new MeasureStage(null!, NullLogger<MeasureStage>.Instance);
        measure.CanRun(ctx, state).Should().BeTrue("有播种分段即应可跑,无需音频");
        state = await measure.ExecuteAsync(ctx, state, CancellationToken.None);

        state.Metrics.Should().NotBeNull();
        state.Metrics!.HasAudio.Should().BeFalse();
        state.Metrics.WordCount.Should().BeGreaterThan(0);
        state.Metrics.PronunciationAccuracy.Should().Be(0);
    }

    // ---------- Diagnose:发音 N/A ----------

    [Fact]
    public void Diagnose_Overall_ExcludesNullPronunciation()
    {
        var seeds = TranscriptSegmentBuilder.SplitText(
            "Me: The short answer is yes. First we analyzed the domain. Second we migrated " +
            "the data layer with zero downtime. Finally we decommissioned the old system.");
        var m = SpeechMetricsCalculator.Calculate(seeds, [], 60, hasAudio: false);

        var scores = DiagnoseStage.Score(m);

        scores.Pronunciation.Should().BeNull("文本模式无音频,发音不可测");
        var expected = (int)Math.Round(new[]
            { scores.Fluency, scores.SentenceIntegrity, scores.Structure,
              scores.TechnicalDepth, scores.Relevance }.Average());
        scores.Overall.Should().Be(expected, "总分应为其余五维的平均");
        scores.WeakestDimension().Should().NotBe("发音", "null 维度不参与最弱判定");
    }

    [Fact]
    public void DimensionScores_Overall_AudioMode_StillAveragesAllSix()
    {
        var s = new DimensionScores(80, 70, 60, 50, 40, 30);
        s.Overall.Should().Be(55); // (80+70+60+50+40+30)/6 = 55
        s.WeakestDimension().Should().Be("相关性");
    }

    // ---------- 端到端:文本链 ----------

    [Fact]
    public async Task EndToEnd_TextMode_FullPipeline_NoPronunciationInReport()
    {
        var seeds = TranscriptSegmentBuilder.SplitText(
            "Interviewer: What was the hardest part?\n\n" +
            "Me: The hardest part was drawing the data boundaries. First, we mapped the domain " +
            "model. Second, we split reads before writes. Finally, we cut traffic after " +
            "reconciliation passed, um, which took about three months.");
        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var ctx = new PipelineContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "/nonexistent.wav", workDir, SeedSegments: seeds);

        IPipelineStage[] stages =
        [
            new AudioPrepareStage(NullLogger<AudioPrepareStage>.Instance),
            new SpeakerLabelStage(NullLogger<SpeakerLabelStage>.Instance),
            new MeasureStage(null!, NullLogger<MeasureStage>.Instance),
            new DiagnoseStage(NullLogger<DiagnoseStage>.Instance),
        ];
        var pipeline = new AnalysisPipeline(stages, NullLogger<AnalysisPipeline>.Instance);
        var artifact = await pipeline.RunAsync(ctx, CancellationToken.None);

        // Prepare/Transcribe 因无音频跳过,其余三段跑完
        artifact.Stages.Should().Contain(s => s.Stage == "SpeakerLabel" && s.Success);
        artifact.Stages.Should().Contain(s => s.Stage == "Measure" && s.Success);
        artifact.Stages.Should().Contain(s => s.Stage == "Diagnose" && s.Success);

        artifact.Metrics.HasAudio.Should().BeFalse();
        artifact.CandidateTranscript.Should().Contain("hardest part was drawing");
        artifact.CandidateTranscript.Should().NotContain("What was the hardest part?");

        artifact.ReportMarkdown.Should().Contain("N/A", "发音维度应标 N/A");
        artifact.ReportMarkdown.Should().Contain("文本模式");
        artifact.ReportMarkdown.Should().NotContain("发音准确度均分");
        artifact.ReportMarkdown.Should().NotContain("### 发音问题词");

        // 计分文本只含候选人发言 —— 前端"计分文本"卡的数据源
        var scores = DiagnoseStage.Score(artifact.Metrics);
        scores.Pronunciation.Should().BeNull();
        scores.Overall.Should().BeGreaterThan(0);
    }
}
