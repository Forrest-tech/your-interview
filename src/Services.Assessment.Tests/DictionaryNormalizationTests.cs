using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>票119 归一化四步（SPEC §4.1）与 Morphy 规则样本（§3.2：.exc + Morphy 各 ≥3 例）。</summary>
public class DictionaryNormalizationTests
{
    private static OpenLayerStore Store() => BuildOpenStore(
        EcdictTsv(
            EcRow("glorp", "ɡlɔːp", "v. to vanish suddenly", "v. 突然消失", "p:glorped/d:glorped/3:glorps/i:glorping"),
            EcRow("glorped", "", "", "v. 突然消失（glorp 的过去式）", "0:glorp/1:pd"),
            EcRow("snarve", "", "v. to twist", "v. 扭曲", ""),
            EcRow("wibble", "", "v. to tremble", "v. 颤抖", ""),
            EcRow("study", "", "v. to learn", "v. 学习", "p:studied/d:studied/i:studying"),
            EcRow("class", "", "n. a group", "n. 班级", "s:classes"),
            EcRow("stop", "", "v. to halt", "v. 停止", "p:stopped/d:stopped/i:stopping"),
            EcRow("year", "", "n. 365 days", "n. 年", "s:years")),
        WordNetJsonl(
            WnEntry("glorp", ("v", [("to disappear suddenly", "It glorped."), ("to cease to exist", null)])),
            WnEntry("snarve", ("v", [("to twist", null)])),
            WnEntry("wibble", ("v", [("to tremble", null)])),
            WnEntry("study", ("v", [("to learn", null)])),
            WnEntry("class", ("n", [("a group of students", null)])),
            WnEntry("stop", ("v", [("to halt", null)])),
            WnEntry("year", ("n", [("a period of 365 days", null)]))),
        exceptionsJson: "{\"snarved\": [\"snarve\"], \"went\": [\"go\"], \"has\": [\"have\"]}");

    private static DictionaryService Service(OpenLayerStore store, string? packDir = null) =>
        BuildService(store, packDir).Service;

    [Fact]
    public void Morphy_RegularInflections_GenerateLemmaCandidates()
    {
        // 规则屈折 ≥3 例（SPEC §3.2/§7）：ed→""、ies→y、ses→s、双写去重
        Assert.Contains("disappear", Morphy.GenerateCandidates("disappeared"));
        Assert.Contains("study", Morphy.GenerateCandidates("studies"));
        Assert.Contains("class", Morphy.GenerateCandidates("classes"));
        Assert.Contains("stop", Morphy.GenerateCandidates("stopped"));
        Assert.Contains("compare", Morphy.GenerateCandidates("compared"));
    }

    [Fact]
    public void Direct_LemmaHit()
    {
        var svc = Service(Store());
        var r = svc.Lookup("glorp");
        Assert.NotNull(r);
        Assert.Equal("glorp", r!.Lemma);
        Assert.Equal("direct", r.MatchedVia);
        Assert.Equal("open", r.Tier);
    }

    [Fact]
    public void EcdictLink_FormResolvesToLemma()
    {
        var svc = Service(Store());
        var r = svc.Lookup("glorped");
        Assert.NotNull(r);
        Assert.Equal("glorp", r!.Lemma);
        Assert.Equal("ecdict-link", r.MatchedVia);
        Assert.Equal("glorped", r.Query);
    }

    [Fact]
    public void Exc_IrregularFormResolves()
    {
        var svc = Service(Store());
        var r = svc.Lookup("snarved");
        Assert.NotNull(r);
        Assert.Equal("snarve", r!.Lemma);
        Assert.Equal("exc", r.MatchedVia);
    }

    [Fact]
    public void Morphy_RuleFormResolves()
    {
        var svc = Service(Store());
        var wibbled = svc.Lookup("wibbled");
        Assert.NotNull(wibbled);
        Assert.Equal("wibble", wibbled!.Lemma);
        Assert.Equal("morphy", wibbled.MatchedVia);

        var studies = svc.Lookup("studies");
        Assert.Equal("study", studies!.Lemma);
        Assert.Equal("morphy", studies.MatchedVia);
    }

    [Fact]
    public void HyphenHead_CompoundFallsBackToHead()
    {
        var svc = Service(Store());
        var r = svc.Lookup("year-old");
        Assert.NotNull(r);
        Assert.Equal("year", r!.Lemma);
        Assert.Equal("hyphen-head", r.MatchedVia);
    }

    [Fact]
    public void Unknown_MadeUpWord_ReturnsNull()
    {
        var svc = Service(Store());
        Assert.Null(svc.Lookup("zorblaxian"));
    }

    [Fact]
    public void OxfordRedirect_TakesPrecedenceWhenPackPresent()
    {
        var root = Path.Combine(Path.GetTempPath(), "dict-test-" + Guid.NewGuid().ToString("N"));
        var packDir = BuildOxfordPack(root,
            [("glorp", OxfordEntryJson("glorp",
                new { uk = "/ɡlɔːp/", us = "/ɡlɔːrp/" },
                new[] { new { pos = "verb", senses = new[] { new { defEn = "to vanish suddenly", glossZh = "突然消失" } } } }))],
            new Dictionary<string, string> { ["glorped"] = "glorp" },
            audioArchivePresent: false);
        try
        {
            var svc = Service(Store(), packDir);
            var r = svc.Lookup("glorped");
            Assert.NotNull(r);
            Assert.Equal("redirect", r!.MatchedVia);
            Assert.Equal("oxford", r.Tier);
            // 牛津在席且有义项时,卡面释义只来自牛津(P1 不混层)
            Assert.Equal("to vanish suddenly", r.PosBlocks[0].Senses[0].DefEn);
            Assert.Equal("突然消失", r.PosBlocks[0].Senses[0].GlossZh);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
