using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using YourInterview.Analysis.Worker.Analysis;
using YourInterview.SharedContracts.Events;

namespace YourInterview.Analysis.Worker.Consumers;

/// <summary>
/// 消费"面试材料已上传/已转写"事件 → 跑完整分析管线 → 把结果回写到 Interviews 服务。
///
/// 为什么用"回写 HTTP"而不是"发事件让 Interviews 自己监听":
/// 分析结果是一个**大的结构化产物**(六维 + 明细 + 报告),塞进消息体不合适;
/// 而且回写需要幂等与失败可重试,用 HTTP + Polly 重试更直接可控。
/// 消息用来"触发",HTTP 用来"传数据" —— 各司其职。
/// </summary>
public sealed class InterviewAnalysisRequestedConsumer(
    AnalysisPipeline pipeline,
    IHttpClientFactory httpFactory,
    ServiceTokenProvider tokens,
    IConfiguration config,
    ILogger<InterviewAnalysisRequestedConsumer> logger)
    : IConsumer<InterviewAnalysisRequested>
{
    public async Task Consume(ConsumeContext<InterviewAnalysisRequested> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        logger.LogInformation("开始分析面试条目 {EntryId}(材料 {AssetId})", m.InterviewEntryId, m.AssetId);

        // 源文件路径:事件里带真实路径(M1 修复后 Interviews 落盘时写入)。
        // 路径是**相对**存储根的(interviews 与本 Worker 挂同一个卷);
        // 没带的老消息/纯文本条目才走兜底推导 —— 那条链路本来就是坏的,别再依赖。
        var sourcePath = ResolveSourcePath(m.StoragePath, m.InterviewEntryId, config);
        if (!File.Exists(sourcePath))
        {
            logger.LogWarning("找不到源文件 {Path},报告失败", sourcePath);
            await ReportFailureAsync(m, $"找不到录音文件:{sourcePath}", ct);
            return;
        }

        // 工作目录同样锚定到仓库根(RootDirectory 优先),避免相对路径随 CWD 漂移。
        // 未配置时退回进程目录下的 storage/analysis,行为可预期。
        var workRootCfg = config["Storage:WorkDirectory"];
        var workRoot = !string.IsNullOrWhiteSpace(workRootCfg) && Path.IsPathRooted(workRootCfg)
            ? workRootCfg!
            : Path.Combine(
                config["Storage:RootDirectory"] is { Length: > 0 } rootDir && Directory.Exists(rootDir)
                    ? rootDir
                    : AppContext.BaseDirectory,
                string.IsNullOrWhiteSpace(workRootCfg) ? "storage/analysis" : workRootCfg!);
        var workDir = Path.Combine(workRoot, m.InterviewEntryId.ToString());

        var ctx = new PipelineContext(m.InterviewEntryId, m.AssetId, m.UserId, sourcePath, workDir);

        try
        {
            var artifact = await pipeline.RunAsync(ctx, ct);

            if (artifact.Metrics is null || artifact.Metrics.WordCount == 0)
            {
                await ReportFailureAsync(m, "管线未产出有效指标(可能是静音或转写全部失败)", ct);
                return;
            }

            await WriteBackAsync(m, artifact, ct);
            logger.LogInformation("分析完成并回写:条目 {EntryId}", m.InterviewEntryId);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消要往上抛,否则 MassTransit 会当成正常完成
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "分析失败:条目 {EntryId}", m.InterviewEntryId);

            // 让消息进重试,最终进死信队列;_error 队列里的消息可以人工重投
            throw;
        }
    }

    /// <summary>
    /// 解析录音源文件路径。
    ///
    /// 规则:
    ///   1. 事件里的 StoragePath 是 Interviews 落盘时写入的**相对路径**(如
    ///      "{entryId}/{uuid}.m4a")—— 锚定到本服务配置的面试存储根;
    ///   2. 绝对路径(遗留/手工触发)原样使用;
    ///   3. 没有路径才走旧约定推导(storage/interviews/{entryId}/audio.*)。
    ///
    /// 根目录解析与 Interviews 的 LocalInterviewAudioStore 保持同一优先级:
    /// Storage:InterviewsRoot 配置 > INTERVIEW_STORAGE_DIR 环境变量 >
    /// Storage:RootDirectory 下的 storage/interviews > 当前目录约定。
    /// docker-compose 里两个服务挂**同一个卷**,所以路径天然一致。
    /// </summary>
    private static string ResolveSourcePath(string? storagePath, Guid entryId, IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(storagePath))
        {
            var normalized = storagePath.Replace('\\', '/');
            if (Path.IsPathRooted(normalized) || normalized.Contains(".."))
                return storagePath;   // 绝对路径:信发送方(服务内部事件,非用户输入)

            return Path.Combine(InterviewsRoot(config),
                normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return ResolveDefaultPath(entryId, config["Storage:RootDirectory"]);
    }

    /// <summary>
    /// 面试录音存储根 —— 解析链与 Interviews 的 LocalInterviewAudioStore **完全一致**:
    ///   1. Storage:InterviewsRoot 配置;
    ///   2. INTERVIEW_STORAGE_DIR 环境变量(docker-compose 里与 interviews 共卷,两边同值);
    ///   3. {Storage:RootDirectory}/interviews(沙盒统一存储根派生);
    ///   4. 仓库根下 storage/interviews(本地裸跑,锚定解决方案目录而非各自 cwd);
    ///   5. cwd/storage/interviews(最后兜底)。
    /// 两边同一条链,相对路径拼出来才是同一个文件。
    /// </summary>
    private static string InterviewsRoot(IConfiguration config)
    {
        var dedicated = config["Storage:InterviewsRoot"];
        if (!string.IsNullOrWhiteSpace(dedicated)) return dedicated;

        var env = Environment.GetEnvironmentVariable("INTERVIEW_STORAGE_DIR");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var rootDir = config["Storage:RootDirectory"];
        if (!string.IsNullOrWhiteSpace(rootDir)) return Path.Combine(rootDir, "interviews");

        var solutionRoot = FindSolutionRoot();
        if (solutionRoot is not null) return Path.Combine(solutionRoot, "storage", "interviews");

        return Path.Combine(Directory.GetCurrentDirectory(), "storage", "interviews");
    }

    private static string? FindSolutionRoot()
    {
        // 与 Interviews 的 LocalInterviewAudioStore.FindSolutionRoot 同一逻辑:
        // 优先 docker-compose.yml 所在的仓库根(.sln 在 src/ 下,只认它会锚错一层)。
        DirectoryInfo? withCompose = null, withSln = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.GetFiles("docker-compose.yml").Length > 0) withCompose = dir;
            if (dir.GetFiles("*.sln").Length > 0) withSln = dir;
        }
        return (withCompose ?? withSln)?.FullName;
    }

    private static string ResolveDefaultPath(Guid entryId, string? configRoot)
    {
        string baseDir;
        // 锚定到仓库根目录,而不是进程的当前工作目录 ——
        // 工作目录随启动方式变化(dotnet run vs 直接跑 dll),
        // 用相对路径会在不同启动方式下指向不同地方,这是踩过的坑。
        //
        // 这里配置优先:Storage:RootDirectory 明确指定是最好的办法,
        // 向上推导只当兜底(而且推导链很脆,别依赖它)。
        var configured = configRoot;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            baseDir = configured!;
        else
            baseDir = Directory.GetCurrentDirectory();
        var dir = Path.Combine(baseDir, "storage", "interviews", entryId.ToString());

        var candidates = new[]
        {
            Path.Combine(dir, "audio.m4a"),
            Path.Combine(dir, "audio.mp3"),
            Path.Combine(dir, "audio.wav")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    /// <summary>
    /// 回写分析结果到 Interviews 服务。
    ///
    /// 六维分数从 DimensionScores 直接映射 ——
    /// Worker 算出的分数与 Interviews 的字段必须一一对应,否则报告和界面会对不上。
    /// </summary>
    private async Task WriteBackAsync(InterviewAnalysisRequested m, AnalysisArtifact artifact,
        CancellationToken ct)
    {
        var baseUrl = config["Services:Interviews"] ?? "http://127.0.0.1:5264";
        var http = httpFactory.CreateClient("interviews");

        // 先把条目推进到 Analyzing —— 状态机不允许从 AssetsUploaded 直接跳到 Analyzed。
        // 这不是多余的一步:它让状态变化可被审计("什么时候开始分析/谁触发的"),
        // 也防止一个已完成分析(或已被人工锁定)的条目被覆盖。
        await AdvanceStateAsync(http, baseUrl, m, artifact, ct);

        var scores = DiagnoseStage.Score(artifact.Metrics!);

        var payload = new
        {
            overall = scores.Overall,
            pronunciation = scores.Pronunciation,
            fluency = scores.Fluency,
            structure = scores.Structure,
            technicalDepth = scores.TechnicalDepth,
            relevance = scores.Relevance,
            summary = BuildSummary(artifact, scores),
            questions = Array.Empty<object>(),   // 问题清单需要面试官问题,由人工/后续分析补
            weaknesses = BuildWeaknesses(artifact, scores)
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/api/interviews/{m.InterviewEntryId}/analysis")
        {
            Content = JsonContent.Create(payload)
        };

        if (!await tokens.AuthorizeAsync(req, ct))
            throw new InvalidOperationException("无法获取服务令牌,回写中止");

        using var resp = await http.SendAsync(req, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"回写分析结果失败 {(int)resp.StatusCode}:{body[..Math.Min(300, body.Length)]}");
        }

        // 方案 B:也用内部端点把报告正文存起来(供前端展示 / 下载)
        logger.LogDebug("分析结果已回写,报告长度 {Len}", artifact.ReportMarkdown?.Length ?? 0);
    }

    /// <summary>
    /// 取条目的音频材料 Id。
    ///
    /// ⚠️ 不能写死 GUID。事件里的 AssetId 可能是占位值(触发脚本/人工重跑时),
    /// 用错 Id 会导致"材料不存在"→ 状态永远推不动 → 回写 409。
    /// 以服务端真实数据为准。
    /// </summary>
    private async Task<Guid?> ResolveAssetIdAsync(HttpClient http, string baseUrl,
        InterviewAnalysisRequested m, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/interviews/{m.InterviewEntryId}");
            if (!await tokens.AuthorizeAsync(req, ct)) return null;

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.TryGetProperty("assets", out var assets)) return null;

            foreach (var a in assets.EnumerateArray())
            {
                var kind = a.TryGetProperty("kind", out var k) ? k.GetString() : null;
                if (string.Equals(kind, "Audio", StringComparison.OrdinalIgnoreCase) &&
                    a.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var g))
                    return g;
            }

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "查询材料 Id 失败");
            return null;
        }
    }

    /// <summary>
    /// 把条目推进到 Analyzing 状态。
    ///
    /// 状态机路径:Draft → AssetsUploaded → Transcribing → Transcribed → Analyzing。
    /// 每一步都可能因为"已经在该状态"而返回 409,这属于正常情况(重复投递/重试),
    /// 不能当成失败 —— 否则一次重试就报错。
    /// </summary>
    private async Task AdvanceStateAsync(HttpClient http, string baseUrl,
        InterviewAnalysisRequested m, AnalysisArtifact artifact, CancellationToken ct)
    {
        // 用服务端真实的音频材料 Id(事件里那个可能是占位值)
        var assetId = await ResolveAssetIdAsync(http, baseUrl, m, ct);
        if (assetId is null)
        {
            logger.LogError("条目 {EntryId} 找不到音频材料,无法推进状态", m.InterviewEntryId);
            return;
        }
        //
        // ⚠️ 这里必须传真文本。聚合根的 StartAnalysis 会校验
        // "有没有可分析的转写文本" —— 传空串会让它报错(而且错误被下面吞掉,
        // 现象是"状态怎么都不动"),这是一个踩过的坑。
        var transcriptUrl = $"{baseUrl}/api/interviews/{m.InterviewEntryId}" +
                            $"/assets/{assetId}/transcript";

        var segmentsJson = JsonSerializer.Serialize(artifact.Segments.Select(s => new
        {
            index = s.Index,
            start = s.StartSeconds,
            end = s.EndSeconds,
            text = s.Text
        }));

        using var tReq = new HttpRequestMessage(HttpMethod.Post, transcriptUrl)
        {
            Content = JsonContent.Create(new
            {
                fullText = artifact.FullTranscript ?? string.Empty,
                segmentsJson
            })
        };

        if (await tokens.AuthorizeAsync(tReq, ct))
        {
            using var tResp = await http.SendAsync(tReq, ct);
            if (!tResp.IsSuccessStatusCode && (int)tResp.StatusCode != 409)
            {
                var body = await tResp.Content.ReadAsStringAsync(ct);
                logger.LogWarning("存转写稿失败 {Status}:{Body}", (int)tResp.StatusCode,
                    body[..Math.Min(200, body.Length)]);
            }
        }

        // 2) 先进入转写中(AssetsUploaded → Transcribing)。
        // 不先走这一步直接存转写稿会被拒(Transcript.InvalidState),
        // 因为聚合根要求转写稿只能在 Transcribing 状态下写入。
        using (var sReq = new HttpRequestMessage(HttpMethod.Post,
                   $"{baseUrl}/api/interviews/{m.InterviewEntryId}/transcription/start"))
        {
            if (await tokens.AuthorizeAsync(sReq, ct))
            {
                using var sResp = await http.SendAsync(sReq, ct);
                if (!sResp.IsSuccessStatusCode && (int)sResp.StatusCode != 409)
                {
                    var body = await sResp.Content.ReadAsStringAsync(ct);
                    logger.LogError("开始转写失败 {Status}:{Body}", (int)sResp.StatusCode,
                        body[..Math.Min(300, body.Length)]);
                }
            }
        }

        // 3) 触发分析(Transcribed → Analyzing)
        using var aReq = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/api/interviews/{m.InterviewEntryId}/analyze");

        if (await tokens.AuthorizeAsync(aReq, ct))
        {
            using var aResp = await http.SendAsync(aReq, ct);
            if (!aResp.IsSuccessStatusCode && (int)aResp.StatusCode != 409)
            {
                var body = await aResp.Content.ReadAsStringAsync(ct);
                // 升为 Error —— 这一步失败会导致后面的回写必然 409,
                // 用 Warning 会淹在日志里(曾经的教训:状态不动但看不到任何错)。
                logger.LogError("推进到 Analyzing 失败 {Status}:{Body}", (int)aResp.StatusCode,
                    body[..Math.Min(300, body.Length)]);
            }
            else
            {
                logger.LogInformation("已推进到 Analyzing(HTTP {Status})", (int)aResp.StatusCode);
            }
        }
    }

    private static string BuildSummary(AnalysisArtifact artifact, DimensionScores s)
    {
        var m = artifact.Metrics!;
        return $"音频 {m.DurationSeconds / 60:F1} 分钟,共 {m.WordCount} 词。" +
               $"语速 {m.WordsPerMinute:F0} wpm,平均句长 {m.AverageSentenceLength:F1} 词," +
               $"填充词 {m.FillerWordCount} 次,发音准确度 {m.PronunciationAccuracy:F0}。" +
               $"最弱维度:{s.WeakestDimension()}。";
    }

    /// <summary>
    /// 把指标里能确定的问题转成短板条目。
    /// 只报有客观证据的问题 —— 没有证据的维度(如结构)不硬造短板。
    /// </summary>
    private static List<object> BuildWeaknesses(AnalysisArtifact artifact, DimensionScores s)
    {
        var m = artifact.Metrics!;
        var list = new List<object>();

        if (m.AverageSentenceLength < 10)
            list.Add(new
            {
                category = "Grammar",
                title = "句子被切碎,缺少完整表达",
                detail = $"平均句长只有 {m.AverageSentenceLength:F1} 词,过短句 {m.ShortSentenceCount} 条。",
                evidence = $"客观指标:平均句长 {m.AverageSentenceLength:F1} 词",
                severity = m.AverageSentenceLength < 8 ? 5 : 4,
                suggestion = "练「结论句 + 两个支撑句」的三句话单元,把短句合并成复合句。"
            });

        if (m.FillerWordCount > 10)
            list.Add(new
            {
                category = "Expression",
                title = "填充词偏多",
                detail = $"共 {m.FillerWordCount} 次。",
                evidence = string.Join(", ", m.FillerWordBreakdown.Select(kv => $"{kv.Key}:{kv.Value}")),
                severity = m.FillerWordCount > 25 ? 4 : 3,
                suggestion = "用两秒静默替代 um/uh。停顿不会让人觉得你卡住,填充词会。"
            });

        if (m.ProblemWords.Any(w => w.AccuracyScore < 60))
        {
            var worst = m.ProblemWords.Where(w => w.AccuracyScore < 60).Take(5)
                .Select(w => $"{w.Word}({w.AccuracyScore:F0})").ToList();
            list.Add(new
            {
                category = "Pronunciation",
                title = "专业术语发音错误",
                detail = $"读错的词:{string.Join(", ", worst)}",
                evidence = $"发音准确度均分 {m.PronunciationAccuracy:F1},低分词占比 {m.LowScoreWordRatio:F1}%",
                severity = 5,
                suggestion = "术语读错对专业可信度的伤害大于普通词。建个人术语表逐个纠正。"
            });
        }

        if (m.WordsPerMinute > 0 && m.WordsPerMinute < 120)
            list.Add(new
            {
                category = "Expression",
                title = "语速偏慢",
                detail = $"当前 {m.WordsPerMinute:F0} wpm,低于舒适区下限 130。",
                evidence = $"语速 {m.WordsPerMinute:F0} wpm",
                severity = 3,
                suggestion = "偏慢通常是因为在边想边说。先把答案骨架想好,再一口气说出来。"
            });

        if (m.SelfRepetitionCount > 3)
            list.Add(new
            {
                category = "Logic",
                title = "自我重复",
                detail = $"检测到 {m.SelfRepetitionCount} 处重复表述。",
                evidence = $"重复计数 {m.SelfRepetitionCount}",
                severity = 3,
                suggestion = "重复通常是在用冗余换思考时间。改为明确停顿,想清楚再说。"
            });

        return list;
    }

    private async Task ReportFailureAsync(InterviewAnalysisRequested m, string reason,
        CancellationToken ct)
    {
        try
        {
            var baseUrl = config["Services:Interviews"] ?? "http://127.0.0.1:5264";
            var http = httpFactory.CreateClient("interviews");
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{baseUrl}/api/interviews/{m.InterviewEntryId}/failure")
            {
                Content = JsonContent.Create(new { reason })
            };

            if (await tokens.AuthorizeAsync(req, ct))
                await http.SendAsync(req, ct);
            logger.LogInformation("已上报分析失败状态:{Reason}", reason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "上报失败状态也失败了");
        }
    }
}

