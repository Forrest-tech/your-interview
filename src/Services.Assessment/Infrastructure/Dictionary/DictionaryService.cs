using System.Collections.Concurrent;

namespace YourInterview.Services.Assessment.Infrastructure.Dictionary;

/// <summary>
/// 词典查询管线（SPEC §4）：归一化四步（§4.1）→ 合并优先 P1–P6（§4.2）→ 响应 schema v2（§4.3）。
/// 缓存（§4.4）：正结果 24h；负结果 10 分钟短 TTL；缓存键含牛津包装载代际，装包即换代。
/// </summary>
public sealed class DictionaryService
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(10);

    private readonly Lazy<OpenLayerStore> _openLazy;
    private readonly OxfordPackStore _oxford;
    private readonly AudioResolver _audio;
    private readonly DictionaryOptions _options;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, CacheItem> _cache = new();

    private sealed record CacheItem(DictionaryLookupResponse? Response, DateTimeOffset ExpiresAt);

    /// <summary>测试可见：核心管线实际执行次数（断言正/负缓存命中与过期用）。</summary>
    internal int CoreLookupCountForTests;


    public DictionaryService(
        Lazy<OpenLayerStore> open,
        OxfordPackStore oxford,
        AudioResolver audio,
        DictionaryOptions options,
        TimeProvider clock)
    {
        _openLazy = open;
        _oxford = oxford;
        _audio = audio;
        _options = options;
        _clock = clock;
    }

    private OpenLayerStore Open => _openLazy.Value;

    // ---------- 对外 ----------

    /// <summary>查词。命中任一层 → 响应；全层未命中 → null（控制器回 404 空结果体）。</summary>
    public DictionaryLookupResponse? Lookup(string word)
    {
        var q = NormalizeQuery(word);
        if (q.Length == 0) return null;

        var cacheKey = $"{_oxford.Generation}|{q}";
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.ExpiresAt > now)
            return hit.Response;

        var response = LookupCore(q);
        if (_cache.Count > 10_000) _cache.Clear();
        _cache[cacheKey] = new CacheItem(
            response,
            now + (response is null ? NegativeTtl : PositiveTtl));
        return response;
    }

    /// <summary>
    /// 发音链单点解析（票126 §8.1/§8.2  supersede 119 §4.3 注）：只供卡内播放入口；
    /// 链耗尽 → Available=false（无 kind、无 key），不用 404、更不以 device/neural 兜底。
    /// 神经 TTS 配置（Dictionary:NeuralTts）本票不动，但 pronounce 呈现不消费其合成结果（S-2）。
    /// </summary>
    public Task<PronounceResponse> PronounceAsync(string word, string accent, CancellationToken ct)
    {
        var q = NormalizeQuery(word);
        if (q.Length == 0) return Task.FromResult(new PronounceResponse { Available = false });
        accent = accent == "uk" ? "uk" : "us";
        var lemma = ResolveLemma(q)?.Lemma ?? q;
        var option = ResolveAudio(lemma, q, accent);
        return Task.FromResult(option is { Available: true, Key: not null }
            ? new PronounceResponse { Kind = option.Kind, Key = option.Key, Available = true }
            : new PronounceResponse { Available = false });
    }

    // ---------- 归一化（§4.1，顺序固定） ----------

    public static string NormalizeQuery(string? word) =>
        (word ?? "").Trim().ToLowerInvariant();

    public sealed record ResolvedLemma(string Lemma, string MatchedVia);

    public ResolvedLemma? ResolveLemma(string q)
    {
        // 第 3 步的前置：Tier 0 在席先查牛津桩（牛津自带归元）
        if (_oxford.IsPresent)
        {
            var target = _oxford.RedirectTarget(q);
            if (target is not null && target != q && HasAny(target))
                return new ResolvedLemma(target, "redirect");
        }
        // 第 1 步：整词键（结构层直命中）
        if (Open.WordNet.ContainsKey(q) || _oxford.GetEntry(q) is not null)
            return new ResolvedLemma(q, "direct");
        // 第 2 步：连字符复合 → 首段重试
        var hyphen = q.IndexOf('-');
        if (hyphen > 0)
        {
            var head = q[..hyphen];
            var inner = ResolveLemma(head);
            if (inner is not null)
                return new ResolvedLemma(inner.Lemma, "hyphen-head");
        }
        // 第 3 步：屈折归元（ECDICT 0: 链接 → WordNet .exc → Morphy 规则）
        if (Open.LemmaByForm.TryGetValue(q, out var byLink) && HasAny(byLink))
            return new ResolvedLemma(byLink, "ecdict-link");
        if (Open.Exceptions.TryGetValue(q, out var excLemmas))
        {
            foreach (var lemma in excLemmas)
                if (HasAny(lemma))
                    return new ResolvedLemma(lemma, "exc");
        }
        foreach (var candidate in Morphy.GenerateCandidates(q))
            if (HasAny(candidate))
                return new ResolvedLemma(candidate, "morphy");
        // ECDICT 独有词（功能词等）：整词即词元
        if (Open.Ecdict.ContainsKey(q))
            return new ResolvedLemma(q, "direct");
        return null;
    }

    private bool HasAny(string word) =>
        Open.WordNet.ContainsKey(word)
        || Open.Ecdict.ContainsKey(word)
        || _oxford.GetEntry(word) is not null;

    // ---------- 合并与响应构建（§4.2 / §4.3） ----------

    private DictionaryLookupResponse? LookupCore(string q)
    {
        CoreLookupCountForTests++;
        var resolved = ResolveLemma(q);
        if (resolved is null) return null;
        var (lemma, via) = resolved;
        var oxEntry = _oxford.GetEntry(lemma);
        if (oxEntry is { SenseCount: > 0 })
            return BuildOxfordResponse(q, lemma, via, oxEntry);
        return BuildOpenResponse(q, lemma, via, oxEntry);
    }

    private DictionaryLookupResponse BuildOxfordResponse(string q, string lemma, string via, OxfordEntry entry)
    {
        var blocks = new List<PosBlockDto>();
        foreach (var block in entry.PosBlocks)
        {
            var senses = block.Senses.Select(s => new SenseDto
            {
                DefEn = s.DefEn,
                GlossZh = s.GlossZh,
                Example = s.Examples.Count > 0
                    ? new ExampleDto { En = s.Examples[0].En, Zh = s.Examples[0].Zh }
                    : null,
            }).ToList();
            blocks.Add(new PosBlockDto
            {
                Pos = NormalizePosName(block.Pos),
                Label = PosLabel(block.Pos),
                Senses = senses,
                FoldedCount = Math.Max(0, senses.Count - 2),
                ZhLines = [],
            });
        }

        // 票126 §9.2：牛津包词形 label 经映射表出（neg/short/rareshortform 等英文码不得透传）
        var forms = MergeForms(entry.Forms.Select(f => (FormLabelOf(f.Label), f.Text)));
        if (forms.Count == 0)
            forms = EcdictForms(lemma); // P1：开放层只补牛津缺失的词形

        var audio = BuildAudioMap(lemma, q);
        return new DictionaryLookupResponse
        {
            Query = q,
            Lemma = lemma,
            MatchedVia = via,
            Tier = "oxford",
            Phonetics = entry.Phonetics,
            PosBlocks = blocks,
            Forms = forms,
            Audio = audio,
            Attribution = new AttributionDto
            {
                Source = "Oxford Advanced Learner's Dictionary 10 (local pack)",
                AudioSource = AudioSourceNote(audio, selfKind: "oxford"),
            },
        };
    }

    private DictionaryLookupResponse BuildOpenResponse(string q, string lemma, string via, OxfordEntry? oxSupplement)
    {
        Open.Ecdict.TryGetValue(lemma, out var ecdict);
        var zhSegments = ParseZhSegments(ecdict?.Translation);
        var blocks = new List<PosBlockDto>();

        if (Open.WordNet.TryGetValue(lemma, out var wn))
        {
            // P2/P3：结构 = WordNet；ECDICT 中文按 P4 归组；ECDICT 英文 definition 不进义项
            var unmatchedZh = zhSegments.Where(s => s.PosKey is not null && !wn.PosBlocks.ContainsKey(s.PosKey)).ToList();
            var firstBlock = true;
            foreach (var pos in new[] { "n", "v", "a", "r" })
            {
                if (!wn.PosBlocks.TryGetValue(pos, out var synsets)) continue;
                var senses = synsets.Select(s => new SenseDto
                {
                    DefEn = s.Gloss,
                    Example = s.Example is null ? null : new ExampleDto { En = s.Example },
                }).ToList();
                var zh = zhSegments
                    .Where(s => firstBlock
                        ? s.PosKey is null || s.PosKey == pos || unmatchedZh.Contains(s)
                        : s.PosKey == pos)
                    .Select(s => s.Text).ToList();
                blocks.Add(new PosBlockDto
                {
                    Pos = NormalizePosName(pos),
                    Label = PosLabel(pos),
                    Senses = senses,
                    FoldedCount = Math.Max(0, senses.Count - 2),
                    ZhLines = zh.Count > 0 ? [string.Join("；", zh)] : [],
                });
                firstBlock = false;
            }
        }
        else
        {
            // 功能词等 WordNet 结构性不收的词：ECDICT 中文译义兜底（RESEARCH §9.1）
            var groups = new List<(string Pos, List<string> Lines)>();
            foreach (var seg in zhSegments)
            {
                var pos = seg.PosKey ?? (groups.Count > 0 ? groups[0].Pos : "");
                var group = groups.FirstOrDefault(g => g.Pos == pos);
                if (group.Lines is null)
                {
                    group = (pos, []);
                    groups.Add(group);
                }
                group.Lines.Add(seg.Text);
            }
            foreach (var (pos, lines) in groups)
            {
                blocks.Add(new PosBlockDto
                {
                    Pos = NormalizePosName(pos),
                    Label = PosLabel(pos),
                    Senses = [],
                    FoldedCount = 0,
                    ZhLines = [string.Join("；", lines)],
                });
            }
        }

        var audio = BuildAudioMap(lemma, q);
        return new DictionaryLookupResponse
        {
            Query = q,
            Lemma = lemma,
            MatchedVia = via,
            Tier = "open",
            Phonetics = OpenPhonetics(lemma, oxSupplement),
            PosBlocks = blocks,
            Forms = EcdictForms(lemma),
            Audio = audio,
            Attribution = new AttributionDto
            {
                Source = "WordNet · ECDICT · Wikimedia Commons",
                AudioSource = AudioSourceNote(audio, selfKind: "commons"),
            },
        };
    }

    // ---------- 音标（§9.5 优先序：牛津 > Kaikki 索引 > ECDICT 规范化） ----------

    private PhoneticsDto OpenPhonetics(string lemma, OxfordEntry? oxSupplement)
    {
        string? uk = oxSupplement?.Phonetics.Uk;
        string? us = oxSupplement?.Phonetics.Us;
        if (Open.AudioIndex.TryGetValue(lemma, out var recs))
        {
            uk ??= recs.Where(r => r.Accent == "uk" && !string.IsNullOrEmpty(r.Ipa))
                       .OrderBy(r => r.File.StartsWith("En-uk-", StringComparison.Ordinal) ? 0 : 1)
                       .Select(r => r.Ipa).FirstOrDefault();
            us ??= recs.Where(r => r.Accent == "us" && !string.IsNullOrEmpty(r.Ipa))
                       .OrderBy(r => r.File.StartsWith("En-us-", StringComparison.Ordinal) ? 0 : 1)
                       .Select(r => r.Ipa).FirstOrDefault();
        }
        if (uk is null && Open.Ecdict.TryGetValue(lemma, out var ecdict) && !string.IsNullOrWhiteSpace(ecdict.Phonetic))
            uk = NormalizeEcdictPhonetic(ecdict.Phonetic); // ECDICT 音标以英标为主 → UK 槽（§9.5 末位兜底）
        return new PhoneticsDto { Uk = uk, Us = us };
    }

    /// <summary>ECDICT 旧式 ASCII 音标规范化：去首尾点、' → ˈ、Cyrillic ә → ə、斜线包裹。</summary>
    public static string NormalizeEcdictPhonetic(string raw)
    {
        var s = raw.Trim().Trim('.').Replace('\'', '\u02C8').Replace('\u04D9', '\u0259');
        return s.Length == 0 ? "" : $"/{s}/";
    }

    // ---------- 词形（P6：由词元 exchange 生成；同文合并标签） ----------

    public List<FormDto> EcdictForms(string lemma)
    {
        if (!Open.Ecdict.TryGetValue(lemma, out var entry)) return [];
        var order = new[] { "p", "d", "3", "i", "s", "r", "t" };
        var labels = new Dictionary<string, string>
        {
            ["p"] = "过去式", ["d"] = "过去分词", ["3"] = "第三人称单数", ["i"] = "现在分词",
            ["s"] = "复数", ["r"] = "比较级", ["t"] = "最高级",
        };
        var raw = new List<(string Label, string Text)>();
        foreach (var code in order)
        {
            foreach (var (c, target) in entry.ExchangePairs)
                if (c == code && target != lemma)
                    raw.Add((labels[code], target));
        }
        return MergeForms(raw);
    }

    public static List<FormDto> MergeForms(IEnumerable<(string Label, string Text)> forms)
    {
        var merged = new List<FormDto>();
        foreach (var (label, text) in forms)
        {
            var existing = merged.FirstOrDefault(f => f.Text == text);
            if (existing is null)
                merged.Add(new FormDto { Label = label, Text = text });
            else if (!existing.Label.Split('/').Contains(label))
                merged[merged.IndexOf(existing)] = new FormDto { Label = $"{existing.Label}/{label}", Text = text };
        }
        return merged;
    }

    // ---------- 发音链（§2.1 逐级回落，响应标注实际命中层） ----------

    private Dictionary<string, AudioOptionDto> BuildAudioMap(string lemma, string queriedForm) => new()
    {
        ["us"] = ResolveAudio(lemma, queriedForm, "us"),
        ["uk"] = ResolveAudio(lemma, queriedForm, "uk"),
    };

    public AudioOptionDto ResolveAudio(string lemma, string queriedForm, string accent)
    {
        // 1) Tier 0 牛津录音（所点屈折形优先于词元）
        var entry = _oxford.GetEntry(lemma);
        if (entry is not null)
        {
            string? fileKey = null;
            if (queriedForm != lemma)
            {
                var form = entry.Forms.FirstOrDefault(f => f.Text == queriedForm);
                if (form is not null && form.AudioKey.TryGetValue(accent, out var fk))
                    fileKey = fk;
            }
            if (fileKey is null && entry.Audio.TryGetValue(accent, out var ek))
                fileKey = ek;
            if (fileKey is not null && _oxford.AudioFileExists(fileKey))
                return new AudioOptionDto { Kind = "oxford", Key = fileKey, Available = true };
        }
        // 2) Tier 1 核心包
        var core = _audio.ResolveCore(lemma, accent)
            ?? (queriedForm != lemma ? _audio.ResolveCore(queriedForm, accent) : null);
        if (core is not null)
            return new AudioOptionDto { Kind = "commons", Key = core.Value.Key, Available = true };
        // 3) Tier 1 懒缓存（audio-index 命中即可，端点负责拉取落盘）
        var rec = PickIndexRec(lemma, accent) ?? (queriedForm != lemma ? PickIndexRec(queriedForm, accent) : null);
        if (rec is not null)
            return new AudioOptionDto { Kind = "commons", Key = AudioResolver.Mp3FileName(rec), Available = true };
        // 票126 §8.1（S-2 关掉机器兜底）：链到此为止。
        // Tier 2 神经 TTS 配置项不动，但词典呈现不消费（呈现 kind 只余 oxford/commons）；
        // Tier 3 设备 TTS 整级移除。耗尽即无发音：Available=false、无 kind、无 key（§8.2）。
        return new AudioOptionDto { Kind = null, Key = null, Available = false };
    }

    private AudioFileRec? PickIndexRec(string word, string accent)
    {
        if (!Open.AudioIndex.TryGetValue(word, out var recs)) return null;
        return recs.Where(r => r.Accent == accent)
                   .OrderBy(r => r.File.StartsWith($"En-{accent}-", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                   .FirstOrDefault();
    }

    private static string? AudioSourceNote(Dictionary<string, AudioOptionDto> audio, string selfKind)
    {
        foreach (var accent in new[] { "us", "uk" })
        {
            if (audio.TryGetValue(accent, out var opt) && opt.Available && opt.Kind != selfKind)
            {
                return opt.Kind switch
                {
                    "oxford" => "Oxford Advanced Learner's Dictionary 10 (local pack)",
                    "commons" => "Wikimedia Commons",
                    _ => null,
                };
            }
        }
        return null;
    }

    // ---------- 中文分段（P4） ----------

    public sealed record ZhSegment(string? PosKey, string Text);

    private static readonly (string Prefix, string PosKey)[] ZhPosPrefixes =
    [
        ("vt.", "v"), ("vi.", "v"), ("v.", "v"), ("n.", "n"), ("adj.", "a"), ("a.", "a"),
        ("adv.", "r"), ("ad.", "r"), ("prep.", "preposition"), ("conj.", "conjunction"),
        ("pron.", "pronoun"), ("int.", "interjection"), ("aux.", "auxiliary verb"),
        ("art.", "article"), ("num.", "numeral"), ("s.", "a"), ("pl.", "n"),
    ];

    public static List<ZhSegment> ParseZhSegments(string? translation)
    {
        var output = new List<ZhSegment>();
        if (string.IsNullOrWhiteSpace(translation)) return output;
        foreach (var rawSeg in translation.Split(['\n', '；', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = rawSeg.Trim();
            if (seg.Length == 0) continue;
            string? posKey = null;
            foreach (var (prefix, key) in ZhPosPrefixes)
            {
                if (seg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    posKey = key;
                    seg = seg[prefix.Length..].Trim();
                    break;
                }
            }
            if (seg.Length > 0)
                output.Add(new ZhSegment(posKey, seg));
        }
        return output;
    }

    // ---------- 词性/词形标签（§9.2.4 学习者标签，源记号不直出） ----------

    public static string NormalizePosName(string pos) => pos switch
    {
        "n" => "noun",
        "v" => "verb",
        "a" => "adjective",
        "r" => "adverb",
        _ => pos.Trim().ToLowerInvariant(),
    };

    /// <summary>
    /// 词性标签映射（票126 §9.2 补全至实测可达全集）：单值标签逐个映射；
    /// 牛津组合标签（如 "adverb, adjective"）按分量逐个映射后以「/」拼合（同 MergeForms 口径）。
    /// 全集普查（2026-10-23 实现期收集，见 VERIFY-126 §9.2 清单）：Tier 0 牛津包 posBlocks.pos
    /// 全量枚举 + ECDICT 中文段 PosKey 集 + WordNet n/v/a/r——集合内标签必须有映射值、不得透传。
    /// </summary>
    private static readonly Dictionary<string, string> PosLabels = new()
    {
        ["noun"] = "名词",
        ["verb"] = "动词",
        ["adjective"] = "形容词",
        ["adverb"] = "副词",
        ["preposition"] = "介词",
        ["conjunction"] = "连词",
        ["pronoun"] = "代词",
        ["determiner"] = "限定词",
        ["interjection"] = "感叹词",
        ["exclamation"] = "感叹词",
        ["auxiliary verb"] = "助动词",
        ["modal verb"] = "情态动词",
        ["linking verb"] = "系动词",
        ["article"] = "冠词",
        ["definite article"] = "定冠词",
        ["indefinite article"] = "不定冠词",
        ["numeral"] = "数词",
        ["number"] = "数词",
        ["ordinal number"] = "序数词",
        ["phrasal verb"] = "短语动词",
        ["abbreviation"] = "缩略词",
        ["short form"] = "缩略形式",
        ["symbol"] = "符号",
        ["prefix"] = "前缀",
        ["suffix"] = "后缀",
        ["combining form"] = "构词成分",
        ["infinitive marker"] = "不定式标记",
    };

    public static string PosLabel(string pos)
    {
        var name = NormalizePosName(pos);
        if (name.Length == 0) return "";
        var parts = name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join("/", parts.Select(p => PosLabels.TryGetValue(p, out var label) ? label : p));
    }

    /// <summary>
    /// 词形标签映射（票126 §9.2）：牛津包词形 label 多为中文现值（恒等登记入表，作值域单一事实源），
    /// 漏网英文码 neg/short/rareshortform 在此补映射；ECDICT exchange 映射值（过去式等）同表登记。
    /// </summary>
    private static readonly Dictionary<string, string> FormLabels = new()
    {
        ["原形"] = "原形",
        ["第三人称单数"] = "第三人称单数",
        ["过去式"] = "过去式",
        ["过去分词"] = "过去分词",
        ["现在分词"] = "现在分词",
        ["复数"] = "复数",
        ["比较级"] = "比较级",
        ["最高级"] = "最高级",
        ["neg"] = "否定形式",
        ["short"] = "缩略形式",
        ["rareshortform"] = "罕用缩略形式",
    };

    public static string FormLabelOf(string label) =>
        FormLabels.TryGetValue(label, out var mapped) ? mapped : label;

    /// <summary>标签映射值域（TB-2 断言用）：单个标签值必须落在此集合内。</summary>
    public static IReadOnlySet<string> LabelValueDomain { get; } =
        PosLabels.Values.Concat(FormLabels.Values).Append("").ToHashSet();

    /// <summary>
    /// 响应 label 的值域判定（TB-2）：label 可为「/」拼合的合并标签（MergeForms 与组合词性），
    /// 每个分量都必须是映射值才算落域——拼合本身合法，分量透传不合法。
    /// </summary>
    public static bool LabelIsInDomain(string label) =>
        label.Split('/').All(part => LabelValueDomain.Contains(part));
}
