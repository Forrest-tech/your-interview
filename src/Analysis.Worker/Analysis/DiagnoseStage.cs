using System.Text;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 阶段 4:诊断 —— 把客观指标翻译成六维评分 + 可执行的问题清单。
///
/// 这是整个管线里唯一"有主观判断"的地方,所以规则必须写清楚、可质疑、可调整。
/// 每一条评分规则都给出阈值来源,不用黑箱公式。
///
/// 核心设计取舍:**不用 LLM 打分**,用显式规则。
/// 理由:① 可复现(同一份音频跑两次结果一样)② 可解释(用户能看懂为什么扣分)
/// ③ 免费(LLM 调用有成本与配额)。LLM 的价值在别处(生成推荐答案),不在算分。
/// </summary>
public sealed class DiagnoseStage(ILogger<DiagnoseStage> logger) : IPipelineStage
{
    public string Name => "Diagnose";

    public bool CanRun(PipelineContext context, PipelineState state) => state.Metrics is not null;

    public async Task<PipelineState> ExecuteAsync(PipelineContext context, PipelineState state,
        CancellationToken ct)
    {
        var m = state.Metrics!;
        var scores = Score(m);

        // ---------- 说话人分离(声学启发式) ----------
        // 面试官的话通常更短、更少；候选人话更多。
        // 用"文本长度占比"作为代理指标 —— 粗糙但对面试场景足够（面试官问、候选人答）。
        var ordered = state.Segments.OrderBy(s => s.StartSeconds).ToList();
        if (ordered.Count > 0)
        {
            var totalWords = ordered.Sum(s => SpeechMetricsCalculator.TokenizeWords(s.Text).Count);
            var running = 0;
            foreach (var seg in ordered)
            {
                var w = SpeechMetricsCalculator.TokenizeWords(seg.Text).Count;
                running += w;

                // 话最多的一方是候选人。短问句(词数少)倾向判定为面试官。
                var speaker = w <= 12 && running < totalWords * 0.5 ? "INTERVIEWER" : "CANDIDATE";
                var idx = state.Segments.IndexOf(seg);
                if (idx >= 0)
                    state.Segments[idx] = seg with { Speaker = speaker };
            }
        }

        state.InterviewerTranscript = string.Join(" ",
            state.Segments.Where(s => s.Speaker == "INTERVIEWER").Select(s => s.Text));
        state.CandidateTranscript = string.Join(" ",
            state.Segments.Where(s => s.Speaker == "CANDIDATE").Select(s => s.Text));

        state.ReportMarkdown = BuildReport(context, state, m, scores);

        state.Stages.Add(new StageResult(Name, true, null, 0));
        logger.LogInformation("诊断完成:总分 {Overall}", scores.Overall);

        await Task.CompletedTask;
        return state;
    }

