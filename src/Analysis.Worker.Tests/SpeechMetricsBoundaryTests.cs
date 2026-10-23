using FluentAssertions;
using YourInterview.Analysis.Worker.Analysis;
using YourInterview.SharedContracts.Analysis;
using Xunit;

namespace YourInterview.Analysis.Worker.Tests;

/// <summary>
/// 票65(ARC-08d):SpeechMetricsCalculator boundary 补强(特征测试锁现状口径)。
/// 既有覆盖未触达:连续重复词/近重复句自重复计数、低分术语与非术语问题词筛选
/// (含 20/10 条上限)、低分占比。与既有三套(候选人口径/说话人/文本模式)互补。
/// boundary 类别:空集合、0/负时长、阈值 ±(停顿 0.3/2.0、句长 5、低分 60/70/50)、
/// 超量截断、标点粘连术语、大小写。
/// </summary>
public sealed class SpeechMetricsBoundaryTests
{
    private static TranscriptSegment Seg(double start, double end, string text,
        string speaker = "CANDIDATE")
        => new(0, start, end, text, speaker);

    private static WordPronunciation Pron(string word, double score)
        => new(word, score, null, 0, 1);

    private static SpeechMetrics Calc(IReadOnlyList<TranscriptSegment> segs,
        IReadOnlyList<WordPronunciation>? prons = null, double duration = 60, bool hasAudio = true)
        => SpeechMetricsCalculator.Calculate(segs, prons ?? [], duration, hasAudio);

    // ---------- 空/null-ish boundary ----------

    [Fact] // 空集合:全零指标,HasAudio 透传
    public void EmptyInputs_AllZero()
    {
        var m = Calc([], [], duration: 0, hasAudio: false);
        m.WordCount.Should().Be(0);
        m.WordsPerMinute.Should().Be(0);
        m.AverageSentenceLength.Should().Be(0);
        m.FillerWordCount.Should().Be(0);
        m.SelfRepetitionCount.Should().Be(0);
        m.LongPauseCount.Should().Be(0);
        m.PronunciationAccuracy.Should().Be(0);
        m.LowScoreWordRatio.Should().Be(0);
        m.ProblemWords.Should().BeEmpty();
        m.HasAudio.Should().BeFalse();
    }

    [Fact] // 无候选人分段 → 回落全量统计(与 IsMine 降级语义一致)
    public void InterviewerOnly_FallsBackToAllSegments()
    {
        var m = Calc([Seg(0, 10, "Tell me about yourself please", "INTERVIEWER")]);
        m.WordCount.Should().Be(5);
    }

    // ---------- 语速 boundary ----------

    [Fact] // 负时长分段:说话时长钳 0 → 语速 0(不除零、不为负)
    public void NegativeDurationSegment_WpmZero()
    {
        var m = Calc([Seg(10, 5, "hello world")]);
        m.WordCount.Should().Be(2);
        m.WordsPerMinute.Should().Be(0);
    }

    [Fact] // 说话时长 ≤0.6s(0.01 分钟阈值)→ 语速按 0 计
    public void TinySpeakingTime_WpmZero()
    {
        var m = Calc([Seg(0, 0.3, "hello world")]);
        m.WordsPerMinute.Should().Be(0);
    }

