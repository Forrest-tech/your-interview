using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using YourInterview.SharedContracts.Analysis;

namespace YourInterview.Analysis.Worker.Analysis;

/// <summary>
/// 从整段 normalized wav 里抽出候选人发言区间,拼成一段 candidate-only wav,
/// 供发音评估使用。
///
/// 为什么必须抽:发音评估的 reference 必须是候选人自己的文本。用整段音频 +
/// 双方文本去评估,面试官说过的词会被算成候选人的"漏读/误读",Accuracy 直接失真。
///
/// 实现:单次 ffmpeg 调用,filter_complex 里 atrim+setpts 切出各区间再 concat 拼接 ——
/// 不产生中间文件,比分段抽取 + concat demuxer 少一次 I/O,失败面也更小。
/// 纯逻辑入口(BuildRanges/BuildArgs)是 public static,单元测试直接调,不碰进程。
/// </summary>
public static class CandidateAudioExtractor
{
    /// <summary>抽取后音频格式 —— 与 AudioPrepareStage 的 normalized wav 一致(评估端点要 16k PCM)。</summary>
    private const int SampleRate = 16000;

    /// <summary>
    /// 抽取候选人音频。任何失败(找不到 ffmpeg、抽取失败、产物无效)都返回 null ——
    /// 调用方降级为"跳过发音评估",不炸流水线。
    /// </summary>
    public static async Task<string?> ExtractAsync(
        string inputWav,
        IReadOnlyList<TranscriptSegment> candidateSegments,
        double totalDurationSeconds,
        string workDir,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var ffmpeg = AudioPrepareStage.ResolveFfmpegCandidates()
                .FirstOrDefault(c => c == "ffmpeg" || c == "ffmpeg.exe" || File.Exists(c));
            if (ffmpeg is null)
            {
                logger.LogWarning("找不到 ffmpeg,跳过候选人音频抽取(发音评估降级)");
                return null;
            }

            var ranges = BuildRanges(candidateSegments, totalDurationSeconds);
            if (ranges.Count == 0)
            {
                logger.LogWarning("候选人分段无有效音频区间,跳过发音评估");
                return null;
            }

            var outPath = Path.Combine(workDir, "candidate.wav");
            var args = BuildArgs(inputWav, ranges, outPath);
            var (code, output) = await RunProcessAsync(ffmpeg, args, ct);

            // ffmpeg 退出码不完全可靠 —— 以产物是否有效为准(与 AudioPrepareStage 同一策略)
            if (!File.Exists(outPath) || new FileInfo(outPath).Length <= 44)
            {
                logger.LogWarning(
                    "候选人音频抽取失败(ffmpeg code {Code}),跳过发音评估。尾部日志:\n{Log}",
                    code, Tail(output, 1500));
                return null;
            }

            return outPath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "候选人音频抽取异常,跳过发音评估");
            return null;
        }
    }

    /// <summary>
    /// 纯逻辑:分段时间戳 → 抽取区间。clamp 到 [0, 总时长],丢弃 &lt;0.05s 的碎片,按开始时间排序。
    /// </summary>
    public static List<(double Start, double End)> BuildRanges(
        IReadOnlyList<TranscriptSegment> candidateSegments, double totalDurationSeconds) =>
        candidateSegments
            .Select(s => (
                Start: Math.Max(0, s.StartSeconds),
                End: totalDurationSeconds > 0
                    ? Math.Min(totalDurationSeconds, s.EndSeconds)
                    : s.EndSeconds))
            .Where(r => r.End - r.Start >= 0.05)
            .OrderBy(r => r.Start)
            .ToList();

    /// <summary>纯逻辑:拼 ffmpeg 参数。路径加引号,时间用 F2 + 不变文化,避免小数点本地化。</summary>
    public static string BuildArgs(string inputWav,
        IReadOnlyList<(double Start, double End)> ranges, string outPath)
    {
        var sb = new StringBuilder();
        var labels = new StringBuilder();
        for (var i = 0; i < ranges.Count; i++)
        {
            var (s, e) = ranges[i];
            sb.Append(CultureInfo.InvariantCulture,
                $"atrim=start={s:F2}:end={e:F2},setpts=PTS-STARTPTS[ca{i}];");
            labels.Append($"[ca{i}]");
        }

        sb.Append($"{labels}concat=n={ranges.Count}:v=0:a=1[cand]");

        return $"-y -i \"{inputWav}\" -filter_complex \"{sb}\" " +
               $"-map \"[cand]\" -ar {SampleRate} -ac 1 -c:a pcm_s16le \"{outPath}\"";
    }

    private static string Tail(string s, int max) =>
        s.Length <= max ? s : s[^max..];

    /// <summary>进程执行 —— 与 AudioPrepareStage.RunProcessAsync 同一模式(先等退出再排空输出流)。</summary>
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

        await p.WaitForExitAsync(ct);
        p.WaitForExit();

        return (p.ExitCode, sb.ToString());
    }
}
