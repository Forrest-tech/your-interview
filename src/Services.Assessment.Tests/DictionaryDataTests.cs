using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票119 真实嵌入数据断言(SPEC §7 TST-1/TST-2 与 RSK-R2-1):
/// 子集计数与 manifest 一致、启动预算、频带抽样、中文行快照回归。
/// 计数口径的事实源 = Resources/Dictionary/*.manifest.json(构建期留档)。
/// </summary>
public class DictionaryDataTests
{
    private static readonly Lazy<OpenLayerStore> RealStore = new(OpenLayerStore.LoadFromEmbeddedResources);

    private static Stream FixtureStream(string suffix)
    {
        var asm = typeof(DictionaryDataTests).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(suffix, StringComparison.Ordinal));
        return asm.GetManifestResourceStream(name)!;
    }

    [Fact]
    public void EmbeddedResources_NoticesAndCounts_MatchManifests()
    {
        var asm = typeof(OpenLayerStore).Assembly;
        var names = asm.GetManifestResourceNames();

        string ReadNotice(string suffix)
        {
            var name = names.First(n => n.EndsWith(suffix, StringComparison.Ordinal));
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            return reader.ReadToEnd();
        }

        // §5.2/§5.3:许可 NOTICE 随嵌入资源同置且含许可正文
        Assert.Contains("MIT License", ReadNotice("NOTICE-ecdict.txt"));
        var wnNotice = ReadNotice("NOTICE-wordnet.txt");
        Assert.Contains("Princeton", wnNotice);
        Assert.Contains("Permission to use, copy, modify", wnNotice);

        // 计数与 manifest(ecdict-subset.manifest.json / wordnet-subset.manifest.json)一致
        Assert.Equal(91_599, RealStore.Value.Ecdict.Count);
        Assert.Equal(43_630, RealStore.Value.WordNet.Count);
        Assert.True(RealStore.Value.Exceptions.Count > 5_000);
    }

    [Fact]
    public void StartupBudget_LoadUnder2s_MemoryUnder250MB()
    {
        GC.Collect();
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();
        var store = OpenLayerStore.LoadFromEmbeddedResources();
        sw.Stop();
        var after = GC.GetTotalMemory(forceFullCollection: false);
        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"开放层载入耗时 {sw.ElapsedMilliseconds} ms,超 §9 预算 2000 ms");
        var deltaMb = (after - before) / 1024.0 / 1024.0;
        Assert.True(deltaMb < 250, $"词典模块常驻增量 {deltaMb:F1} MB,超 §9 预算 250 MB");
        Assert.True(store.Ecdict.Count > 0);
    }

    [Fact]
    public void StateC_Disappeared_OpenLayerShape()
    {
        // §8 状态 C 核心断言:WordNet 动词结构 + ECDICT 中文行 + 词形标注 + Tier 1 发音
        var svc = BuildService(RealStore.Value, packDir: null).Service;
        var r = svc.Lookup("disappeared");
        Assert.NotNull(r);
        Assert.Equal("disappear", r!.Lemma);
        Assert.Equal("open", r.Tier);
        var verb = Assert.Single(r.PosBlocks, b => b.Pos == "verb");
        Assert.Contains(verb.Senses, s => s.DefEn.Contains("get lost, as without warning"));
        Assert.Contains(verb.ZhLines, l => l.Contains("消失"));
        var form = Assert.Single(r.Forms, f => f.Text == "disappeared");
        Assert.Equal("过去式/过去分词", form.Label);
        Assert.Equal("commons", r.Audio["us"].Kind); // RESEARCH 实测:disappear 有 US 真人录音
        Assert.True(r.Audio["us"].Available);
    }

    [Fact]
    public void BandSampling_HitRates_WithinToleranceOfResearch()
    {
        using var doc = JsonDocument.Parse(FixtureStream("band-fixture.json"));
        var root = doc.RootElement;
        var refs = root.GetProperty("referenceRates");
        var tolerance = refs.GetProperty("tolerancePt").GetDouble();
        var store = RealStore.Value;
        foreach (var band in new[] { "B1", "B2", "B3", "B4", "B5" })
        {
            var words = root.GetProperty("bands").GetProperty(band)
                .EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.Equal(100, words.Count);
            var wnHit = words.Count(w => store.WordNet.ContainsKey(w)) * 100.0 / words.Count;
            // 音频口径 = RESEARCH §8.2 仪器 A:含经典标题 En-us-/En-uk- 录音(大小写不敏感)
            var audioHit = words.Count(w =>
                store.AudioIndex.TryGetValue(w, out var recs)
                && recs.Any(r => r.File.StartsWith("En-us-", StringComparison.OrdinalIgnoreCase)
                              || r.File.StartsWith("En-uk-", StringComparison.OrdinalIgnoreCase))
            ) * 100.0 / words.Count;
            var wnRef = refs.GetProperty("wordNetHit").GetProperty(band).GetDouble();
            var audioRef = refs.GetProperty("audioClassicHit").GetProperty(band).GetDouble();
            Assert.True(Math.Abs(wnHit - wnRef) <= tolerance,
                $"{band} WordNet 命中 {wnHit:F1}% 与参照 {wnRef}% 偏差超 ±{tolerance}pt");
            Assert.True(Math.Abs(audioHit - audioRef) <= tolerance,
                $"{band} 音频经典档命中 {audioHit:F1}% 与参照 {audioRef}% 偏差超 ±{tolerance}pt");
        }
    }

    [Fact]
    public void ZhSnapshot_PassageWords_OpenLayerRegression()
    {
        using var doc = JsonDocument.Parse(FixtureStream("zh-snapshot.json"));
        var svc = BuildService(RealStore.Value, packDir: null).Service;
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var query = row.GetProperty("query").GetString()!;
            var expectedLemma = row.GetProperty("lemma").ValueKind == JsonValueKind.Null
                ? null : row.GetProperty("lemma").GetString();
            var r = svc.Lookup(query);
            if (expectedLemma is null)
            {
                Assert.Null(r); // 快照基线:该词开放层无卡(如 14-year-old)
                continue;
            }
            Assert.NotNull(r);
            Assert.Equal(expectedLemma, r!.Lemma);
            Assert.Equal(row.GetProperty("matchedVia").GetString(), r.MatchedVia);
            var expectedBlocks = row.GetProperty("blocks").EnumerateArray()
                .Select(b => (Pos: b.GetProperty("pos").GetString()!,
                              Zh: b.GetProperty("zhLines").EnumerateArray().Select(z => z.GetString()!).ToList()))
                .ToList();
            Assert.Equal(expectedBlocks.Count, r.PosBlocks.Count);
            for (var i = 0; i < expectedBlocks.Count; i++)
            {
                Assert.Equal(expectedBlocks[i].Pos, r.PosBlocks[i].Pos);
                Assert.Equal(expectedBlocks[i].Zh, r.PosBlocks[i].ZhLines);
            }
        }
    }
}

