using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 六维评分 —— 与 SharedContracts.Analysis.AnalysisScores 保持同构。
/// 放在 Worker 本地是为了让管线能独立测试(不必引用整个契约项目)。
/// </summary>
public sealed record DimensionScores(int Pronunciation, int Fluency, int SentenceIntegrity,
    int Structure, int TechnicalDepth, int Relevance)
{
    /// <summary>
    /// 加权总分。权重反映"对拿到 offer 的实际影响":
    /// 技术深度与结构权重最高 —— 这是历次复盘得出的排序
    /// (发音 92.8 分却没过的案例说明:发音不是决定性变量)。
    /// </summary>
    public int Overall => (int)Math.Round(
        TechnicalDepth * 0.28 +
        Structure * 0.22 +
        Relevance * 0.18 +
        Fluency * 0.14 +
        SentenceIntegrity * 0.10 +
        Pronunciation * 0.08);

    /// <summary>最弱的一维 —— 决定下一步该练什么。</summary>
    public string WeakestDimension()
    {
        var all = new (string Name, int Score)[]
        {
            ("发音", Pronunciation), ("流利度", Fluency), ("语句完整性", SentenceIntegrity),
            ("结构", Structure), ("技术深度", TechnicalDepth), ("相关性", Relevance)
        };
        return all.OrderBy(x => x.Score).First().Name;
    }
}

/// <summary>
/// 管线编排器。
///
/// 设计要点:
///   1. **阶段可跳过** —— 输入不具备(如没有 WAV)时不硬跑,而是记录"跳过"并继续。
///      这让"部分成功"成为可能,而不是一个阶段失败就全盘皆输。
///   2. **每阶段计时** —— 报告要能回答"慢在哪"。
///   3. **失败也产出产物** —— 即使中途失败,已完成阶段的成果也要能落盘,
///      下一次重跑可以从断点继续(而不是从零开始重烧配额)。
/// </summary>
public sealed class AnalysisPipeline(IEnumerable<IPipelineStage> stages,
    ILogger<AnalysisPipeline> logger)
{
    public async Task<AnalysisArtifact> RunAsync(PipelineContext context, CancellationToken ct)
    {
        // 工作目录由管线自己负责创建 —— Prepare 阶段要往里写 normalized.wav,
        // 调用方(Consumer)只传路径不建目录。之前没人建它,Prepare 第一步就
        // DirectoryNotFoundException(整条链路从未跑通过,所以没暴露)。
        Directory.CreateDirectory(context.WorkingDirectory);

        // 按 Name 显式排序,不依赖 DI 注入顺序 —— 阶段顺序是业务约束,不能碰运气
        var ordered = new[] { "Prepare", "Transcribe", "Measure", "Diagnose" }
            .Select(name => stages.FirstOrDefault(s => s.Name == name))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        logger.LogInformation("分析管线启动:条目 {EntryId},共 {Count} 个阶段",
            context.InterviewEntryId, ordered.Count);

        var state = new PipelineState();
        var sw = Stopwatch.StartNew();

        foreach (var stage in ordered)
        {
            ct.ThrowIfCancellationRequested();

            if (!stage.CanRun(context, state))
            {
                logger.LogWarning("阶段 {Stage} 输入不足,跳过", stage.Name);
                state.Stages.Add(new StageResult(stage.Name, false,
                    "输入不足,已跳过(不影响其余阶段)", 0));
                continue;
            }

            try
            {
                logger.LogInformation("执行阶段 {Stage} …", stage.Name);
                state = await stage.ExecuteAsync(context, state, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "阶段 {Stage} 失败,终止后续阶段", stage.Name);
                state.Stages.Add(new StageResult(stage.Name, false, ex.Message, sw.Elapsed.TotalMilliseconds));
                break; // 后续阶段依赖前序产物,继续跑没有意义
            }
        }

        logger.LogInformation("分析管线结束,总耗时 {Elapsed:F1}s", sw.Elapsed.TotalSeconds);

        var artifact = new AnalysisArtifact(
            context.InterviewEntryId,
            context.AssetId,
            state.Segments,
            state.Metrics ?? EmptyMetrics(),
            state.FullTranscript ?? "",
            state.InterviewerTranscript,
            state.CandidateTranscript,
            state.Stages,
            state.ReportMarkdown);

        // 产物落盘:即使事件投递失败,结果也不会丢
        await PersistAsync(context, artifact, ct);

        return artifact;
    }

    private static SpeechMetrics EmptyMetrics() => new(
        DurationSeconds: 0, WordCount: 0, WordsPerMinute: 0, AverageSentenceLength: 0,
        ShortSentenceCount: 0, FillerWordCount: 0, FillerWordBreakdown: [], SelfRepetitionCount: 0,
        LongPauseCount: 0, TotalSilenceSeconds: 0, PronunciationAccuracy: 0,
        LowScoreWordRatio: 0, ProblemWords: []);

    private async Task PersistAsync(PipelineContext context, AnalysisArtifact artifact,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(context.WorkingDirectory);

            var jsonPath = Path.Combine(context.WorkingDirectory, "analysis.json");
            await File.WriteAllTextAsync(jsonPath,
                System.Text.Json.JsonSerializer.Serialize(artifact, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }), ct);

            if (artifact.ReportMarkdown is not null)
            {
                var mdPath = Path.Combine(context.WorkingDirectory, "report.md");
                await File.WriteAllTextAsync(mdPath, artifact.ReportMarkdown, ct);
            }

            logger.LogInformation("产物已写入 {Dir}", context.WorkingDirectory);
        }
        catch (Exception ex)
        {
            // 落盘失败不该让整场分析失败 —— 内存里的产物还会通过事件发出去
            logger.LogError(ex, "产物落盘失败");
        }
    }
}
