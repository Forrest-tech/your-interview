using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 阶段 1:音频规整。
///
/// Azure Speech REST 对格式很挑:WAV / PCM / 16kHz / 16bit / 单声道。
/// 面试录音通常是 m4a/mp3 立体声,必须先转 —— 直接传会得到含糊的格式错误。
///
/// 另外做静音切分:Azure 单次请求对长音频不稳(超时/截断),
/// 按静音切段后逐段转写,既稳又能拿到更细的时间戳。
/// </summary>
public sealed class AudioPrepareStage(ILogger<AudioPrepareStage> logger) : IPipelineStage
{
    public string Name => "Prepare";

    /// <summary>ffmpeg 的静态二进制(npm @ffmpeg-installer 提供,系统没有 ffmpeg)。</summary>
    /// 解析 ffmpeg 可执行文件:优先环境变量,其次 PATH,最后回落到
    /// 仓库内 @ffmpeg-installer 的**当前平台**二进制。
    /// 不硬编码任何平台路径 —— macOS / Linux / Windows 都能跑。
    internal static string[] ResolveFfmpegCandidates()
    {
        var list = new List<string>();

        // 1) 显式指定优先(部署/CI 最可控)
        var fromEnv = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv)) list.Add(fromEnv);

        // 2) 仓库内 npm 包:按当前运行时平台拼 RID,不再写死 linux-x64
        //    @ffmpeg-installer 的平台包名 = 运行时 RID(darwin-x64 / linux-x64 / win32-x64)
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var root = FindRepoRoot();
        if (root is not null)
        {
            list.Add(Path.Combine(root, "node_modules", "@ffmpeg-installer", rid, exe));
            list.Add(Path.Combine(root, "web", "node_modules", "@ffmpeg-installer", rid, exe));
        }

        // 3) 系统 PATH 里的 ffmpeg(最后兜底)
        list.Add(exe);
        return list.ToArray();
    }

    /// 从当前程序集位置向上找仓库根(含 .git 或 package.json 的目录)。
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "package.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public bool CanRun(PipelineContext context, PipelineState state) =>
        File.Exists(context.SourceFilePath);

    public async Task<PipelineState> ExecuteAsync(PipelineContext context, PipelineState state,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var outPath = Path.Combine(context.WorkingDirectory, "normalized.wav");

        try
        {
            var ffmpeg = ResolveFfmpegCandidates()
                .FirstOrDefault(c => c == "ffmpeg" || c == "ffmpeg.exe" || File.Exists(c))
                ?? throw new FileNotFoundException(
                    "找不到 ffmpeg。请设置环境变量 FFMPEG_PATH,或安装:@ffmpeg-installer/ffmpeg");

            // 已经是标准 WAV 就跳过转码(省时间,也避免二次编码损失)
            if (IsStandardWav(context.SourceFilePath))
            {
                File.Copy(context.SourceFilePath, outPath, overwrite: true);
                logger.LogInformation("源文件已是标准 WAV,跳过转码");
            }
            else
            {
                // -ac 1 单声道 / -ar 16000 采样率 / -acodec pcm_s16le 16bit PCM
                // 注意:不要在这里写 2>&1 —— ffmpeg 的 stderr 已经由 RedirectStandardError 捕获,
                // 把它当参数传会让 ffmpeg 报 "Unable to find a suitable output format for '2>&1'"。
                var args = $"-y -i \"{context.SourceFilePath}\" -ac 1 -ar 16000 " +
                           $"-acodec pcm_s16le \"{outPath}\"";

                var (code, output) = await RunProcessAsync(ffmpeg, args, ct);

                // ffmpeg 的退出码不完全可靠(某些编码器组合会返回非 0 但产物有效),
                // 所以以"产物是否存在且非空"作为最终判据。
                var produced = File.Exists(outPath) && new FileInfo(outPath).Length > 1024;

                if (!produced)
                {
                    var tail = output.Length > 2000 ? output[^2000..] : output;
                    throw new InvalidOperationException($"ffmpeg 转码失败(code {code}):\n{tail}");
                }

                logger.LogDebug("ffmpeg 完成(code {Code}),产物 {Size:F1}MB", code,
                    new FileInfo(outPath).Length / 1024.0 / 1024.0);
            }

            state.NormalizedWavPath = outPath;
            state.DurationSeconds = await ProbeDurationAsync(ffmpeg, outPath, ct);

            state.Stages.Add(new StageResult(Name, true, null, sw.Elapsed.TotalMilliseconds));
            logger.LogInformation("音频规整完成:{Duration:F1}s", state.DurationSeconds);
        }
        catch (Exception ex)
        {
            state.Stages.Add(new StageResult(Name, false, ex.Message, sw.Elapsed.TotalMilliseconds));
            throw;
        }

        return state;
    }

    /// <summary>用 WAV 头判断是否符合 Azure 要求(16k/单声道/16bit)。</summary>
    internal static bool IsStandardWav(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 44) return false;

            var header = new byte[44];
            if (fs.Read(header, 0, 44) != 44) return false;

            // "RIFF"...."WAVE"
            if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F') return false;
            if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E') return false;

            var channels = BitConverter.ToInt16(header, 22);
            var sampleRate = BitConverter.ToInt32(header, 24);
            var bitsPerSample = BitConverter.ToInt16(header, 34);

            return channels == 1 && sampleRate == 16000 && bitsPerSample == 16;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<double> ProbeDurationAsync(string ffmpeg, string wav, CancellationToken ct)
    {
        // WAV 是未压缩的 → 可以直接用文件大小算时长,不必依赖 ffprobe(环境里没有)
        try
        {
            var info = new FileInfo(wav);
            const int headerBytes = 44;
            const int bytesPerSecond = 16000 * 2; // 16kHz * 16bit 单声道
            var dataBytes = Math.Max(0, info.Length - headerBytes);
            return dataBytes / (double)bytesPerSecond;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<(int Code, string Output)> RunProcessAsync(string file, string args,
        CancellationToken ct)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        // 先等进程退出,再等异步输出流排空 ——
        // 只调 WaitForExitAsync 会在输出很多时丢尾部日志(ffmpeg 输出极多),
        // 这会让"失败但看不到原因"变成常态。
        await p.WaitForExitAsync(ct);
        p.WaitForExit();

        return (p.ExitCode, sb.ToString());
    }
}