    [Fact] // 60 词 / 60 秒候选人时长 = 60 wpm(分母是候选人时长,非总时长)
    public void Wpm_UsesCandidateSpeakingTime()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 60));
        var m = Calc([Seg(0, 60, text)], duration: 600);
        m.WordsPerMinute.Should().Be(60);
        m.DurationSeconds.Should().Be(600);
    }

    // ---------- 句长 boundary ----------

    [Fact] // 短句阈值 5:长度 1/1/3 全记短句;平均 (1+1+3)/3≈1.7
    public void ShortSentences_ThresholdFive()
    {
        var m = Calc([Seg(0, 10, "Yes. No. I think so.")]);
        m.ShortSentenceCount.Should().Be(3);
        m.AverageSentenceLength.Should().Be(1.7);
    }

    // ---------- 自我重复(既有未覆盖行) ----------

    [Fact] // 连续重复词(大小写不敏感)计数
    public void RepeatedWord_CountsSelfRepetition()
    {
        var m = Calc([Seg(0, 10, "I I think the the system works")]);
        m.SelfRepetitionCount.Should().Be(2);
    }

    [Fact] // 连续近重复句(长 >8 且 Jaccard >0.75)计数:第二句仅多一个中插词
           // (句末词带句点粘连 token,故差异词放句中,否则相似度被句点拉低)
    public void NearDuplicateConsecutiveSentences_CountsSelfRepetition()
    {
        var m = Calc([Seg(0, 20,
            "The system handles caching very well today. " +
            "The system handles caching really very well today.")]);
        m.SelfRepetitionCount.Should().Be(1);
    }

    [Fact] // 不相邻的相同句不计数(只看相邻对)
    public void NonAdjacentDuplicates_NotCounted()
    {
        var m = Calc([Seg(0, 30,
            "The system handles caching very well today. Something else entirely here now. " +
            "The system handles caching very well today.")]);
        m.SelfRepetitionCount.Should().Be(0);
    }

    // ---------- 停顿阈值 ± ----------

    [Theory]
    [InlineData(0.29, 0, 0.0)] // gap<0.3:不计静音(恰 0.3 的浮点表示不稳定,不取等值点)
    [InlineData(0.4, 0, 0.4)]  // gap>0.3 计静音,未 >2.0 不计长停顿
    [InlineData(2.0, 0, 2.0)]  // gap=2.0 不 >2.0:不计长停顿
    [InlineData(2.5, 1, 2.5)]  // gap>2.0:长停顿 +1
    public void PauseThresholds(double gap, int longPauses, double silence)
    {
        var m = Calc([Seg(0, 10, "first part here"), Seg(10 + gap, 20 + gap, "second part here")]);
        m.LongPauseCount.Should().Be(longPauses);
        m.TotalSilenceSeconds.Should().Be(silence);
    }

    [Fact] // 面试官插入 → 两侧间隙都不算候选人停顿
    public void InterviewerBetweenCandidates_PausesSkipped()
    {
        var m = Calc([
            Seg(0, 5, "my first answer"),
            Seg(20, 25, "follow up question", "INTERVIEWER"),
            Seg(40, 45, "my second answer")]);
        m.LongPauseCount.Should().Be(0);
        m.TotalSilenceSeconds.Should().Be(0);
    }

    // ---------- 填充词 ----------

    [Fact] // 犹豫类与口语类分开统计;多词填充 "you know" 整体命中
    public void Fillers_BreakdownByClass()
    {
        var m = Calc([Seg(0, 10, "Um um, you know, like basically yes")]);
        m.FillerWordBreakdown["[犹豫] um"].Should().Be(2);
        m.FillerWordBreakdown["[口语] you know"].Should().Be(1);
        m.FillerWordBreakdown["[口语] like"].Should().Be(1);
        m.FillerWordBreakdown["[口语] basically"].Should().Be(1);
        m.FillerWordCount.Should().Be(5);
    }

    // ---------- 发音/问题词(既有未覆盖行) ----------

    [Fact] // 准确率均值 + 低分(<60)占比;空白词剔除不入分母
    public void Pronunciation_AverageAndLowRatio_BlankWordsExcluded()
    {
        var m = Calc([Seg(0, 10, "hello world")],
            [Pron("apple", 40), Pron("banana", 80), Pron("  ", 10), Pron("cherry", 100)]);
        m.PronunciationAccuracy.Should().Be(73.3);
        m.LowScoreWordRatio.Should().Be(33.3);
    }

    [Fact] // 问题词:术语(<70)优先在前,非术语须 <50;术语带标点粘连仍识别
    public void ProblemWords_TermsFirst_PunctuationTrimmed()
    {
        var m = Calc([Seg(0, 10, "hello world")],
            [Pron("banana", 30), Pron("Docker,", 40), Pron("zebra", 90), Pron("melon", 55)]);
        m.ProblemWords.Select(p => p.Word).Should().Equal(["Docker,", "banana"]);
    }

    [Fact] // 术语问题词上限 20 条(按分数升序;词表取 CriticalTerms 中可命中者 24 个)
    public void ProblemWords_TermCapTwenty()
    {
        var terms = new[]
        {
            "c#", "wpf", "mvvm", "algorithm", "library", "vision", "opencv",
            "multithreading", "monolithic", "microservices", "on-call", "dealtrax",
            "kubernetes", "docker", "postgres", "postgresql", "angular", "rxjs",
            "sql", "api", "async", "await", "cache", "repository"
        };
        var m = Calc([Seg(0, 10, "hello world")], terms.Select(t => Pron(t, 10)).ToList());
        m.ProblemWords.Should().HaveCount(20);
    }

    [Fact] // ⚠ 缺陷候选 D2(只登记不修,见 evidence-65/CLOSE-REPORT):
           // CriticalTerms 含 ".net",但匹配前先 Trim('.',',','?','!') 把 ".net" 削成 "net",
           // 永远匹配不上 —— 65 分的 ".net"(术语阈值 <70 本应入选,非术语阈值 <50 又够不上)
           // 直接漏出问题词列表。本例锁现状,修复须另立票后同步改期望。
    public void DotNetTerm_TrimStripsDot_NeverMatchesCriticalTerms()
    {
        var m = Calc([Seg(0, 10, "hello world")], [Pron(".net", 65)]);
        m.ProblemWords.Should().BeEmpty();
    }

    // ---------- internal 纯函数 boundary(IVT 已开) ----------

    [Fact]
    public void Similarity_Edges()
    {
        SpeechMetricsCalculator.Similarity("", "").Should().Be(0);
        SpeechMetricsCalculator.Similarity("the cat sat", "the cat sat").Should().Be(1);
        SpeechMetricsCalculator.Similarity("alpha beta", "gamma delta").Should().Be(0);
    }

    [Fact]
    public void TokenizeAndSplit_Edges()
    {
        SpeechMetricsCalculator.TokenizeWords("hello, world!")
            .Should().Equal(["hello", "world"]);
        SpeechMetricsCalculator.TokenizeWords("").Should().BeEmpty();
        SpeechMetricsCalculator.SplitSentences("One. Two? Three!\nFour")
            .Should().HaveCount(4);
        SpeechMetricsCalculator.SplitSentences("   ").Should().BeEmpty();
    }

    [Fact] // 结构标记:单词标记走词界(firsthand 不误命中 first)
    public void StructureMarkers_WordBoundary()
    {
        var markers = SpeechMetricsCalculator.DetectStructureMarkers(
            "First, however, this firsthand account ends in conclusion");
        markers.Should().ContainKey("first");
        markers["first"].Should().Be(1);
        markers.Should().ContainKey("however");
        markers.Should().ContainKey("in conclusion");
    }
}