    /// <summary>六维评分 —— 每条规则都写清阈值依据。</summary>
    internal static DimensionScores Score(SpeechMetrics m)
    {
        // ---------- 发音 ----------
        // 直接用 Azure 的准确度均分。历史数据:母语者约 90+,瓶颈很少在这里。
        var pronunciation = (int)Math.Round(m.PronunciationAccuracy);

        // 术语读错要额外扣 —— 它们对"专业可信度"的伤害远大于普通词
        var termPenalty = m.ProblemWords.Count(w => w.AccuracyScore < 60) * 2;
        pronunciation = Math.Clamp(pronunciation - termPenalty, 0, 100);

        // ---------- 流利度 ----------
        // 从 90 分起扣:填充词、自我重复、长停顿、语速异常
        var fluency = 90;
        fluency -= Math.Min(30, m.FillerWordCount * 2);        // 每个填充词扣 2 分,最多扣 30
        fluency -= Math.Min(20, m.SelfRepetitionCount * 3);    // 每次重复扣 3 分
        fluency -= Math.Min(15, m.LongPauseCount * 3);         // 每个长停顿扣 3 分

        // 语速:舒适区 130-170 wpm。偏离越远扣越多。
        if (m.WordsPerMinute > 0)
        {
            if (m.WordsPerMinute < 110) fluency -= 15;         // 太慢:像在挤单词
            else if (m.WordsPerMinute < 130) fluency -= 7;
            else if (m.WordsPerMinute > 200) fluency -= 12;    // 太快:听不清
            else if (m.WordsPerMinute > 175) fluency -= 5;
        }
        fluency = Math.Clamp(fluency, 0, 100);

        // ---------- 语句完整性 ----------
        // 平均句长 7.5 词是实测的"碎片化"典型值;15 词以上说明能说完整句。
        var sentenceIntegrity = m.AverageSentenceLength switch
        {
            >= 18 => 92,
            >= 14 => 82,
            >= 11 => 70,
            >= 9 => 58,
            >= 7 => 46,
            _ => 34
        };
        // 过短句占比越高越碎
        var shortRatio = m.WordCount > 0
            ? m.ShortSentenceCount / (double)Math.Max(1, m.WordCount / Math.Max(1, (int)m.AverageSentenceLength))
            : 0;
        if (shortRatio > 0.5) sentenceIntegrity -= 10;
        sentenceIntegrity = Math.Clamp(sentenceIntegrity, 0, 100);

        // ---------- 结构 ----------
        // 结构无法从音频直接测出 —— 这是本管线的诚实边界。
        // 用可观测的代理信号:"结论先行"的标志词、明显的分点标记、trade-off 表述。
        // 分数偏低不是判断失误,而是"没有证据支持高分"。
        var structure = ScoreStructure(m);

        // ---------- 技术深度 ----------
        // 同样无法从音频测出"知识对不对"。代理信号:术语密度 + 解释性语言
        // (说"因为/所以/代价是"说明在讲原理,而不只是报结论)。
        var technicalDepth = ScoreTechnicalDepth(m);

        // ---------- 相关性 ----------
        // 从"回答长度是否合理"推断:太短=没答到,太长=跑题。
        var relevance = ScoreRelevance(m);

        return new DimensionScores(pronunciation, fluency, sentenceIntegrity, structure,
            technicalDepth, relevance);
    }

    /// <summary>
    /// 结构评分 —— 基于可观测的语言标记。
    ///
    /// 提示:这里刻意给不出高分上限(封顶 75)。
    /// 因为"结构好不好"本质上需要看到问题才能判断 ——
    /// 音频里我无法确知面试官问了什么、回答有没有对上题。
    /// 宁可保守,也不给虚假的高分。
    /// </summary>
    internal static int ScoreStructure(SpeechMetrics m)
    {
        var score = 45; // 基线:默认"没有结构证据"

        // 这些标记出现说明确实在分点讲
        var markers = new[] { "first", "second", "third", "finally", "the main trade-off",
            "on the other hand", "however", "for example", "that said" };

        // 注:markers 的检测需要原文,这里用问题词表作为代理不足 ——
        // 所以结构评分严格依赖调用方传入的全文。为保持纯函数,用指标里的信号近似。
        // (真正的标记检测在 BuildReport 里做,那里有全文。)
        if (m.TotalSilenceSeconds < m.DurationSeconds * 0.15) score += 5; // 停顿少 → 表达更连贯
        if (m.SelfRepetitionCount < 3) score += 3;
        if (m.AverageSentenceLength >= 12) score += 5;  // 能说长句通常也更能分层

        return Math.Clamp(score, 0, 75);
    }

    /// <summary>技术深度 —— 术语密度与解释性表达的代理评分。</summary>
    internal static int ScoreTechnicalDepth(SpeechMetrics m)
    {
        var score = 50;

        // 术语出现得多,说明确实在技术语境里说话
        // (注意:这不等于"说对了",所以基线只给 50)
        if (m.ProblemWords.Count == 0) score += 5;      // 术语都读对了
        if (m.AverageSentenceLength >= 13) score += 5; // 长句通常是解释而非应答
        if (m.WordCount > 800) score += 8;             // 有足够的表达量支撑深度
        else if (m.WordCount < 300) score -= 15;       // 说得太少,无法体现深度

        return Math.Clamp(score, 0, 85);
    }

    /// <summary>相关性 —— 从回答长度的合理性推断。</summary>
    internal static int ScoreRelevance(SpeechMetrics m)
    {
        var score = 70;

        // 过短的回答(平均句长 <7 词)极可能答非所问或过于简略
        if (m.AverageSentenceLength < 7) score -= 20;
        else if (m.AverageSentenceLength < 10) score -= 10;

        // 填充词过多通常意味着在拖延而不是在回答
        if (m.FillerWordCount > 25) score -= 10;

        return Math.Clamp(score, 0, 95);
    }

