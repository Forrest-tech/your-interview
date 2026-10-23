using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// Tier 1 开放层数据（SPEC §3.1/§3.2）：ECDICT 子集 v2 + WordNet 子集 + 音频索引，
/// 全部以嵌入资源随应用分发，启动后载入内存。归一表（ECDICT 0: 链接 ∪ WordNet .exc）
/// 在此构建；Morphy 规则在 <see cref="Morphy"/>。
/// </summary>
public sealed class OpenLayerStore
{
    public IReadOnlyDictionary<string, EcdictEntry> Ecdict { get; }
    public IReadOnlyDictionary<string, WordNetEntry> WordNet { get; }
    /// <summary>屈折形 → 词元（来自各词条 exchange 的 0: 链接，即「该词条是 X 的变形」）。</summary>
    public IReadOnlyDictionary<string, string> LemmaByForm { get; }
    /// <summary>WordNet .exc 例外表：屈折形 → 候选词元。</summary>
    public IReadOnlyDictionary<string, string[]> Exceptions { get; }
    public IReadOnlyDictionary<string, List<AudioFileRec>> AudioIndex { get; }

    private OpenLayerStore(
        Dictionary<string, EcdictEntry> ecdict,
        Dictionary<string, WordNetEntry> wordNet,
        Dictionary<string, string> lemmaByForm,
        Dictionary<string, string[]> exceptions,
        Dictionary<string, List<AudioFileRec>> audioIndex)
    {
        Ecdict = ecdict;
        WordNet = wordNet;
        LemmaByForm = lemmaByForm;
        Exceptions = exceptions;
        AudioIndex = audioIndex;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // WordNet JSONL 行 DTO。反序列化走源生成上下文（WnJsonContext）：反射式逐行
    // Deserialize 是冷启动的主要成本之一（§9 预算），源生成可显著压低。

    public static OpenLayerStore Load(Stream ecdictGz, Stream wordNetGz, Stream exceptionsJson, Stream audioIndexJson)
    {
        // 四节数据互相独立:两节大的(ECDICT / WordNet)用专用线程并行载入
        // (避开线程池冷启动爬坡),两节小的内联(冷启动预算 §9:全量 < 2 s)。
        var ecdictTask = Task.Factory.StartNew(() => LoadEcdict(ecdictGz),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var wordNetTask = Task.Factory.StartNew(() => LoadWordNet(wordNetGz),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var audioTask = Task.Factory.StartNew(() => LoadAudioIndex(audioIndexJson),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var exceptions = LoadExceptions(exceptionsJson);
        Task.WaitAll(ecdictTask, wordNetTask, audioTask);
        var (ecdict, lemmaByForm) = ecdictTask.Result;
        return new OpenLayerStore(ecdict, wordNetTask.Result, lemmaByForm, exceptions, audioTask.Result);
    }

    private static string DecompressText(Stream gz)
    {
        using var reader = new StreamReader(new GZipStream(gz, CompressionMode.Decompress));
        return reader.ReadToEnd();
    }

    private static (Dictionary<string, EcdictEntry>, Dictionary<string, string>) LoadEcdict(Stream ecdictGz)
    {
        // ---------- ECDICT 子集（TSV.gz，8 列） ----------
        // 整表一次解压 + span 切行/切字段:避免逐行 Split 的中间数组(载入预算)。
        var ecdict = new Dictionary<string, EcdictEntry>(91_599);
        var lemmaByForm = new Dictionary<string, string>();
        var text = DecompressText(ecdictGz).AsSpan();
        while (text.Length > 0)
        {
            var nl = text.IndexOf('\n');
            var line = nl < 0 ? text : text[..nl];
            text = nl < 0 ? default : text[(nl + 1)..];
            if (line.Length == 0) continue;
            if (line[^1] == '\r') line = line[..^1];

            Span<Range> cols = stackalloc Range[8];
            var colCount = 0;
            var rest = line;
            while (colCount < 8)
            {
                var tab = rest.IndexOf('\t');
                if (tab < 0)
                {
                    cols[colCount++] = new Range(line.Length - rest.Length, line.Length);
                    rest = default;
                    break;
                }
                cols[colCount++] = new Range(line.Length - rest.Length, line.Length - rest.Length + tab);
                rest = rest[(tab + 1)..];
            }
            if (colCount < 8) continue;

            var exchange = line[cols[4]].ToString();
            // 空字段共享 string.Empty / 空列表，避免无谓分配（冷启动预算）。
            var entry = new EcdictEntry
            {
                Word = line[cols[0]].ToString(),
                Phonetic = Unescape(Field(line, cols[1])),
                Definition = Unescape(Field(line, cols[2])),
                Translation = Unescape(Field(line, cols[3])),
                Exchange = exchange,
                Collins = Field(line, cols[5]),
                Oxford = Field(line, cols[6]),
                Tag = Field(line, cols[7]),
                ExchangePairs = exchange.Length == 0 ? [] : new LazyExchangePairs(exchange),
            };
            ecdict[entry.Word] = entry;
            // 载入期只需要 0: 链接（归一表）：span 扫描单段，避免全量解析（冷启动预算）。
            var zero = ExtractZeroTarget(exchange);
            if (zero is not null)
                lemmaByForm[entry.Word] = zero;
        }
        return (ecdict, lemmaByForm);
    }

    private static string Field(ReadOnlySpan<char> line, Range range) =>
        line[range].Length == 0 ? "" : line[range].ToString();

    /// <summary>从 exchange 串中提取 "0:" 段的目标词元（无则 null）。</summary>
    private static string? ExtractZeroTarget(string exchange)
    {
        var span = exchange.AsSpan();
        while (span.Length > 0)
        {
            var slash = span.IndexOf('/');
            var segment = slash < 0 ? span : span[..slash];
            var colon = segment.IndexOf(':');
            if (colon > 0 && segment[..colon].Trim().SequenceEqual("0"))
                return segment[(colon + 1)..].Trim().ToString();
            if (slash < 0) break;
            span = span[(slash + 1)..];
        }
        return null;
    }

    private static Dictionary<string, WordNetEntry> LoadWordNet(Stream wordNetGz)
    {
        // ---------- WordNet 子集（JSONL.gz） ----------
        var wordNet = new Dictionary<string, WordNetEntry>(43_630);
        var text = DecompressText(wordNetGz).AsSpan();
        while (text.Length > 0)
        {
            var nl = text.IndexOf('\n');
            var line = nl < 0 ? text : text[..nl];
            text = nl < 0 ? default : text[(nl + 1)..];
            if (line.Length == 0) continue;
            if (line[^1] == '\r') line = line[..^1];
            var dto = JsonSerializer.Deserialize(line, WnJsonContext.Default.WnLine);
            if (dto is null) continue;
            var blocks = new Dictionary<string, List<WordNetSynset>>();
            foreach (var block in dto.PosBlocks)
            {
                blocks[block.Pos] = block.Synsets
                    .Select(s => new WordNetSynset { Gloss = s.Gloss, Example = s.Example })
                    .ToList();
            }
            wordNet[dto.Lemma] = new WordNetEntry { Lemma = dto.Lemma, PosBlocks = blocks };
        }
        return wordNet;
    }

    private static Dictionary<string, string[]> LoadExceptions(Stream exceptionsJson)
    {
        // ---------- .exc 例外表 ----------
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(ReadAllText(exceptionsJson), JsonOptions)
            ?? [];
    }

    private static Dictionary<string, List<AudioFileRec>> LoadAudioIndex(Stream audioIndexJson)
    {
        // ---------- 音频索引 ----------
        // 流式 Utf8JsonReader 逐字段读:15 MB 级索引下,DTO 反射的冷启动成本过高(实测 ~2 s),
        // 流式读可压到数百 ms(§9 载入预算)。形状 = {word: [{file, accent, ipa?, license, artist, mp3Url}]}。
        using var buffer = new MemoryStream();
        audioIndexJson.CopyTo(buffer);
        var reader = new Utf8JsonReader(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        var audioIndex = new Dictionary<string, List<AudioFileRec>>();
        // 深度口径:root 对象 0 / 词键 1 / 数组 1 / 录音对象 2 / 字段名与值 3
        string? currentWord = null;
        List<AudioFileRec>? currentRecs = null;
        string? file = null, accent = null, ipa = null, license = null, artist = null, mp3Url = null;
        // 字段名以 ValueSpan 字节比对代替 GetString()+字符串 switch：54k 录音 × 6 字段的
        // 属性名分配是本节载入的主要成本之一（冷启动预算）。字段码：1 file 2 accent 3 ipa
        // 4 license 5 artist 6 mp3Url。
        var field = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    currentWord = reader.GetString();
                    break;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 3:
                    var nameSpan = reader.ValueSpan;
                    field = nameSpan.SequenceEqual("file"u8) ? 1
                        : nameSpan.SequenceEqual("accent"u8) ? 2
                        : nameSpan.SequenceEqual("ipa"u8) ? 3
                        : nameSpan.SequenceEqual("license"u8) ? 4
                        : nameSpan.SequenceEqual("artist"u8) ? 5
                        : nameSpan.SequenceEqual("mp3Url"u8) ? 6
                        : 0;
                    break;
                case JsonTokenType.StartArray when reader.CurrentDepth == 1:
                    currentRecs = [];
                    break;
                case JsonTokenType.EndArray when reader.CurrentDepth == 1:
                    if (currentWord is not null && currentRecs is { Count: > 0 })
                        audioIndex[currentWord] = currentRecs;
                    currentRecs = null;
                    currentWord = null;
                    break;
                case JsonTokenType.StartObject when reader.CurrentDepth == 2:
                    file = accent = ipa = license = artist = mp3Url = null;
                    break;
                case JsonTokenType.EndObject when reader.CurrentDepth == 2 && currentRecs is not null:
                    currentRecs.Add(new AudioFileRec
                    {
                        File = file ?? "",
                        Accent = string.IsNullOrEmpty(accent) ? "other" : accent,
                        Ipa = ipa,
                        License = license ?? "",
                        Artist = artist ?? "",
                        Mp3Url = mp3Url ?? "",
                    });
                    field = 0;
                    break;
                case JsonTokenType.String when reader.CurrentDepth == 3 && field != 0:
                    var value = reader.GetString();
                    switch (field)
                    {
                        case 1: file = value; break;
                        case 2: accent = value; break;
                        case 3: ipa = value; break;
                        case 4: license = value; break;
                        case 5: artist = value; break;
                        case 6: mp3Url = value; break;
                    }
                    field = 0;
                    break;
            }
        }
        return audioIndex;
    }

    /// <summary>从本程序集嵌入资源加载（运行时默认路径）。</summary>
    public static OpenLayerStore LoadFromEmbeddedResources()
    {
        var asm = typeof(OpenLayerStore).Assembly;
        Stream Required(string suffix)
        {
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Embedded dictionary resource missing: {suffix}");
            return asm.GetManifestResourceStream(name)!;
        }
        using var ecdict = Required("ecdict-subset.tsv.gz");
        using var wordNet = Required("wordnet-subset.jsonl.gz");
        using var exc = Required("wordnet-exceptions.json");
        using var audio = Required("audio-index.json");
        return Load(ecdict, wordNet, exc, audio);
    }

    public static IReadOnlyList<(string Code, string Target)> ParseExchange(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange)) return [];
        var pairs = new List<(string, string)>();
        foreach (var part in exchange.Split('/'))
        {
            var idx = part.IndexOf(':');
            if (idx > 0)
                pairs.Add((part[..idx].Trim(), part[(idx + 1)..].Trim()));
        }
        return pairs;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n");

    private static string ReadAllText(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

// ---------- WordNet JSONL 行 DTO + 源生成序列化上下文 ----------

internal sealed class WnLine
{
    public string Lemma { get; set; } = "";
    public List<WnBlock> PosBlocks { get; set; } = [];
}

internal sealed class WnBlock
{
    public string Pos { get; set; } = "";
    public List<WnSynset> Synsets { get; set; } = [];
}

internal sealed class WnSynset
{
    public string Gloss { get; set; } = "";
    public string? Example { get; set; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(WnLine))]
internal partial class WnJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
