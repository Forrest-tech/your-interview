using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// Azure Speech 客户端。
///
/// 实现选择:直接用 REST API(而不是 Cognitive Services SDK)。
/// 理由:① 免去一个重量级依赖 ② REST 的行为在脚本里已经实测跑通,
/// 换成 SDK 等于把已验证的路径重新赌一次 ③ Free F0 层的能力边界(只有 AccuracyScore,
/// 没有 Fluency/Prosody)在 REST 下更清楚,不会误以为 SDK 能拿到更多。
///
/// 已知平台限制(实测得出,不是猜测):
///   Free F0 + conversation 端点只返回 AccuracyScore。
///   流利度/韵律需付费层或 SDK —— 所以本项目用自研声学指标替代,而不是编造分数。
/// </summary>
public sealed class AzureSpeechClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AzureSpeechClient> _logger;
    private readonly string _key;
    private readonly string _region;
    private readonly string _endpoint;

    // 每片音频上限:Azure Speech REST 端点对单次请求体有大小限制,
    // 传超大文件会直接被断开连接(Broken pipe),现象是"上传到一半连接没了"。
    // 这也是为什么长录音必须先切片再逐片转写。
    private const int MaxSegmentBytes = 9 * 1024 * 1024;

    public AzureSpeechClient(IHttpClientFactory httpFactory, IConfiguration config,
        ILogger<AzureSpeechClient> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _key = config["AzureSpeech:Key"]
            ?? Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY")
            ?? throw new InvalidOperationException("缺少 AzureSpeech:Key");

        _region = config["AzureSpeech:Region"]
            ?? Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? "canadacentral";

        _endpoint = (config["AzureSpeech:Endpoint"] ?? $"https://{_region}.api.cognitive.microsoft.com/")
            .TrimEnd('/');

        logger.LogInformation("Azure Speech 客户端就绪(region={Region}, key={KeyLen} 字符, endpoint={Endpoint})",
            _region, _key.Length, _endpoint);
    }

    /// <summary>
    /// 专用的 HttpClient。
    ///
    /// 为什么要显式建而不是用 DI 里那个默认的:
    ///   默认 HttpClient 会走 chunked transfer-encoding,而 Azure Speech 的 REST 端点
    ///   对 chunked 上传处理不佳 —— 表现为 Broken pipe。
    ///   自定义客户端 + 显式 Content-Length 就能稳定上传。
    /// </summary>
    private HttpClient CreateClient() => _httpFactory.CreateClient("azure-speech");

    /// <summary>
    /// 转写一段音频(≤60 秒的分片最佳实践)。
    /// 用带重试的调用 —— 免费层有配额与瞬时失败,一次性失败就放弃会让长录音必然失败。
    /// </summary>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavPath,
        int segmentIndex, double offsetSeconds, string language, CancellationToken ct)
    {
        var url = $"{_endpoint}/stt/speech/recognition/conversation/cognitiveservices/v1" +
                  $"?language={language}&profanity=raw&format=detailed";

        var audio = await File.ReadAllBytesAsync(wavPath, ct);

        if (audio.Length > MaxSegmentBytes)
            _logger.LogWarning("分片 {Index} 大小 {MB:F1}MB 超过 {Limit}MB 上限,上传很可能失败",
                segmentIndex, audio.Length / 1024.0 / 1024.0, MaxSegmentBytes / 1024 / 1024);

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Ocp-Apim-Subscription-Key", _key);
                req.Headers.Add("Accept", "application/json");

                // 用 StreamContent + 显式长度,避免 chunked 编码
                var ms = new MemoryStream(audio, writable: false);
                var content = new StreamContent(ms, audio.Length);
                content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
                content.Headers.ContentLength = audio.Length;
                req.Content = content;

                using var resp = await CreateClient().SendAsync(req, ct);

                if ((int)resp.StatusCode == 429)
                {
                    // 免费层配额限流 —— 退避后重试
                    var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    _logger.LogWarning("Azure Speech 限流(429),{Wait}s 后重试", wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    last = new HttpRequestException($"Azure STT {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    continue;
                }

                return ParseSttResponse(body, segmentIndex, offsetSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _logger.LogWarning(ex, "转写第 {Attempt} 次失败", attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        throw new InvalidOperationException($"转写失败(重试 3 次):{last?.Message}", last);
    }

    /// <summary>
    /// 解析 STT 响应。
    /// 踩过的坑:字段名是 DisplayText / NBest[0].Lexical,
    /// 不是想当然的 NBest[0].Display —— 用错会导致转写结果全空但不报错。
    /// </summary>
    internal static IReadOnlyList<TranscriptSegment> ParseSttResponse(string json, int segmentIndex,
        double offsetSeconds)    {
        var result = new List<TranscriptSegment>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("RecognitionStatus", out var status) ||
            status.GetString() != "Success")
        {
            return result; // 静音片段很正常,不算失败
        }

        string? text = null;
        if (root.TryGetProperty("DisplayText", out var display) && !string.IsNullOrWhiteSpace(display.GetString()))
            text = display.GetString();
        else if (root.TryGetProperty("NBest", out var nbest) && nbest.GetArrayLength() > 0)
        {
            var best = nbest[0];
            if (best.TryGetProperty("Lexical", out var lex) && !string.IsNullOrWhiteSpace(lex.GetString()))
                text = lex.GetString();
            else if (best.TryGetProperty("Display", out var disp))
                text = disp.GetString();
        }

        if (string.IsNullOrWhiteSpace(text)) return result;

        var duration = 0.0;
        if (root.TryGetProperty("Duration", out var dur))
        {
            // ⚠️ Duration 的 JSON 类型不固定:有时是数字(百纳秒 ticks),有时是 "PT12.3S" 字符串。
            // 直接 GetString() 会抛 "requires an element of type 'String', but the target element has type 'Number'"。
            duration = dur.ValueKind switch
            {
                JsonValueKind.Number => dur.GetDouble() / 10_000_000.0,
                JsonValueKind.String => ParseDotNetDuration(dur.GetString()),
                _ => 0.0
            };
        }

        result.Add(new TranscriptSegment(segmentIndex, offsetSeconds, offsetSeconds + duration,
            text.Trim(), "UNKNOWN"));
        return result;
    }

    /// <summary>
    /// 解析 .NET TimeSpan 格式的时长串(如 "PT12.345S" 或 "00:00:12.3450000")。
    /// Azure 在这两个端点返回的格式不同,两种都要认。
    /// </summary>
    internal static double ParseDotNetDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        // 纯数字串 = 百纳秒 ticks
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var ticks))
            return ticks / 10_000_000.0;

        if (DurationFormat.IsMatch(raw))
        {
            var t = TimeSpan.Parse(raw);
            return t.TotalSeconds;
        }

        var m = IsoDuration.IsMatch(raw) ? IsoDuration.Match(raw) : null;
        if (m is not null)
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        return 0;
    }

    private static readonly Regex DurationFormat = new(@"^\d{2}:\d{2}:\d{2}");
    private static readonly Regex IsoDuration = new(@"PT([\d.]+)S");

    /// <summary>
    /// 逐词发音评估。
    /// 注意:Free F0 只返回 AccuracyScore —— 不要假设有 FluencyScore/ProsodyScore,
    /// 拿到 null 就如实返回 null,不要用 0 冒充(那会污染平均值)。
    /// </summary>
    public async Task<IReadOnlyList<WordPronunciation>> AssessPronunciationAsync(string wavPath,
        string referenceText, string language, CancellationToken ct)
    {
        var url = $"{_endpoint}/stt/speech/recognition/conversation/cognitiveservices/v1" +
                  $"?language={language}&format=detailed";

        var paConfig = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ReferenceText = referenceText,
            GradingSystem = "HundredMark",
            Granularity = "Word",
            EnableMiscue = false
        })));

        var audio = await File.ReadAllBytesAsync(wavPath, ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _key);
        req.Headers.Add("Pronunciation-Assessment", paConfig);

        var ms = new MemoryStream(audio, writable: false);
        var content = new StreamContent(ms, audio.Length);
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Headers.ContentLength = audio.Length;
        req.Content = content;

        using var resp = await CreateClient().SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("发音评估失败 {Status}", (int)resp.StatusCode);
            return [];
        }

        return ParsePronunciationResponse(body);
    }

    internal static IReadOnlyList<WordPronunciation> ParsePronunciationResponse(string json)
    {
        var words = new List<WordPronunciation>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("NBest", out var nbest) || nbest.GetArrayLength() == 0) return words;
        var best = nbest[0];
        if (!best.TryGetProperty("Words", out var ws)) return words;

        foreach (var w in ws.EnumerateArray())
        {
            var word = w.TryGetProperty("Word", out var wv) ? wv.GetString() ?? "" : "";
            double accuracy = GetScore(w, "PronunciationAssessment", "AccuracyScore");

            double? fluency = null;
            if (w.TryGetProperty("PronunciationAssessment", out var pa) &&
                pa.TryGetProperty("FluencyScore", out var fs) &&
                fs.ValueKind == JsonValueKind.Number)
                fluency = fs.GetDouble();

            var start = GetTime(w, "Offset");
            var dur = GetTime(w, "Duration");

            words.Add(new WordPronunciation(word, accuracy, fluency, start, start + dur));
        }

        return words;
    }

    private static double GetScore(JsonElement w, string parent, string field)
    {
        if (w.TryGetProperty(parent, out var pa) && pa.TryGetProperty(field, out var v) &&
            v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return 0;
    }

    /// <summary>Azure 的时间字段是"百纳秒"单位(Ticks)。</summary>
    private static double GetTime(JsonElement w, string field)
    {
        if (w.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble() / 10_000_000.0;
        return 0;
    }
}
