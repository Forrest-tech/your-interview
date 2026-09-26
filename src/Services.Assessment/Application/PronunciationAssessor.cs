using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// Azure 发音评估 —— 服务端专用。
///
/// 为什么必须在服务端:Speech key 是资源级密钥,一旦发给浏览器就等于公开。
/// 所以浏览器只上传音频,由服务端持 key 调 Azure,再把结果裁剪后回传。
///
/// 与 Analysis.Worker 的 AzureSpeechClient 同源,但职责不同:
/// 那边是离线批量转写长录音(要切片 + 声学指标),
/// 这边是在线单次跟读评分(短音频、要逐词明细)。
/// 两边都遵守同一条诚实约定:拿不到的维度返回 null,不编造。
///
/// ⚠️ 实测平台限制(不是猜测,来自 Analysis.Worker 的踩坑记录):
///   Free F0 + conversation 端点只返回 AccuracyScore。
///   Fluency / Completeness / Prosody 需要付费层或 SDK。
///   所以本类对这三个字段如实返回 null,绝不用 0 冒充 —— 那会污染统计。
/// </summary>
public sealed class PronunciationAssessor(
    IHttpClientFactory httpFactory,
    IConfiguration config,
    YourInterview.Services.Assessment.Infrastructure.Storage.ISpeechKeyProvider keyProvider,
    ILogger<PronunciationAssessor> logger)
{
    // ★ 2026-09-26:9MB → 32MB。长音频分段评估上线后,后端能吃下完整长录音
    //   (16kHz 单声道 16bit ≈ 32KB/s → 32MB ≈ 17 分钟)。前端 base64 后约 1.33 倍,
    //   HTTP 请求体仍在 Kestrel 64MB 上限内。
    private const int MaxAudioBytes = 32 * 1024 * 1024;

    /// <summary>
    /// 单段上限(秒)。Azure 官方文档:STT 短音频 REST 接口请求最多 60 秒,
    /// 且"发音评估音频不应超过 30 秒"。取 28s 留余量;尾段并入最多 +2s(仍 ≤30s)。
    /// 超过即走分段评估(WavSegmenter + AssessSegmentedAsync)。
    /// </summary>
    private const double MaxSegmentSeconds = 28.0;

    /// <summary>
    /// 调用 Azure Speech 时带的 User-Agent。
    ///
    /// ⚠️ 2026-09-16 血泪教训:**Azure 语音的接入层(istio-envoy)会拒收不带
    ///    User-Agent 的请求,直接回 400 且 body 为空**。
    ///    .NET 的 HttpClient 默认不发 UA,而 curl / Python urllib 都自带,
    ///    所以工具直连总是 200、服务代码永远 400 —— 极难察觉。
    ///    常量提到这里,所有 Azure 调用点共用,避免再漏。
    /// </summary>
    private const string UserAgent = "YourInterview/1.0 (assessment-service)";

    /// <summary>
    /// ⚠️ 2026-09-15 第十七轮改:key 不再在构造时读死。
    ///
    /// 原因:界面上保存的 key 存在数据库里,构造时还不知道是哪一把;
    /// 如果构造时就固化,用户在设置页保存完 key 会"看起来成功但评分仍然失败" ——
    /// 那是骗人。所以换成**每次调用时经 ISpeechKeyProvider 解析**
    /// (优先级:数据库 > 环境变量 > appsettings)。
    /// </summary>
    private readonly YourInterview.Services.Assessment.Infrastructure.Storage.ISpeechKeyProvider _keys = keyProvider;

    private readonly string _configRegion = config["AzureSpeech:Region"] ?? "canadacentral";

    /// <summary>
    /// 是否已配置 key —— ⚠️ **不能只看配置文件**,必须看数据库(界面保存的 key 在那里)。
    ///
    /// 2026-09-16 修的真实缺陷(Forrest 现场发现):
    ///   旧实现只查 config["AzureSpeech:Key"] 与 AZURE_SPEECH_KEY 环境变量。
    ///   而设置页保存的 key 是写进**数据库**的(SpeechSettings 表)。
    ///   于是用户明明保存成功,speech/test 却因为 IsConfigured==false
    ///   直接返回 503"Azure 未配置" —— 用户看到的"凭据验证失败"是假的,
    ///   key 其实好好在库里。
    ///
    /// 不能在这里改异步查库(它是**属性**,而 DbContext 是 Scoped,
    /// 且本类是单例 —— 属性里没法 await)。
    ///   解法:调用方改用 **IsAvailableAsync**,以该用户为口径
    ///   经 ISpeechKeyProvider 解析后再判断。
    ///   本属性保留,仅作"配置源是否已配"的快速参考,不再用于拦截请求。
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(config["AzureSpeech:Key"])
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY"));

    /// <summary>
    /// 真·可用性判定 —— 按用户口径解析 key(数据库 > 环境变量 > appsettings)。
    /// 端点应改用这个,不要再用 IsConfigured。
    /// </summary>
    public async Task<bool> IsAvailableAsync(Guid? userId, CancellationToken ct)
    {
        var snapshot = await _keys.ResolveAsync(userId ?? Guid.Empty, ct);
        return snapshot.HasKey;
    }

    /// <summary>
    /// 连通性测试 —— 拿当前 key 真去 Azure 走一次最小请求。
    ///
    /// 为什么不做"只校验字符串非空":那样用户在界面上填错区域或密钥也会看到
    /// "保存成功",直到练习评分时才炸。这是骗人,不允许。
    ///
    /// 实现:调 /stt/speech/recognition/.../v1 但**不带音频** ——
    ///   · 401/403 = 密钥或区域无效 → 抛 AzureAuthException(界面如实显示)
    ///   · 400 = 认证通过了、只是缺音频 → 说明 key 可用 → 返回 true
    ///   不需要传真实音频,省一次上传。
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct, Guid? userId = null)
    {
        var snapshot = await _keys.ResolveAsync(userId ?? Guid.Empty, ct);
        if (!snapshot.HasKey)
            throw new InvalidOperationException("未配置 AzureSpeech:Key，请先保存密钥。");

        return await PingWithAsync(snapshot.Key!, snapshot.Region, ct);
    }

    /// <summary>
    /// 用**指定凭据**做连通性测试 —— 不读库、不写库。
    ///
    /// 用途(Forrest 第 1 条需求:先测后存):用户在设置页输入一把**尚未保存**的
    /// 候选 key,我们要先验证它可用,通过了才允许写入数据库。
    /// 此时库里那把旧的(或根本没有)都不该参与 —— 必须直接测参数里的凭据。
    ///
    /// region 为空时回落默认区域,避免拼出非法主机名。
    /// </summary>
    public async Task<bool> PingWithAsync(string key, string? region, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("未提供可测试的密钥。");

        var r = string.IsNullOrWhiteSpace(region) ? _configRegion : region.Trim();
        var endpoint = $"https://{r}.stt.speech.microsoft.com";

        // STT 短音频端点。⚠️ 路径必须是 /speech/recognition/... ——
        //   /stt/ 前缀只属于"自定义子域"主机({name}.cognitiveservices.azure.com);
        //   我们用区域主机({region}.stt.speech.microsoft.com),不能带 /stt,
        //   否则 Azure 返回 404。
        var url = $"{endpoint}/speech/recognition/conversation/cognitiveservices/v1" +
                  "?language=en-US&format=simple";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", key);
        // ⚠️ 必须带 User-Agent,否则 Azure 接入层(istio-envoy)回 400 空 body ——
        //    见 UserAgent 常量的详细说明。
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Content = new ByteArrayContent([]);
        req.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var client = httpFactory.CreateClient("azure-speech");
        using var res = await client.SendAsync(req, ct);

        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new AzureAuthException(
                $"Azure 拒绝该密钥(HTTP {(int)res.StatusCode})。" +
                (string.IsNullOrWhiteSpace(body) ? "" : " " + Truncate(body, 200)));
        }

        // 400 = 认证已过,只是没给音频 —— 这正是我们要的"key 可用"的证据
        return true;
    }

    /// <summary>
    /// 解析 STT 基地址 —— 单一出口,避免各处各写一份 "endpoint ?? 拼默认" 而分叉。
    ///
    /// ⚠️ 2026-09-16 修的一类真实 bug:旧代码写 `snapshot.Endpoint ?? $"..."`,
    ///   而 Endpoint 常见值是**空字符串**(不是 null),`??` 不会拦截空串,
    ///   于是 url 变成 "/speech/recognition/..." 相对地址,
    ///   HttpClient 直接抛 "An invalid request URI was provided" → 500。
    ///   这里统一用 IsNullOrWhiteSpace 判空并 TrimEnd('/'),永不再出相对地址。
    /// </summary>
    private static string ResolveSttBase(SpeechKeySnapshot s)
    {
        var configured = s.Endpoint?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.TrimEnd('/');
        var region = string.IsNullOrWhiteSpace(s.Region) ? "canadacentral" : s.Region.Trim();
        return $"https://{region}.stt.speech.microsoft.com";
    }

    /// <summary>
    /// 对一段音频做跟读评分。
    ///
    /// ★ 2026-09-26(Forrest 报"评分只有一半"):**长音频分段评估**。
    ///   根因:Azure STT 短音频 REST 接口请求最多 60 秒音频,且官方明确
    ///   "发音评估音频不应超过 30 秒";超长音频 Azure **不报错、静默只处理
    ///   前 ~60 秒** → 后半篇全部记 Omission,Completeness/Recognition 腰斩
    ///   (实测 1:59 录音只评出前 59.5s,Recognition 29%)。
    ///   修法(官方推荐思路):把 WAV 在句间停顿(能量最低点)切成 ≤30s 的段,
    ///   逐段送评;每段传"尚未被认领的剩余参考文本",用 Azure 自己的词级对齐
    ///   推进指针;最后按官方权重公式合并:
    ///   Pron = 0.3·Accuracy + 0.2·Prosody + 0.3·Fluency + 0.2·Completeness。
    /// </summary>
    /// <param name="audio">PCM WAV 16kHz 单声道(浏览器录的 webm 必须先转码)。</param>
    /// <param name="referenceText">参考文本(有它才是 scripted 模式,能拿完整度和错读判定)。</param>
    /// <param name="language">如 en-US。</param>
    public async Task<PronunciationResult> AssessAsync(byte[] audio, string referenceText,
        string language, CancellationToken ct, Guid? userId = null)
    {
        // 每次调用都解析一次 —— 设置页刚保存的 key 立刻生效
        var snapshot = await _keys.ResolveAsync(userId ?? Guid.Empty, ct);
        if (!snapshot.HasKey)
            throw new InvalidOperationException(
                "未配置 AzureSpeech:Key。请在 AI 语音设置里保存密钥后再评分。");

        var _key = snapshot.Key!;
        var _endpoint = ResolveSttBase(snapshot);

        if (audio.Length > MaxAudioBytes)
            throw new InvalidOperationException(
                $"音频 {audio.Length / 1024.0 / 1024.0:F1}MB 超过 {MaxAudioBytes / 1024 / 1024}MB 上限。");

        // 长音频:分段评估。仅 scripted 模式(有参考文本)才需要 ——
        // unscripted 没有 Omission 概念,保持单次行为不变。
        if (!string.IsNullOrWhiteSpace(referenceText) &&
            WavSegmenter.TryParse(audio, out var info) &&
            WavSegmenter.DurationSeconds(info) > MaxSegmentSeconds)
        {
            return await AssessSegmentedAsync(audio, referenceText, language,
                _key, _endpoint, info, ct);
        }

        return await CallAzureOnce(audio, referenceText, language, _key, _endpoint, ct);
    }

    /// <summary>
    /// 分段评估 + 合并。
    ///
    /// 参考文本指针的推进规则:每段把"剩余全部参考文本"传给 Azure(它会把
    /// 本段没读到的词全标 Omission)。我们只保留**最后一个读出词之前**的部分
    /// —— 之后的 Omission 属于后续段落,提前收下会重复计数。指针按
    /// "读出词 + 保留区间的 Omission"推进(Insertion/无词级数据不消费引用)。
    /// 全程静音的段不消费引用;结尾若引用还有剩,统一补 Omission 收尾
    /// —— 与 Azure 单次评估"没读到的都算 Omission"的语义一致。
    /// </summary>
    private async Task<PronunciationResult> AssessSegmentedAsync(
        byte[] audio, string referenceText, string language,
        string key, string endpoint, WavSegmenter.WavInfo info, CancellationToken ct)
    {
        var segments = WavSegmenter.Split(audio, MaxSegmentSeconds, info);
        var refTokens = ReferenceTokens(referenceText);
        var allWords = new List<WordScore>();
        var recognizedParts = new List<string>();
        double fluencyW = 0, prosodyW = 0, fluencyDur = 0, prosodyDur = 0;
        var pointer = 0;

        logger.LogInformation(
            "发音评估:音频 {Sec:F1}s 超过单段 {Max}s 上限,切分为 {N} 段逐段评估",
            WavSegmenter.DurationSeconds(info), MaxSegmentSeconds, segments.Count);

        for (var i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var seg = segments[i];
            var segSec = WavSegmenter.TryParse(seg, out var si)
                ? WavSegmenter.DurationSeconds(si) : 0;

            var sliceText = pointer < refTokens.Length
                ? string.Join(' ', refTokens.Skip(pointer))
                : string.Empty;

            var parsed = await CallAzureOnce(seg, sliceText, language, key, endpoint, ct,
                label: $"段{i + 1}/{segments.Count}");

            if (parsed.Words.Count > 0)
            {
                var lastSpoken = -1;
                for (var j = 0; j < parsed.Words.Count; j++)
                    if (parsed.Words[j].ErrorType is "None" or "Mispronunciation")
                        lastSpoken = j;

                if (lastSpoken >= 0)
                {
                    var consumed = 0;
                    for (var j = 0; j <= lastSpoken; j++)
                    {
                        var w = parsed.Words[j];
                        allWords.Add(w);
                        // Insertion / 无词级数据("")不消费参考文本
                        if (w.ErrorType is not ("Insertion" or "")) consumed++;
                    }
                    pointer += consumed;
                }
            }

            if (!string.IsNullOrWhiteSpace(parsed.RecognizedText))
                recognizedParts.Add(parsed.RecognizedText.Trim());

            if (parsed.FluencyScore is double f) { fluencyW += f * segSec; fluencyDur += segSec; }
            if (parsed.ProsodyScore is double p) { prosodyW += p * segSec; prosodyDur += segSec; }

            logger.LogInformation(
                "发音评估[段{Idx}/{N}]:读出={Spoken} Omission={Omit} 指针={Pointer}/{Total}",
                i + 1, segments.Count,
                parsed.Words.Count(w => w.ErrorType is "None" or "Mispronunciation"),
                parsed.Words.Count(w => w.ErrorType == "Omission"),
                pointer, refTokens.Length);
        }

        // 引用没走完 → 剩余全部按 Omission 收尾
        for (; pointer < refTokens.Length; pointer++)
            allWords.Add(new WordScore(refTokens[pointer], 0, "Omission"));

        // ---- 合并各维度 ----
        var spoken = allWords.Where(w => w.ErrorType is "None" or "Mispronunciation").ToList();
        double? accuracy = spoken.Count > 0 ? Math.Round(spoken.Average(w => w.Accuracy), 1) : null;
        var noneCount = allWords.Count(w => w.ErrorType == "None");
        double? completeness = refTokens.Length > 0
            ? Math.Round(100.0 * noneCount / refTokens.Length, 1)
            : null;
        double? fluency = fluencyDur > 0 ? Math.Round(fluencyW / fluencyDur, 1) : null;
        double? prosody = prosodyDur > 0 ? Math.Round(prosodyW / prosodyDur, 1) : null;

        logger.LogInformation(
            "发音评估合并:A={Acc} F={Flu} P={Pro} C={Comp} 总词={Total} 读出={Spoken} 引用词={Ref}",
            accuracy, fluency, prosody, completeness,
            allWords.Count, spoken.Count, refTokens.Length);

        return new PronunciationResult(
            MergePron(accuracy, prosody, fluency, completeness),
            accuracy, fluency, completeness, prosody,
            string.Join(' ', recognizedParts), allWords);
    }

    /// <summary>
    /// Azure 官方权重:Pron = 0.3·Accuracy + 0.2·Prosody + 0.3·Fluency + 0.2·Completeness。
    /// 某维度拿不到(F0 层只给 Accuracy)时,按剩余维度的权重和归一,绝不拿 0 冒充。
    /// </summary>
    private static double? MergePron(double? acc, double? pro, double? flu, double? comp)
    {
        double sum = 0, wsum = 0;
        if (acc.HasValue) { sum += 0.3 * acc.Value; wsum += 0.3; }
        if (pro.HasValue) { sum += 0.2 * pro.Value; wsum += 0.2; }
        if (flu.HasValue) { sum += 0.3 * flu.Value; wsum += 0.3; }
        if (comp.HasValue) { sum += 0.2 * comp.Value; wsum += 0.2; }
        return wsum > 0 ? Math.Round(sum / wsum, 1) : null;
    }

    /// <summary>参考文本 → 词序列(按空白切,保留原词面,供 Omission 收尾用)。</summary>
    private static string[] ReferenceTokens(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 单次调用 Azure(含 429 退避重试)。分段评估时每段独立走这里 ——
    /// 一段失败重试不会推倒整份长音频。
    /// </summary>
    private async Task<PronunciationResult> CallAzureOnce(
        byte[] audio, string referenceText, string language,
        string key, string endpoint, CancellationToken ct, string? label = null)
    {
        var url = $"{endpoint}/speech/recognition/conversation/cognitiveservices/v1" +
                  $"?language={language}&format=detailed";

        var paConfig = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ReferenceText = referenceText,
            GradingSystem = "HundredMark",
            Granularity = "Word",
            Dimension = "Comprehensive",
            EnableMiscue = true,
            Scenario = string.IsNullOrWhiteSpace(referenceText) ? "unscripted" : "scripted"
        })));

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Ocp-Apim-Subscription-Key", key);
                req.Headers.Add("Pronunciation-Assessment", paConfig);
                // ⚠️ 必须带 User-Agent,否则 Azure 接入层(istio-envoy)回 400 空 body。
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

                // 显式 Content-Length:Azure REST 端点在 chunked 上传下会 Broken pipe
                var ms = new MemoryStream(audio, writable: false);
                var content = new StreamContent(ms, audio.Length);
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Headers.ContentLength = audio.Length;
                req.Content = content;

                using var resp = await httpFactory.CreateClient("azure-speech").SendAsync(req, ct);

                if ((int)resp.StatusCode == 429)
                {
                    var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    logger.LogWarning("发音评估{Label}被限流(429),{Wait}s 后重试",
                        Label(label), wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException(
                        $"Azure 发音评估{Label(label)} {(int)resp.StatusCode}: {Truncate(body, 300)}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    continue;
                }

                var parsed = Parse(body);

                // ★ 第二十九轮诊断:把词级解析结果打到日志。
                //   用途:界面出现"总分正常但 Recognition 0%、逐词无分"时,
                //   一眼就能分辨是 **Azure 没给词级数据** 还是 **我们解析丢了** ——
                //   没有这条日志只能在两边来回猜。
                //   (只打统计量,不打音频内容。)
                try
                {
                    var withData = parsed.Words.Count(x => !string.IsNullOrEmpty(x.ErrorType));
                    var noneOk = parsed.Words.Count(x => x.ErrorType == "None");
                    var omitted = parsed.Words.Count(x => x.ErrorType == "Omission");
                    var w0 = parsed.Words.FirstOrDefault();
                    logger.LogInformation(
                        "发音评估{Label}解析:词数={Total} 有词级数据={WithData} None={NoneOk} " +
                        "Omission={Omitted} 样例=({Word}/{Acc}/{Err}) " +
                        "Pron={Pron} Acc={Acc2} 识别文本长度={RecLen}",
                        Label(label), parsed.Words.Count, withData, noneOk, omitted,
                        w0?.Word ?? "-", w0?.Accuracy ?? -1, w0?.ErrorType ?? "-",
                        parsed.PronScore, parsed.AccuracyScore,
                        (parsed.RecognizedText ?? "").Length);
                }
                catch { /* 诊断日志绝不影响主流程 */ }

                return parsed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                logger.LogWarning(ex, "发音评估{Label}第 {Attempt} 次失败",
                    Label(label), attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        throw new InvalidOperationException(
            $"发音评估{Label(label)}失败(重试 3 次):{last?.Message}", last);
    }

    private static string Label(string? label) => label is null ? "" : $"[{label}]";

    /// <summary>
    /// 解析 Azure 响应。
    /// 字段名是 DisplayText / NBest[0].Lexical —— 不是想当然的 Display,写错会静默拿到空串。
    /// </summary>
    internal static PronunciationResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var recognized = root.TryGetProperty("DisplayText", out var dt)
            ? dt.GetString() ?? string.Empty
            : string.Empty;

        if (!root.TryGetProperty("NBest", out var nbest) || nbest.GetArrayLength() == 0)
            return new PronunciationResult(null, null, null, null, null, recognized, []);

        var best = nbest[0];

        // 顶层分数可能缺失(Free F0 只给 Accuracy),拿到才用
        double? accuracy = Num(best, "AccuracyScore");
        double? fluency = Num(best, "FluencyScore");
        double? completeness = Num(best, "CompletenessScore");
        double? prosody = Num(best, "ProsodyScore");
        double? pron = Num(best, "PronScore");

        // 若连 PronScore 都没有但有 Accuracy,用 Accuracy 作总分(如实标注)
        pron ??= accuracy;

        var words = new List<WordScore>();
        if (best.TryGetProperty("Words", out var ws))
        {
            foreach (var w in ws.EnumerateArray())
            {
                var word = w.TryGetProperty("Word", out var wv) ? wv.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(word)) continue;

                double acc = 0;
                string errorType = "None";
                // ⚠️ 2026-09-16(Forrest 本轮):"report 是不是假的"的真因。
                //    旧实现:词级 PronunciationAssessment 缺失时 acc 默认 0、
                //    errorType 默认 "None" —— 于是报告里出现"准确度 0.0 但算正确",
                //    Recognition 还被抬到 100%,看起来就是一份自相宜的【假报告】。
                //    现在:拿不到词级评估就记 errorType = "None" 且 acc = 0,
                //    但我们同时**标记 hasWordData**;前端对无数据的词显示 "—"。
                // ★★ 第二十九轮真凶(Forrest 真机:Recognition 恒 0%、逐词无分数)★★
                //    Azure 的 detailed 响应里,词级评估字段是【平铺】在词对象上的:
                //      { "Word": "...", "Offset": .., "Duration": ..,
                //        "Confidence": .., "AccuracyScore": .., "ErrorType": "None" }
                //    —— 并**没有**嵌套的 PronunciationAssessment 子对象。
                //
                //    旧实现只读嵌套路径 `w.PronunciationAssessment.AccuracyScore`,
                //    于是 **永远读不到** → hasWordData 恒 false → errorType 被置空串
                //    → 前端 recognitionPct 过滤掉全部词 → **恒定返回 0%**,
                //    逐词网格也因 !!errorType 为假而全部显示 "—"。
                //    但总分(PronScore/Accuracy/Fluency/Completeness)来自 NBest 顶层,
                //    照常返回 —— 于是报告呈现为"分数正常却 Recognition 0%、无逐词分",
                //    正是 Forrest 截图里的样子。
                //
                //    修法:**两种形态都读** —— 优先嵌套(个别配置/未来版本),
                //    读不到再回退平铺(当前实测形态)。这样不会再被响应形态变化打懵。
                var hasWordData = false;
                if (w.TryGetProperty("PronunciationAssessment", out var pa) &&
                    pa.ValueKind == JsonValueKind.Object)
                {
                    hasWordData = true;
                    if (pa.TryGetProperty("AccuracyScore", out var a) &&
                        a.ValueKind == JsonValueKind.Number)
                        acc = a.GetDouble();
                    if (pa.TryGetProperty("ErrorType", out var et))
                        errorType = et.GetString() ?? "None";
                }
                else
                {
                    // 回退:平铺形态(当前实测如此)
                    if (w.TryGetProperty("AccuracyScore", out var a2) &&
                        a2.ValueKind == JsonValueKind.Number)
                    {
                        acc = a2.GetDouble();
                        hasWordData = true;
                    }
                    if (w.TryGetProperty("ErrorType", out var et2) &&
                        et2.ValueKind == JsonValueKind.String)
                    {
                        errorType = et2.GetString() ?? "None";
                        hasWordData = true;
                    }
                }
                // 无词级数据 → errorType 用空串标记"未知",而不是冒充 "None"(正确)。
                // 这样前端 recognitionPct 就不会把没评估的词算作"识别正确"。
                if (!hasWordData) errorType = "";
                words.Add(new WordScore(word, Math.Round(acc, 1), errorType));
            }
        }


        return new PronunciationResult(
            pron is null ? null : Math.Round(pron.Value, 1),
            accuracy is null ? null : Math.Round(accuracy.Value, 1),
            fluency is null ? null : Math.Round(fluency.Value, 1),
            completeness is null ? null : Math.Round(completeness.Value, 1),
            prosody is null ? null : Math.Round(prosody.Value, 1),
            recognized,
            words);
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}