/// <summary>票119 §5.1 .gitignore 断言:本地词典数据/牛津包文件在任意层级被忽略。</summary>
public class GitIgnoreDictionaryTests
{
    private static string RepoRoot()
    {
        // 注意:AppContext.BaseDirectory 在 xunit 控制台 runner 下指向 runner 自身目录,
        // 必须以测试程序集位置为锚向上找仓库根。
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(GitIgnoreDictionaryTests).Assembly.Location)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ".gitignore"))
                && Directory.Exists(Path.Combine(dir.FullName, "src", "Services.Assessment")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found from test bin dir");
    }

    private static int CheckIgnore(string root, string path)
    {
        var psi = new ProcessStartInfo("git", $"check-ignore -q \"{path}\"")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode; // 0 = ignored, 1 = not ignored
    }

    [Theory]
    [InlineData("dict-data/oxford/oxford-entries.jsonl.gz")]
    [InlineData("dict-data/audio-core/c000001.mp3")]
    [InlineData("src/Services.Assessment/dict-data/cache/x.mp3")]
    [InlineData("oald10.mdx")]
    [InlineData("some/deep/V14_3.4.mdd")]
    [InlineData("foo-oxford-pack-bar/entries.json")]
    public void LocalDictionaryData_IsGitIgnored(string path)
    {
        Assert.Equal(0, CheckIgnore(RepoRoot(), path));
    }

    [Fact]
    public void NormalSourceFile_IsNotIgnored()
    {
        Assert.Equal(1, CheckIgnore(RepoRoot(), "src/Services.Assessment/Program.cs"));
    }
}
