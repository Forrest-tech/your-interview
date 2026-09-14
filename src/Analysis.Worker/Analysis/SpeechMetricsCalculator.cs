using System.Text.RegularExpressions;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 客观声学与语言指标计算 —— 不依赖任何外部服务,纯本地可复现。
///
/// 为什么坚持自己算:指标必须**可解释**。
/// 面试者看到"平均句长 7.5 词"能立刻理解并改进;
/// 看到"流利度 72 分"却无从下手 —— 分数是结论,指标才是行动依据。
///
/// 这套指标的阈值来自真实分析经验(Geotab 那场 24 分 35 秒的录音),
/// 不是拍脑袋定的。
/// </summary>
public static class SpeechMetricsCalculator
{
    /// <summary>
    /// 填充词表。分两类:
    ///   hesitation = 思考时的无意义填充(要消除)
    ///   discourse  = 口语连接词(适度使用自然,过量才减分)
    /// 分开统计是因为"消除所有 like"会让语言变得僵硬,这不是目标。
    /// </summary>
    private static readonly HashSet<string> HesitationFillers =
        ["um", "uh", "erm", "hmm", "mm", "ah", "eh", "er"];

    private static readonly HashSet<string> DiscourseFillers =
        ["like", "basically", "actually", "literally", "you know", "kind of", "sort of"];

    /// <summary>术语发音关注表 —— 这些词读错会直接损害专业可信度。</summary>
    private static readonly string[] CriticalTerms =
    [
        ".net", "c#", "wpf", "mvvm", "algorithm", "library", "vision", "opencv",
        "multithreading", "monolithic", "microservices", "on-call", "dealtrax",
        "kubernetes", "docker", "postgres", "postgresql", "angular", "rxjs",
        "sql", "api", "async", "await", "cache", "repository", "aggregate",
        "idempotent", "outbox", "saga", "latency", "throughput", "scalability"
    ];

