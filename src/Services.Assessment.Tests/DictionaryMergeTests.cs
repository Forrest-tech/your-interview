using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>票119 合并优先 P1–P6（SPEC §4.2）逐条断言 + 中文归组 P4 + 词形/音标规则。</summary>
public class DictionaryMergeTests
{
    // glorp: 词元;glorped: 其过去式。WordNet 有动词 3 条 synset + 名词 1 条;
    // ECDICT translation 分段:动词两段(一段无码)、名词一段、外加一个 prep 码段(结构层无此组)。
    private static OpenLayerStore Store() => BuildOpenStore(
        EcdictTsv(
            EcRow("glorp", "'ɡlɔːp", "v. ECDICT ENGLISH MUST NOT APPEAR", "突然消失；v. 消失不见；n. 虚构之物；prep. 某介词义", "p:glorped/d:glorped/3:glorps/i:glorping"),
            EcRow("glorped", "", "", "v. 突然消失（过去式）", "0:glorp/1:pd")),
        WordNetJsonl(
            WnEntry("glorp",
                ("v", [("to disappear suddenly", "It glorped into the fog."), ("to cease to exist", null), ("to be lost", null)]),
                ("n", [("a fictional thing", null)]))),
        audioIndexJson: "{\"glorp\": [{\"file\": \"En-us-glorp.ogg\", \"accent\": \"us\", \"ipa\": \"/ɡlɔːrp/\", \"license\": \"CC0\", \"artist\": \"Synthetic\", \"mp3Url\": \"https://example.invalid/En-us-glorp.ogg.mp3\"}]}");

    private const string OxfordGlorpJson = """
        {"headword": "glorp",
         "phonetics": {"uk": "/ˈɡlɔːp/", "us": "/ˈɡlɔːrp/"},
         "posBlocks": [{"pos": "verb", "senses": [
           {"defEn": "OXFORD SENSE ONE", "glossZh": "牛津义一", "examples": [{"en": "Oxford example.", "zh": "牛津例句。"}]},
           {"defEn": "OXFORD SENSE TWO"},
           {"defEn": "OXFORD SENSE THREE"}]}],
         "forms": [{"label": "过去式", "text": "glorped"}, {"label": "过去分词", "text": "glorped"}],
         "audio": {"uk": "glorp__gb_1.mp3", "us": "glorp__us_1.mp3"},
         "oxford3000": false}
        """;

    private static string OxfordPack(string root, bool withAudio) => BuildOxfordPack(root,
        [("glorp", OxfordGlorpJson)],
        new Dictionary<string, string> { ["glorped"] = "glorp" },
        audioArchivePresent: withAudio,
        audioFiles: withAudio ? ["glorp__gb_1.mp3", "glorp__us_1.mp3"] : null);