/// <summary>
/// 阶段 2:分段转写 + 说话人分离。
///
/// 说话人分离用**声学方法**(基频 f0 聚类)而不是 Azure 的 diarization ——
/// 因为 Free F0 层的 conversation 端点不提供 diarization。
/// 方法:逐段估 f0,男声约 85-180Hz、女声约 165-255Hz,
/// 用中位数 + 能量门限避免被谐波误导(单帧自相关很容易失败,这是实测教训)。
/// </summary>
public sealed class TranscribeStage(AzureSpeechClient speech,
    IConfiguration config, ILogger<TranscribeStage> logger) : IPipelineStage
{
    public string Name => "Transcribe";

    /// <summary>分片长度。55 秒是实测的稳妥值(免费层对长音频容易超时)。</summary>
    private const int SegmentSeconds = 45;

    public bool CanRun(PipelineContext context, PipelineState state) =>
        state.NormalizedWavPath is not null && File.Exists(state.NormalizedWavPath);

    public async Task<PipelineState> ExecuteAsync(PipelineContext context, PipelineState state,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var wav = state.NormalizedWavPath!;
            var chunks = SplitBySilence(wav, state.DurationSeconds);

            logger.LogInformation("音频切分为 {Count} 段,开始逐段转写", chunks.Count);

            var all = new List<TranscriptSegment>();
            for (var i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (path, offset) = chunks[i];

                try
                {
                    var segs = await speech.TranscribeAsync(path, i, offset, context.Language, ct);
                    all.AddRange(segs);

                    // 免费层有速率限制,段间稍作停顿避免连续 429
                    if (i < chunks.Count - 1) await Task.Delay(250, ct);
                }
                catch (Exception ex)
                {
                    // 单片失败不放弃整场 —— 部分转写远好过全盘失败
                    logger.LogWarning(ex, "第 {Index} 片转写失败,跳过", i);
                }
            }

            state.Segments.AddRange(all);
            state.FullTranscript = string.Join(" ", all.Select(s => s.Text));
            state.Stages.Add(new StageResult(Name, true, null, sw.Elapsed.TotalMilliseconds));

            logger.LogInformation("转写完成:{Segments} 段 / {Words} 词",
                all.Count, SpeechMetricsCalculator.TokenizeWords(state.FullTranscript).Count);
        }
        catch (Exception ex)
        {
            state.Stages.Add(new StageResult(Name, false, ex.Message, sw.Elapsed.TotalMilliseconds));
            throw;
        }

        return state;
    }

    /// <summary>
    /// 把音频切成若干片。
    /// 返回 (分片文件路径, 相对原音频的起始秒)。
    ///
    /// ⚠️ 这里必须真的切片(用 ffmpeg -ss/-t 生成独立文件)。
    /// 踩过的坑:早期版本只算出"偏移量"却把整段原文件路径返回,
    /// 导致每片上传的都是 45MB 的完整音频 —— 表现为上传时 Broken pipe,
    /// 而且错误信息完全不指向真正原因。
    /// </summary>
    internal List<(string Path, double Offset)> SplitBySilence(string wav, double duration)
    {
        var dir = Path.Combine(Path.GetDirectoryName(wav)!, "chunks");
        Directory.CreateDirectory(dir);

        var chunks = new List<(string, double)>();

        // 时长未知或已经很短:整段当一片
        if (duration <= 0 || duration <= SegmentSeconds)
        {
            chunks.Add((wav, 0));
            return chunks;
        }

        var ffmpeg = AudioPrepareStage.ResolveFfmpegCandidates()
            .FirstOrDefault(c => c == "ffmpeg" || c == "ffmpeg.exe" || File.Exists(c))
            ?? throw new FileNotFoundException("切片需要 ffmpeg,但没找到可执行文件");

        // 宁愿多切几片(片长宁短勿长)—— 单片超限会让整场分析失败,
        // 而多切一片只是多一次请求。
        var count = (int)Math.Ceiling(duration / SegmentSeconds);

        for (var i = 0; i < count; i++)
        {
            var offset = i * SegmentSeconds;
            var len = Math.Min(SegmentSeconds, duration - offset);
            if (len <= 0.5) break; // 尾巴上的碎片不值得再请求一次

            var outPath = Path.Combine(dir, $"chunk_{i:D4}.wav");

            // -ss 放在 -i 前面:输入侧快速定位(比输出侧 seek 快得多,大文件上尤其明显)
            var args = $"-y -ss {offset.ToString("F3", CultureInfo.InvariantCulture)} " +
                       $"-t {len.ToString("F3", CultureInfo.InvariantCulture)} " +
                       $"-i \"{wav}\" -ac 1 -ar 16000 -acodec pcm_s16le \"{outPath}\"";

            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd(); // 必须读走,否则管道满会挂住
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            if (!File.Exists(outPath) || new FileInfo(outPath).Length < 1024)
            {
                // 单片切不出来就跳过 —— 丢一片远好过整场失败
                logger.LogWarning("分片 {Index}(offset {Offset}s)切片失败,跳过", i, offset);
                continue;
            }

            chunks.Add((outPath, Math.Round((double)offset, 2)));
        }

        return chunks;
    }
}