/// <summary>
/// 消费"分析完成"事件 → 通知 Analytics 刷新读模型。
///
/// 注意:这里也回写能力快照 —— 让"面试报告出来"与"雷达图更新"在同一时刻发生,
/// 用户不会看到"报告已生成但雷达图还是旧的"这种不一致。
/// </summary>
public sealed class InterviewAnalysisCompletedConsumer(
    IHttpClientFactory httpFactory,
    ServiceTokenProvider tokens,
    IConfiguration config,
    ILogger<InterviewAnalysisCompletedConsumer> logger)
    : IConsumer<InterviewAnalysisCompleted>
{
    public async Task Consume(ConsumeContext<InterviewAnalysisCompleted> context)
    {
        var m = context.Message;
        var ct = context.CancellationToken;

        var analyticsUrl = config["Services:Analytics"] ?? "http://127.0.0.1:5267";
        var http = httpFactory.CreateClient("analytics");

        foreach (var (dim, score) in new[]
                 {
                     ("pronunciation", m.PronunciationScore),
                     ("structure", m.StructureScore),
                     ("fluency", m.FluencyScore)
                 })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{analyticsUrl}/api/analytics/ability")
                {
                    Content = JsonContent.Create(new
                    {
                        userId = m.UserId,
                        dimension = dim,
                        score,
                        source = "interview"
                    })
                };

                if (!await tokens.AuthorizeAsync(req, ct)) continue;
                using var resp = await http.SendAsync(req, ct);
            }
            catch (Exception ex)
            {
                // 单维失败不阻断其他维度 —— 雷达图部分更新好过全部不更新
                logger.LogWarning(ex, "刷新 Analytics 的 {Dimension} 失败", dim);
            }
        }

        logger.LogInformation("已通知 Analytics 更新:条目 {EntryId}", m.InterviewSessionId);
    }
}