    [Fact]
    public void P1_OxfordHit_UsesOxfordOnly_OpenLayerNeverMixed()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = BuildService(Store(), OxfordPack(root, withAudio: false)).Service;
            var r = svc.Lookup("glorp");
            Assert.NotNull(r);
            Assert.Equal("oxford", r!.Tier);
            var block = Assert.Single(r.PosBlocks);
            Assert.Equal("verb", block.Pos);
            Assert.Equal(3, block.Senses.Count);
            Assert.Equal("OXFORD SENSE ONE", block.Senses[0].DefEn);
            Assert.Equal("牛津义一", block.Senses[0].GlossZh);
            Assert.Equal("Oxford example.", block.Senses[0].Example?.En);
            Assert.Equal(1, block.FoldedCount); // 3 条,呈现上限 2 → 折叠 1
            Assert.Empty(block.ZhLines); // 牛津中文随义项,不走 ECDICT 中文行
            Assert.Equal("/ˈɡlɔːp/", r.Phonetics.Uk);
            Assert.Contains("Oxford", r.Attribution.Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void P1_OxfordFormsWin_AndSameTextLabelsMerge()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = BuildService(Store(), OxfordPack(root, withAudio: false)).Service;
            var r = svc.Lookup("glorp")!;
            var form = Assert.Single(r.Forms);
            Assert.Equal("glorped", form.Text);
            Assert.Equal("过去式/过去分词", form.Label);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void P2_OpenLayer_WordNetStructure_EcdictEnglishExcluded()
    {
        var svc = BuildService(Store(), packDir: null).Service;
        var r = svc.Lookup("glorp");
        Assert.NotNull(r);
        Assert.Equal("open", r!.Tier);
        Assert.Equal(2, r.PosBlocks.Count);
        var verb = r.PosBlocks.First(b => b.Pos == "verb");
        Assert.Equal("动词", verb.Label);
        Assert.Equal("to disappear suddenly", verb.Senses[0].DefEn);
        Assert.Equal("It glorped into the fog.", verb.Senses[0].Example?.En);
        Assert.DoesNotContain(r.PosBlocks.SelectMany(b => b.Senses), s => s.DefEn.Contains("ECDICT"));
        Assert.Equal(1, verb.FoldedCount);
    }

    [Fact]
    public void P3_InflectedForm_StructureComesFromLemmaPos_NotFormEntry()
    {
        // glorped 的 ECDICT 词条自带 v. 标记也好、无也好,结构恒从词元 glorp 的 WordNet 分组读
        var svc = BuildService(Store(), packDir: null).Service;
        var r = svc.Lookup("glorped")!;
        Assert.Equal("glorp", r.Lemma);
        Assert.Contains(r.PosBlocks, b => b.Pos == "verb");
        // 词形行含 glorped 且标注合并标签(过去式/过去分词) —— §8 状态 C 口径
        var form = r.Forms.First(f => f.Text == "glorped");
        Assert.Equal("过去式/过去分词", form.Label);
    }

    [Fact]
    public void P4_ZhSegments_GroupedByPosCode_UncodedToFirstBlock_UnmatchedAppended()
    {
        var svc = BuildService(Store(), packDir: null).Service;
        var r = svc.Lookup("glorp")!;
        var verb = r.PosBlocks.First(b => b.Pos == "verb");
        Assert.Equal("消失不见", Assert.Single(verb.ZhLines));
        // 第一组 = 响应首个词性组(名词组):收 n. 段 + 无码段 + 结构层无组的 prep 段,保持原段序
        var noun = r.PosBlocks[0];
        Assert.Equal("noun", noun.Pos);
        Assert.Equal("突然消失；虚构之物；某介词义", Assert.Single(noun.ZhLines));
    }

    [Fact]
    public void P5_Examples_StayWithTheirSynset()
    {
        var svc = BuildService(Store(), packDir: null).Service;
        var r = svc.Lookup("glorp")!;
        var verb = r.PosBlocks.First(b => b.Pos == "verb");
        Assert.Equal("It glorped into the fog.", verb.Senses[0].Example?.En);
        Assert.Null(verb.Senses[1].Example); // 无例句义项不借用
    }

    [Fact]
    public void PhoneticsPriority_KaikkiIpaBeatsEcdict_EcdictNormalized()
    {
        var svc = BuildService(Store(), packDir: null).Service;
        var r = svc.Lookup("glorp")!;
        Assert.Equal("/ɡlɔːrp/", r.Phonetics.Us); // Kaikki 索引 IPA
        Assert.Equal("/ˈɡlɔːp/", r.Phonetics.Uk); // ECDICT 'ɡlɔːp 规范化(重音符 + 斜线包裹)
    }

    [Fact]
    public void EcdictPhoneticNormalization_Rules()
    {
        // 规范化规则仅:去首尾点、'→ˈ、ә(U+04D9)→ə、斜线包裹;ECDICT 原文的 ASCII i 不动
        Assert.Equal("/disəˈpiə/", DictionaryService.NormalizeEcdictPhonetic(".dis\u04D9'pi\u04D9"));
        Assert.Equal("", DictionaryService.NormalizeEcdictPhonetic(""));
    }
}
