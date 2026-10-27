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
    /// 发音链单点解析（票254 §4，取代票126 S-2 与票128 §7 的 pronounce 语义）：
    /// 只供卡内播放入口；默认 Main 面（本形真人层 + 带标注合成层），
    /// humanOnly=true 时为 Shadow 面（本形→词元真人层 1–6，绝不合成）。
    /// excludeKeys：逗号分隔的本次请求排除键（§4.2 回落重试用；仅当次请求有效；
    /// 合成键永不可排除）。链耗尽 → Available=false（无 kind、无 key），端点仍回 200。
    /// </summary>
    public Task<PronounceResponse> PronounceAsync(string word, string accent, CancellationToken ct,
        string? excludeKeys = null, bool humanOnly = false)
    {
        var q = NormalizeQuery(word);
        if (q.Length == 0) return Task.FromResult(new PronounceResponse { Available = false });
        accent = accent == "uk" ? "uk" : "us";
        var lemma = ResolveLemma(q)?.Lemma ?? q;
        var option = ResolvePlayable(q, lemma, accent,
            humanOnly ? DictionaryAudioSurface.Shadow : DictionaryAudioSurface.Main,
            ParseExcludeKeys(excludeKeys));
        return Task.FromResult(option is { Available: true, Key: not null }
            ? new PronounceResponse { Kind = option.Kind, Key = option.Key, Available = true }
            : new PronounceResponse { Available = false });
    }

    private static IReadOnlySet<string>? ParseExcludeKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var set = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        return set.Count == 0 ? null : set;
    }

    // ---------- 归一化（§4.1，顺序固定） ----------

    public static string NormalizeQuery(string? word) =>
        (word ?? "").Trim().ToLowerInvariant();

    public sealed record ResolvedLemma(string Lemma, string MatchedVia);

    /// <summary>
    /// 票254 P4：归元候选全表（去重、保序）。阶段顺序与旧 ResolveLemma 的首命中顺序逐字一致，
    /// 因此 ResolveLemma = 首候选，行为不变；中文回落链（开放路、牛津补丁）按此表顺序取首个
    /// 有 ECDICT 译义的候选。连字符内层候选统一记 hyphen-head（与旧 via 口径一致）。
    /// </summary>
    internal IReadOnlyList<(string Word, string Via)> ResolveLemmaCandidates(string q)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<(string Word, string Via)>();
        void Add(string? word, string via)
        {
            if (!string.IsNullOrEmpty(word) && seen.Add(word))
                list.Add((word, via));
        }
        // 第 3 步的前置：Tier 0 在席先查牛津桩（牛津自带归元）
        if (_oxford.IsPresent)
        {
            var target = _oxford.RedirectTarget(q);
            if (target is not null && target != q && HasAny(target))
                Add(target, "redirect");
        }
        // 第 1 步：整词键（结构层直命中）
        if (Open.WordNet.ContainsKey(q) || _oxford.GetEntry(q) is not null)
            Add(q, "direct");
        // 第 2 步：连字符复合 → 首段重试
        var hyphen = q.IndexOf('-');
        if (hyphen > 0)
            foreach (var (inner, _) in ResolveLemmaCandidates(q[..hyphen]))
                Add(inner, "hyphen-head");
        // 第 3 步：屈折归元（ECDICT 0: 链接 → WordNet .exc → Morphy 规则）
        if (Open.LemmaByForm.TryGetValue(q, out var byLink) && HasAny(byLink))
            Add(byLink, "ecdict-link");
        if (Open.Exceptions.TryGetValue(q, out var excLemmas))
            foreach (var lemma in excLemmas)
                if (HasAny(lemma))
                    Add(lemma, "exc");
        foreach (var candidate in Morphy.GenerateCandidates(q))
            if (HasAny(candidate))
                Add(candidate, "morphy");
        // ECDICT 独有词（功能词等）：整词即词元（旧流程仅在前面全落空时到此步，
        // 故只在表仍空时补——否则会抢占 morphy 等更早阶段的首命中）
        if (list.Count == 0 && Open.Ecdict.ContainsKey(q))
            Add(q, "direct");
        return list;
    }

    public ResolvedLemma? ResolveLemma(string q)
    {
        var candidates = ResolveLemmaCandidates(q);
        return candidates.Count == 0
            ? null
            : new ResolvedLemma(candidates[0].Word, candidates[0].Via);
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
        var response = oxEntry is { SenseCount: > 0 }
            ? BuildOxfordResponse(q, lemma, via, oxEntry)
            : BuildOpenResponse(q, lemma, via, oxEntry);
        // 票128 §5.2：逐词形音频可用性的唯一挂接点（牛津路与开放路共用），返回前统一回填。
        AttachFormAudio(response);
        return response;
    }

    private DictionaryLookupResponse BuildOxfordResponse(string q, string lemma, string via, OxfordEntry entry)
    {
        // 票254 P4/P6：中文回落与牛津补丁共用归元候选表。
        var candidates = ResolveLemmaCandidates(q).Select(c => c.Word).ToList();
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
            // P6：任一义项缺中文才补；补丁只取该词性段，逐义项 GlossZh 永不改写、不复制进 ZhLines。
            var zhLines = senses.Any(s => string.IsNullOrWhiteSpace(s.GlossZh))
                ? SupplementZhLines(NormalizePosName(block.Pos), candidates)
                : new List<string>();
            blocks.Add(new PosBlockDto
            {
                Pos = NormalizePosName(block.Pos),
                Label = PosLabel(block.Pos),
                Senses = senses,
                FoldedCount = Math.Max(0, senses.Count - 2),
                ZhLines = zhLines,
            });
        }

        // 票254 P6：词形标签并集——牛津标签与 ECDICT exchange 标签同文合并（/ 拼合），
        // 取代旧「牛津有则全用牛津、无则全用 ECDICT」的二选一（purchases 曾只标第三人称单数）。
        // 票126 §9.2：牛津包词形 label 经映射表出（neg/short/rareshortform 等英文码不得透传）。
        var forms = MergeForms(entry.Forms.Select(f => (FormLabelOf(f.Label), f.Text))
            .Concat(EcdictFormPairs(lemma)));

        var audio = BuildAudioMap(lemma, q);
        // 票254 P6 归因：牛津路此前从不产 ZhLines，非空 ⟺ 本票补丁产出，故可据此推导来源行；
        // 未补丁的牛津卡来源行保持原样，不新增 DTO 字段。
        var supplemented = blocks.Any(b => b.ZhLines.Count > 0);
        return new DictionaryLookupResponse
        {
            Query = q,
            Lemma = lemma,
            MatchedVia = via,
            Tier = "oxford",
            Phonetics = entry.Phonetics,
            FormPhonetics = ResolveFormPhonetics(q, lemma, entry),
            PosBlocks = blocks,
            Forms = forms,
            Audio = audio,
            Attribution = new AttributionDto
            {
                Source = supplemented
                    ? "Oxford Advanced Learner's Dictionary 10 (local pack) · ECDICT"
                    : "Oxford Advanced Learner's Dictionary 10 (local pack)",
                AudioSource = AudioSourceNote(audio, selfKind: "oxford"),
            },
        };
    }

    /// <summary>票254 P6：取首个有该词性中文段的候选的 ECDICT 译义段（仅该词性）；全无 → 空（不补）。</summary>
    private List<string> SupplementZhLines(string normalizedPos, IReadOnlyList<string> candidates)
    {
        var posKey = normalizedPos switch
        {
            "noun" => "n", "verb" => "v", "adjective" => "a", "adverb" => "r",
            _ => normalizedPos,
        };
        foreach (var cand in candidates)
        {
            if (!Open.Ecdict.TryGetValue(cand, out var e)) continue;
            var segs = ParseZhSegments(e.Translation)
                .Where(s => s.PosKey == posKey).Select(s => s.Text).ToList();
            if (segs.Count > 0) return [string.Join("；", segs)];
        }
        return [];
    }

    /// <summary>
    /// 票254 P3：词形本形音标。词元卡（q == lemma）返回 null（前端用词元音标原样）。
    /// 来源序：牛津词形 Phon → audio-index 该形 Ipa（与 OpenPhonetics 同口音偏好）；
    /// ECDICT phonetic 永不用（它属于词条词，不属于所点词形）。
    /// </summary>
    private PhoneticsDto? ResolveFormPhonetics(string q, string lemma, OxfordEntry entry)
    {
        if (q == lemma) return null;
        var form = entry.Forms.FirstOrDefault(f => f.Text == q);
        string? uk = form is null ? null : PhonOf(form, "uk");
        string? us = form is null ? null : PhonOf(form, "us");
        if (Open.AudioIndex.TryGetValue(q, out var recs))
        {
            uk ??= recs.Where(r => r.Accent == "uk" && !string.IsNullOrEmpty(r.Ipa))
                       .OrderBy(r => r.File.StartsWith("En-uk-", StringComparison.Ordinal) ? 0 : 1)
                       .Select(r => r.Ipa).FirstOrDefault();
            us ??= recs.Where(r => r.Accent == "us" && !string.IsNullOrEmpty(r.Ipa))
                       .OrderBy(r => r.File.StartsWith("En-us-", StringComparison.Ordinal) ? 0 : 1)
                       .Select(r => r.Ipa).FirstOrDefault();
        }
        return uk is null && us is null ? null : new PhoneticsDto { Uk = uk, Us = us };

        static string? PhonOf(OxfordForm f, string accent)
        {
            if (f.Phon.TryGetValue(accent, out var p) && !string.IsNullOrWhiteSpace(p)) return p;
            var kv = f.Phon.FirstOrDefault(kv => string.Equals(kv.Key, accent, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value;
        }
    }

    private DictionaryLookupResponse BuildOpenResponse(string q, string lemma, string via, OxfordEntry? oxSupplement)
    {
        Open.Ecdict.TryGetValue(lemma, out var ecdict);
        var zhSegments = ParseZhSegments(ecdict?.Translation);
        // 票254 P4：词元译义为空时按归元候选表顺序取首个有译义的候选（常见于词元本身为屈折形）；
        // 仍全空则后续分组自然为空，不虚构。现有 POS 归组逻辑对替换后的段表原样生效。
        if (zhSegments.Count == 0)
        {
            foreach (var (cand, _) in ResolveLemmaCandidates(q))
            {
                if (cand == lemma) continue;
                if (!Open.Ecdict.TryGetValue(cand, out var alt)) continue;
                var altSegs = ParseZhSegments(alt.Translation);
                if (altSegs.Count > 0) { zhSegments = altSegs; break; }
            }
        }
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

    /// <summary>票254 P6：ECDICT exchange 原始词形对（未合并），供牛津标签并集与开放路共用。</summary>
    public List<(string Label, string Text)> EcdictFormPairs(string lemma)
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
        return raw;
    }

    public List<FormDto> EcdictForms(string lemma) => MergeForms(EcdictFormPairs(lemma));

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
        ["us"] = ResolvePlayable(queriedForm, lemma, "us", DictionaryAudioSurface.Main),
        ["uk"] = ResolvePlayable(queriedForm, lemma, "uk", DictionaryAudioSurface.Main),
    };

    /// <summary>旧公开名保留：现为 Main 面解析（票254 D1 取代词元回落；词元回落只在 Shadow 面）。</summary>
    public AudioOptionDto ResolveAudio(string lemma, string queriedForm, string accent) =>
        ResolvePlayable(queriedForm, lemma, accent, DictionaryAudioSurface.Main);

    /// <summary>
    /// 票254 §1：发音解析面。调用方选面，服务端绝不从词形推断面——
    /// Main（卡面主按钮 + pronounce 默认）：本形真人层 1–3 + 第 7 层带标注合成（仅本形合成）；
    /// Shadow（跟读）：真人层 1–6（本形三层 + 词元三层），永不合成。
    /// </summary>
    public enum DictionaryAudioSurface { Main, Shadow }

    /// <summary>
    /// 票254 §1/§4：发音解析唯一入口（D2）。excludeKeys：本次请求排除的人声键，逐层适用；
    /// 合成键永不可排除（第 7 层不查排除集）。验收反例（panel 绑定）：本形无人声、
    /// 词元有人声时，Main 必须返回合成键，绝不返回词元键（以测试证明）。
    /// </summary>
    public AudioOptionDto ResolvePlayable(string queriedForm, string lemma, string accent,
        DictionaryAudioSurface surface, IReadOnlySet<string>? excludeKeys = null)
    {
        bool Excluded(string? key) => key is not null && excludeKeys is not null && excludeKeys.Contains(key);
        // q == lemma 时牛津词头录音即本形录音（投影段只查 Forms，不含词头，须先查）；
        // q != lemma 时词头录音不属于本形，绝不查（票128 D2 在 Main 面同样适用）。
        if (queriedForm == lemma)
        {
            var head = _oxford.GetEntry(lemma);
            if (head is not null && head.Audio.TryGetValue(accent, out var hk)
                && _oxford.AudioFileExists(hk) && !Excluded(hk))
                return new AudioOptionDto { Kind = "oxford", Key = hk, Available = true };
        }
        // 1–3) 本形真人层（票128 投影段，逐层适用排除）
        var form = ResolveFormProjection(lemma, queriedForm, accent, excludeKeys);
        if (form is not null)
            return form;
        if (surface == DictionaryAudioSurface.Main)
        {
            // 7) 带标注合成（本形合成；能力广告见 AudioResolver.IsSynthesisAvailable——
            // 查找期不调合成、不查密钥；物化只在音频端点 serve-time 发生）
            if (_audio.IsSynthesisAvailable)
                return new AudioOptionDto
                {
                    Kind = "synthetic",
                    Key = AudioResolver.SyntheticKeyFor(queriedForm, accent),
                    Available = true,
                };
            return new AudioOptionDto { Kind = null, Key = null, Available = false };
        }
        // Shadow 4–6) 词元真人层（逐层适用排除；永不合成）
        var entry = _oxford.GetEntry(lemma);
        if (entry is not null && entry.Audio.TryGetValue(accent, out var ek)
            && _oxford.AudioFileExists(ek) && !Excluded(ek))
            return new AudioOptionDto { Kind = "oxford", Key = ek, Available = true };
        var core = _audio.ResolveCore(lemma, accent);
        if (core is not null && !Excluded(core.Value.Key))
            return new AudioOptionDto { Kind = "commons", Key = core.Value.Key, Available = true };
        var rec = PickIndexRec(lemma, accent);
        if (rec is not null)
        {
            var indexKey = AudioResolver.Mp3FileName(rec);
            if (!Excluded(indexKey))
                return new AudioOptionDto { Kind = "commons", Key = indexKey, Available = true };
        }
        return new AudioOptionDto { Kind = null, Key = null, Available = false };
    }

    private AudioFileRec? PickIndexRec(string word, string accent)
    {
        if (!Open.AudioIndex.TryGetValue(word, out var recs)) return null;
        return recs.Where(r => r.Accent == accent)
                   .OrderBy(r => r.File.StartsWith($"En-{accent}-", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                   .FirstOrDefault();
    }

    // ---------- 票128 逐词形可用性判定（§4）与本形投影段（§7） ----------

    /// <summary>
    /// 本形投影段（§7 第 1 段 = §4.1 判定链本体）：专查词形 F 自己的真人录音，层序与条件与判定逐字同构——
    /// ① Tier 0 本形音键在且文件实存（词元词头 entry.Audio 不在此段出现，不抢占）；
    /// ② 核心包本形；③ audio-index 本形记录（懒层算命中，§4.2）。
    /// 判定（FormAudioAvailable）与 pronounce 投影段共用此实现，保证「判定命中层 ≡ 投影命中层」（§4.3），
    /// 不许平行再写一份。命中 → 该条录音的呈现；全落空 → null。
    /// 全程只读内存结构与文件存在性，零网络（§4.4）。
    /// </summary>
    private AudioOptionDto? ResolveFormProjection(string lemma, string formText, string accent,
        IReadOnlySet<string>? excludeKeys = null)
    {
        bool Excluded(string? key) => key is not null && excludeKeys is not null && excludeKeys.Contains(key);
        var entry = _oxford.GetEntry(lemma);
        if (entry is not null)
        {
            var form = entry.Forms.FirstOrDefault(f => f.Text == formText);
            if (form is not null && form.AudioKey.TryGetValue(accent, out var fk) && _oxford.AudioFileExists(fk)
                && !Excluded(fk))
                return new AudioOptionDto { Kind = "oxford", Key = fk, Available = true };
        }
        var core = _audio.ResolveCore(formText, accent);
        if (core is not null && !Excluded(core.Value.Key))
            return new AudioOptionDto { Kind = "commons", Key = core.Value.Key, Available = true };
        var rec = PickIndexRec(formText, accent);
        if (rec is not null)
        {
            var key = AudioResolver.Mp3FileName(rec);
            if (!Excluded(key))
                return new AudioOptionDto { Kind = "commons", Key = key, Available = true };
        }
        return null;
    }

    /// <summary>
    /// TB-6 注入接缝（§4.5 最小接缝）：测试可对单个词形给出强制结论（返回 null = 走真判定）或抛异常；
    /// 生产恒为 null。接缝只替换结论来源，异常隔离仍由 AttachFormAudio 的逐词形 try 承担。
    /// </summary>
    internal Func<string, string, string, bool?>? FormAudioAvailableOverrideForTests;

    /// <summary>
    /// 词形 F 在口音 A 下是否有它自己的真人录音（§4.1）：= 本形投影段是否命中。
    /// 明示排除（D2）：不查词元的任何录音、不查 F 自身作为词头词条的录音、不查懒缓存落盘文件。
    /// </summary>
    public bool FormAudioAvailable(string lemma, string formText, string accent)
    {
        if (FormAudioAvailableOverrideForTests?.Invoke(lemma, formText, accent) is bool forced)
            return forced;
        return ResolveFormProjection(lemma, formText, accent) is not null;
    }

    /// <summary>
    /// §5.2 唯一挂接点：对响应每个词形以 (Lemma, form.Text) 判定并回填 Audio。
    /// 逐词形异常隔离（§4.5）：单个词形判定抛错 → 该形 false/false，其余词形与 Lookup 整体不受影响。
    /// </summary>
    private void AttachFormAudio(DictionaryLookupResponse response)
    {
        foreach (var form in response.Forms)
        {
            try
            {
                form.Audio = new FormAudioDto
                {
                    Us = FormAudioAvailable(response.Lemma, form.Text, "us"),
                    Uk = FormAudioAvailable(response.Lemma, form.Text, "uk"),
                };
            }
            catch (Exception)
            {
                // 判定只读内存结构本不应抛错；万一抛错按「无录音」诚实降级该词形（F-S2），不放大到整卡。
                form.Audio = new FormAudioDto { Us = false, Uk = false };
            }
        }
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
                    "synthetic" => "AI 朗读",
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
        // 显式 char[]：集合表达式 `['\n','；',';']` 在 .NET 8 下同时匹配
        // Split(char[], StringSplitOptions) 与 Split(string[], StringSplitOptions)
        // 两个重载 → 编译歧义 CS0121。显式转型为 char[] 消歧,行为不变。
        foreach (var rawSeg in translation.Split(new[] { '\n', '；', ';' }, StringSplitOptions.RemoveEmptyEntries))
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
