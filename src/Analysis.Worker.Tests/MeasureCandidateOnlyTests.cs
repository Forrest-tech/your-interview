using FluentAssertions;
using Xunit;
using YourInterview.Analysis.Worker.Analysis;

namespace YourInterview.Analysis.Worker.Tests;

/// <summary>
/// R06"只算候选人自己的发言"第二部分 —— Measure 层候选人口径。
/// 覆盖:语速分母只用候选人说话时长、停顿排除面试官间隔、UNKNOWN 兜底不变、
/// 候选人音频抽取的参数拼装。
/// </summary>
public sealed class MeasureCandidateOnlyTests
{
    private static TranscriptSegment Seg(int i, double start, double end, string text, string speaker) =>
        new(i, start, end, text, speaker, 1.0);

    private static string Words(int n) =>
        string.Join(" ", Enumerable.Range(1, n).Select(k => $"word{k}"));

    // ---------- 1. 语速分母:候选人说话时长 ----------

    [Fact]
    public void Wpm_UsesCandidateSpeakingTimeOnly()
    {
        // 候选人 20 词 / 30 秒 = 40 wpm;整段音频 300 秒(含面试官 270 秒)。
        // 旧算法:20 / (300/60) = 4 wpm —— 错得离谱。
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 270, "interviewer talking for a very long time about the company", "INTERVIEWER"),
            Seg(1, 270, 300, Words(20), "CANDIDATE"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 300);

        m.WordsPerMinute.Should().BeApproximately(40, 0.5);
    }

    [Fact]
    public void Wpm_MultipleCandidateSegments_SumsTheirDurations()
    {
        // 候选人两段:10 秒 + 20 秒,共 30 词 → 60 wpm
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 10, Words(10), "CANDIDATE"),
            Seg(1, 10, 100, "interviewer follow up question here", "INTERVIEWER"),
            Seg(2, 100, 120, Words(20), "CANDIDATE"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 120);

        m.WordsPerMinute.Should().BeApproximately(60, 0.5);
    }

    // ---------- 2. 停顿:排除面试官间隔 ----------

    [Fact]
    public void Pauses_IgnoreGapsAdjacentToInterviewer()
    {
        // C[0,10] → I[10,20] → C[25,35]:面试官前后的间隔都不是"我的停顿"
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 10, "my answer part one here", "CANDIDATE"),
            Seg(1, 10, 20, "interviewer asks a question", "INTERVIEWER"),
            Seg(2, 25, 35, "my answer part two here", "CANDIDATE"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 35);

        m.LongPauseCount.Should().Be(0);
        m.TotalSilenceSeconds.Should().Be(0);
    }

    [Fact]
    public void Pauses_CountGapsBetweenCandidateSegments()
    {
        // C[0,10] → C[15,25]:全序相邻且双方都是候选人 → 5 秒间隙计长停顿
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 10, "my answer part one here", "CANDIDATE"),
            Seg(1, 15, 25, "my answer part two here", "CANDIDATE"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 25);

        m.LongPauseCount.Should().Be(1);
        m.TotalSilenceSeconds.Should().BeApproximately(5, 0.01);
    }

    [Fact]
    public void Pauses_SmallCandidateGap_NotCountedAsSilence()
    {
        // 0.2 秒间隙:低于 0.3s 阈值,不计
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 10, "my answer part one here", "CANDIDATE"),
            Seg(1, 10.2, 20, "my answer part two here", "CANDIDATE"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 20);

        m.LongPauseCount.Should().Be(0);
        m.TotalSilenceSeconds.Should().Be(0);
    }

    // ---------- 3. UNKNOWN 兜底行为不变 ----------

    [Fact]
    public void UnknownSpeaker_TreatedAsCandidate_Unchanged()
    {
        // 20 词 / 30 秒 = 40 wpm;UNKNOWN 按候选人算(与第一部分 SpeakerLabelStage 的降级语义一致)
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 30, Words(20), "UNKNOWN"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 300);

        m.WordCount.Should().Be(20);
        m.WordsPerMinute.Should().BeApproximately(40, 0.5);
    }

    [Fact]
    public void NoCandidateSegments_FallsBackToAllSegments()
    {
        // 全是 INTERVIEWER → 回退为全部,不丢数据(与旧 fallback 一致)
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 0, 15, Words(10), "INTERVIEWER"),
        };

        var m = SpeechMetricsCalculator.Calculate(segs, [], 15);

        m.WordCount.Should().Be(10);
        m.WordsPerMinute.Should().BeApproximately(40, 0.5);
    }

    // ---------- 4. 候选人音频抽取:纯逻辑 ----------

    [Fact]
    public void BuildRanges_ClampsSortsAndDropsFragments()
    {
        var segs = new List<TranscriptSegment>
        {
            Seg(0, 20, 35, "b", "CANDIDATE"),   // 乱序 → 应排到后面
            Seg(1, -5, 10, "a", "CANDIDATE"),   // 起点 <0 → clamp 到 0
            Seg(2, 90, 200, "c", "CANDIDATE"),  // 终点超总时长 → clamp 到 100
            Seg(3, 50, 50.01, "d", "CANDIDATE"), // <0.05s 碎片 → 丢弃
        };

        var ranges = CandidateAudioExtractor.BuildRanges(segs, 100);

        ranges.Should().HaveCount(3);
        ranges[0].Should().Be((0.0, 10.0));
        ranges[1].Should().Be((20.0, 35.0));
        ranges[2].Should().Be((90.0, 100.0));
    }

    [Fact]
    public void BuildRanges_EmptyInput_ReturnsEmpty()
    {
        CandidateAudioExtractor.BuildRanges([], 100).Should().BeEmpty();
    }

    [Fact]
    public void BuildArgs_ProducesTrimConcatFilter()
    {
        var ranges = new List<(double, double)> { (0.5, 12.3), (20.0, 35.7) };

        var args = CandidateAudioExtractor.BuildArgs("in.wav", ranges, "out.wav");

        args.Should().Contain("atrim=start=0.50:end=12.30,setpts=PTS-STARTPTS[ca0]");
        args.Should().Contain("atrim=start=20.00:end=35.70,setpts=PTS-STARTPTS[ca1]");
        args.Should().Contain("[ca0][ca1]concat=n=2:v=0:a=1[cand]");
        args.Should().Contain("-map \"[cand]\"");
        args.Should().Contain("-ar 16000 -ac 1 -c:a pcm_s16le");
        args.Should().Contain("-y -i \"in.wav\"");
        args.Should().EndWith("\"out.wav\"");
    }
}
