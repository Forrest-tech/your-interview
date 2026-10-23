using System.Text.Json;
using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票126 后端验收（SPEC §11.1 TB-1/TB-2/TB-3）：
/// TB-1 发音呈现只认真人录音、链耗尽语义；TB-2 标签映射全集无透传；TB-3 归元标注数据侧。
/// </summary>
public class DictionaryTicket126Tests
{
    private static readonly Lazy<OpenLayerStore> RealStore = new(OpenLayerStore.LoadFromEmbeddedResources);

    // ---------- TB-1（S-2①）：假层构造链耗尽/命中，kind 永不为 device/neural ----------

    private static OpenLayerStore StoreWithIndexAudio() => BuildOpenStore(
        EcdictTsv(
            EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped/d:glorped"),
            EcRow("quieton", "", "n. silent thing", "n. 静默物", "")),
        WordNetJsonl(
            WnEntry("glorp", ("v", [("to disappear suddenly", null)])),
            WnEntry("quieton", ("n", [("a silent thing", null)]))),
        audioIndexJson: "{\"glorp\": [{\"file\": \"En-us-glorp.ogg\", \"accent\": \"us\", \"license\": \"CC BY-SA 4.0\", \"artist\": \"Synth\", \"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-us-glorp.ogg/En-us-glorp.ogg.mp3\"}]}");

    [Fact]
    public void TB1_ChainExhausted_PronounceReturnsAvailableFalse_NoKindNoKey()
    {
        var svc = BuildService(StoreWithIndexAudio(), packDir: null).Service;
        var pron = svc.PronounceAsync("quieton", "us", CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(pron.Available);
        Assert.Null(pron.Kind);
        Assert.Null(pron.Key);
        // 未命中词同口径（负卡不调 pronounce，此为端点契约本身的钉子）
        var miss = svc.PronounceAsync("zorblax", "uk", CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(miss.Available);
        Assert.Null(miss.Kind);
    }

    [Fact]
    public void TB1_RealRecordingHits_KindOnlyOxfordOrCommons()
    {
        var svc = BuildService(StoreWithIndexAudio(), packDir: null).Service;
        var pron = svc.PronounceAsync("glorp", "us", CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(pron.Available);
        Assert.Equal("commons", pron.Kind);
        Assert.NotNull(pron.Key);
        var card = svc.Lookup("glorp")!;
        Assert.All(card.Audio.Values, p =>
            Assert.True(p.Kind is null or "oxford" or "commons",
                $"呈现 kind 只许 oxford/commons/null，实得 {p.Kind}"));
        var quiet = svc.Lookup("quieton")!;
        Assert.All(quiet.Audio.Values, p =>
        {
            Assert.Null(p.Kind);
            Assert.False(p.Available);
        });
    }

    // ---------- TB-2（F-5②）：TST-1 语料全量标签落映射值域 + 冻结种子清单（M-4） ----------

    private static List<string> Tst1Words()
    {
        var asm = typeof(DictionaryTicket126Tests).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("zh-snapshot.json", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(asm.GetManifestResourceStream(name)!);
        return doc.RootElement.EnumerateArray().Select(r => r.GetProperty("query").GetString()!).ToList();
    }

    [Fact]
    public void TB2_Tst1Corpus_AllLabels_InMappingDomain()
    {
        var svc = BuildService(RealStore.Value, packDir: null).Service;
        var labels = new HashSet<string>();
        foreach (var word in Tst1Words())
        {
            var r = svc.Lookup(word);
            if (r is null) continue;
            foreach (var b in r.PosBlocks) labels.Add(b.Label);
            foreach (var f in r.Forms) labels.Add(f.Label);
        }
        Assert.NotEmpty(labels);
        foreach (var label in labels)
            Assert.True(DictionaryService.LabelIsInDomain(label),
                $"标签「{label}」不在映射值域内（英文术语透传）");
        // 实证回归：the 的词性标签必须是映射值，不得是 "definite article" 原文
        var the = svc.Lookup("the");
        Assert.NotNull(the);
        Assert.All(the!.PosBlocks, b => Assert.NotEqual("definite article", b.Label));
    }

    /// <summary>
    /// M-4 冻结种子清单下限（2026-10-23 普查冻结，全集在此之上扩展）：
    /// Tier 0 牛津包 posBlocks.pos 全量枚举的英文原始标签（含组合与实证 definite article），
    /// 每个原始标签经 PosLabel 必须落映射值域且不得与原文相同（"" 除外，映射为 ""）。
    /// </summary>
    [Theory]
    [InlineData("noun")]
    [InlineData("verb")]
    [InlineData("adjective")]
    [InlineData("adverb")]
    [InlineData("preposition")]
    [InlineData("conjunction")]
    [InlineData("pronoun")]
    [InlineData("determiner")]
    [InlineData("interjection")]
    [InlineData("exclamation")]
    [InlineData("auxiliary verb")]
    [InlineData("modal verb")]
    [InlineData("linking verb")]
    [InlineData("article")]
    [InlineData("definite article")]
    [InlineData("indefinite article")]
    [InlineData("numeral")]
    [InlineData("number")]
    [InlineData("ordinal number")]
    [InlineData("phrasal verb")]
    [InlineData("abbreviation")]
    [InlineData("short form")]
    [InlineData("symbol")]
    [InlineData("prefix")]
    [InlineData("suffix")]
    [InlineData("combining form")]
    [InlineData("infinitive marker")]
    [InlineData("adverb, adjective")]
    [InlineData("noun, verb")]
    [InlineData("determiner, pronoun")]
    [InlineData("preposition, conjunction, adverb")]
    [InlineData("n")]
    [InlineData("v")]
    [InlineData("a")]
    [InlineData("r")]
    public void TB2_FrozenSeedPosLabels_AllMapped_NeverPassthrough(string raw)
    {
        var mapped = DictionaryService.PosLabel(raw);
        Assert.True(DictionaryService.LabelIsInDomain(mapped),
            $"种子标签「{raw}」映射值「{mapped}」不在值域内");
        Assert.NotEqual(raw, mapped);
        Assert.Equal("", DictionaryService.PosLabel(""));
    }

    /// <summary>种子清单（词形）：牛津包英文码必须映射；中文现值恒等登记、必落值域。</summary>
    [Theory]
    [InlineData("neg", "否定形式")]
    [InlineData("short", "缩略形式")]
    [InlineData("rareshortform", "罕用缩略形式")]
    [InlineData("原形", "原形")]
    [InlineData("第三人称单数", "第三人称单数")]
    [InlineData("过去式", "过去式")]
    [InlineData("过去分词", "过去分词")]
    [InlineData("现在分词", "现在分词")]
    [InlineData("复数", "复数")]
    [InlineData("比较级", "比较级")]
    [InlineData("最高级", "最高级")]
    public void TB2_FrozenSeedFormLabels_Mapped(string raw, string expected)
    {
        Assert.Equal(expected, DictionaryService.FormLabelOf(raw));
        Assert.True(DictionaryService.LabelIsInDomain(expected));
    }

    // ---------- TB-3（F-5①，后端侧）：forms 含所查词形条目且带角色 label ----------

    [Fact]
    public void TB3_Disappeared_FormsCarryRoleLabels_ForQueriedForm()
    {
        var svc = BuildService(RealStore.Value, packDir: null).Service;
        var r = svc.Lookup("disappeared");
        Assert.NotNull(r);
        var form = Assert.Single(r!.Forms, f => f.Text == "disappeared");
        Assert.Contains("过去式", form.Label);
        Assert.Contains("过去分词", form.Label);
    }
}
