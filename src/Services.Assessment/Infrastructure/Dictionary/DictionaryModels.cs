namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

// ---------- 响应 DTO（SPEC §4.3 schema v2，字段名经全局 camelCase 序列化） ----------

public sealed class DictionaryLookupResponse
{
    public required string Query { get; init; }
    public required string Lemma { get; init; }
    /// <summary>redirect | morphy | exc | ecdict-link | hyphen-head | direct</summary>
    public required string MatchedVia { get; init; }
    /// <summary>oxford | open</summary>
    public required string Tier { get; init; }
    public required PhoneticsDto Phonetics { get; init; }
    public required List<PosBlockDto> PosBlocks { get; init; }
    public required List<FormDto> Forms { get; init; }
    public required Dictionary<string, AudioOptionDto> Audio { get; init; }
    public required AttributionDto Attribution { get; init; }
}

public sealed class PhoneticsDto
{
    public string? Uk { get; init; }
    public string? Us { get; init; }
}

public sealed class PosBlockDto
{
    public required string Pos { get; init; }
    public required string Label { get; init; }
    public required List<SenseDto> Senses { get; init; }
    public required int FoldedCount { get; init; }
    public required List<string> ZhLines { get; init; }
}

public sealed class SenseDto
{
    public required string DefEn { get; init; }
    public string? GlossZh { get; init; }
    public ExampleDto? Example { get; init; }
}

public sealed class ExampleDto
{
    public required string En { get; init; }
    public string? Zh { get; init; }
}

public sealed class FormDto
{
    public required string Label { get; init; }
    public required string Text { get; init; }
}

public sealed class AudioOptionDto
{
    /// <summary>
    /// oxford | commons（票126 §8.1：呈现 kind 只余真人录音两档）；
    /// 无真人录音时为 null（序列化省略），以 Available=false 表达（票126 §8.2）。
    /// </summary>
    public string? Kind { get; init; }
    /// <summary>音频端点的不透明 key；无真人录音时无。</summary>
    public string? Key { get; init; }
    /// <summary>true = 有真人录音可播；false = 该口音无真人录音（前端不渲染播放钮，票126 §8.3/§8.4）。</summary>
    public required bool Available { get; init; }
}

public sealed class AttributionDto
{
    public required string Source { get; init; }
    /// <summary>发音来源与释义来源不同层时单列（SPEC §8 状态 B：来源行分别标注）。</summary>
    public string? AudioSource { get; init; }
}

public sealed class PronounceResponse
{
    /// <summary>oxford | commons（票126 §8.1）；链耗尽时为 null（序列化省略）。</summary>
    public string? Kind { get; init; }
    public string? Key { get; init; }
    /// <summary>
    /// 票126 §8.2：链耗尽 → Available=false（无 kind、无 key），端点仍回 200；
    /// 不再以 device/neural 兜底填充（S-2 关掉机器兜底）。
    /// </summary>
    public bool Available { get; init; }
}

// ---------- 开放层内存模型 ----------

public sealed class EcdictEntry
{
    public required string Word { get; init; }
    public string Phonetic { get; init; } = "";
    public string Definition { get; init; } = "";
    public string Translation { get; init; } = "";
    public string Exchange { get; init; } = "";
    public string Collins { get; init; } = "";
    public string Oxford { get; init; } = "";
    public string Tag { get; init; } = "";
    /// <summary>exchange 解析后的 (code, target) 对。</summary>
    public IReadOnlyList<(string Code, string Target)> ExchangePairs { get; init; } = [];
}

/// <summary>
/// 延迟解析的 exchange 对列表：91k 词条在载入期全量 ParseExchange 的分配成本过高
/// （§9 冷启动预算），改为首次访问（实际查词时，仅被查词条）才解析并缓存。
/// </summary>
public sealed class LazyExchangePairs(string exchange) : IReadOnlyList<(string Code, string Target)>
{
    private IReadOnlyList<(string Code, string Target)>? _parsed;
    private IReadOnlyList<(string Code, string Target)> Parsed =>
        _parsed ??= OpenLayerStore.ParseExchange(exchange);

    public int Count => Parsed.Count;
    public (string Code, string Target) this[int index] => Parsed[index];
    public IEnumerator<(string Code, string Target)> GetEnumerator() => Parsed.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class WordNetEntry
{
    public required string Lemma { get; init; }
    /// <summary>pos 字符 n/v/a/r → synset 列表（WNdb 原序）。</summary>
    public required IReadOnlyDictionary<string, List<WordNetSynset>> PosBlocks { get; init; }
}

public sealed class WordNetSynset
{
    public required string Gloss { get; init; }
    public string? Example { get; init; }
}

public sealed class AudioFileRec
{
    public required string File { get; init; }
    /// <summary>us | uk | other</summary>
    public required string Accent { get; init; }
    public string? Ipa { get; init; }
    public string License { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Mp3Url { get; init; } = "";
}

// ---------- Tier 0 牛津内存模型（提取产物 oxford-entries.jsonl 的行形状） ----------

public sealed class OxfordEntry
{
    public required string Headword { get; init; }
    public PhoneticsDto Phonetics { get; init; } = new();
    public List<OxfordPosBlock> PosBlocks { get; init; } = [];
    public List<OxfordForm> Forms { get; init; } = [];
    /// <summary>accent (uk/us) → oxford-audio/ 内文件 key。</summary>
    public Dictionary<string, string> Audio { get; init; } = [];
    public bool Oxford3000 { get; init; }
    public int SenseCount => PosBlocks.Sum(b => b.Senses.Count);
}

public sealed class OxfordPosBlock
{
    public string Pos { get; init; } = "";
    public List<OxfordSense> Senses { get; init; } = [];
}

public sealed class OxfordSense
{
    public string DefEn { get; init; } = "";
    public string? GlossZh { get; init; }
    public string? Grammar { get; init; }
    public List<OxfordExample> Examples { get; init; } = [];
}

public sealed class OxfordExample
{
    public string En { get; init; } = "";
    public string? Zh { get; init; }
}

public sealed class OxfordForm
{
    public string Label { get; init; } = "";
    public string Text { get; init; } = "";
    public Dictionary<string, string> Phon { get; init; } = [];
    public Dictionary<string, string> AudioKey { get; init; } = [];
}
