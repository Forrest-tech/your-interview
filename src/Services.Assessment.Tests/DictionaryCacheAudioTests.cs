using System.Net;
using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>票119 缓存策略（SPEC §4.4：正 24h / 负 10min / 键含 Generation）与发音链（§4.5/§2.1）回落。</summary>
public class DictionaryCacheAudioTests
{
    private static OpenLayerStore StoreWithAudio() => BuildOpenStore(
        EcdictTsv(
            EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped/d:glorped"),
            EcRow("neuralon", "", "n. synthetic thing", "n. 合成物", "")),
        WordNetJsonl(
            WnEntry("glorp", ("v", [("to disappear suddenly", null)])),
            WnEntry("neuralon", ("n", [("a synthetic thing", null)]))),
        audioIndexJson: "{\"glorp\": [{\"file\": \"En-us-glorp.ogg\", \"accent\": \"us\", \"license\": \"CC BY-SA 4.0\", \"artist\": \"Synth\", \"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-us-glorp.ogg/En-us-glorp.ogg.mp3\"}]}");

    private static string WriteCorePack(string root, params (string Key, string Word, string Accent, byte[] Bytes)[] files)
    {
        var coreDir = Path.Combine(root, "audio-core");
        Directory.CreateDirectory(coreDir);
        var manifestFiles = new List<object>();
        foreach (var f in files)
        {
            File.WriteAllBytes(Path.Combine(coreDir, f.Key + ".mp3"), f.Bytes);
            manifestFiles.Add(new
            {
                key = f.Key, word = f.Word, accent = f.Accent, file = f.Key + ".mp3",
                storedAs = f.Key + ".mp3", license = "CC0", artist = "Synth",
                bytes = f.Bytes.Length, sha256 = "",
            });
        }
        File.WriteAllText(Path.Combine(coreDir, "audio-core-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(new { files = manifestFiles }));
        return coreDir;
    }

    [Fact]
    public void PositiveCache_HitWithin24h_ExpiresAfter()
    {
        var clock = new FakeTimeProvider();
        var svc = BuildService(StoreWithAudio(), packDir: null, clock: clock).Service;
        var first = svc.Lookup("glorp");
        Assert.NotNull(first);
        Assert.Equal(1, svc.CoreLookupCountForTests);
        clock.Advance(TimeSpan.FromHours(23));
        Assert.Same(first, svc.Lookup("glorp")); // 24h 内命中缓存,不重算
        Assert.Equal(1, svc.CoreLookupCountForTests);
        clock.Advance(TimeSpan.FromHours(2)); // 累计 25h → 过期
        Assert.NotSame(first, svc.Lookup("glorp"));
        Assert.Equal(2, svc.CoreLookupCountForTests);
    }

    [Fact]
    public void NegativeCache_HitWithin10Min_ExpiresAfter()
    {
        var clock = new FakeTimeProvider();
        var svc = BuildService(StoreWithAudio(), packDir: null, clock: clock).Service;
        Assert.Null(svc.Lookup("nopeword"));
        Assert.Equal(1, svc.CoreLookupCountForTests);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Null(svc.Lookup("nopeword"));
        Assert.Equal(1, svc.CoreLookupCountForTests); // 负缓存兜住
        clock.Advance(TimeSpan.FromMinutes(2)); // 累计 11 分钟 → 过期重算
        Assert.Null(svc.Lookup("nopeword"));
        Assert.Equal(2, svc.CoreLookupCountForTests);
    }

    [Fact]
    public void PronunciationChain_CorePackWins_AndAudioServesFromCore()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var coreDir = WriteCorePack(root, ("c000001", "glorp", "us", new byte[] { 1, 2, 3 }));
            var built = BuildService(StoreWithAudio(), packDir: null, coreDir: coreDir,
                cacheDir: Path.Combine(root, "cache"));
            var r = built.Service.Lookup("glorp")!;
            var us = r.Audio["us"];
            Assert.Equal("commons", us.Kind);
            Assert.True(us.Available);
            Assert.Equal("c000001", us.Key);
            var path = built.Audio.GetAudioFileAsync("c000001", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.NotNull(path);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PronunciationChain_IndexHit_KeyIsMp3Name_LazyFetchOnAudioRequest()
    {
        // 索引命中:卡上 kind=commons、key = 转码 MP3 文件名;实际点播时经 HTTP 拉取落盘,二次走本地
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = StubHttpHandler.Bytes([9, 9, 9]);
            var built = BuildService(StoreWithAudio(), packDir: null, httpHandler: handler,
                cacheDir: Path.Combine(root, "cache"));
            var r = built.Service.Lookup("glorp")!;
            var us = r.Audio["us"];
            Assert.Equal("commons", us.Kind);
            Assert.Equal("En-us-glorp.ogg.mp3", us.Key);
            var path = built.Audio.GetAudioFileAsync(us.Key!, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.NotNull(path);
            Assert.Equal([9, 9, 9], File.ReadAllBytes(path!));
            Assert.Single(handler.RequestedUrls);
            Assert.True(File.Exists(Path.Combine(root, "cache", "cache-index.json")));
            var again = built.Audio.GetAudioFileAsync(us.Key!, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.NotNull(again);
            Assert.Single(handler.RequestedUrls); // 第二次走本地缓存,不再发请求
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PronunciationChain_LazyFetchFailure_ReturnsNull_NotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var built = BuildService(StoreWithAudio(), packDir: null,
                httpHandler: StubHttpHandler.Failure(), cacheDir: Path.Combine(root, "cache"));
            var path = built.Audio.GetAudioFileAsync("En-us-glorp.ogg.mp3", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.Null(path); // 拉取失败 → null(端点 404 → 前端回落),绝不抛 5xx
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PronunciationChain_NoRecording_DeviceFallback_NotAvailable()
    {
        var svc = BuildService(StoreWithAudio(), packDir: null).Service;
        var r = svc.Lookup("neuralon")!;
        Assert.All(r.Audio.Values, p =>
        {
            Assert.Equal("device", p.Kind);
            Assert.False(p.Available); // device 不是真人录音:卡上不渲染该口音钮(§4.3)
            Assert.Null(p.Key);
        });
    }

    [Fact]
    public void PronunciationChain_NeuralTts_PronounceSynthesizesAndCaches_WhenEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([7, 7]),
            });
            var options = new DictionaryOptions
            {
                NeuralTts = new NeuralTtsOptions
                {
                    Enabled = true,
                    Endpoint = "https://tts.example.invalid/v1/audio/speech",
                    ApiKey = "test-key",
                    Model = "fake-model",
                    Voice = "fake-voice",
                },
            };
            var built = BuildService(StoreWithAudio(), packDir: null, options: options,
                httpHandler: handler, cacheDir: Path.Combine(root, "cache"));
            var card = built.Service.Lookup("neuralon")!;
            Assert.All(card.Audio.Values, p => Assert.Equal("neural", p.Kind));

            var pron = built.Service.PronounceAsync("neuralon", "us", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.Equal("neural", pron.Kind);
            Assert.NotNull(pron.Key);
            Assert.StartsWith("neural-", pron.Key);
            var path = built.Audio.GetAudioFileAsync(pron.Key!, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.NotNull(path);
            Assert.Equal([7, 7], File.ReadAllBytes(path!));
            var callsAfterFirst = handler.RequestedUrls.Count;
            var pron2 = built.Service.PronounceAsync("neuralon", "us", CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.Equal(pron.Key, pron2.Key);
            Assert.Equal(callsAfterFirst, handler.RequestedUrls.Count); // 命中缓存不重复合成
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void OxfordPackStore_MissingDir_IsAbsentWithoutThrowing()
    {
        var store = new OxfordPackStore(packDir: null);
        Assert.False(store.IsPresent);
        Assert.Null(store.GetEntry("glorp"));
        Assert.Null(store.RedirectTarget("glorped"));
        var missing = new OxfordPackStore(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N")));
        Assert.False(missing.IsPresent);
        Assert.Null(missing.GetEntry("glorp"));
    }

    [Fact]
    public void OxfordPackStore_ReadsEntryAndRedirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = BuildOxfordPack(root,
                [("glorp", OxfordEntryJson("glorp",
                    new { uk = "/ɡ/", us = "/ɡ/" },
                    new[] { new { pos = "verb", senses = new[] { new { defEn = "to vanish" } } } }))],
                new Dictionary<string, string> { ["glorped"] = "glorp" },
                audioArchivePresent: false);
            var store = new OxfordPackStore(dir);
            Assert.True(store.IsPresent);
            Assert.False(store.AudioArchivePresent);
            Assert.True(store.Generation >= 1); // 装包即换代(§4.4)
            var entry = store.GetEntry("glorp"); // 调用方恒传小写归一形(DictionaryService 口径)
            Assert.NotNull(entry);
            Assert.Equal(1, entry!.PosBlocks[0].Senses.Count);
            Assert.Equal("glorp", store.RedirectTarget("glorped"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
