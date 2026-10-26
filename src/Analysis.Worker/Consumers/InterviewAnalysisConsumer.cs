using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using YourInterview.Analysis.Worker.Analysis;
using YourInterview.BuildingBlocks.Storage;
using YourInterview.SharedContracts.Events;
using YourInterview.SharedContracts.Analysis;

namespace YourInterview.Analysis.Worker.Consumers;

/// <summary>
/// 消费"面试材料已上传/已转写"事件 → 跑完整分析管线 → 把结果回写到 Interviews 服务。
///
/// 为什么用"回写 HTTP"而不是"发事件让 Interviews 自己监听":
/// 分析结果是一个**大的结构化产物**(六维 + 明细 + 报告),塞进消息体不合适;
/// 而且回写需要幂等与失败可重试,用 HTTP + Polly 重试更直接可控。
/// 消息用来"触发",HTTP 用来"传数据" —— 各司其职。
///
/// COD-01/票03(裁决 B):全部 Interviews 调用带 ?ownerUserId={事件 UserId} ——
/// 服务账号 JWT 不是条目主人,服务端以 ownerUserId 校验 entry.UserId,
/// 主人对不上即拒绝;不给服务账号全量豁免。
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

        // R01:粘贴文本路径 —— 无音频文件时,不直接失败,而是尝试从转写文本播种分段。
        // (事件自带 TranscriptText 优先,其次调 Interviews API 查 asset 的转写稿)
        List<TranscriptSegment>? seedSegments = null;
        if (!File.Exists(sourcePath))
        {
            seedSegments = await TryLoadTranscriptSegmentsAsync(m, ct);
            if (seedSegments is not { Count: > 0 })
            {
                logger.LogWarning("找不到源文件 {Path} 且无可用的粘贴文本,报告失败", sourcePath);
                await ReportFailureAsync(m, $"找不到录音文件:{sourcePath},且该条目没有可分析的粘贴文本", ct);
                return;
            }
            logger.LogInformation("无音频文件,走粘贴文本路径:播种 {Count} 个分段", seedSegments.Count);
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

        var ctx = new PipelineContext(m.InterviewEntryId, m.AssetId, m.UserId, sourcePath, workDir,
            SeedSegments: seedSegments);

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
    /// 根目录解析已收编 BuildingBlocks adapter(ARC-12/票39):与 Interviews 服务
    /// 共用 <see cref="StorageRootResolver.ResolveInterviewsRoot(IConfiguration)"/> 唯一实现,
    /// 不再自带拷贝。docker-compose 里两个服务挂**同一个卷**,所以路径天然一致。
    /// </summary>
    private static string ResolveSourcePath(string? storagePath, Guid entryId, IConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(storagePath))
        {
            var normalized = storagePath.Replace('\\', '/');
            if (Path.IsPathRooted(normalized) || normalized.Contains(".."))
                return storagePath;   // 绝对路径:信发送方(服务内部事件,非用户输入)

            return Path.Combine(StorageRootResolver.ResolveInterviewsRoot(config),
                normalized.Replace('/', Path.DirectorySeparatorChar));
        }

        return ResolveDefaultPath(entryId, config["Storage:RootDirectory"]);
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
    /// 文本模式:从转写文本播种分段。
    /// 两段式:① 事件自带的 TranscriptText(目前 dispatcher 恒为 null,留作未来/手工触发);
    /// ② 调 Interviews API 查 asset 的转写稿(segmentsJson 优先,退回 fullText 切分)。
    /// 都拿不到返回 null,调用方按失败上报。
    /// </summary>
    private async Task<List<TranscriptSegment>?> TryLoadTranscriptSegmentsAsync(
        InterviewAnalysisRequested m, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(m.TranscriptText))
        {
            var segs = TranscriptSegmentBuilder.SplitText(m.TranscriptText);
            if (segs.Count > 0) return segs;
        }

        var baseUrl = config["Services:Interviews"] ?? "http://127.0.0.1:5264";
        var http = httpFactory.CreateClient("interviews");
        try
        {
            var (fullText, segmentsJson) = await FetchTranscriptAsync(http, baseUrl, m, ct);
            var parsed = TranscriptSegmentBuilder.TryParseSegmentsJson(segmentsJson);
            if (parsed is { Count: > 0 }) return parsed;
            if (!string.IsNullOrWhiteSpace(fullText))
            {
                var split = TranscriptSegmentBuilder.SplitText(fullText);
                if (split.Count > 0) return split;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "获取粘贴文本失败:条目 {EntryId}", m.InterviewEntryId);
        }
        return null;
    }

    /// <summary>
    /// 查有转写稿的 asset 并取其文本。优先事件里的 AssetId(若有转写稿),
    /// 否则取第一个 HasTranscript 的 asset。
    /// </summary>
    private async Task<(string? FullText, string? SegmentsJson)> FetchTranscriptAsync(
        HttpClient http, string baseUrl, InterviewAnalysisRequested m, CancellationToken ct)
    {
        using var entryReq = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/interviews/{m.InterviewEntryId}?ownerUserId={m.UserId}");
        if (!await tokens.AuthorizeAsync(entryReq, ct)) return (null, null);
        using var entryResp = await http.SendAsync(entryReq, ct);
        if (!entryResp.IsSuccessStatusCode) return (null, null);

        var entryJson = await entryResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (!entryJson.TryGetProperty("assets", out var assets)) return (null, null);

        Guid? assetId = null;
        foreach (var a in assets.EnumerateArray())
        {
            var hasTranscript = a.TryGetProperty("hasTranscript", out var h) &&
                h.ValueKind == JsonValueKind.True;
            if (!hasTranscript) continue;
            if (a.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), out var g))
            {
                assetId = g;
                // 事件里的 AssetId 优先(人工指定的材料)
                if (g == m.AssetId) break;
            }
        }
        if (assetId is null) return (null, null);

        using var tReq = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/api/interviews/{m.InterviewEntryId}/assets/{assetId}/transcript?ownerUserId={m.UserId}");
        if (!await tokens.AuthorizeAsync(tReq, ct)) return (null, null);
        using var tResp = await http.SendAsync(tReq, ct);
        if (!tResp.IsSuccessStatusCode) return (null, null);

        var tJson = await tResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var fullText = tJson.TryGetProperty("fullText", out var ft) &&
            ft.ValueKind == JsonValueKind.String ? ft.GetString() : null;
        var segmentsJson = tJson.TryGetProperty("segmentsJson", out var sj) &&
            sj.ValueKind == JsonValueKind.String ? sj.GetString() : null;
        return (fullText, segmentsJson);
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
            weaknesses = BuildWeaknesses(artifact, scores),
            speechMetrics = artifact.Metrics,  // 缺口4:客观声学指标回写入库,前端展示
            candidateTranscript = artifact.CandidateTranscript // 2026-10-15:计分文本,前端复盘面板展示
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{baseUrl}/api/interviews/{m.InterviewEntryId}/analysis?ownerUserId={m.UserId}")
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
                $"{baseUrl}/api/interviews/{m.InterviewEntryId}?ownerUserId={m.UserId}");
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
            // 文本模式:没有音频材料是正常的 —— 转写文本已存在,条目已在 Analyzing,
            // 这里不需要做转写稿回写与状态推进,直接返回等 WriteBack。
            if (artifact.Metrics is { HasAudio: false })
            {
                logger.LogInformation("文本模式:跳过转写稿回写与状态推进(条目 {EntryId} 已在分析中)",
                    m.InterviewEntryId);
                return;
            }
            logger.LogError("条目 {EntryId} 找不到音频材料,无法推进状态", m.InterviewEntryId);
            return;
        }
        //
        // ⚠️ 这里必须传真文本。聚合根的 StartAnalysis 会校验
        // "有没有可分析的转写文本" —— 传空串会让它报错(而且错误被下面吞掉,
        // 现象是"状态怎么都不动"),这是一个踩过的坑。
        var transcriptUrl = $"{baseUrl}/api/interviews/{m.InterviewEntryId}" +
                            $"/assets/{assetId}/transcript?ownerUserId={m.UserId}";

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
                   $"{baseUrl}/api/interviews/{m.InterviewEntryId}/transcription/start?ownerUserId={m.UserId}"))
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
            $"{baseUrl}/api/interviews/{m.InterviewEntryId}/analyze?ownerUserId={m.UserId}");

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
        // 文本模式:无音频时长,发音未测量 —— 措辞必须诚实,不能写"音频 X 分钟"
        var head = m.HasAudio
            ? $"音频 {m.DurationSeconds / 60:F1} 分钟,共 {m.WordCount} 词。"
            : $"文本模式(无音频),共 {m.WordCount} 词,发音未测量。";
        var tail = m.HasAudio
            ? $"填充词 {m.FillerWordCount} 次,发音准确度 {m.PronunciationAccuracy:F0}。"
            : $"填充词 {m.FillerWordCount} 次。";
        return head +
               $"语速 {m.WordsPerMinute:F0} wpm,平均句长 {m.AverageSentenceLength:F1} 词," +
               tail +
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
                $"{baseUrl}/api/interviews/{m.InterviewEntryId}/failure?ownerUserId={m.UserId}")
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

        foreach (var (dim, score) in new (string, int?)[]
                 {
                     ("pronunciation", m.PronunciationScore),
                     ("structure", m.StructureScore),
                     ("fluency", m.FluencyScore)
                 })
        {
            // 文本模式下发音为 null:跳过上报,不拿"没测到"污染雷达图
            if (!score.HasValue) continue;
            try
            {
                // 票56:归属走可信通道 ?ownerUserId=(票03 query 形态)——Analytics 仅对带
                // analytics.writeback claim 的服务账号 honoured;body 不再传 userId(票06 禁止形态)。
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{analyticsUrl}/api/analytics/ability?ownerUserId={m.UserId}")
                {
                    Content = JsonContent.Create(new
                    {
                        dimension = dim,
                        score = score.Value,
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