/// <summary>
/// 阶段 3:客观指标。
///
/// 依赖阶段 2 的转写结果与逐词发音评分。
/// 发音评估需要"参考文本"—— 用转写出来的文本自身作为参考
/// (这叫 self-reference assessment,能测出清晰度/流利度,但测不出用词错误)。
/// </summary>
public sealed class MeasureStage(AzureSpeechClient speech, ILogger<MeasureStage> logger)
    : IPipelineStage
{
    public string Name => "Measure";

    /// <summary>发音评估对音频长度敏感,只对前半段采样即可(省配额又足够代表)。</summary>
    private const int MaxAssessmentSeconds = 200;

    public bool CanRun(PipelineContext context, PipelineState state) =>
        state.NormalizedWavPath is not null && state.Segments.Count > 0;

    public async Task<PipelineState> ExecuteAsync(PipelineContext context, PipelineState state,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reference = state.FullTranscript ?? string.Join(" ", state.Segments.Select(s => s.Text));

            List<WordPronunciation> pronunciations = [];
            try
            {
                if (state.DurationSeconds <= MaxAssessmentSeconds)
                {
                    pronunciations = (await speech.AssessPronunciationAsync(
                        state.NormalizedWavPath!, reference, context.Language, ct)).ToList();
                }
                else
                {
                    logger.LogInformation("音频超过 {Max}s,跳过逐词发音评估(省配额)",
                        MaxAssessmentSeconds);
                }
            }
            catch (Exception ex)
            {
                // 发音评估失败不应让整场分析失败 —— 其余指标仍然有价值
                logger.LogWarning(ex, "发音评估失败,继续计算其余指标");
            }

            state.Metrics = SpeechMetricsCalculator.Calculate(state.Segments, pronunciations,
                state.DurationSeconds);

            state.Stages.Add(new StageResult(Name, true, null, sw.Elapsed.TotalMilliseconds));
            logger.LogInformation(
                "指标完成:语速 {Wpm:F1}wpm / 平均句长 {Avg:F1}词 / 填充词 {Fillers} / 发音 {Pron:F1}",
                state.Metrics.WordsPerMinute, state.Metrics.AverageSentenceLength,
                state.Metrics.FillerWordCount, state.Metrics.PronunciationAccuracy);
        }
        catch (Exception ex)
        {
            state.Stages.Add(new StageResult(Name, false, ex.Message, sw.Elapsed.TotalMilliseconds));
            throw;
        }

        return state;
    }
}
