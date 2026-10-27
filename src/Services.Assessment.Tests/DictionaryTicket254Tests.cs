using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票254 后端验收（PLAN-PATCHED §3/§4 + panel 绑定项）：
/// Main 面本形优先 + 带标注合成、Shadow 面真人回落、排除键回落、中文回落链、
/// 牛津补丁与归因、词形音标、标签并集、合成物化与键校验。夹具词均为虚构词。
/// </summary>
public class DictionaryTicket254Tests
{
    private static readonly byte[] FakeMp3 = [0xFF, 0xF3, 0x01, 0x02, 0x03];

    private sealed class FakeKeyProvider : ISpeechKeyProvider
    {
        public Task<SpeechKeySnapshot> ResolveAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult(new SpeechKeySnapshot("fake-key", "eastus", null));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakeMp3),
            });
        }
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "dict-test-254-" + Guid.NewGuid().ToString("N"));

    /// <summary>带 Azure 合成器的服务对（stub HTTP 合成 + 假密钥；懒层 HTTP 恒失败）。
    /// 缓存目录建在调用方 root 下并立即创建，调用方 finally 删 root 即可（无悬空删除）。</summary>
    private static (DictionaryService Service, AudioResolver Audio, CountingHandler SynthHttp, string CacheDir) BuildWithSynth(
        OpenLayerStore open, string? packDir, string root, DictionaryOptions? options = null, string? coreDir = null)
    {
        var cacheDir = Path.Combine(root, "audio-cache");
        Directory.CreateDirectory(cacheDir);
        options ??= new DictionaryOptions();
        options.AudioCacheDir = cacheDir;
        if (coreDir is not null) options.AudioCoreDir = coreDir;
        var oxford = new OxfordPackStore(packDir);
        var synthHttp = new CountingHandler();
        var synth = new SpeechSynthesizer(
            new StubHttpClientFactory(synthHttp), new FakeKeyProvider(), NullLogger<SpeechSynthesizer>.Instance);
        var audio = new AudioResolver(
            new Lazy<OpenLayerStore>(() => open), oxford, options,
            new StubHttpClientFactory(StubHttpHandler.Failure()), TimeProvider.System, synthesizer: synth);
        var service = new DictionaryService(
            new Lazy<OpenLayerStore>(() => open), oxford, audio, options, TimeProvider.System);
        return (service, audio, synthHttp, cacheDir);
    }

    private static string WriteCorePack(string root, params (string Key, string Word, string Accent)[] files)
    {
        var coreDir = Path.Combine(root, "audio-core");
        Directory.CreateDirectory(coreDir);
        var manifestFiles = files.Select(f => (object)new
        {
            key = f.Key, word = f.Word, accent = f.Accent, storedAs = f.Key + ".mp3",
        }).ToList();
        File.WriteAllText(Path.Combine(coreDir, "audio-core-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(new { files = manifestFiles }));
        return coreDir;
    }

    private static OpenLayerStore StoreMurmed() => BuildOpenStore(
        EcdictTsv(
            EcRow("murm", "", "v. to murm", "v. 默姆", "p:murmed/d:murmed"),
            EcRow("murmed", "", "", "", "0:murm")),
        WordNetJsonl(WnEntry("murm", ("v", [("to murm", null)]))));

    private static Task<PronounceResponse> Pron(DictionaryService svc, string word, string accent,
        string? excludeKeys = null, bool humanOnly = false) =>
        svc.PronounceAsync(word, accent, CancellationToken.None, excludeKeys, humanOnly);

    // ---------- §1 Main 面：本形优先 + 合成层 ----------

    [Fact]
    public void MainFormFirst_OxfordFormBeatsLemmaHeadword()
    {
        var root = TempRoot();
        try
        {
            var entry = """
                {"headword":"glorp","phonetics":{"uk":"/ɡ/","us":"/ɡ/"},
                 "posBlocks":[{"pos":"verb","senses":[{"defEn":"to vanish","glossZh":"消失"}]}],
                 "forms":[{"label":"过去式","text":"glorped","audioKey":{"us":"glorped__us_1.mp3"}}],
                 "audio":{"us":"glorp__us_1.mp3"},"oxford3000":false}
                """;
            var pack = BuildOxfordPack(root, [("glorp", entry)], new Dictionary<string, string>(),
                audioArchivePresent: true, audioFiles: ["glorped__us_1.mp3", "glorp__us_1.mp3"]);
            var store = BuildOpenStore(
                EcdictTsv(EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped")),
                WordNetJsonl(WnEntry("glorp", ("v", [("to vanish", null)]))));
            var svc = BuildService(store, pack).Service;
            var formCard = svc.Lookup("glorped")!;
            Assert.Equal("glorp", formCard.Lemma);
            Assert.Equal("oxford", formCard.Audio["us"].Kind);
            Assert.Equal("glorped__us_1.mp3", formCard.Audio["us"].Key);
            var lemmaCard = svc.Lookup("glorp")!;
            Assert.Equal("glorp__us_1.mp3", lemmaCard.Audio["us"].Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Panel 绑定验收反例：本形无人声、词元有人声时，Main 面必须返回合成键，
    /// 绝不返回词元键（两口音同断言）。
    /// </summary>
    [Fact]
    public async Task MainFormAbsentLemmaPresent_ReturnsSyntheticNeverLemmaKey()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root, ("c254m00", "murm", "us"));
            var (svc, _, _, _) = BuildWithSynth(StoreMurmed(), packDir: null, root, coreDir: coreDir);
            var pronUs = await Pron(svc, "murmed", "us");
            Assert.True(pronUs.Available);
            Assert.Equal("synthetic", pronUs.Kind);
            Assert.Equal(AudioResolver.SyntheticKeyFor("murmed", "us"), pronUs.Key);
            Assert.NotEqual("c254m00", pronUs.Key);
            var pronUk = await Pron(svc, "murmed", "uk");
            Assert.True(pronUk.Available);
            Assert.Equal("synthetic", pronUk.Kind);
            Assert.Equal(AudioResolver.SyntheticKeyFor("murmed", "uk"), pronUk.Key);
            var card = svc.Lookup("murmed")!;
            Assert.Equal("synthetic", card.Audio["us"].Kind);
            Assert.Equal(AudioResolver.SyntheticKeyFor("murmed", "us"), card.Audio["us"].Key);
            // 旧公开名 ResolveAudio 现为 Main 面：同断言。
            var legacy = svc.ResolveAudio("murm", "murmed", "us");
            Assert.Equal("synthetic", legacy.Kind);
            Assert.Equal(AudioResolver.SyntheticKeyFor("murmed", "us"), legacy.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MainSynthesisDisabled_HonestNoButton()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root, ("c254m00", "murm", "us"));
            var options = new DictionaryOptions { SynthesisEnabled = false };
            var (svc, _, synthHttp, _) = BuildWithSynth(StoreMurmed(), packDir: null, root, options: options, coreDir: coreDir);
            var pron = await Pron(svc, "murmed", "us");
            Assert.False(pron.Available);
            Assert.Null(pron.Kind);
            Assert.Null(pron.Key);
            var card = svc.Lookup("murmed")!;
            Assert.False(card.Audio["us"].Available);
            Assert.Equal(0, synthHttp.Calls); // 杀开关关断：零合成调用
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- §4.2 排除键回落 ----------

    [Fact]
    public async Task ExcludeKeys_SkipsFailedHumanKey_FallsToSynthesis()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root, ("c254f01", "murmed", "us"));
            var (svc, _, _, _) = BuildWithSynth(StoreMurmed(), packDir: null, root, coreDir: coreDir);
            var first = await Pron(svc, "murmed", "us");
            Assert.Equal("c254f01", first.Key);
            var after = await Pron(svc, "murmed", "us", excludeKeys: "c254f01");
            Assert.True(after.Available);
            Assert.Equal("synthetic", after.Kind);
            Assert.Equal(AudioResolver.SyntheticKeyFor("murmed", "us"), after.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExcludeKeys_SyntheticKeyNeverExcludable()
    {
        var root = TempRoot();
        try
        {
            var (svc, _, _, _) = BuildWithSynth(StoreMurmed(), packDir: null, root);
            var synthKey = AudioResolver.SyntheticKeyFor("murmed", "us");
            var pron = await Pron(svc, "murmed", "us", excludeKeys: synthKey);
            Assert.True(pron.Available);
            Assert.Equal("synthetic", pron.Kind);
            Assert.Equal(synthKey, pron.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- Shadow 面：真人回落，永不合成 ----------

    [Fact]
    public async Task ShadowSurface_LemmaHumanFallback_NoSynthesis()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root, ("c254m00", "murm", "us"));
            var (svc, _, _, _) = BuildWithSynth(StoreMurmed(), packDir: null, root, coreDir: coreDir);
            // 本形无人声、词元有人声 → Shadow 面返回词元真人键（票127 回落语义保留在此面）。
            var shadow = await Pron(svc, "murmed", "us", humanOnly: true);
            Assert.True(shadow.Available);
            Assert.Equal("commons", shadow.Kind);
            Assert.Equal("c254m00", shadow.Key);
            // 本形词元全无 → Shadow 面耗尽，纵有合成器也不合成。
            var none = await Pron(svc, "murmed", "uk", humanOnly: true);
            Assert.False(none.Available);
            Assert.Null(none.Kind);
            Assert.Null(none.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- P4 归元候选表与中文回落 ----------

    [Fact]
    public void ResolveLemmaCandidates_OrderPreserved_FirstIsLemma()
    {
        var store = BuildOpenStore(
            EcdictTsv(
                EcRow("zorblax", "", "v. to zorblax", "", ""),
                EcRow("zorblaxe", "", "n. something", "n. 某种东西", "")),
            WordNetJsonl(WnEntry("zorblax", ("v", [("to zorblax", null)]))));
        var svc = BuildService(store, packDir: null).Service;
        // zorblaxed：morphy 规则 ed→"" 得 zorblax、ed→"e" 得 zorblaxe（规则表顺序）。
        var candidates = svc.ResolveLemmaCandidates("zorblaxed");
        (string, string)[] expected = [("zorblax", "morphy"), ("zorblaxe", "morphy")];
        Assert.Equal(expected, candidates);
        Assert.Equal("zorblax", svc.ResolveLemma("zorblaxed")?.Lemma);
        Assert.Equal("morphy", svc.ResolveLemma("zorblaxed")?.MatchedVia);
        // 连字符：内层候选统一记 hyphen-head。
        var hyphen = svc.ResolveLemmaCandidates("zorblax-like");
        Assert.Equal([("zorblax", "hyphen-head")], hyphen);
    }

    [Fact]
    public void OpenZhFallback_WalksCandidatesToFirstNonEmptyTranslation()
    {
        var store = BuildOpenStore(
            EcdictTsv(
                EcRow("zorblax", "", "v. to zorblax", "", ""),
                EcRow("zorblaxe", "", "n. something", "n. 某种东西", "")),
            WordNetJsonl(WnEntry("zorblax", ("v", [("to zorblax", null)]))));
        var svc = BuildService(store, packDir: null).Service;
        var card = svc.Lookup("zorblaxed")!;
        Assert.Equal("zorblax", card.Lemma);
        // 词元 zorblax 译义为空 → 回落至候选 zorblaxe 的 n 段（首块收纳 unmatched 段）。
        Assert.Contains("某种东西", card.PosBlocks[0].ZhLines.Single());
    }

    // ---------- P6 牛津补丁、归因、词形音标、标签并集 ----------

    [Fact]
    public void OxfordSupplement_PosSegmentOnly_AndAttribution()
    {
        var root = TempRoot();
        try
        {
            var entry = OxfordEntryJson("glorp", new { us = "/ɡ/", uk = "/ɡ/" },
                new[]
                {
                    new { pos = "verb", senses = new[] { new { defEn = "to vanish", glossZh = (string?)null } } },
                    new { pos = "noun", senses = new[] { new { defEn = "a vanish", glossZh = "消失物" } } },
                });
            var pack = BuildOxfordPack(root, [("glorp", entry)], new Dictionary<string, string>(),
                audioArchivePresent: false);
            var store = BuildOpenStore(
                EcdictTsv(EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失；n. 消失物", "p:glorped")),
                WordNetJsonl(WnEntry("glorp", ("v", [("to vanish", null)]))));
            var svc = BuildService(store, pack).Service;
            var card = svc.Lookup("glorp")!;
            var verb = Assert.Single(card.PosBlocks, b => b.Pos == "verb");
            Assert.Equal(["消失"], verb.ZhLines); // 仅动词段，不取名词段
            var noun = Assert.Single(card.PosBlocks, b => b.Pos == "noun");
            Assert.Empty(noun.ZhLines); // 义项已有 GlossZh → 不补丁
            Assert.Equal(
                "Oxford Advanced Learner's Dictionary 10 (local pack) · ECDICT",
                card.Attribution.Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OxfordSupplement_NoSegmentForAnyCandidate_ZhLinesStayEmpty()
    {
        var root = TempRoot();
        try
        {
            var entry = OxfordEntryJson("glorp", new { us = "/ɡ/", uk = "/ɡ/" },
                new[] { new { pos = "verb", senses = new[] { new { defEn = "to vanish" } } } });
            var pack = BuildOxfordPack(root, [("glorp", entry)], new Dictionary<string, string>(),
                audioArchivePresent: false);
            var store = BuildOpenStore(
                EcdictTsv(EcRow("glorp", "ɡlɔːp", "v. to vanish", "n. 消失物", "p:glorped")),
                WordNetJsonl(WnEntry("glorp", ("v", [("to vanish", null)]))));
            var svc = BuildService(store, pack).Service;
            var card = svc.Lookup("glorp")!;
            // ECDICT 只有 n 段、无 v 段 → 动词块不补（前端显式“暂无中文释义”行，见前端契约）。
            Assert.Empty(Assert.Single(card.PosBlocks).ZhLines);
            Assert.Equal(
                "Oxford Advanced Learner's Dictionary 10 (local pack)",
                card.Attribution.Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FormPhonetics_FromOxfordPhon_AndLemmaCardNull()
    {
        var root = TempRoot();
        try
        {
            var entry = """
                {"headword":"glorp","phonetics":{"uk":"/ɡ/","us":"/ɡ/"},
                 "posBlocks":[{"pos":"verb","senses":[{"defEn":"to vanish","glossZh":"消失"}]}],
                 "forms":[{"label":"过去式","text":"glorped","phon":{"us":"/ɡlɔːpt/","uk":"/ɡlɔːpt/"}}],
                 "audio":{},"oxford3000":false}
                """;
            var pack = BuildOxfordPack(root, [("glorp", entry)], new Dictionary<string, string>(),
                audioArchivePresent: false);
            var store = BuildOpenStore(
                EcdictTsv(EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped")),
                WordNetJsonl(WnEntry("glorp", ("v", [("to vanish", null)]))));
            var svc = BuildService(store, pack).Service;
            var formCard = svc.Lookup("glorped")!;
            Assert.NotNull(formCard.FormPhonetics);
            Assert.Equal("/ɡlɔːpt/", formCard.FormPhonetics!.Us);
            Assert.Equal("/ɡlɔːpt/", formCard.FormPhonetics!.Uk);
            Assert.Null(svc.Lookup("glorp")!.FormPhonetics); // 词元卡不用本形音标字段
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LabelUnion_OxfordPlusEcdict_SameTextJoined()
    {
        var root = TempRoot();
        try
        {
            var entry = """
                {"headword":"glorp","phonetics":{"uk":"/ɡ/","us":"/ɡ/"},
                 "posBlocks":[{"pos":"verb","senses":[{"defEn":"to vanish","glossZh":"消失"}]}],
                 "forms":[{"label":"第三人称单数","text":"glorps"}],
                 "audio":{},"oxford3000":false}
                """;
            var pack = BuildOxfordPack(root, [("glorp", entry)], new Dictionary<string, string>(),
                audioArchivePresent: false);
            var store = BuildOpenStore(
                EcdictTsv(EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "s:glorps")),
                WordNetJsonl(WnEntry("glorp", ("v", [("to vanish", null)]))));
            var svc = BuildService(store, pack).Service;
            var form = Assert.Single(svc.Lookup("glorp")!.Forms, f => f.Text == "glorps");
            Assert.Equal("第三人称单数/复数", form.Label);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- §1 合成物化与键校验 ----------

    [Fact]
    public async Task Materialize_CacheHitNoReinvoke_AndMismatchReturnsNull()
    {
        var root = TempRoot();
        try
        {
            var (_, audio, synthHttp, cacheDir) = BuildWithSynth(StoreMurmed(), packDir: null, root);
            var key = AudioResolver.SyntheticKeyFor("murmed", "us");
            var userId = Guid.NewGuid();
            var path = await audio.GetAudioFileAsync(key, CancellationToken.None, userId, "murmed", "us");
            Assert.NotNull(path);
            Assert.StartsWith(cacheDir, path);
            Assert.Equal(FakeMp3, await File.ReadAllBytesAsync(path));
            Assert.Equal(1, synthHttp.Calls);
            var path2 = await audio.GetAudioFileAsync(key, CancellationToken.None, userId, "murmed", "us");
            Assert.Equal(path, path2);
            Assert.Equal(1, synthHttp.Calls); // 缓存命中不重调合成
            // 键与词/口音不匹配 → 不物化（控制器层对此回 400，见下测试；
            // 已落盘文件的按 key serve 属缓存通用语义，不在此断言）。
            var freshKey = AudioResolver.SyntheticKeyFor("freshword", "us");
            var mismatch = await audio.GetAudioFileAsync(freshKey, CancellationToken.None, userId, "other", "us");
            Assert.Null(mismatch);
            Assert.False(File.Exists(Path.Combine(cacheDir, freshKey)));
            // 无词/口音参数的未缓存合成键 → null（不物化）。
            var noParams = await audio.GetAudioFileAsync(
                AudioResolver.SyntheticKeyFor("unseen", "us"), CancellationToken.None, userId, null, null);
            Assert.Null(noParams);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>票254 §1 控制器层校验：合成键与 word/accent 不匹配 → 400，绝不 serve 错词音频。</summary>
    [Fact]
    public async Task ControllerAudio_SyntheticKeyMismatch_Returns400()
    {
        var root = TempRoot();
        try
        {
            var (svc, audio, _, _) = BuildWithSynth(StoreMurmed(), packDir: null, root);
            var currentUser = new TestCurrentUser(Guid.NewGuid());
            var controller = new Api.DictionaryController(svc, audio, currentUser);
            var key = AudioResolver.SyntheticKeyFor("murmed", "us");
            static Microsoft.AspNetCore.Http.DefaultHttpContext CtxWithServices()
            {
                var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
                services.AddLogging();
                services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
                var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
                ctx.RequestServices = services.BuildServiceProvider();
                ctx.Response.Body = new MemoryStream();
                return ctx;
            }
            var ctx = CtxWithServices();
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = ctx };
            var mismatch = await controller.Audio(key, word: "other", accent: "us");
            await mismatch.ExecuteAsync(ctx);
            Assert.Equal(400, ctx.Response.StatusCode);
            var okCtx = CtxWithServices();
            controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = okCtx };
            var ok = await controller.Audio(key, word: "murmed", accent: "us");
            await ok.ExecuteAsync(okCtx);
            Assert.Equal(200, okCtx.Response.StatusCode);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TestCurrentUser(Guid id) : BuildingBlocks.Security.ICurrentUser
    {
        public Guid? UserId => id;
        public string? Email => null;
        public bool IsAuthenticated => true;
        public string? IpAddress => null;
        public string? UserAgent => null;
        public IReadOnlyList<string> Roles => [];
    }

    [Fact]
    public void SyntheticSlug_IsInjective_AcrossPunctuation()
    {
        var keys = new[]
        {
            AudioResolver.SyntheticKeyFor("mother-in-law", "us"),
            AudioResolver.SyntheticKeyFor("motherinlaw", "us"),
            AudioResolver.SyntheticKeyFor("mother in law", "us"),
            AudioResolver.SyntheticKeyFor("l'amour", "us"),
            AudioResolver.SyntheticKeyFor("lamour", "us"),
        };
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.False(AudioResolver.IsSyntheticKey("../escape.mp3"));
        Assert.True(AudioResolver.IsSyntheticKey(AudioResolver.SyntheticKeyFor("dig", "uk")));
    }
}