    public static SpeechMetrics Calculate(IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<WordPronunciation> pronunciations, double durationSeconds)
    {
        // 只统计"我"说的部分 —— 面试官的话不该算进我的语速与句长
        var mySegments = segments
            .Where(s => s.Speaker == "CANDIDATE" || s.Speaker == "UNKNOWN")
            .ToList();

        if (mySegments.Count == 0) mySegments = segments.ToList();

        var text = string.Join(" ", mySegments.Select(s => s.Text));
        var words = TokenizeWords(text);
        var wordCount = words.Count;

        var minutes = durationSeconds / 60.0;
        var wpm = minutes > 0.01 ? wordCount / minutes : 0;

        // ---------- 句长 ----------
        var sentences = SplitSentences(text);
        var sentenceLengths = sentences.Select(s => TokenizeWords(s).Count)
            .Where(n => n > 0).ToList();

        var avgSentenceLength = sentenceLengths.Count > 0 ? sentenceLengths.Average() : 0;
        // 过短句阈值取 5:低于这个长度的"句子"通常不是完整表达,而是被切碎的片段
        var shortSentences = sentenceLengths.Count(n => n < 5);

        // ---------- 填充词 ----------
        var lower = " " + text.ToLowerInvariant() + " ";
        var breakdown = new Dictionary<string, int>();

        foreach (var f in HesitationFillers)
        {
            var c = Regex.Matches(lower, $@"\b{Regex.Escape(f)}\b").Count;
            if (c > 0) breakdown["[犹豫] " + f] = c;
        }

        foreach (var f in DiscourseFillers)
        {
            var c = Regex.Matches(lower, $@"\b{Regex.Escape(f)}\b").Count;
            if (c > 0) breakdown["[口语] " + f] = c;
        }

        var fillerCount = breakdown.Values.Sum();

        // ---------- 自我重复 ----------
        // 检测"连续重复同一个词"与"连续重复同一短语"两种模式
        var selfRepetition = 0;
        for (var i = 0; i < words.Count - 1; i++)
            if (string.Equals(words[i], words[i + 1], StringComparison.OrdinalIgnoreCase))
                selfRepetition++;

        var sentenceList = sentences.ToList();
        for (var i = 0; i < sentenceList.Count - 1; i++)
            if (sentenceList[i].Length > 8 &&
                Similarity(sentenceList[i], sentenceList[i + 1]) > 0.75)
                selfRepetition++;

        // ---------- 停顿 ----------
        // 用分段时间戳之间的间隙估算。>2 秒算长停顿
        var ordered = segments.OrderBy(s => s.StartSeconds).ToList();
        var longPauses = 0;
        var totalSilence = 0.0;
        for (var i = 0; i < ordered.Count - 1; i++)
        {
            var gap = ordered[i + 1].StartSeconds - ordered[i].EndSeconds;
            if (gap > 0.3) totalSilence += gap;
            if (gap > 2.0) longPauses++;
        }

        // ---------- 发音 ----------
        var scored = pronunciations.Where(p => !string.IsNullOrWhiteSpace(p.Word)).ToList();
        var accuracy = scored.Count > 0 ? scored.Average(p => p.AccuracyScore) : 0;
        // 60 分是经验阈值:低于它基本是"读错"而不是"口音"
        var lowScoreRatio = scored.Count > 0
            ? scored.Count(p => p.AccuracyScore < 60) / (double)scored.Count * 100
            : 0;

        // 只挑"专业术语"里的低分词 —— 这才是真正影响可信度的部分
        var problemWords = scored
            .Where(p => p.AccuracyScore < 70)
            .Where(p => CriticalTerms.Contains(p.Word.ToLowerInvariant().Trim('.',',','?','!')))
            .OrderBy(p => p.AccuracyScore)
            .Take(20)
            .ToList();

        // 术语读错的优先级高于普通词,所以问题词列表把术语排在前面
        var nonTermProblems = scored
            .Where(p => p.AccuracyScore < 50)
            .Where(p => !CriticalTerms.Contains(p.Word.ToLowerInvariant().Trim('.',',','?','!')))
            .OrderBy(p => p.AccuracyScore)
            .Take(10)
            .ToList();

        return new SpeechMetrics(
            DurationSeconds: Math.Round(durationSeconds, 1),
            WordCount: wordCount,
            WordsPerMinute: Math.Round(wpm, 1),
            AverageSentenceLength: Math.Round(avgSentenceLength, 1),
            ShortSentenceCount: shortSentences,
            FillerWordCount: fillerCount,
            FillerWordBreakdown: breakdown,
            SelfRepetitionCount: selfRepetition,
            LongPauseCount: longPauses,
            TotalSilenceSeconds: Math.Round(totalSilence, 1),
            PronunciationAccuracy: Math.Round(accuracy, 1),
            LowScoreWordRatio: Math.Round(lowScoreRatio, 1),
            ProblemWords: [.. problemWords, .. nonTermProblems]);
    }

    internal static List<string> TokenizeWords(string text) =>
        Regex.Matches(text, @"[A-Za-z0-9#\.\+']+")
            .Select(m => m.Value)
            .Where(w => w.Length > 0)
            .ToList();

    internal static IEnumerable<string> SplitSentences(string text) =>
        Regex.Split(text, @"(?<=[.!?])\s+|\n+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0);

    /// <summary>词集合的 Jaccard 相似度 —— 用于检测"换汤不换药"的重复表述。</summary>
    internal static double Similarity(string a, string b)
    {
        var wa = TokenizeWords(a.ToLowerInvariant()).ToHashSet();
        var wb = TokenizeWords(b.ToLowerInvariant()).ToHashSet();
        if (wa.Count == 0 || wb.Count == 0) return 0;

        var inter = wa.Intersect(wb).Count();
        var union = wa.Union(wb).Count();
        return union == 0 ? 0 : inter / (double)union;
    }
}
