using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票119 词典测试基建：全部夹具为人工编造的合成词（zorblax/glorp 系），
/// 不含任何真实词典条目（SPEC §5.5）。牛津包夹具按提取产物格式在临时目录合成。
/// </summary>
public static class DictionaryTestHarness
{
    public static Stream ToStream(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    public static Stream GzipOf(string text)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gz.Write(bytes, 0, bytes.Length);
        }
        ms.Position = 0;
        return ms;
    }

    /// <summary>合成 ECDICT 子集 TSV（8 列）。行 = 词条数组。</summary>
    public static string EcdictTsv(params string[][] rows) =>
        string.Join("\n", rows.Select(r => string.Join("\t", r)));

    public static string[] EcRow(string word, string phonetic, string definition, string translation,
        string exchange, string collins = "", string oxford = "", string tag = "") =>
        [word, phonetic, definition, translation, exchange, collins, oxford, tag];

    public static string WordNetJsonl(params object[] entries) =>
        string.Join("\n", entries.Select(e => JsonSerializer.Serialize(e)));

    public static object WnEntry(string lemma, params (string Pos, (string Gloss, string? Example)[] Synsets)[] blocks) =>
        new
        {
            lemma,
            posBlocks = blocks.Select(b => new
            {
                pos = b.Pos,
                synsets = b.Synsets.Select(s => s.Example is null
                    ? (object)new { gloss = s.Gloss }
                    : new { gloss = s.Gloss, example = s.Example }).ToArray(),
            }).ToArray(),
        };

    public static OpenLayerStore BuildOpenStore(
        string ecdictTsv,
        string wordNetJsonl,
        string exceptionsJson = "{}",
        string audioIndexJson = "{}") =>
        OpenLayerStore.Load(
            GzipOf(ecdictTsv),
            GzipOf(wordNetJsonl),
            ToStream(exceptionsJson),
            ToStream(audioIndexJson));

    /// <summary>合成牛津包：entries = (键名, JSON 行)；redirects 原样写入；可选音频归档 + 音频文件。</summary>
    public static string BuildOxfordPack(
        string root,
        IReadOnlyList<(string Headword, string JsonLine)> entries,
        IReadOnlyDictionary<string, string> redirects,
        bool audioArchivePresent,
        IReadOnlyList<string>? audioFiles = null)
    {
        var dir = Path.Combine(root, "oxford");
        Directory.CreateDirectory(dir);
        var entriesPath = Path.Combine(dir, "oxford-entries.jsonl.gz");
        var idx = new Dictionary<string, long[]>();
        using (var fs = new FileStream(entriesPath, FileMode.Create, FileAccess.Write))
        {
            foreach (var (headword, line) in entries)
            {
                var offset = fs.Position;
                using (var member = new GZipStream(fs, CompressionLevel.Fastest, leaveOpen: true))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    member.Write(bytes, 0, bytes.Length);
                }
                idx[headword.ToLowerInvariant()] = [offset, fs.Position - offset];
            }
        }
        File.WriteAllText(Path.Combine(dir, "oxford-entries.idx.json"), JsonSerializer.Serialize(idx));
        File.WriteAllText(Path.Combine(dir, "oxford-redirects.json"), JsonSerializer.Serialize(redirects));
        File.WriteAllText(Path.Combine(dir, "oxford-pack-manifest.json"),
            JsonSerializer.Serialize(new { audioArchive = audioArchivePresent ? "present" : "absent" }));
        if (audioArchivePresent && audioFiles is not null)
        {
            var audioDir = Path.Combine(dir, "oxford-audio");
            Directory.CreateDirectory(audioDir);
            foreach (var f in audioFiles)
                File.WriteAllBytes(Path.Combine(audioDir, f), [0xFF, 0xF3, 0x01, 0x02]);
        }
        return dir;
    }

    public static string OxfordEntryJson(
        string headword,
        object phonetics,
        object posBlocks,
        object? forms = null,
        object? audio = null,
        bool oxford3000 = false) =>
        JsonSerializer.Serialize(new
        {
            headword,
            phonetics,
            posBlocks,
            forms = forms ?? Array.Empty<object>(),
            audio = audio ?? new { },
            oxford3000,
        });

    public sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<string> RequestedUrls { get; } = [];
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        public static StubHttpHandler Bytes(byte[] bytes) =>
            new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        public static StubHttpHandler Failure() =>
            new(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(_responder(request));
        }
    }

    public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    public static (DictionaryService Service, AudioResolver Audio, OxfordPackStore Oxford) BuildService(
        OpenLayerStore open,
        string? packDir,
        DictionaryOptions? options = null,
        HttpMessageHandler? httpHandler = null,
        TimeProvider? clock = null,
        string? coreDir = null,
        string? cacheDir = null)
    {
        options ??= new DictionaryOptions();
        if (coreDir is not null) options.AudioCoreDir = coreDir;
        if (cacheDir is not null) options.AudioCacheDir = cacheDir;
        var oxford = new OxfordPackStore(packDir);
        var audio = new AudioResolver(
            new Lazy<OpenLayerStore>(() => open),
            oxford,
            options,
            new StubHttpClientFactory(httpHandler ?? StubHttpHandler.Failure()),
            clock ?? TimeProvider.System);
        var service = new DictionaryService(
            new Lazy<OpenLayerStore>(() => open),
            oxford,
            audio,
            options,
            clock ?? TimeProvider.System);
        return (service, audio, oxford);
    }
}