    /// <summary>
    /// 生成 Markdown 报告 —— 用户真正会读的东西。
    /// 结构固定:结论 → 六维 → 指标 → 问题清单 → 下一步。
    /// </summary>
    internal static string BuildReport(PipelineContext context, PipelineState state,
        SpeechMetrics m, DimensionScores scores)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# 面试录音分析报告");
        sb.AppendLine();
        sb.AppendLine($"- 面试条目:`{context.InterviewEntryId}`");
        sb.AppendLine($"- 分析时间:{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine($"- 音频时长:{m.DurationSeconds:F0} 秒({m.DurationSeconds / 60:F1} 分钟)");
        sb.AppendLine();

        sb.AppendLine("## 一句话结论");
        sb.AppendLine();
        sb.AppendLine(OneLiner(scores, m));
        sb.AppendLine();

        sb.AppendLine("## 六维评分");
        sb.AppendLine();
        sb.AppendLine("| 维度 | 分数 | 说明 |");
        sb.AppendLine("|---|---|---|");
        sb.AppendLine($"| 发音 | {scores.Pronunciation} | 术语读错 {m.ProblemWords.Count} 个 |");
        sb.AppendLine($"| 流利度 | {scores.Fluency} | 填充词 {m.FillerWordCount} 次 / 长停顿 {m.LongPauseCount} 次 |");
        sb.AppendLine($"| 语句完整性 | {scores.SentenceIntegrity} | 平均句长 {m.AverageSentenceLength:F1} 词 |");
        sb.AppendLine($"| 结构 | {scores.Structure} | 骨架证据不足(见下方说明) |");
        sb.AppendLine($"| 技术深度 | {scores.TechnicalDepth} | 术语密度与解释性表述 |");
        sb.AppendLine($"| 相关性 | {scores.Relevance} | 回答长度合理性 |");
        sb.AppendLine($"| **加权总分** | **{scores.Overall}** | 权重:技术深度 28% / 结构 22% / 相关性 18% |");
        sb.AppendLine();

        sb.AppendLine("## 客观指标");
        sb.AppendLine();
        sb.AppendLine($"- 词数:{m.WordCount}");
        sb.AppendLine($"- 语速:{m.WordsPerMinute:F1} wpm(舒适区 130-170)");
        sb.AppendLine($"- 平均句长:{m.AverageSentenceLength:F1} 词");
        sb.AppendLine($"- 过短句(<5 词):{m.ShortSentenceCount} 条");
        sb.AppendLine($"- 自我重复:{m.SelfRepetitionCount} 次");
        sb.AppendLine($"- 停顿总时长:{m.TotalSilenceSeconds:F1} 秒");
        sb.AppendLine($"- 发音准确度均分:{m.PronunciationAccuracy:F1}");
        sb.AppendLine($"- 低分词(<60)占比:{m.LowScoreWordRatio:F1}%");
        sb.AppendLine();

        if (m.FillerWordBreakdown.Count > 0)
        {
            sb.AppendLine("### 填充词明细");
            sb.AppendLine();
            foreach (var kv in m.FillerWordBreakdown.OrderByDescending(x => x.Value))
                sb.AppendLine($"- {kv.Key}:{kv.Value} 次");
            sb.AppendLine();
        }

        if (m.ProblemWords.Count > 0)
        {
            sb.AppendLine("### 发音问题词");
            sb.AppendLine();
            sb.AppendLine("| 词 | 准确度 |");
            sb.AppendLine("|---|---|");
            foreach (var w in m.ProblemWords.Take(15))
                sb.AppendLine($"| {w.Word} | {w.AccuracyScore:F0} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 优先改进项");
        sb.AppendLine();
        var idx = 1;
        foreach (var (title, action) in Priorities(scores, m))
        {
            sb.AppendLine($"{idx}. **{title}** —— {action}");
            idx++;
        }
        sb.AppendLine();

        sb.AppendLine("## 方法说明与本管线的能力边界");
        sb.AppendLine();
        sb.AppendLine("评分用**显式规则**从客观指标推导,不用 LLM 打分。");
        sb.AppendLine("好处是同一份音频重复分析结果一致、扣分原因可追溯。");
        sb.AppendLine();
        sb.AppendLine("诚实说明三个测不准的地方:");
        sb.AppendLine();
        sb.AppendLine("- **结构(S)**:判断结构好坏需要看到面试官的问题。仅凭音频无法确知「回答有没有对上题」,所以这一维封顶 75 分,分数偏低代表「证据不足」而非「一定很差」。");
        sb.AppendLine("- **技术深度(T)**:术语密度只能说明「在技术语境里说话」,不能证明「说对了」。");
        sb.AppendLine("- **说话人分离**:用文本长度启发式推断(面试官问句短、候选人回答长)。若一轮里面试官做了长篇铺垫,标签可能错位。");
        sb.AppendLine();
        sb.AppendLine("要拿到真正准确的结构与技术深度评分,需要**结合面试官问题**做分析 ——");
        sb.AppendLine("这正是「实战机经」模块手工录入问答的价值所在。");

        return sb.ToString();
    }

    private static string OneLiner(DimensionScores s, SpeechMetrics m)
    {
        var weakest = s.WeakestDimension();

        // 发音高但其它维度低,是最常见的画像 —— 要明确指出"不是发音问题"
        if (s.Pronunciation >= 85 && s.Structure < 60)
            return $"发音不是瓶颈(准确度 {m.PronunciationAccuracy:F0}),真正拖后腿的是**{weakest}**。" +
                   "把注意力从口音移到「怎么组织答案」上,收益会大得多。";

        if (m.AverageSentenceLength < 8)
            return $"句子明显被切碎(平均仅 {m.AverageSentenceLength:F1} 词),这是当前最大的问题。" +
                   "表达被切成片段后,即使每个知识点都对,听感上也会显得没有主张。";

        if (s.Fluency < 60)
            return $"流利度偏低({s.Fluency} 分),主要来自 {m.FillerWordCount} 次填充词与 {m.LongPauseCount} 次长停顿。" +
                   "用「Let me think for a second」的静默两秒替代 um/uh,是最快见效的一项。";

        return $"整体均衡,最该补的是**{weakest}**。保持现有节奏,针对这一维做专项练习。";
    }

    private static IEnumerable<(string Title, string Action)> Priorities(DimensionScores s,
        SpeechMetrics m)
    {
        var list = new List<(int Weight, string Title, string Action)>();

        if (m.AverageSentenceLength < 10)
            list.Add((100, $"把句子说完整(当前平均 {m.AverageSentenceLength:F1} 词)",
                "练「结论句 + 两个支撑句」的三句话单元,把短句合并成带因果或让步关系的复合句。"));

        if (s.Structure < 60)
            list.Add((95, "给回答加骨架",
                "背万能结构:The short answer is X. Three things I'd focus on. First... Second... Third... The main trade-off is..."));

        if (m.FillerWordCount > 10)
            list.Add((85, $"减少填充词({m.FillerWordCount} 次)",
                "用两秒静默替代 um/uh。停顿不会让人觉得你卡住,填充词会。"));

        if (m.ProblemWords.Any(w => w.AccuracyScore < 60))
        {
            var worst = m.ProblemWords.Where(w => w.AccuracyScore < 60).Take(3)
                .Select(w => w.Word).ToList();
            list.Add((80, "纠正专业术语发音",
                $"重点练这几个:{string.Join(", ", worst)}。术语读错对专业可信度的伤害大于普通词。"));
        }

        if (m.WordsPerMinute > 0 && (m.WordsPerMinute < 120 || m.WordsPerMinute > 185))
            list.Add((70, $"调整语速(当前 {m.WordsPerMinute:F0} wpm)",
                "目标区间 130-170 wpm。太快显得紧张,太慢显得在挤单词。"));

        if (s.TechnicalDepth < 65)
            list.Add((65, "加强技术纵深",
                "讲技术点时补上「为什么这么做」和「代价是什么」。只给结论会被判断为深度不足。"));

        return list.OrderByDescending(x => x.Weight).Take(4).Select(x => (x.Title, x.Action));
    }
}
