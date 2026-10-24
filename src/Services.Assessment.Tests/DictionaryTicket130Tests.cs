using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票130 后端验收（SPEC §8.1 TB-1…TB-5）：
/// TB-1 预热触发与五段完成判据 + 幂等；TB-2 并发单次构建 + 核心包一次性发布（无半成品假阴性）；
/// TB-3 失败降级三型（坏包 / 坏 manifest / 开放层工厂抛错）；TB-4 预热零网络；TB-5 预算与探针零改动（源码契约）。
/// 预热经最小 ServiceCollection 解析依赖（与生产同形态：依赖在 RunAsync 内懒解析、逐段收口），
/// 被测单例均为手工构造的真实类，不走假管线。
/// </summary>
public class DictionaryTicket130Tests
{
    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "dict130-" + Guid.NewGuid().ToString("N"));

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("TB-4: warmup/lookup/pronounce must not touch HTTP");
    }

    /// <summary>合成核心包 manifest（键/词/口音三元组），文件本体不落盘（ResolveCore 只读 manifest）。</summary>
    private static string WriteCoreManifest(string root, params (string Key, string Word, string Accent)[] files)
    {
        var coreDir = Path.Combine(root, "audio-core");
        Directory.CreateDirectory(coreDir);
        var manifestFiles = files.Select(f => (object)new
        {
            key = f.Key, word = f.Word, accent = f.Accent, storedAs = f.Key + ".mp3",
        }).ToList();
        File.WriteAllText(Path.Combine(coreDir, "audio-core-manifest.json"),
            JsonSerializer.Serialize(new { files = manifestFiles }));
        return coreDir;
    }

    private static DictionaryOptions OptionsFor(string root, string coreDir) => new()
    {
        AudioCoreDir = coreDir,
        AudioCacheDir = Path.Combine(root, "audio-cache"),
    };

    private static DictionaryWarmup WarmupOver(
        Lazy<OpenLayerStore> lazy, OxfordPackStore oxford, DictionaryOptions options,
        AudioResolver? audio, DictionaryService? svc, ListLogger<DictionaryWarmup> logger)
    {
        var services = new ServiceCollection();
        services.AddSingleton(lazy);
        services.AddSingleton(oxford);
        services.AddSingleton(options);
        if (audio is not null) services.AddSingleton(audio);
        if (svc is not null) services.AddSingleton(svc);
        return new DictionaryWarmup(services.BuildServiceProvider(), logger);
    }

    private static (AudioResolver Audio, DictionaryService Svc) BuildPair(
        Lazy<OpenLayerStore> lazy, OxfordPackStore oxford, DictionaryOptions options,
        IHttpClientFactory? httpFactory = null)
    {
        var audio = new AudioResolver(lazy, oxford, options,
            httpFactory ?? new StubHttpClientFactory(StubHttpHandler.Failure()), TimeProvider.System);
        var svc = new DictionaryService(lazy, oxford, audio, options, TimeProvider.System);
        return (audio, svc);
    }

    private static OpenLayerStore OpenWithTheAndZorblax() => BuildOpenStore(
        EcdictTsv(
            EcRow("the", "", "definite article", "这;那", ""),
            EcRow("zorblax", "", "synthetic thing", "合成物", "")),
        WordNetJsonl(
            WnEntry("the", ("a", new (string, string?)[] { ("definite article gloss", null) })),
            WnEntry("zorblax", ("n", new (string, string?)[] { ("a synthetic thing", null) }))));

    // ---------- TB-1（触发与完成判据，§8.1） ----------

    [Fact]
    public async Task TB1_Warmup_FiveSegmentsComplete_ThenLookupHasZeroBuilds_AndIdempotent()
    {
        var root = TempRoot();
        try
        {
            var pack = BuildOxfordPack(root,
                [("glorp", OxfordEntryJson("glorp", new { us = "/ɡ/", uk = "/ɡ/" },
                    new[] { new { pos = "noun", senses = new[] { new { defEn = "synthetic glorp" } } } }))],
                new Dictionary<string, string>(), audioArchivePresent: false);
            var coreDir = WriteCoreManifest(root, ("c130a", "apple", "us"));
            var options = OptionsFor(root, coreDir);

            var builds = 0;
            var lazy = new Lazy<OpenLayerStore>(() =>
            {
                Interlocked.Increment(ref builds);
                return OpenLayerStore.LoadFromEmbeddedResources();
            });
            var oxford = new OxfordPackStore(pack);
            var (audio, svc) = BuildPair(lazy, oxford, options);
            var logger = new ListLogger<DictionaryWarmup>();
            var warmup = WarmupOver(lazy, oxford, options, audio, svc, logger);

            var result = await warmup.RunAsync();

            Assert.All(result.Segments, s => Assert.True(s.Ok, $"{s.Name} failed: {s.Note}"));
            Assert.Equal("present", result.Segment("W2-oxford-state").Note);
            Assert.Equal("published", result.Segment("W3-core-manifest").Note);
            Assert.StartsWith("hit", result.Segment("W4-dummy-lookup").Note);
            Assert.Equal(1, builds);
            Assert.Equal(1, audio.CoreBuildCountForTests);
            Assert.True(oxford.StateEvaluated);
            Assert.True(audio.CoreTablePublished);
            // 完成日志锚点：含「warm」与总毫秒数（活体 E-130-C 的等待锚点）
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
                && e.Message.Contains("warm", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains(result.TotalMs.ToString()));

            // 预热后首个真实 Lookup：核心管线照常跑一次（缓存未命中），但构建计数零增长
            var coreBefore = svc.CoreLookupCountForTests;
            var r = svc.Lookup("disappeared");
            Assert.NotNull(r);
            Assert.Equal(coreBefore + 1, svc.CoreLookupCountForTests);
            Assert.Equal(1, builds);
            Assert.Equal(1, audio.CoreBuildCountForTests);

            // 幂等：二次预热不重复构建
            var again = await warmup.RunAsync();
            Assert.All(again.Segments, s => Assert.True(s.Ok, $"{s.Name} failed: {s.Note}"));
            Assert.Equal(1, builds);
            Assert.Equal(1, audio.CoreBuildCountForTests);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录清理失败不影响断言 */ }
        }
    }

    // ---------- TB-1b（托管服务非阻塞，§4.1） ----------

    [Fact]
    public async Task TB1b_HostedService_StartAsyncReturnsBeforeWarmupFinishes()
    {
        // 预热本体全同步（W1–W4 同步、W5 为 Task.FromResult）：若 ExecuteAsync 直接 await，
        // StartAsync 调用路径上会同步跑完整个预热、端口监听被拖后（race4 探针实证）。
        // 断言：StartAsync 在预热完成前返回；预热随后在后台跑完。
        var lazy = new Lazy<OpenLayerStore>(() =>
        {
            Thread.Sleep(800);
            return OpenWithTheAndZorblax();
        });
        var oxford = new OxfordPackStore(null);
        var options = OptionsFor(TempRoot(), WriteCoreManifest(TempRoot()));
        var (audio, svc) = BuildPair(lazy, oxford, options);
        var warmup = WarmupOver(lazy, oxford, options, audio, svc, new ListLogger<DictionaryWarmup>());
        var hosted = new DictionaryWarmupHostedService(warmup, new ListLogger<DictionaryWarmupHostedService>());

        var sw = Stopwatch.StartNew();
        await hosted.StartAsync(CancellationToken.None);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 400,
            $"StartAsync 耗时 {sw.ElapsedMilliseconds} ms：预热阻塞了启动路径");
        Assert.Null(warmup.LastResult); // 预热尚未跑完（仍在后台）
        for (var i = 0; i < 100 && warmup.LastResult is null; i++)
            await Task.Delay(50);
        Assert.NotNull(warmup.LastResult);
        Assert.All(warmup.LastResult.Segments, s => Assert.True(s.Ok, $"{s.Name} failed: {s.Note}"));
        await hosted.StopAsync(CancellationToken.None);
    }

    // ---------- TB-2（并发单次构建 + 一次性发布，§8.1 / §4.3） ----------

    [Fact]
    public void TB2_LazyOpenLayer_BuildsExactlyOnceUnderParallelFirstTouch()
    {
        var builds = 0;
        var lazy = new Lazy<OpenLayerStore>(() =>
        {
            Interlocked.Increment(ref builds);
            Thread.Sleep(50);
            return OpenWithTheAndZorblax();
        });
        Parallel.For(0, 32, (int i) => { _ = lazy.Value; });
        Assert.Equal(1, builds);
        Assert.True(lazy.IsValueCreated);
    }

    [Fact]
    public void TB2_CoreManifest_PublishedAtomically_NoHalfBuiltFalseNegatives()
    {
        var root = TempRoot();
        try
        {
            // 大 manifest（2 万条）放大构建窗口：目标词放最后一条——
            // 基线「先发布空表再逐条填充」下并发首读必假阴性，新口径必须全命中。
            var sb = new StringBuilder("{\"files\":[");
            for (var i = 0; i < 20_000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"key\":\"k{i}\",\"word\":\"w{i}\",\"accent\":\"us\",\"storedAs\":\"k{i}.mp3\"}}");
            }
            sb.Append(",{\"key\":\"kzz\",\"word\":\"zztarget\",\"accent\":\"us\",\"storedAs\":\"kzz.mp3\"}]}");
            var coreDir = Path.Combine(root, "audio-core");
            Directory.CreateDirectory(coreDir);
            File.WriteAllText(Path.Combine(coreDir, "audio-core-manifest.json"), sb.ToString());
            var options = OptionsFor(root, coreDir);
            var lazy = new Lazy<OpenLayerStore>(OpenWithTheAndZorblax);
            var (audio, _) = BuildPair(lazy, new OxfordPackStore(null), options);

            using var gate = new ManualResetEventSlim(false);
            var results = new (string Key, string FileName)?[32];
            var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            {
                gate.Wait();
                results[i] = audio.ResolveCore("zztarget", "us");
            })).ToArray();
            gate.Set();
            Task.WaitAll(tasks);

            Assert.All(results, r => Assert.NotNull(r));
            Assert.All(results, r => Assert.Equal("kzz", r!.Value.Key));
            Assert.Equal(1, audio.CoreBuildCountForTests);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 同上 */ }
        }
    }

    [Fact]
    public async Task TB2_WarmupRacesFirstLookups_ResultsEqualSerialBaseline()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCoreManifest(root, ("c130z", "zorblax", "us"));
            var options = OptionsFor(root, coreDir);
            var open = OpenWithTheAndZorblax();

            // 串行基线
            var serialLazy = new Lazy<OpenLayerStore>(() => open);
            var (serialAudio, serialSvc) = BuildPair(serialLazy, new OxfordPackStore(null), OptionsFor(root, coreDir));
            var serialLookup = serialSvc.Lookup("zorblax");
            var serialPron = await serialSvc.PronounceAsync("zorblax", "us", CancellationToken.None);
            var serialCore = serialAudio.ResolveCore("zorblax", "us");
            Assert.NotNull(serialLookup);
            Assert.True(serialPron.Available);
            Assert.NotNull(serialCore);

            // 赛跑组：预热与 32 路并发 Lookup/Pronounce/ResolveCore 混合起跑
            var builds = 0;
            var lazy = new Lazy<OpenLayerStore>(() =>
            {
                Interlocked.Increment(ref builds);
                Thread.Sleep(80);
                return open;
            });
            var oxford = new OxfordPackStore(null);
            var (audio, svc) = BuildPair(lazy, oxford, options);
            var logger = new ListLogger<DictionaryWarmup>();
            var warmup = WarmupOver(lazy, oxford, options, audio, svc, logger);

            var warmTask = warmup.RunAsync();
            var ops = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
            {
                switch (i % 3)
                {
                    case 0: return (object?)svc.Lookup("zorblax");
                    case 1: return (object?)await svc.PronounceAsync("zorblax", "us", CancellationToken.None);
                    default: return (object?)audio.ResolveCore("zorblax", "us");
                }
            })).ToArray();
            var results = await Task.WhenAll(ops);
            var warm = await warmTask;

            Assert.All(warm.Segments, s => Assert.True(s.Ok, $"{s.Name} failed: {s.Note}"));
            Assert.Equal(1, builds);
            Assert.Equal(1, audio.CoreBuildCountForTests);
            foreach (var r in results)
            {
                switch (r)
                {
                    case DictionaryLookupResponse lookup:
                        Assert.Equal(serialLookup!.Lemma, lookup.Lemma);
                        Assert.Equal(serialLookup.Tier, lookup.Tier);
                        Assert.Equal(serialLookup.Audio["us"].Available, lookup.Audio["us"].Available);
                        Assert.Equal(serialLookup.Audio["us"].Kind, lookup.Audio["us"].Kind);
                        Assert.Equal(serialLookup.Audio["us"].Key, lookup.Audio["us"].Key);
                        break;
                    case PronounceResponse pron:
                        Assert.Equal(serialPron.Available, pron.Available);
                        Assert.Equal(serialPron.Kind, pron.Kind);
                        Assert.Equal(serialPron.Key, pron.Key);
                        break;
                    case ValueTuple<string, string> core:
                        Assert.Equal(serialCore!.Value.Key, core.Item1);
                        break;
                    default:
                        Assert.Fail($"unexpected op result: {r}");
                        break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 同上 */ }
        }
    }

    // ---------- TB-3（失败降级三型，§8.1 / §4.4） ----------

    [Fact]
    public async Task TB3_CorruptOxfordPack_WarmupDegrades_LookupMatchesStateC()
    {
        var root = TempRoot();
        try
        {
            var pack = BuildOxfordPack(root,
                [("glorp", OxfordEntryJson("glorp", new { us = "/ɡ/", uk = "/ɡ/" },
                    new[] { new { pos = "noun", senses = new[] { new { defEn = "synthetic glorp" } } } }))],
                new Dictionary<string, string>(), audioArchivePresent: false);
            File.WriteAllText(Path.Combine(pack, "oxford-entries.idx.json"), "{ this is not json");
            var options = OptionsFor(root, WriteCoreManifest(root));
            var lazy = new Lazy<OpenLayerStore>(OpenWithTheAndZorblax);
            var oxford = new OxfordPackStore(pack);
            var (audio, svc) = BuildPair(lazy, oxford, options);
            var warmup = WarmupOver(lazy, oxford, options, audio, svc, new ListLogger<DictionaryWarmup>());

            var result = await warmup.RunAsync(); // 不许抛

            Assert.True(result.Segment("W2-oxford-state").Ok);
            Assert.Equal("absent", result.Segment("W2-oxford-state").Note);
            Assert.False(oxford.IsPresent);
            var r = svc.Lookup("the"); // 状态 C 基线：开放层卡
            Assert.NotNull(r);
            Assert.Equal("open", r!.Tier);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 同上 */ }
        }
    }

    [Fact]
    public async Task TB3_CorruptCoreManifest_WarmupCompletesWithEmptyTable()
    {
        var root = TempRoot();
        try
        {
            var coreDir = Path.Combine(root, "audio-core");
            Directory.CreateDirectory(coreDir);
            File.WriteAllText(Path.Combine(coreDir, "audio-core-manifest.json"), "{ broken manifest");
            var options = OptionsFor(root, coreDir);
            var lazy = new Lazy<OpenLayerStore>(OpenWithTheAndZorblax);
            var oxford = new OxfordPackStore(null);
            var (audio, svc) = BuildPair(lazy, oxford, options);
            var warmup = WarmupOver(lazy, oxford, options, audio, svc, new ListLogger<DictionaryWarmup>());

            var result = await warmup.RunAsync(); // 不许抛

            Assert.True(result.Segment("W3-core-manifest").Ok);
            Assert.True(audio.CoreTablePublished); // 落空表 = 已定格（与基线核心包缺席一致）
            Assert.Null(audio.ResolveCore("zorblax", "us"));
            Assert.NotNull(svc.Lookup("zorblax")); // 查词主链不受影响
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 同上 */ }
        }
    }

    [Fact]
    public async Task TB3_OpenLayerFactoryThrows_WarmupNeverThrows_SegmentsFailHonestly()
    {
        var throwing = new Lazy<OpenLayerStore>(() =>
            throw new InvalidOperationException("embedded resources missing (build corruption)"));
        var services = new ServiceCollection();
        services.AddSingleton(throwing);
        services.AddSingleton(new OxfordPackStore(null));
        services.AddSingleton(new DictionaryOptions());
        services.AddSingleton(sp => new AudioResolver(
            throwing, sp.GetRequiredService<OxfordPackStore>(), sp.GetRequiredService<DictionaryOptions>(),
            new StubHttpClientFactory(StubHttpHandler.Failure()), TimeProvider.System));
        services.AddSingleton(sp => new DictionaryService(
            throwing, sp.GetRequiredService<OxfordPackStore>(), sp.GetRequiredService<AudioResolver>(),
            sp.GetRequiredService<DictionaryOptions>(), TimeProvider.System));
        var logger = new ListLogger<DictionaryWarmup>();
        var warmup = new DictionaryWarmup(services.BuildServiceProvider(), logger);

        var result = await warmup.RunAsync(); // 顶层 catch-all：绝不冒泡

        Assert.False(result.Segment("W1-open-layer").Ok);
        Assert.True(result.Segment("W2-oxford-state").Ok); // 牛津求值不依赖开放层：缺席定格
        Assert.False(result.Segment("W3-core-manifest").Ok); // AudioResolver 构造期强制求值 → 解析失败记段失败
        Assert.False(result.Segment("W4-dummy-lookup").Ok);
        Assert.False(result.Segment("W5-dummy-pronounce").Ok);
        Assert.True(logger.Entries.Count(e => e.Level >= LogLevel.Warning) >= 4,
            "失败段须逐段 Warning 留痕");
    }

    // ---------- TB-4（零网络，§8.1） ----------

    [Fact]
    public async Task TB4_WarmupLookupPronounce_WithExplodingHttpFactory_ResultsUnchanged()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCoreManifest(root, ("c130z", "zorblax", "us"));
            var options = OptionsFor(root, coreDir);
            var open = OpenWithTheAndZorblax();

            var controlLazy = new Lazy<OpenLayerStore>(() => open);
            var (_, controlSvc) = BuildPair(controlLazy, new OxfordPackStore(null), OptionsFor(root, coreDir));
            var controlLookup = controlSvc.Lookup("zorblax");
            var controlPron = await controlSvc.PronounceAsync("zorblax", "us", CancellationToken.None);

            var lazy = new Lazy<OpenLayerStore>(() => open);
            var oxford = new OxfordPackStore(null);
            var (audio, svc) = BuildPair(lazy, oxford, options, new ThrowingHttpClientFactory());
            var warmup = WarmupOver(lazy, oxford, options, audio, svc, new ListLogger<DictionaryWarmup>());

            var result = await warmup.RunAsync(); // 任一段碰 HTTP 即炸 → 段失败，断言会红
            Assert.All(result.Segments, s => Assert.True(s.Ok, $"{s.Name} failed: {s.Note}"));

            var lookup = svc.Lookup("zorblax");
            var pron = await svc.PronounceAsync("zorblax", "us", CancellationToken.None);
            Assert.Equal(controlLookup!.Lemma, lookup!.Lemma);
            Assert.Equal(controlLookup.Audio["us"].Key, lookup.Audio["us"].Key);
            Assert.Equal(controlPron.Available, pron.Available);
            Assert.Equal(controlPron.Key, pron.Key);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 同上 */ }
        }
    }

    // ---------- TB-5（预算与探针零改动：源码契约，§8.1 / §4.6） ----------

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(DictionaryTicket130Tests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".gitignore"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Services.Assessment")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found from test bin dir");
    }

    [Fact]
    public void TB5_StartupBudgetTest_BodyAndNumbersUntouched()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(),
            "src", "Services.Assessment.Tests", "DictionaryDataTests.cs"));
        Assert.Contains("StartupBudget_LoadUnder2s_MemoryUnder320MB", text);
        Assert.Contains("sw.ElapsedMilliseconds < 2_000", text);
        Assert.Contains("deltaMb < 320", text);
    }

    [Fact]
    public void TB5_HealthChecksRegistration_UntouchedByWarmup()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(),
            "src", "Services.Assessment", "Program.cs"));
        Assert.Contains(
            "builder.Services.AddHealthChecks().AddDbContextCheck<AssessmentDbContext>(\"postgres\");",
            program);
        // 预热只许以单例 + 托管服务形态出现，不许挂进健康检查链
        Assert.DoesNotContain("AddCheck", program);
        Assert.Contains("DictionaryWarmupHostedService", program);
    }
}
