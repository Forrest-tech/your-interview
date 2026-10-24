using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using YourInterview.Services.Assessment.Infrastructure.Dictionary;
using static YourInterview.Services.Assessment.Tests.DictionaryTestHarness;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票128 后端验收（SPEC §10.1 TB-1…TB-7）：
/// TB-1 DTO 形态；TB-2 判定不吃词元回落（判别性）；TB-3 懒层算可用 + 判定零网络；
/// TB-4 一致性双向语料（含跨层混合 ⑥⑦）；TB-5 pronounce 本形投影全程优先判别组（H1/H2/同层/回落/主端点隔离）；
/// TB-6 判定异常逐词形隔离；TB-7 契约对照（除 forms[].audio 外与基线逐字段一致）。
/// TB-5① 变体 B 按 SCORE-R2 微瑕-新1 的折入修正构造：词元在核心包另有一条录音，
/// 使「旧机制必红」在变体 B 下同样成立（旧机制 Tier 0 被本形音键挡住词头回落后，
/// 核心包层词元先行命中 → 返回词元核心包 key；新机制投影段先行 → 返回本形核心包 key）。
/// </summary>
public class DictionaryTicket128Tests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "dict128-" + Guid.NewGuid().ToString("N"));

    /// <summary>核心包夹具（同 DictionaryCacheAudioTests.WriteCorePack 口径）：manifest + 文件落盘。</summary>
    private static string WriteCorePack(string root, params (string Key, string Word, string Accent)[] files)
    {
        var coreDir = Path.Combine(root, "audio-core");
        Directory.CreateDirectory(coreDir);
        var manifestFiles = new List<object>();
        foreach (var f in files)
        {
            var bytes = new byte[] { 0x49, 0x44, 0x33 };
            File.WriteAllBytes(Path.Combine(coreDir, f.Key + ".mp3"), bytes);
            manifestFiles.Add(new
            {
                key = f.Key, word = f.Word, accent = f.Accent, file = f.Key + ".mp3",
                storedAs = f.Key + ".mp3", license = "CC0", artist = "Synth",
                bytes = bytes.Length, sha256 = "",
            });
        }
        File.WriteAllText(Path.Combine(coreDir, "audio-core-manifest.json"),
            JsonSerializer.Serialize(new { files = manifestFiles }));
        return coreDir;
    }

    private static string OxEntry(string headword, string formsJson, string audioJson) =>
        $$"""
        {"headword":"{{headword}}","phonetics":{"uk":"/x/","us":"/x/"},
         "posBlocks":[{"pos":"verb","senses":[{"defEn":"synthetic sense of {{headword}}"}]}],
         "forms":{{formsJson}},"audio":{{audioJson}},"oxford3000":false}
        """;

    private static PronounceResponse Pron(DictionaryService svc, string word, string accent) =>
        svc.PronounceAsync(word, accent, CancellationToken.None).GetAwaiter().GetResult();

    private static FormDto FormOf(DictionaryLookupResponse r, string text) =>
        Assert.Single(r.Forms, f => f.Text == text);

    // ---------- TB-1（DTO 形态，§5.1 线格式） ----------

    [Fact]
    public void TB1_FormAudio_WireShape_PerFormPerAccent()
    {
        var root = TempRoot();
        try
        {
            var pack = BuildOxfordPack(root,
                [("glorp", OxEntry("glorp",
                    """[{"label":"过去式","text":"glorped","audioKey":{"us":"glorped__us_1.mp3","uk":"glorped__gb_1.mp3"}},{"label":"现在分词","text":"glorping","audioKey":{"us":"glorping__us_1.mp3"}}]""",
                    """{"us":"glorp__us_1.mp3","uk":"glorp__gb_1.mp3"}"""))],
                new Dictionary<string, string>(),
                audioArchivePresent: true,
                audioFiles: ["glorped__us_1.mp3", "glorped__gb_1.mp3", "glorping__us_1.mp3", "glorp__us_1.mp3", "glorp__gb_1.mp3"]);
            var svc = BuildService(OpenMinimal(), pack).Service;
            var r = svc.Lookup("glorp")!;
            var glorped = FormOf(r, "glorped");
            Assert.True(glorped.Audio.Us);
            Assert.True(glorped.Audio.Uk);
            var glorping = FormOf(r, "glorping");
            Assert.True(glorping.Audio.Us);
            Assert.False(glorping.Audio.Uk);
            // 线格式：audio 对象恒在、两布尔恒输出、字段名 camelCase、不带 key/kind
            var json = JsonSerializer.Serialize(r, WireOptions);
            using var doc = JsonDocument.Parse(json);
            var forms = doc.RootElement.GetProperty("forms");
            var f0 = forms.EnumerateArray().First(f => f.GetProperty("text").GetString() == "glorped");
            var audio = f0.GetProperty("audio");
            Assert.Equal(["uk", "us"], audio.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
            Assert.True(audio.GetProperty("us").GetBoolean());
            Assert.True(audio.GetProperty("uk").GetBoolean());
            Assert.False(audio.TryGetProperty("key", out _));
            Assert.False(audio.TryGetProperty("kind", out _));
            Assert.Contains("\"audio\":{\"us\":true,\"uk\":false}", json); // glorping 的线格式
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static OpenLayerStore OpenMinimal() => BuildOpenStore(
        EcdictTsv(EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped/d:glorped/i:glorping")),
        WordNetJsonl(WnEntry("glorp", ("v", [("to disappear suddenly", null)]))));

    // ---------- TB-2（判别性：判定不吃词元回落，D2 命门） ----------

    [Fact]
    public void TB2_LemmaHasAudio_FormHasNone_JudgedFalse()
    {
        var root = TempRoot();
        try
        {
            var pack = BuildOxfordPack(root,
                [("glorp", OxEntry("glorp",
                    """[{"label":"过去式","text":"glorped"}]""",
                    """{"us":"glorp__us_1.mp3","uk":"glorp__gb_1.mp3"}"""))],
                new Dictionary<string, string>(),
                audioArchivePresent: true,
                audioFiles: ["glorp__us_1.mp3", "glorp__gb_1.mp3"]);
            var svc = BuildService(OpenMinimal(), pack).Service;
            var r = svc.Lookup("glorp")!;
            // 夹具有效性：词元自己的录音在席（卡面 map 可用）——以现行 ResolveAudio(lemma, form) 直判必然误报 true
            Assert.True(r.Audio["us"].Available);
            Assert.Equal("glorp__us_1.mp3", r.Audio["us"].Key);
            // 本形无任何录音 → 两口音皆 false（不许借词元录音撑钮）
            var glorped = FormOf(r, "glorped");
            Assert.False(glorped.Audio.Us);
            Assert.False(glorped.Audio.Uk);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- TB-3（懒层算可用 + 判定/pronounce 全程零网络） ----------

    [Fact]
    public void TB3_IndexOnlyForm_JudgedTrue_AndZeroHttp()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("network forbidden in TB-3"));
        var store = BuildOpenStore(
            EcdictTsv(
                EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped/d:glorped/i:glorping"),
                EcRow("glorped", "", "", "v. 消失（过去式）", "0:glorp/1:pd")),
            WordNetJsonl(WnEntry("glorp", ("v", [("to disappear suddenly", null)]))),
            audioIndexJson: "{\"glorped\": [{\"file\": \"En-us-glorped.ogg\", \"accent\": \"us\", \"license\": \"CC BY-SA 4.0\", \"artist\": \"Synth\", \"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-us-glorped.ogg/En-us-glorped.ogg.mp3\"}]}");
        var svc = BuildService(store, packDir: null, httpHandler: handler).Service;
        var r = svc.Lookup("glorp")!;
        var glorped = FormOf(r, "glorped");
        Assert.True(glorped.Audio.Us);   // 仅 index 有记录 → 算可用（§4.2）
        Assert.False(glorped.Audio.Uk);
        var glorping = FormOf(r, "glorping");
        Assert.False(glorping.Audio.Us);
        Assert.False(glorping.Audio.Uk);
        var pron = Pron(svc, "glorped", "us");
        Assert.True(pron.Available);
        Assert.Equal("commons", pron.Kind);
        Assert.Equal("En-us-glorped.ogg.mp3", pron.Key);
        Assert.Empty(handler.RequestedUrls); // 判定与解析全程零 HTTP（§4.4）
    }

    // ---------- TB-4（一致性双向语料，§4.3；含跨层混合 ⑥⑦） ----------

    [Fact]
    public void TB4_Corpus_JudgmentImpliesPronounceFormKey_AndLocalExistenceImpliesJudgment()
    {
        var root = TempRoot();
        try
        {
            var pack = BuildOxfordPack(root,
                [
                    ("glorp", OxEntry("glorp",
                        """[{"label":"过去式","text":"glorped","audioKey":{"us":"glorped__us_1.mp3","uk":"glorped__gb_1.mp3"}},{"label":"现在分词","text":"glorping"}]""",
                        """{"us":"glorp__us_1.mp3","uk":"glorp__gb_1.mp3"}""")),
                    ("blint", OxEntry("blint",
                        """[{"label":"过去式","text":"blinted"}]""",
                        """{"us":"blint__us_1.mp3"}""")),
                ],
                new Dictionary<string, string> { ["glorped"] = "glorp", ["glorping"] = "glorp", ["blinted"] = "blint" },
                audioArchivePresent: true,
                audioFiles: ["glorped__us_1.mp3", "glorped__gb_1.mp3", "glorp__us_1.mp3", "glorp__gb_1.mp3", "blint__us_1.mp3"]);
            var coreDir = WriteCorePack(root,
                ("c128g02", "glorping", "uk"),   // ② 核心包本形
                ("c128b01", "blinted", "us"),    // ⑥ 本形核心包（词元有 Tier 0 词头录音）
                ("c128s00", "snarp", "us"),      // ④ 词元核心包（本形不得吃）
                ("c128f00", "flim", "uk"));      // ⑦ 词元核心包（本形仅 index）
            var store = BuildOpenStore(
                EcdictTsv(
                    EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped/d:glorped/i:glorping"),
                    EcRow("snarp", "", "v. to snarp", "v. 斯纳普", "p:snarped/i:snarping/3:snarps"),
                    EcRow("snarped", "", "", "", "0:snarp"),
                    EcRow("snarping", "", "", "", "0:snarp"),
                    EcRow("flim", "", "v. to flim", "v. 弗利姆", "d:flimmed"),
                    EcRow("flimmed", "", "", "", "0:flim")),
                WordNetJsonl(
                    WnEntry("glorp", ("v", [("to disappear suddenly", null)])),
                    WnEntry("snarp", ("v", [("to snarp", null)])),
                    WnEntry("flim", ("v", [("to flim", null)]))),
                audioIndexJson: "{\"snarped\": [{\"file\": \"En-us-snarped.ogg\", \"accent\": \"us\", \"license\": \"CC0\", \"artist\": \"Synth\", \"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-us-snarped.ogg/En-us-snarped.ogg.mp3\"}], \"flimmed\": [{\"file\": \"En-uk-flimmed.ogg\", \"accent\": \"uk\", \"license\": \"CC0\", \"artist\": \"Synth\", \"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-uk-flimmed.ogg/En-uk-flimmed.ogg.mp3\"}]}");
            var svc = BuildService(store, pack, coreDir: coreDir).Service;

            var glorp = svc.Lookup("glorp")!;
            var blint = svc.Lookup("blint")!;
            var snarp = svc.Lookup("snarp")!;
            var flim = svc.Lookup("flim")!;
            // 夹具有效性：④/⑦ 的词元录音确实在席（卡面 map 命中词元层）
            Assert.Equal("c128s00", snarp.Audio["us"].Key);
            Assert.Equal("c128f00", flim.Audio["uk"].Key);

            // (词条响应, 词形, 口音, 期望判定, 判定为真时 pronounce 必须返回的本形 key)
            var cases = new (DictionaryLookupResponse Card, string Form, string Accent, bool Expected, string? FormKey)[]
            {
                (glorp, "glorped", "us", true, "glorped__us_1.mp3"),   // ① Tier 0 本形（文件在盘 → 判定必 true：反向）
                (glorp, "glorped", "uk", true, "glorped__gb_1.mp3"),   // ①
                (glorp, "glorping", "uk", true, "c128g02"),            // ② 核心包本形（manifest 在席 → 判定必 true：反向）
                (glorp, "glorping", "us", false, null),                // ②' 本形 us 全无（词元 us 有 Tier 0 也不吃）
                (snarp, "snarped", "us", true, "En-us-snarped.ogg.mp3"), // ③ 仅 index 本形
                (snarp, "snarped", "uk", false, null),                 // ③'
                (snarp, "snarping", "us", false, null),                // ④ 本形全无、词元核心包有 → 不吃回落
                (snarp, "snarps", "us", false, null),                  // ⑤ 本形全无
                (snarp, "snarps", "uk", false, null),                  // ⑤ 本形词元全无（uk）
                (blint, "blinted", "us", true, "c128b01"),             // ⑥ H1 型跨层混合
                (blint, "blinted", "uk", false, null),                 // ⑥'
                (flim, "flimmed", "uk", true, "En-uk-flimmed.ogg.mp3"), // ⑦ H2 型跨层混合
                (flim, "flimmed", "us", false, null),                  // ⑦'
            };
            foreach (var (card, form, accent, expected, formKey) in cases)
            {
                var dto = FormOf(card, form);
                var judged = accent == "us" ? dto.Audio.Us : dto.Audio.Uk;
                Assert.True(judged == expected,
                    $"{form}/{accent}：判定期望 {expected} 实得 {judged}");
                if (!expected) continue;
                var pron = Pron(svc, form, accent);
                Assert.True(pron.Available, $"{form}/{accent}：判定 true 但 pronounce 不可用");
                Assert.Equal(formKey, pron.Key); // 必是本形那条录音，不是词元的
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- TB-5（pronounce 全程优先：判别组 + 回落 + 主端点隔离） ----------

    /// <summary>TB-5① 夹具：blint（主构造）+ drint（变体 B，含 SCORE-R2 折入修正：词元核心包另有一条）。</summary>
    private static (DictionaryService Service, string Root) Tb5H1Service()
    {
        var root = TempRoot();
        var pack = BuildOxfordPack(root,
            [
                ("blint", OxEntry("blint",
                    """[{"label":"过去式","text":"blinted"}]""",
                    """{"us":"blint__us_1.mp3"}""")),
                ("drint", OxEntry("drint",
                    """[{"label":"过去式","text":"drinted","audioKey":{"us":"drinted__us_9.mp3"}}]""",
                    """{"us":"drint__us_1.mp3"}""")),
            ],
            new Dictionary<string, string> { ["blinted"] = "blint", ["drinted"] = "drint" },
            audioArchivePresent: true,
            // 注意：drinted__us_9.mp3 故意不落盘（变体 B：音键在、文件缺失）
            audioFiles: ["blint__us_1.mp3", "drint__us_1.mp3"]);
        var coreDir = WriteCorePack(root,
            ("c128b01", "blinted", "us"),   // 主构造：本形核心包录音
            ("c128d01", "drinted", "us"),   // 变体 B：本形核心包录音
            ("c128d00", "drint", "us"));    // 变体 B 折入修正：词元在核心包另有一条录音
        var store = BuildOpenStore(
            EcdictTsv(
                EcRow("blinted", "", "", "", "0:blint"),
                EcRow("drinted", "", "", "", "0:drint")),
            WordNetJsonl());
        var svc = BuildService(store, pack, coreDir: coreDir).Service;
        return (svc, root);
    }

    [Fact]
    public void TB5_1_H1_FormCoreWins_OverLemmaOxfordHeadword()
    {
        var (svc, root) = Tb5H1Service();
        try
        {
            // 判定经 §4.1 第 2 层（核心包本形）为 true
            Assert.True(FormOf(svc.Lookup("blint")!, "blinted").Audio.Us);
            var pron = Pron(svc, "blinted", "us");
            Assert.True(pron.Available);
            Assert.Equal("commons", pron.Kind);
            Assert.Equal("c128b01", pron.Key); // 本形核心包 key；旧机制必返词元 oxford 词头 key → 本断言判红
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TB5_1_VariantB_KeyPresentFileMissing_LemmaCoreAlsoPresent()
    {
        var (svc, root) = Tb5H1Service();
        try
        {
            // 变体 B：本形音键在但文件缺失 → 第 1 层不算命中；判定经核心包本形为 true
            Assert.True(FormOf(svc.Lookup("drint")!, "drinted").Audio.Us);
            var pron = Pron(svc, "drinted", "us");
            Assert.True(pron.Available);
            Assert.Equal("commons", pron.Kind);
            // 新机制：本形核心包 key。旧机制：Tier 0 被音键挡住词头回落 → 核心包层词元先行 → 必返 c128d00 判红；
            // 同层内优先版（R1 稿机制）同样到不了本形 → 判红。仅全程优先必绿（SCORE-R2 折入修正后判别成立）。
            Assert.Equal("c128d01", pron.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TB5_2_H2_FormIndexWins_OverLemmaCore()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root, ("c128f00", "flim", "uk"));
            var store = BuildOpenStore(
                EcdictTsv(
                    EcRow("flim", "", "v. to flim", "v. 弗利姆", "d:flimmed"),
                    EcRow("flimmed", "", "", "", "0:flim")),
                WordNetJsonl(WnEntry("flim", ("v", [("to flim", null)]))),
                audioIndexJson: "{\"flimmed\": [{\"file\": \"En-uk-flimmed.ogg\", \"accent\": \"uk\", \"license\": \"CC0\", \"artist\": \"Synth\", \"mp3Url\": \"https://upload.wikimedia.org/wikipedia/commons/transcoded/x/En-uk-flimmed.ogg/En-uk-flimmed.ogg.mp3\"}]}");
            var svc = BuildService(store, packDir: null, coreDir: coreDir).Service;
            Assert.True(FormOf(svc.Lookup("flim")!, "flimmed").Audio.Uk);
            var pron = Pron(svc, "flimmed", "uk");
            Assert.True(pron.Available);
            Assert.Equal("commons", pron.Kind);
            Assert.Equal("En-uk-flimmed.ogg.mp3", pron.Key); // 本形 index 派生 key；旧机制核心包层先命中词元 → 判红
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TB5_3_SameLayerCore_FormWinsOverLemma()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root,
                ("c128g00", "glorp", "us"),
                ("c128g01", "glorped", "us"));
            var store = BuildOpenStore(
                EcdictTsv(
                    EcRow("glorp", "ɡlɔːp", "v. to vanish", "v. 消失", "p:glorped/d:glorped"),
                    EcRow("glorped", "", "", "", "0:glorp/1:pd")),
                WordNetJsonl(WnEntry("glorp", ("v", [("to disappear suddenly", null)]))));
            var svc = BuildService(store, packDir: null, coreDir: coreDir).Service;
            var pron = Pron(svc, "glorped", "us");
            Assert.True(pron.Available);
            Assert.Equal("c128g01", pron.Key); // 同层双有 → 本形（投影段内层序）
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TB5_4_FallbackRetained_FormNothing_LemmaRecording()
    {
        var root = TempRoot();
        try
        {
            var coreDir = WriteCorePack(root, ("c128m00", "murm", "us"));
            var store = BuildOpenStore(
                EcdictTsv(
                    EcRow("murm", "", "v. to murm", "v. 默姆", "p:murmed/d:murmed"),
                    EcRow("murmed", "", "", "", "0:murm")),
                WordNetJsonl(WnEntry("murm", ("v", [("to murm", null)]))));
            var svc = BuildService(store, packDir: null, coreDir: coreDir).Service;
            // 本形三层全无 → 回落既有整链 → 词元录音（跟读票127 依赖，不许取消）
            var pron = Pron(svc, "murmed", "us");
            Assert.True(pron.Available);
            Assert.Equal("commons", pron.Kind);
            Assert.Equal("c128m00", pron.Key);
            // 本形词元全无 → 耗尽
            var none = Pron(svc, "murmed", "uk");
            Assert.False(none.Available);
            Assert.Null(none.Kind);
            Assert.Null(none.Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TB5_5_MainEndpointAudioMap_UnchangedByAlignment()
    {
        var (svc, root) = Tb5H1Service();
        try
        {
            // 以屈折形查词的主端点 map 走未对齐基线：Tier 0 本形键无 → 词元词头 oxford key，
            // 不得被 pronounce 的投影模式污染成核心包本形 key。
            var r = svc.Lookup("blinted")!;
            Assert.Equal("blint", r.Lemma);
            Assert.Equal("oxford", r.Audio["us"].Kind);
            Assert.Equal("blint__us_1.mp3", r.Audio["us"].Key);
            var direct = svc.Lookup("blint")!;
            Assert.Equal("blint__us_1.mp3", direct.Audio["us"].Key);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- TB-6（判定异常逐词形隔离，§4.5） ----------

    [Fact]
    public void TB6_SingleFormJudgmentThrows_IsolatedToThatForm()
    {
        var root = TempRoot();
        try
        {
            var pack = BuildOxfordPack(root,
                [("glorp", OxEntry("glorp",
                    """[{"label":"过去式","text":"glorped","audioKey":{"us":"glorped__us_1.mp3"}},{"label":"现在分词","text":"glorping"}]""",
                    """{}"""))],
                new Dictionary<string, string>(),
                audioArchivePresent: true,
                audioFiles: ["glorped__us_1.mp3"]);
            var coreDir = WriteCorePack(root, ("c128g09", "glorping", "us"));
            var svc = BuildService(OpenMinimal(), pack, coreDir: coreDir).Service;
            svc.FormAudioAvailableOverrideForTests = (_, form, _) =>
                form == "glorped" ? throw new InvalidOperationException("TB-6 injected") : (bool?)null;
            var r = svc.Lookup("glorp")!; // Lookup 本身不抛
            var glorped = FormOf(r, "glorped");
            Assert.False(glorped.Audio.Us); // 异常形隔离为 false/false（其 Tier 0 录音本可判 true）
            Assert.False(glorped.Audio.Uk);
            var glorping = FormOf(r, "glorping");
            Assert.True(glorping.Audio.Us); // 其余词形判定照常（核心包本形命中）
            Assert.False(glorping.Audio.Uk);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ---------- TB-7（契约对照：除 forms[].audio 外与基线逐字段一致） ----------

    [Fact]
    public void TB7_ResponseJson_BaselineExceptFormsAudio()
    {
        var root = TempRoot();
        try
        {
            var entryJson = """
                {"headword":"glorp","phonetics":{"uk":"/ɡlɔːp/","us":"/ɡlɔːp/"},
                 "posBlocks":[{"pos":"verb","senses":[{"defEn":"to vanish suddenly","glossZh":"突然消失","examples":[{"en":"It glorped.","zh":"它突然消失了。"}]}]}],
                 "forms":[{"label":"过去式","text":"glorped","audioKey":{"us":"glorped__us_1.mp3"}}],
                 "audio":{"us":"glorp__us_1.mp3"},"oxford3000":false}
                """;
            var pack = BuildOxfordPack(root,
                [("glorp", entryJson)],
                new Dictionary<string, string>(),
                audioArchivePresent: true,
                audioFiles: ["glorped__us_1.mp3", "glorp__us_1.mp3"]);
            var svc = BuildService(OpenMinimal(), pack).Service;
            var r = svc.Lookup("glorp")!;
            var json = JsonSerializer.Serialize(r, WireOptions);
            using var doc = JsonDocument.Parse(json);
            // 新增面：forms 项恰有 audio 且形态合 §5.1
            var form = doc.RootElement.GetProperty("forms")[0];
            Assert.Equal(["audio", "label", "text"],
                form.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
            Assert.True(form.GetProperty("audio").GetProperty("us").GetBoolean());
            Assert.False(form.GetProperty("audio").GetProperty("uk").GetBoolean());
            // 基线对照：剥掉 forms[].audio 后与票128 之前的响应形状逐字段一致
            var stripped = StripFormsAudio(doc.RootElement);
            const string baseline = """
                {"query":"glorp","lemma":"glorp","matchedVia":"direct","tier":"oxford","phonetics":{"uk":"/ɡlɔːp/","us":"/ɡlɔːp/"},"posBlocks":[{"pos":"verb","label":"动词","senses":[{"defEn":"to vanish suddenly","glossZh":"突然消失","example":{"en":"It glorped.","zh":"它突然消失了。"}}],"foldedCount":0,"zhLines":[]}],"forms":[{"label":"过去式","text":"glorped"}],"audio":{"us":{"kind":"oxford","key":"glorp__us_1.mp3","available":true},"uk":{"available":false}},"attribution":{"source":"Oxford Advanced Learner's Dictionary 10 (local pack)"}}
                """;
            Assert.Equal(
                JsonSerializer.Serialize(JsonDocument.Parse(baseline).RootElement),
                JsonSerializer.Serialize(stripped));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>深拷贝响应 JSON 并删去每个 forms 项的 audio 属性（TB-7 基线对照用）。</summary>
    private static JsonElement StripFormsAudio(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                writer.WritePropertyName(prop.Name);
                if (prop.Name != "forms")
                {
                    prop.Value.WriteTo(writer);
                    continue;
                }
                writer.WriteStartArray();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    writer.WriteStartObject();
                    foreach (var fp in item.EnumerateObject())
                    {
                        if (fp.Name == "audio") continue;
                        writer.WritePropertyName(fp.Name);
                        fp.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }
}