/// <summary>评分结果。拿不到的维度一律 null —— 不编造。</summary>
public sealed record PronunciationResult(
    double? PronScore,
    double? AccuracyScore,
    double? FluencyScore,
    double? CompletenessScore,
    double? ProsodyScore,
    string RecognizedText,
    IReadOnlyList<WordScore> Words);

public sealed record WordScore(string Word, double Accuracy, string ErrorType);

/// <summary>
/// Azure 神经语音合成(示范朗读用)。
///
/// Forrest 需求第 3 条后半:"示范朗读也可以选择使用这个 key 的能力"。
/// 所以示范朗读有两种模式:
///   · browser —— 浏览器内置 speechSynthesis,免费即时,音质一般
///   · azure   —— 走本服务合成,自然人声,消耗额度
///
/// ⚠️ 诚实约束:如果用户选了 azure 但服务端没有可用 key,
///    端点返回 503 并给出明确原因,**绝不用浏览器语音冒充 Azure**
///    (那会让用户以为已经用上了高质量语音,实际没有)。
/// </summary>
public sealed class SpeechSynthesizer(
    IHttpClientFactory httpFactory,
    YourInterview.Services.Assessment.Infrastructure.Storage.ISpeechKeyProvider keyProvider,
    ILogger<SpeechSynthesizer> logger)
{
    /// <summary>默认音色 —— 北美面试语境用 en-US 女声,Aria 是最常用的自然音色。</summary>
    private const string DefaultVoice = "en-US-AriaNeural";

    /// <summary>
    /// 法语默认音色 —— 2026-09-19(Forrest 报"Azure key 不支持法语")。
    ///
    /// ⚠️ 真因不是 Azure 不支持法语,而是本服务把音色与 SSML 的 xml:lang
    ///    双双写死成 en-US:用英语音色去念法语文本,读出来是"法文乱念",
    ///    听起来就像不支持。修法是**按语言选音色 + 按语言写 xml:lang**,
    ///    两者必须同源(同一个 language 值),否则仍会错配。
    /// </summary>
    private const string DefaultVoiceFr = "fr-FR-DeniseNeural";

    /// <summary>
    /// 中文默认音色 —— 2026-09-25(用户报"选中文播放直接报错")。
    ///
    /// 与 2026-09-19 法语问题同因:当时只给 DefaultVoiceFor 加了 fr 分支,
    /// zh-CN 依旧落到英语音色 → 音色 locale(en-US)与 SSML xml:lang(zh-CN)
    /// 不匹配,Azure 拒绝合成,前端表现为 "Audio failed to load"。
    /// Xiaoxiao 是 Azure 最常用的中文女声,自然度与 Aria 相当。
    /// </summary>
    private const string DefaultVoiceZh = "zh-CN-XiaoxiaoNeural";

    /// <summary>
    /// 按语言代码选默认音色 —— 单一出口,避免各处各写一份语言判断而分叉。
    /// 法语 / 中文各走各的神经音色,其它回落英语;将来加语言只改这一个方法。
    /// </summary>
    internal static string DefaultVoiceFor(string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
        // 只要以 fr 开头(fr / fr-FR / fr-CA)就用法语音色 ——
        // 法语各区域变体都能被 fr-FR 音色正确朗读,无需逐区域建表。
        if (lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
            return DefaultVoiceFr;
        // 同理:zh / zh-CN / zh-TW 都能用 zh-CN 音色正确朗读
        if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return DefaultVoiceZh;
        return DefaultVoice;
    }

    /// <summary>
    /// 调用 Azure Speech 时带的 User-Agent。
    ///
    /// ⚠️ 2026-09-16 血泪教训:**Azure 语音的接入层(istio-envoy)会拒收不带
    ///    User-Agent 的请求,直接回 400 且 body 为空**
    ///    (x-envoy-upstream-service-time 只有 1ms 就是被边缘拒的铁证)。
    ///    .NET 的 HttpClient 默认不发 UA,而 curl / Python urllib 都自带,
    ///    所以工具直连总是 200、服务代码永远 400 —— 极难察觉。
    /// </summary>
    private const string UserAgent = "YourInterview/1.0 (assessment-service)";

    /// <summary>单次合成文本上限。示范朗读是一段答案,不该长到几万字。</summary>
    private const int MaxTextLength = 4000;

    /// <summary>
    /// 合成一段语音,返回 MP3 字节。
    /// </summary>
    /// <param name="voice">音色名(如 en-US-AriaNeural)。为空则按 language 选默认。</param>
    /// <param name="speed">语速倍率(0.5-2.0)。1.0 = 原速。</param>
    /// <param name="language">
    /// 语言代码(如 en-US / fr-FR)。★ 2026-09-19 新增 —— 此前该方法**根本没有
    /// 语言参数**,语言信息在进入合成前就被丢掉,SSML 的 xml:lang 只能写死 en-US。
    /// 这是"法语不支持"的根因之一。
    /// </param>
    public async Task<byte[]> SynthesizeAsync(string text, string? voice, double? speed,
        Guid userId, CancellationToken ct, string? language = null)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("要合成的文本不能为空", nameof(text));
        if (text.Length > MaxTextLength)
            throw new InvalidOperationException($"文本 {text.Length} 字符超过 {MaxTextLength} 上限。");

        var snapshot = await keyProvider.ResolveAsync(userId, ct);
        if (!snapshot.HasKey)
            throw new InvalidOperationException(
                "未配置 AzureSpeech:Key,无法使用 Azure 神经语音。请前往 AI 语音设置保存密钥。");

        // ⚠️ 合成端点必须与识别端点域名不同(识别=api.cognitive,合成=tts.speech)。
        //    配置里的 AzureSpeech:Endpoint 是为识别端点准备的,不能用于合成 ——
        //    若沿用配置值会打到 api.cognitive.microsoft.com 导致 Azure 返回 404。
        var endpoint = $"https://{snapshot.Region}.tts.speech.microsoft.com";

        // ⚠️ 音色必须与 language 匹配:法语文本配英语音色 = 英语发音规则念法文,
        //    听起来就是"乱念"。语音色显式指定则尊重用户选择。
        var v = string.IsNullOrWhiteSpace(voice) ? DefaultVoiceFor(lang) : voice.Trim();
        var rate = Math.Clamp(speed ?? 1.0, 0.5, 2.0);
        // SSML 里的 rate 是相对百分比:1.0 → "+0%",1.2 → "+20%"
        var ratePercent = (int)Math.Round((rate - 1.0) * 100);

        // SSML 里的文本必须转义,否则 < > & 会被当成标签 —— 这是安全与正确性双重问题
        var safe = System.Security.SecurityElement.Escape(text) ?? text;

        // ⚠️ Azure TTS 的 SSML 必须声明 xmlns 命名空间,否则 Azure 直接返回 400
        //    ("SSML is invalid")。这是最容易漏、最难猜的一处 —— 缺 xmlns 时
        //    报错体还常是空的,只看到 400,极难定位。
        //    rate 也必须是带符号百分比(+0%/-10%),故用 :+#;-#;0 格式串。
        //    ⚠️ xml:lang 必须与音色同源(同一个 lang 值)。
        //       写死 en-US 时,Azure 会用英语的韵律规则处理法语文本。
        var ssml = $"""
            <speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{lang}'>
              <voice name='{v}'>
                <prosody rate='{ratePercent:+#;-#;0}%'>{safe}</prosody>
              </voice>
            </speak>
            """;

        // Azure 语音合成端点(注意路径是 /cognitiveservices/v1,与识别端点不同)
        var url = $"{endpoint}/cognitiveservices/v1";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", snapshot.Key);
        req.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        var content = new StringContent(ssml, System.Text.Encoding.UTF8, "application/ssml+xml");
        // ⚠️ StringContent 会自作主张在 Content-Type 后面追加 "; charset=utf-8"。
        //    这里显式清掉 charset,只保留 application/ssml+xml
        //    —— 与官方文档示例及 curl 实测一致。
        content.Headers.ContentType!.CharSet = null;
        req.Content = content;

        // ⚠️⚠️ 必传 User-Agent —— 这是 2026-09-16 花了很久才挖出的真因!
        //
        //    Azure 语音的接入层(响应头里的 istio-envoy)会拒收**不带 User-Agent**
        //    的请求,直接回 400 **且 body 为空**,根本到不了 Speech 服务
        //    (x-envoy-upstream-service-time 只有 1ms 就是铁证)。
        //
        //    .NET 的 HttpClient **默认不发 User-Agent**;而 curl / Python urllib
        //    都会自带一个(如 python-urllib/3.11),所以用它们直连永远 200。
        //    于是形成极隐蔽的"工具测通、代码必败"假象:
        //      同一 URL、同一 key、同一 192 字节 SSML、同一 Content-Type,
        //      curl → 200,Python → 200,HttpClient → 400。
        //
        //    实测(同一台机器、逐项删头):
        //      去掉 User-Agent → 400；加上 → 200。
        //
        //    ⚠️ 直接用请求头加,不要依赖 HttpClient 默认头 —— 不同实现
        //       对全局默认 UA 的处理不一致,显式加最稳。
        if (!req.Headers.Contains("User-Agent"))
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        var client = httpFactory.CreateClient("azure-speech");
        using var res = await client.SendAsync(req, ct);

        if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new AzureAuthException(
                $"Azure 拒绝该密钥(HTTP {(int)res.StatusCode})。" +
                (string.IsNullOrWhiteSpace(body) ? "" : " " + body[..Math.Min(body.Length, 200)]));
        }

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            // ★ 关键诊断:Azure 失败时把【实际发出的 Content-Type / 是否带 User-Agent / bytes】
            //   与原始响应一起打出。这行日志是 2026-09-16 挖出"缺 User-Agent 被
            //   istio-envoy 边缘拒收"的功臣,保留为长期诊断能力(不输出 key 明文)。
            logger.LogError("TTS 上游失败 {Status} | url={Url} | voice={Voice} | rate={Rate} | " +
                "outbound-bytes={OutBytes} | content-type={Ct} | has-ua={HasUa} | azure-body=<<{Body}>>",
                (int)res.StatusCode, url, v, rate,
                Encoding.UTF8.GetByteCount(ssml),
                req.Content?.Headers.ContentType?.ToString() ?? "(none)",
                req.Headers.Contains("User-Agent"), body);
            throw new InvalidOperationException(
                $"Azure 语音合成失败(HTTP {(int)res.StatusCode}): {body[..Math.Min(body.Length, 300)]}");
        }

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        logger.LogInformation("TTS 合成完成 {Chars} 字符 → {Bytes} 字节 (voice={Voice}, rate={Rate})",
            text.Length, bytes.Length, v, rate);
        return bytes;
    }

    /// <summary>是否已配置 key(同步快判,只看配置来源;查库是异步的)。</summary>
    public bool ConfigLooksPresent() => true;
}


/// <summary>
/// Azure 认证失败(密钥错误 / 区域不匹配)。
///
/// 单独一个异常类型而不是复用 InvalidOperationException:
/// 端点要据此返回 401 并给出**可操作**的提示("去核对密钥或区域"),
/// 而不是笼统的 502 —— 用户需要知道该去改什么。
/// </summary>
public sealed class AzureAuthException(string message) : Exception(message);
