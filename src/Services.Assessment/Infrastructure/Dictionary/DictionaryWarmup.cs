using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// 票130 §4 启动预热：把压在第一次查词里的一次性构建（开放层 / 牛津状态 / 核心包 manifest /
/// 首条解压 / 发音链 JIT）搬到主机启动后、请求路径之外一次性完成。
///
/// 口径钉死（SPEC §4.1–§4.4）：
/// - 只触碰 DI 注册的同一批单例（同一个 Lazy、同一个 store/resolver/service），不另建第二份；
/// - 依赖在 RunAsync 内经 IServiceProvider 懒解析并逐段 try/catch——单例构造本身失败
///   （如开放层构建损坏，AudioResolver 构造期强制求值会抛）只记该段失败，绝不冒泡为启动失败，
///   其后词典端点的失败形态与未实施本票的基线逐字一致（§4.4「不放大」）；
/// - 全程零网络：W4/W5 只走本地数据路径（Lookup / PronounceAsync 本地解析，不取流）；
/// - 完成判据是功能性的：逐段状态回读（见 RunAsync 各段），不是「调用过某方法」。
/// </summary>
public sealed class DictionaryWarmup(IServiceProvider services, ILogger<DictionaryWarmup> logger)
{
    /// <summary>一段预热的结果：Ok = 该段达成成功能性完成（缺席定格也算完成，见 Note）。</summary>
    public sealed record SegmentResult(string Name, bool Ok, long ElapsedMs, string? Note);

    /// <summary>一次预热的完整结果（测试回读用；生产只进日志）。</summary>
    public sealed record Result(IReadOnlyList<SegmentResult> Segments, long TotalMs)
    {
        public SegmentResult Segment(string name) => Segments.First(s => s.Name == name);
    }

    /// <summary>最近一次预热结果（观测接缝：测试回读，生产不用）。</summary>
    public Result? LastResult { get; private set; }

    /// <summary>
    /// 执行五段预热。本方法按契约不抛：任何段的任何异常都被收进该段结果并记日志。
    /// 幂等：底层构建由 Lazy 单次性 / 核心包一次性发布保证，重复执行只是缓存命中级廉价调用。
    /// </summary>
    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var segments = new List<SegmentResult>();

        // W1 开放层构建（ECDICT / WordNet / 音频索引 / 归一表）
        segments.Add(RunSegment("W1-open-layer", () =>
        {
            var lazy = services.GetRequiredService<Lazy<OpenLayerStore>>();
            _ = lazy.Value; // 强制求值：与首查触碰的是同一个 Lazy、只建一次（§4.3）
            return "built";
        }));

        // W2 牛津状态载入（idx / redirects 反序列化）；缺席/损坏 = 定格为缺席，与基线同结果
        segments.Add(RunSegment("W2-oxford-state", () =>
        {
            var oxford = services.GetRequiredService<OxfordPackStore>();
            var present = oxford.IsPresent; // 读一次即求值定格
            return present ? "present" : "absent";
        }));

        // W3 核心包 manifest 解析；缺席/损坏 = 定格为空表，与基线同结果
        segments.Add(RunSegment("W3-core-manifest", () =>
        {
            var audio = services.GetRequiredService<AudioResolver>();
            audio.WarmCore();
            return audio.CoreTablePublished ? "published" : "unpublished";
        }));

        // W4 哑查词：必然命中的常用词走完整 Lookup 管线（预付 GetEntry 首条解压 + 管线 JIT）
        segments.Add(RunSegment("W4-dummy-lookup", () =>
        {
            var dictionary = services.GetRequiredService<DictionaryService>();
            var hit = dictionary.Lookup("the");
            return hit is not null ? $"hit:{hit.Tier}" : "miss";
        }, okWhen: note => note.StartsWith("hit", StringComparison.Ordinal)));

        // W5 哑 pronounce：只本地解析、不取流（预付发音链 JIT）；链耗尽（无录音）也是完成
        var w5 = Stopwatch.StartNew();
        try
        {
            var dictionary = services.GetRequiredService<DictionaryService>();
            var res = await dictionary.PronounceAsync("the", "us", ct);
            w5.Stop();
            segments.Add(new SegmentResult("W5-dummy-pronounce", true, w5.ElapsedMilliseconds,
                res.Available ? $"available:{res.Kind}" : "exhausted"));
        }
        catch (Exception ex)
        {
            w5.Stop();
            logger.LogWarning(ex, "Dictionary warmup segment {Segment} failed; degrading to baseline lazy behavior", "W5-dummy-pronounce");
            segments.Add(new SegmentResult("W5-dummy-pronounce", false, w5.ElapsedMilliseconds, ex.GetType().Name));
        }

        total.Stop();
        var result = new Result(segments, total.ElapsedMilliseconds);
        LastResult = result;
        logger.LogInformation(
            "Dictionary warmup complete in {TotalMs} ms (预热完成 warm): {Segments}",
            result.TotalMs,
            string.Join(", ", segments.Select(s => $"{s.Name}={(s.Ok ? "ok" : "FAILED")} {s.ElapsedMs} ms {s.Note}".Trim())));
        return result;
    }

    private SegmentResult RunSegment(string name, Func<string> body, Func<string, bool>? okWhen = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var note = body();
            sw.Stop();
            return new SegmentResult(name, okWhen?.Invoke(note) ?? true, sw.ElapsedMilliseconds, note);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // §4.4：失败只记日志、诚实降级——绝不冒泡为启动失败，失败段行为回落基线懒语义。
            logger.LogWarning(ex, "Dictionary warmup segment {Segment} failed; degrading to baseline lazy behavior", name);
            return new SegmentResult(name, false, sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }
}

/// <summary>
/// 预热托管服务：主机启动层一次性触发（BackgroundService 的 StartAsync 不阻塞监听与端点可用，
/// §4.1 非阻塞）；启动迁移在 Program.cs 中先于 app.Run() 完成，故触发点天然在迁移成功之后。
/// </summary>
public sealed class DictionaryWarmupHostedService(
    DictionaryWarmup warmup, ILogger<DictionaryWarmupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // §4.1 非阻塞的关键一行：预热本体是 2–4.5s 的同步 CPU 构建（W1–W4 全同步、
        // W5 的 PronounceAsync 也是 Task.FromResult），若直接 await 会在 StartAsync
        // 调用路径上同步跑完——实测端口监听会被拖到预热完成后（race4 探针：首响应
        // 晚于 warmup-complete 日志）。Task.Run 把它卸到线程池，StartAsync 立即返回、
        // Kestrel 照常先监听，赛跑窗内的查词走 §4.3 并发语义。
        try
        {
            await Task.Run(() => warmup.RunAsync(stoppingToken), stoppingToken);
        }
        catch (Exception ex)
        {
            // RunAsync 按契约不抛；此处兜底保证后台任务异常永不未观测、永不拖垮主机。
            logger.LogWarning(ex, "Dictionary warmup host caught an unexpected error; dictionary stays on baseline lazy behavior");
        }
    }
}
