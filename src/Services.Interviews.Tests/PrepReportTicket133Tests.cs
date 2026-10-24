using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票133:Prep 报告 12+1 模板升级。
/// 结构/路由/归一化/语言/提示词断言全部离线(DTO 直测,不碰数据库);
/// Handle 黑盒断言走沙盒 DB,无 DB 时跳过(与 PrepPlanFlowTests 同款口径)。
/// 本文件断言内容一律为机制占位文本(MechanicsCo),不是真实样本;
/// 真实样本(TB-5/TB-7/TB-10/TB-12)走 PREP_REPORT_SAMPLE_EXPORT 导出缝,缺导出时跳过并登记。
/// </summary>
public sealed class PrepReportTicket133Tests
{
    internal const string StuckQ = "What is the difference between a hash map and a tree map?";
    internal const string StuckReason = "Could not explain collision handling";
    internal const string StuckAnswer = "Buckets plus hash function; chaining or open addressing; rehash on resize.";
    internal const string StuckQ2 = "Explain how you would scale a cache layer.";
    internal const string StuckReason2 = "Could not name eviction policies";
    internal const string StuckAnswer2 = "LRU and TTL; size the cache from working set; measure hit rate.";

    internal static PrepReportSourceData Sample(
        string stage = "SystemDesign", bool bank = true, bool company = true,
        string? profile = "MechanicsCo builds widgets.", string? resume = "Senior .NET resume text.",
        IReadOnlyList<PrepQuestionInput>? questions = null,
        IReadOnlyList<PrepWeaknessInput>? weaknesses = null) =>
        new("MechanicsCo", "Senior .NET Developer", profile,
            2, stage, "Ada Lovelace", "Video", new DateOnly(2026, 10, 13),
            "JD text here", null, null, resume, null,
            bank, company, [],
            questions ?? DefaultQuestions(), weaknesses ?? DefaultWeaknesses());

    internal static List<PrepQuestionInput> DefaultQuestions() =>
    [
        new(StuckQ, QuestionCategory.Technical, true, StuckReason, StuckAnswer, 1),
        new("How does async await work under the hood?", QuestionCategory.Technical, false, null, null, 2),
        new("Tell me about a time you resolved a team conflict.", QuestionCategory.Behavioral, false, null, null, 3),
        new("What is your salary expectation?", QuestionCategory.Screening, false, null, null, 4),
        new("Design a URL shortener.", QuestionCategory.SystemDesign, false, null, null, 5),
        new("How was your weekend?", QuestionCategory.SmallTalk, false, null, null, 6),
        new(StuckQ2, QuestionCategory.Technical, true, StuckReason2, StuckAnswer2, 7)
    ];

    internal static List<PrepWeaknessInput> DefaultWeaknesses() =>
    [
        new(WeaknessCategory.TechnicalDepth, "Hash map internals", "Cannot explain collision handling", "Study hash maps", 3),
        new(WeaknessCategory.Logic, "Answer structure", "Jumps to conclusions", "Use First Second Third", 2)
    ];

    internal static List<string> SectionIds(JsonObject root) =>
        (root["sections"] as JsonArray)!.Select(s => s!["id"]!.GetValue<string>()).ToList();

    internal static JsonObject Section(JsonObject root, string id) =>
        (root["sections"] as JsonArray)!.OfType<JsonObject>()
            .First(s => s["id"]!.GetValue<string>() == id);

    internal static List<string> QuestionTexts(JsonObject root, string sectionId) =>
        (Section(root, sectionId)["questions"] as JsonArray)!
            .Select(q => q!["question"]!.GetValue<string>()).ToList();

    internal static List<string> BlockTexts(JsonObject root, string sectionId) =>
        (Section(root, sectionId)["blocks"] as JsonArray)!
            .SelectMany(b => (b!["items"] as JsonArray ?? new JsonArray())
                .Select(i => i!.GetValue<string>()))
            .ToList();

    internal static JsonObject CannedRoot(params (string Id, string[] Questions)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, qs) in sections)
        {
            var qarr = new JsonArray();
            foreach (var q in qs)
                qarr.Add(new JsonObject
                {
                    ["question"] = q, ["category"] = "Technical",
                    ["sourceTags"] = new JsonArray("past"),
                    ["weaknessRelated"] = false,
                    ["stdSpoken"] = JsonValue.Create((string?)null),
                    ["stdTechnical"] = JsonValue.Create((string?)null)
                });
            arr.Add(new JsonObject
            {
                ["id"] = id, ["title"] = id,
                ["blocks"] = new JsonArray(new JsonObject
                    { ["type"] = "points", ["items"] = new JsonArray("canned") }),
                ["questions"] = qarr
            });
        }
        return new JsonObject { ["sections"] = arr };
    }

    // ---------------- TB-11 分类器夹具 ----------------

    [Theory]
    [InlineData("Coding", true, false, false, false, false)]
    [InlineData("Live Coding", true, false, false, false, false)]
    [InlineData("Pair Programming", true, false, false, false, false)]
    [InlineData("Online Assessment", true, false, false, false, false)]
    [InlineData("OA", true, false, false, false, false)]
    [InlineData("HackerRank", true, false, false, false, false)]
    [InlineData("Codility", true, false, false, false, false)]
    [InlineData("System Design", false, true, false, false, false)]
    [InlineData("SystemDesign", false, true, false, false, false)]
    [InlineData("Architect", false, true, false, false, false)]
    [InlineData("Design", false, true, false, false, false)]
    [InlineData("Screen", false, false, true, false, false)]
    [InlineData("Phone Screen", false, false, true, false, false)]
    [InlineData("Recruiter Call", false, false, true, false, false)]
    [InlineData("HR Round", false, false, true, false, false)]
    [InlineData("Behavioral", false, false, false, true, false)]
    [InlineData("Culture Fit", false, false, false, true, false)]
    [InlineData("Final", false, false, false, false, true)]
    [InlineData("Hiring Manager", false, false, false, false, true)]
    [InlineData("Onsite", false, false, false, false, true)]
    [InlineData("Panel", false, false, false, false, true)]
    [InlineData("Technical", false, false, false, false, false)]
    [InlineData("", false, false, false, false, false)]
    [InlineData(null, false, false, false, false, false)]
    public void Classifier_FixtureTable(string? stage, bool coding, bool design,
        bool screen, bool behavioral, bool final)
    {
        var cls = PrepReportClassifier.Classify(stage);
        cls.IsCoding.Should().Be(coding);
        cls.IsDesign.Should().Be(design);
        cls.IsScreen.Should().Be(screen);
        cls.IsBehavioral.Should().Be(behavioral);
        cls.IsFinal.Should().Be(final);
    }

    // ---------------- 分流路由(§6.2) ----------------

    [Fact]
    public void Routing_Landings_PerCategory()
    {
        var design = PrepReportRouting.RouteQuestions(DefaultQuestions(),
            PrepReportClassifier.Classify("SystemDesign"));
        design.TechQa.Select(q => q.Text).Should()
            .Equal("How does async await work under the hood?");
        design.Behavioral.Select(q => q.Text).Should()
            .Equal("Tell me about a time you resolved a team conflict.");
        design.CompProcess.Select(q => q.Text).Should().Equal("What is your salary expectation?");
        design.DesignRound.Select(q => q.Text).Should().Equal("Design a URL shortener.");
        design.Stuck.Select(q => q.Text).Should().Equal(StuckQ, StuckQ2);
        design.TechQa.Concat(design.Behavioral).Concat(design.CompProcess)
            .Concat(design.DesignRound).Concat(design.Stuck)
            .Should().NotContain(q => q.Text == "How was your weekend?");

        var tech = PrepReportRouting.RouteQuestions(DefaultQuestions(),
            PrepReportClassifier.Classify("Technical"));
        tech.DesignRound.Should().BeEmpty();
        tech.TechQa.Select(q => q.Text).Should().Equal(
            "How does async await work under the hood?", "Design a URL shortener.");
    }

    [Fact]
    public void Routing_Caps()
    {
        List<PrepQuestionInput> Many(string prefix, QuestionCategory cat, int n, int seqBase) =>
            Enumerable.Range(1, n)
                .Select(i => new PrepQuestionInput($"{prefix} {i}", cat, false, null, null, seqBase + i))
                .ToList();
        var stuck = Enumerable.Range(1, 12)
            .Select(i => new PrepQuestionInput($"stuck {i}", QuestionCategory.Technical, true, "r", "a", i))
            .ToList();
        PrepReportRouting.RouteQuestions(stuck, PrepReportClassifier.Classify("Technical"))
            .Stuck.Should().HaveCount(10);
        PrepReportRouting.RouteQuestions(Many("tech", QuestionCategory.Technical, 25, 100),
                PrepReportClassifier.Classify("Technical")).TechQa.Should().HaveCount(20);
        PrepReportRouting.RouteQuestions(Many("beh", QuestionCategory.Behavioral, 12, 100),
                PrepReportClassifier.Classify("Technical")).Behavioral.Should().HaveCount(10);
        PrepReportRouting.RouteQuestions(Many("scr", QuestionCategory.Screening, 7, 100),
                PrepReportClassifier.Classify("Technical")).CompProcess.Should().HaveCount(5);
        PrepReportRouting.RouteQuestions(Many("des", QuestionCategory.SystemDesign, 12, 100),
                PrepReportClassifier.Classify("SystemDesign")).DesignRound.Should().HaveCount(10);
        var mixed = Many("tech", QuestionCategory.Technical, 25, 100)
            .Concat(Many("beh", QuestionCategory.Behavioral, 12, 200))
            .Concat(Many("scr", QuestionCategory.Screening, 7, 300))
            .Concat(Many("des", QuestionCategory.SystemDesign, 12, 400))
            .ToList();
        var routed = PrepReportRouting.RouteQuestions(mixed, PrepReportClassifier.Classify("SystemDesign"));
        var totalNonStuck = routed.TechQa.Count + routed.Behavioral.Count
            + routed.CompProcess.Count + routed.DesignRound.Count;
        totalNonStuck.Should().BeLessThanOrEqualTo(30);
    }

    // ---------------- TB-4 归一化四变体 ----------------

    [Fact]
    public void Normalize_MissingSections_FilledAndRecorded()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("behavioral", []), ("weakness-plan", []),
            ("ask-back", []), ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        SectionIds(normalized).Should().Equal(PrepReportSections.ExpectedSectionIds(cls));
        var filled = (normalized["filledSections"] as JsonArray)!
            .Select(x => x!.GetValue<string>()).ToList();
        filled.Should().Equal("greeting", "tech-qa", "design-round");
        normalized["structureVersion"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public void Normalize_BankOff_CannedWeaknessPlan_StrippedToEmptyState()
    {
        // F-133-R1：AI 路题库关时，模型输出的弱点块须剥离为空态（规格 §6.1）。
        var data = Sample(bank: false);
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", []), ("design-round", []), ("behavioral", []),
            ("weakness-plan", []), ("ask-back", []), ("comp-process", []), ("closing", []));
        var plan = Section(root, "weakness-plan");
        plan["blocks"] = new JsonArray(new JsonObject
        {
            ["type"] = "points", ["title"] = "Hash map internals",
            ["items"] = new JsonArray("Review the internals once.")
        });
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        QuestionTexts(normalized, "weakness-plan").Should().BeEmpty();
        var texts = BlockTexts(normalized, "weakness-plan");
        texts.Should().Contain("No identified weaknesses or stuck questions yet");
        texts.Should().NotContain("Hash map internals").And.NotContain("Review the internals once.");
    }

    [Fact]
    public void Normalize_ShuffledAndUnknown_ReorderedAndDropped()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("closing", []), ("tech-qa", []), ("mystery-section", ["How does async await work under the hood?"]),
            ("greeting", []), ("round-brief", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("design-round", []), ("behavioral", []), ("weakness-plan", []),
            ("ask-back", []), ("comp-process", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        SectionIds(normalized).Should().Equal(PrepReportSections.ExpectedSectionIds(cls));
        normalized.ContainsKey("filledSections").Should().BeFalse();
    }

    [Fact]
    public void Normalize_Salvage_GroundedQuestionOnlyInDiscardedCodingSection()
    {
        // 评分微瑕 N-1 对应测试:有据题只出现在 ¬IsCoding 的 coding-round 节内。
        var data = Sample(stage: "Technical");
        var cls = PrepReportClassifier.Classify("Technical");
        cls.IsCoding.Should().BeFalse();
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", []),
            ("coding-round", ["How does async await work under the hood?"]),
            ("behavioral", []), ("weakness-plan", []), ("ask-back", []),
            ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        SectionIds(normalized).Should().NotContain("coding-round");
        QuestionTexts(normalized, "tech-qa").Should()
            .Contain("How does async await work under the hood?");
    }

    [Fact]
    public void Normalize_DoubleEmptySection_FilledWithScaffoldBlocks()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", []), ("design-round", []), ("behavioral", []),
            ("weakness-plan", []), ("ask-back", []), ("comp-process", []), ("closing", []));
        var greeting = Section(root, "greeting");
        greeting["blocks"] = new JsonArray();
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        (Section(normalized, "greeting")["blocks"] as JsonArray)!.Should().NotBeEmpty();
        var filled = (normalized["filledSections"] as JsonArray)!
            .Select(x => x!.GetValue<string>()).ToList();
        filled.Should().Contain("greeting");
    }

    // ---------------- TB-6 对抗:§6.5 四步 ----------------

    [Fact]
    public void Normalize_Adversarial_StuckMovedAndMissingStuckAddedBack()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", [StuckQ]), ("design-round", []),
            ("behavioral", []), ("weakness-plan", []), ("ask-back", []),
            ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        QuestionTexts(normalized, "tech-qa").Should().NotContain(StuckQ);
        QuestionTexts(normalized, "weakness-plan").Should().Contain(StuckQ, StuckQ2);
        var plan = Section(normalized, "weakness-plan");
        var card2 = (plan["questions"] as JsonArray)!.OfType<JsonObject>()
            .First(q => q["question"]!.GetValue<string>() == StuckQ2);
        card2["stdTechnical"]!.GetValue<string>().Should().Be(StuckAnswer2);
        card2["note"]!.GetValue<string>().Should().Be(StuckReason2);
        card2["weaknessRelated"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Normalize_Adversarial_BehavioralMisplaced_MovedToBehavioral()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", ["Tell me about a time you resolved a team conflict."]),
            ("design-round", []), ("behavioral", []), ("weakness-plan", []),
            ("ask-back", []), ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        QuestionTexts(normalized, "tech-qa").Should()
            .NotContain("Tell me about a time you resolved a team conflict.");
        QuestionTexts(normalized, "behavioral").Should()
            .Contain("Tell me about a time you resolved a team conflict.");
    }

    [Fact]
    public void Normalize_Adversarial_BehavioralDuplicated_KeptOnlyInBehavioral()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var beh = "Tell me about a time you resolved a team conflict.";
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", [beh]), ("design-round", []),
            ("behavioral", [beh]), ("weakness-plan", []), ("ask-back", []),
            ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        QuestionTexts(normalized, "tech-qa").Should().NotContain(beh);
        QuestionTexts(normalized, "behavioral").Should().Equal(beh);
    }

    [Fact]
    public void Normalize_Adversarial_SmallTalkRemovedReportWide()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", ["How was your weekend?"]), ("design-round", []),
            ("behavioral", []), ("weakness-plan", []), ("ask-back", []),
            ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        (normalized["sections"] as JsonArray)!.OfType<JsonObject>()
            .SelectMany(s => (s["questions"] as JsonArray)!
                .Select(q => q!["question"]!.GetValue<string>()))
            .Should().NotContain("How was your weekend?");
    }

    [Fact]
    public void Normalize_Dedup_KeepsFirstCopyInCanonicalSection()
    {
        var data = Sample();
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var t = PrepReportLocalTexts.For("en");
        var q = "How does async await work under the hood?";
        var root = CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", [q, q]), ("design-round", []),
            ("behavioral", []), ("weakness-plan", []), ("ask-back", []),
            ("comp-process", []), ("closing", []));
        var normalized = PrepReportNormalizer.Normalize(root, data, "en", t, cls);
        QuestionTexts(normalized, "tech-qa").Where(x => x == q).Should().HaveCount(1);
    }
}

/// <summary>本地 builder 与提示词断言(离线)。</summary>
public sealed class PrepReportTicket133LocalTests
{
    private static JsonObject Local(PrepReportSourceData data, string lang = "en")
    {
        var cls = PrepReportClassifier.Classify(data.Stage);
        var t = PrepReportLocalTexts.For(lang);
        var routed = data.QuestionBankEnabled
            ? PrepReportRouting.RouteQuestions(data.Questions, cls)
            : new RoutedPrepQuestions([], [], [], [], []);
        return PrepReportBuilders.BuildLocalReport(data, lang, t, cls, routed, new JsonObject());
    }

    [Fact]
    public void Local_DesignRound_Has12Sections_NoFilledSections()
    {
        var root = Local(PrepReportTicket133Tests.Sample());
        PrepReportTicket133Tests.SectionIds(root).Should().Equal(
            "round-brief", "greeting", "intro-listen", "self-intro", "deep-dive", "tech-qa",
            "design-round", "behavioral", "weakness-plan", "ask-back", "comp-process", "closing");
        root["structureVersion"]!.GetValue<int>().Should().Be(2);
        root.ContainsKey("filledSections").Should().BeFalse();
        root["generatedBy"]!.GetValue<string>().Should().Be("local-aggregation");
    }

    [Fact]
    public void Local_CodingRound_Has12Sections_CodingAtPosition7_DesignCardInTechQa()
    {
        var root = Local(PrepReportTicket133Tests.Sample(stage: "Coding"));
        var ids = PrepReportTicket133Tests.SectionIds(root);
        ids.Should().HaveCount(12);
        ids[6].Should().Be("coding-round");
        ids.Should().NotContain("design-round");
        var techQa = PrepReportTicket133Tests.Section(root, "tech-qa");
        (techQa["blocks"] as JsonArray)!.OfType<JsonObject>()
            .Select(b => b["title"]?.GetValue<string>())
            .Should().Contain("Design discussion card");
    }

    [Fact]
    public void Local_EmptyStage_Has11Sections_DesignCardInTechQa()
    {
        var root = Local(PrepReportTicket133Tests.Sample(stage: ""));
        var ids = PrepReportTicket133Tests.SectionIds(root);
        ids.Should().HaveCount(11);
        ids.Should().NotContain("coding-round").And.NotContain("design-round");
        var techQa = PrepReportTicket133Tests.Section(root, "tech-qa");
        (techQa["blocks"] as JsonArray)!.OfType<JsonObject>()
            .Select(b => b["title"]?.GetValue<string>())
            .Should().Contain("Design discussion card");
    }

    [Fact]
    public void Local_FactBlock_FirstInRoundBrief_AllFieldsThenMissingOmitted()
    {
        var root = Local(PrepReportTicket133Tests.Sample());
        var first = ((PrepReportTicket133Tests.Section(root, "round-brief")["blocks"] as JsonArray)!
            .First() as JsonObject)!;
        (first["items"] as JsonArray)!.Select(i => i!.GetValue<string>()).Should().Equal(
            "Company: MechanicsCo", "Role: Senior .NET Developer", "Round 2 · SystemDesign",
            "Interviewers: Ada Lovelace", "Format: Video", "Date: 2026-10-13");

        var sparse = PrepReportTicket133Tests.Sample() with
        {
            Interviewers = null, Format = null, ScheduledDate = null
        };
        var root2 = Local(sparse);
        var first2 = ((PrepReportTicket133Tests.Section(root2, "round-brief")["blocks"] as JsonArray)!
            .First() as JsonObject)!;
        (first2["items"] as JsonArray)!.Select(i => i!.GetValue<string>()).Should().Equal(
            "Company: MechanicsCo", "Role: Senior .NET Developer", "Round 2 · SystemDesign");
    }

    [Fact]
    public void Local_SelfIntro_ThreeScripts_CompanyFilled_NoSpecificExperience()
    {
        var root = Local(PrepReportTicket133Tests.Sample());
        var scripts = (PrepReportTicket133Tests.Section(root, "self-intro")["blocks"] as JsonArray)!
            .OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "script").ToList();
        scripts.Should().HaveCount(3);
        scripts.Select(b => b["title"]!.GetValue<string>()).Should()
            .Equal("30-second version", "60-second version", "120-second version");
        foreach (var s in scripts)
        {
            var text = s["text"]!.GetValue<string>();
            text.Should().Contain("MechanicsCo");
            text.Should().NotContain("Citigroup").And.NotContain("DealTrax");
        }
    }

    [Fact]
    public void Local_HonestEmptyStates()
    {
        var noProfile = Local(PrepReportTicket133Tests.Sample(profile: null));
        PrepReportTicket133Tests.BlockTexts(noProfile, "round-brief").Should()
            .Contain("No company background provided for this round");

        var noWeak = Local(PrepReportTicket133Tests.Sample(
            questions: PrepReportTicket133Tests.DefaultQuestions().Where(q => !q.GotStuck).ToList(),
            weaknesses: []));
        PrepReportTicket133Tests.QuestionTexts(noWeak, "weakness-plan").Should().BeEmpty();
        PrepReportTicket133Tests.BlockTexts(noWeak, "weakness-plan").Should()
            .Contain("No identified weaknesses or stuck questions yet");

        var noResume = Local(PrepReportTicket133Tests.Sample(resume: null));
        PrepReportTicket133Tests.BlockTexts(noResume, "behavioral").Should()
            .Contain("No resume provided — this section is a generic framework");
        (PrepReportTicket133Tests.Section(noResume, "behavioral")["blocks"] as JsonArray)!
            .OfType<JsonObject>().Select(b => b["type"]?.GetValue<string>())
            .Should().NotContain("script");
    }

    [Fact]
    public void Local_BankOff_DefaultListOnly_GeneralTags()
    {
        var root = Local(PrepReportTicket133Tests.Sample(bank: false));
        var techQa = PrepReportTicket133Tests.Section(root, "tech-qa");
        var qs = (techQa["questions"] as JsonArray)!.OfType<JsonObject>().ToList();
        qs.Should().HaveCount(6);
        qs.Select(q => q["question"]!.GetValue<string>().Contains("hash map")).Should().Contain(true);
        foreach (var q in qs)
            (q["sourceTags"] as JsonArray)!.Select(x => x!.GetValue<string>())
                .Should().Equal("General");
        PrepReportTicket133Tests.QuestionTexts(root, "behavioral").Should().BeEmpty();
        PrepReportTicket133Tests.QuestionTexts(root, "weakness-plan").Should().BeEmpty();
        // F-133-R1 缺口补断言：题库关 + 默认弱点存在时，弱点节只呈现空态。
        PrepReportTicket133Tests.BlockTexts(root, "weakness-plan").Should()
            .Contain("No identified weaknesses or stuck questions yet");
        PrepReportTicket133Tests.BlockTexts(root, "weakness-plan").Should()
            .NotContain("Hash map internals").And.NotContain("Answer structure");
    }

    [Fact]
    public void Local_BankOff_WithWeaknesses_WeaknessPlanEmptyState()
    {
        // F-133-R1：题库关时弱点一律不进报告（规格 §6.1），弱点节呈现空态（5.10）。
        var root = Local(PrepReportTicket133Tests.Sample(bank: false));
        PrepReportTicket133Tests.QuestionTexts(root, "weakness-plan").Should().BeEmpty();
        var texts = PrepReportTicket133Tests.BlockTexts(root, "weakness-plan");
        texts.Should().Contain("No identified weaknesses or stuck questions yet");
        texts.Should().NotContain("Hash map internals").And.NotContain("Answer structure")
            .And.NotContain("Cannot explain collision handling").And.NotContain("Jumps to conclusions");
    }

    [Fact]
    public void Local_Zh_LanguageRule_ScriptsAndAskBackEnglish_TitlesPrefixFree()
    {
        var root = Local(PrepReportTicket133Tests.Sample(stage: "Technical"), "zh");
        foreach (var id in PrepReportTicket133Tests.SectionIds(root))
        {
            var title = PrepReportTicket133Tests.Section(root, id)["title"]!.GetValue<string>();
            title.Any(char.IsDigit).Should().BeFalse($"节 {id} 标题不得带数字前缀:{title}");
        }
        var scripts = (PrepReportTicket133Tests.Section(root, "self-intro")["blocks"] as JsonArray)!
            .OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "script");
        foreach (var s in scripts)
        {
            var text = s["text"]!.GetValue<string>();
            text.Should().Contain("MechanicsCo");
            text.Any(c => c is >= '\u4e00' and <= '\u9fff').Should().BeFalse("自介话术行必须英文");
        }
        PrepReportTicket133Tests.BlockTexts(root, "ask-back").Should()
            .Contain(x => x.Contains("first 90 days"));
        PrepReportTicket133Tests.BlockTexts(root, "greeting").Should()
            .Contain(x => HasCjk(x));
    }

    private static bool HasCjk(string s) => s.Any(c => c is >= '\u4e00' and <= '\u9fff');

    [Fact]
    public void Local_CompProcess_NoMoneyNumbers()
    {
        var root = Local(PrepReportTicket133Tests.Sample(stage: "Technical"), "zh");
        var text = string.Join(" ",
            PrepReportTicket133Tests.BlockTexts(root, "comp-process")
                .Concat(PrepReportTicket133Tests.QuestionTexts(root, "comp-process")));
        System.Text.RegularExpressions.Regex.IsMatch(text, @"\d").Should().BeFalse(
            "薪资节不得出现任何数字(槽位 [range]/[date] 均无数字):" + text);
    }

    // ---------------- TB-8 提示词断言 ----------------

    [Fact]
    public void Prompt_SystemPrompt_SP2_AndSectionRules()
    {
        var sp = PrepReportPrompts.SystemPrompt("zh");
        sp.Should().Contain("SPOKEN LINES STAY IN ENGLISH");
        sp.Should().Contain("Before finishing, check: every script text and every stdSpoken is in English");
        sp.Should().NotContain("every natural-language value");
        foreach (var id in PrepReportSections.CanonicalOrder)
            sp.Should().Contain(id);
        sp.Should().Contain("Users should be able to");
        sp.Should().Contain("The system should be able to");
        sp.Should().Contain("three things about the company");
        sp.Should().Contain("keywords with no evidence");
        sp.Should().Contain("ONLY slot scaffolds");
        sp.Should().Contain("NEVER write any concrete experience");
        sp.Should().Contain("five steps, exactly");
        sp.Should().Contain("five beats");
        sp.Should().Contain("four beats (one line of definition");
    }

    [Fact]
    public void Prompt_UserPrompt_ClassificationLine_AndGroupedSupply()
    {
        var data = PrepReportTicket133Tests.Sample() with
        {
            FetchedItems = [("MechanicsCo interview experience", "https://example.com/exp")]
        };
        var cls = PrepReportClassifier.Classify("SystemDesign");
        var prompt = PrepReportPrompts.UserPrompt(data, "en", cls);
        prompt.Should().Contain("Round classification: coding=false, design=true");
        prompt.Should().Contain(PrepReportTicket133Tests.StuckQ);
        prompt.Should().Contain("Could not explain collision handling");
        prompt.Should().Contain("[TechnicalDepth · severity 3]");
        prompt.Should().Contain("Round 2 / SystemDesign");
        prompt.Should().Contain("also ground for round-brief");
        prompt.Should().Contain("MechanicsCo interview experience");
    }
}

/// <summary>真实样本导出缝(规格 §15.0):PREP_REPORT_SAMPLE_EXPORT 指向只读真实记录导出 JSON 时,
/// TB-5/TB-7/TB-10 按真实内容断言;缺导出时跳过(登记于 VERIFY-133.md),绝不虚构样本。</summary>
public static class PrepReportSampleExport
{
    public sealed record Export(
        string CompanyName, string Role, string? CompanyProfile, int RoundOrder, string Stage,
        string? Interviewers, string? Format, string? ScheduledDate,
        string? RequestJd, string? EntryJd, string? EntryJdSummary, string? ResumeText,
        string? Experiences, bool QuestionBankEnabled, bool CompanyBackgroundEnabled,
        List<(string Title, string Url)> FetchedItems,
        List<PrepQuestionInput> Questions, List<PrepWeaknessInput> Weaknesses);

    public static Export? TryLoad()
    {
        var path = Environment.GetEnvironmentVariable("PREP_REPORT_SAMPLE_EXPORT");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string? S(string k) => root[k]?.GetValue<string>();
        var questions = new List<PrepQuestionInput>();
        foreach (var q in (root["questions"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            questions.Add(new PrepQuestionInput(
                q["text"]!.GetValue<string>(),
                Enum.Parse<QuestionCategory>(q["category"]!.GetValue<string>()),
                q["gotStuck"]?.GetValue<bool>() ?? false,
                q["stuckReason"]?.GetValue<string>(),
                q["recommendedAnswer"]?.GetValue<string>(),
                q["sequence"]?.GetValue<int>() ?? 0));
        var weaknesses = new List<PrepWeaknessInput>();
        foreach (var w in (root["weaknesses"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            weaknesses.Add(new PrepWeaknessInput(
                Enum.Parse<WeaknessCategory>(w["category"]!.GetValue<string>()),
                w["title"]!.GetValue<string>(), w["detail"]?.GetValue<string>(),
                w["suggestion"]?.GetValue<string>(), w["severity"]?.GetValue<int>() ?? 0));
        var fetched = new List<(string, string)>();
        foreach (var f in (root["fetchedItems"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            fetched.Add((f["title"]!.GetValue<string>(), f["url"]!.GetValue<string>()));
        return new Export(
            S("companyName") ?? "", S("role") ?? "", S("companyProfile"),
            root["roundOrder"]?.GetValue<int>() ?? 1, S("stage") ?? "",
            S("interviewers"), S("format"), S("scheduledDate"),
            S("requestJd"), S("entryJd"), S("entryJdSummary"), S("resumeText"),
            S("experiences"), root["questionBankEnabled"]?.GetValue<bool>() ?? true,
            root["companyBackgroundEnabled"]?.GetValue<bool>() ?? true,
            fetched, questions, weaknesses);
    }

    public static PrepReportSourceData ToData(Export e) => new(
        e.CompanyName, e.Role, e.CompanyProfile, e.RoundOrder, e.Stage,
        e.Interviewers, e.Format,
        e.ScheduledDate is { } ds && DateOnly.TryParse(ds, out var d) ? d : null,
        e.RequestJd, e.EntryJd, e.EntryJdSummary, e.ResumeText, e.Experiences,
        e.QuestionBankEnabled, e.CompanyBackgroundEnabled, e.FetchedItems,
        e.Questions, e.Weaknesses);
}

public sealed class PrepReportTicket133ExportTests
{
    private static JsonObject Local(PrepReportSourceData data)
    {
        var cls = PrepReportClassifier.Classify(data.Stage);
        var t = PrepReportLocalTexts.For("en");
        var routed = data.QuestionBankEnabled
            ? PrepReportRouting.RouteQuestions(data.Questions, cls)
            : new RoutedPrepQuestions([], [], [], [], []);
        return PrepReportBuilders.BuildLocalReport(data, "en", t, cls, routed, new JsonObject());
    }

    [Fact]
    public void Export_TB5_RoutingLandingPoints()
    {
        var exp = PrepReportSampleExport.TryLoad();
        if (exp is null) return; // 无真实导出:跳过并登记,绝不虚构
        var data = PrepReportSampleExport.ToData(exp);
        var cls = PrepReportClassifier.Classify(data.Stage);
        var root = Local(data);
        foreach (var q in data.Questions)
        {
            var target = PrepReportRouting.CanonicalSection(q, cls);
            if (target is null)
            {
                var all = PrepReportTicket133Tests.SectionIds(root)
                    .SelectMany(id => PrepReportTicket133Tests.QuestionTexts(root, id));
                all.Should().NotContain(q.Text);
            }
            else
            {
                PrepReportTicket133Tests.QuestionTexts(root, target).Should().Contain(q.Text);
                foreach (var other in PrepReportTicket133Tests.SectionIds(root))
                    if (other != target)
                        PrepReportTicket133Tests.QuestionTexts(root, other).Should().NotContain(q.Text);
            }
        }
    }

    [Fact]
    public void Export_TB7_FactBlock_AndSelfIntro_AndWeaknessLandings()
    {
        var exp = PrepReportSampleExport.TryLoad();
        if (exp is null) return;
        var data = PrepReportSampleExport.ToData(exp);
        var root = Local(data);
        var first = ((PrepReportTicket133Tests.Section(root, "round-brief")["blocks"] as JsonArray)!
            .First() as JsonObject)!;
        var items = (first["items"] as JsonArray)!.Select(i => i!.GetValue<string>()).ToList();
        items.Should().Contain($"Company: {data.CompanyName}");
        items.Should().Contain($"Role: {data.Role}");
        if (!string.IsNullOrWhiteSpace(data.CompanyProfile))
            string.Join(" ", PrepReportTicket133Tests.BlockTexts(root, "round-brief")).Should()
                .Contain(data.CompanyProfile[..Math.Min(50, data.CompanyProfile.Length)]);
        else
            PrepReportTicket133Tests.BlockTexts(root, "round-brief").Should()
                .Contain("No company background provided for this round");
        var scripts = (PrepReportTicket133Tests.Section(root, "self-intro")["blocks"] as JsonArray)!
            .OfType<JsonObject>().Where(b => b["type"]?.GetValue<string>() == "script");
        foreach (var s in scripts)
            s["text"]!.GetValue<string>().Should().Contain(data.CompanyName);
        foreach (var w in data.Weaknesses)
        {
            var inWeaknessPlan = PrepReportTicket133Tests.BlockTexts(root, "weakness-plan")
                .Any(x => x.Contains(w.Title));
            inWeaknessPlan.Should().BeTrue($"弱点 {w.Title} 应落在 weakness-plan");
        }
    }

    [Fact]
    public void Export_TB10_EmptyFaces_AndMoneyDiscipline()
    {
        var exp = PrepReportSampleExport.TryLoad();
        if (exp is null) return;
        var data = PrepReportSampleExport.ToData(exp);
        var root = Local(data);
        if (data.Weaknesses.Count == 0 && !data.Questions.Any(q => q.GotStuck))
        {
            PrepReportTicket133Tests.QuestionTexts(root, "weakness-plan").Should().BeEmpty();
            PrepReportTicket133Tests.BlockTexts(root, "weakness-plan").Should()
                .Contain("No identified weaknesses or stuck questions yet");
        }
        if (string.IsNullOrWhiteSpace(data.ResumeText))
        {
            (PrepReportTicket133Tests.Section(root, "behavioral")["blocks"] as JsonArray)!
                .OfType<JsonObject>().Select(b => b["type"]?.GetValue<string>())
                .Should().NotContain("script");
        }
        var comp = string.Join(" ",
            PrepReportTicket133Tests.BlockTexts(root, "comp-process")
                .Concat(PrepReportTicket133Tests.QuestionTexts(root, "comp-process")));
        System.Text.RegularExpressions.Regex.IsMatch(comp, @"\$\d|\d{4,}").Should().BeFalse(
            "薪资节不得出现金额数字:" + comp);
    }
}

/// <summary>Handle 黑盒(DB 门控):AI 路结构 + 提示词捕获 + 本地兜底 + 卡题归一化。</summary>
public sealed class PrepReportTicket133DbTests : IAsyncLifetime
{
    private static readonly string Cs = Environment.GetEnvironmentVariable("INTERVIEWS_TEST_CS")
        ?? "Host=localhost;Port=5432;Database=yourinterview;Username=postgres";
    private InterviewsDbContext _db = null!;
    private bool _available;

    private sealed class CaptureAi : IAiGatewayClient
    {
        public string? Purpose, Prompt, SystemPrompt;
        public double? Temperature;
        public int? MaxTokens;
        public string CannedJson = "{}";
        public bool Throw;
        public int Calls;

        public Task<AiCompletionResult> CompleteAsync(string purpose, string prompt,
            string? systemPrompt, double? temperature, int? maxTokens, CancellationToken ct)
        {
            Calls++;
            Purpose = purpose; Prompt = prompt; SystemPrompt = systemPrompt;
            Temperature = temperature; MaxTokens = maxTokens;
            if (Throw) throw new AiGatewayException("not configured", isConfigurationError: true);
            return Task.FromResult(new AiCompletionResult(CannedJson, "fake-model", 10, 20, 5));
        }

        public Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct)
            => Task.FromResult(new PrepSourceSettingsDto(true, true, false, false, null, false, null, null));

        public Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string source, string company,
            string? role, string? stage, CancellationToken ct)
            => Task.FromResult(new PrepSourceSearchDto(source, "disabled", null, [], null));
    }

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        _db = new InterviewsDbContext(opts, new TestCurrentUser());
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<(InterviewEntry Entry, InterviewRound Round)> NewEntryAsync(string stage)
    {
        var e = new InterviewEntry(Guid.NewGuid(), "MechanicsCo-" + Guid.NewGuid().ToString("N")[..6],
            "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        var round = e.AddRound(stage);
        await _db.SaveChangesAsync();
        e.UpdateRound(round.Id, stage, null, "Ada Lovelace", "Video", null,
            InterviewRoundOutcome.Pending, null, null);
        await _db.SaveChangesAsync();
        return (e, round);
    }

    private static string CannedDesignJson()
    {
        var ids = new[]
        {
            "round-brief", "greeting", "intro-listen", "self-intro", "deep-dive", "tech-qa",
            "design-round", "behavioral", "weakness-plan", "ask-back", "comp-process", "closing"
        };
        return PrepReportTicket133Tests.CannedRoot(ids.Select(id => (id, Array.Empty<string>())).ToArray())
            .ToJsonString();
    }

    [Fact]
    public async Task Handle_AiPath_Structure_PromptCapture_Sources()
    {
        if (!_available) return;
        var (e, round) = await NewEntryAsync("SystemDesign");
        var ai = new CaptureAi { CannedJson = CannedDesignJson() };
        var handler = new GeneratePrepReportCommandHandler(_db, ai);
        var result = await handler.Handle(
            new GeneratePrepReportCommand(Guid.Empty, e.Id, round.Id), default);

        result.IsSuccess.Should().BeTrue();
        var root = JsonNode.Parse(result.Value!)!.AsObject();
        PrepReportTicket133Tests.SectionIds(root).Should().Equal(
            "round-brief", "greeting", "intro-listen", "self-intro", "deep-dive", "tech-qa",
            "design-round", "behavioral", "weakness-plan", "ask-back", "comp-process", "closing");
        root["structureVersion"]!.GetValue<int>().Should().Be(2);
        root.ContainsKey("filledSections").Should().BeFalse();
        root["generatedBy"]!.GetValue<string>().Should().Be("fake-model");
        ai.Purpose.Should().Be("round-prep-report");
        ai.MaxTokens.Should().Be(16000);
        ai.Temperature.Should().Be(0.5);
        ai.SystemPrompt.Should().Contain("SPOKEN LINES STAY IN ENGLISH");
        ai.SystemPrompt.Should().NotContain("every natural-language value");
        ai.Prompt.Should().Contain("Round classification: coding=false, design=true");
        var sources = root["sources"]!.AsObject();
        sources["pastQuestions"]!.GetValue<int>().Should().Be(0);
        sources["weaknesses"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Handle_AiFailure_FallsBackToLocal()
    {
        if (!_available) return;
        var (e, round) = await NewEntryAsync("SystemDesign");
        var ai = new CaptureAi { Throw = true };
        var handler = new GeneratePrepReportCommandHandler(_db, ai);
        var result = await handler.Handle(
            new GeneratePrepReportCommand(Guid.Empty, e.Id, round.Id), default);

        result.IsSuccess.Should().BeTrue();
        var root = JsonNode.Parse(result.Value!)!.AsObject();
        root["generatedBy"]!.GetValue<string>().Should().Be("local-aggregation");
        root["structureVersion"]!.GetValue<int>().Should().Be(2);
        PrepReportTicket133Tests.SectionIds(root).Should().HaveCount(12);
    }

    [Fact]
    public async Task Handle_AiMisplacedStuck_NormalizedToWeaknessPlan()
    {
        if (!_available) return;
        var (e, round) = await NewEntryAsync("SystemDesign");
        e.AttachAsset(AssetKind.Transcript, "transcript.txt", "text/plain", 100, null, null,
            transcriptText: "candidate interview transcript text");
        e.StartAnalysis();
        e.ApplyAnalysis(80, null, 0, 0, 70, 80, "ok",
            questions:
            [
                new QuestionDraft(1, PrepReportTicket133Tests.StuckQ, null,
                    QuestionCategory.Technical, 3, null, true,
                    "Could not explain collision handling",
                    "Buckets plus hash function.", null)
            ],
            weaknesses:
            [
                new WeaknessDraft(WeaknessCategory.TechnicalDepth, "Hash map internals",
                    "Cannot explain collision handling", null, 3, "Study hash maps")
            ]);
        await _db.SaveChangesAsync();
        var misplaced = PrepReportTicket133Tests.CannedRoot(
            ("round-brief", []), ("greeting", []), ("intro-listen", []), ("self-intro", []),
            ("deep-dive", []), ("tech-qa", [PrepReportTicket133Tests.StuckQ]),
            ("design-round", []), ("behavioral", []), ("weakness-plan", []),
            ("ask-back", []), ("comp-process", []), ("closing", []));
        var ai = new CaptureAi { CannedJson = misplaced.ToJsonString() };
        var handler = new GeneratePrepReportCommandHandler(_db, ai);
        var result = await handler.Handle(
            new GeneratePrepReportCommand(Guid.Empty, e.Id, round.Id), default);

        result.IsSuccess.Should().BeTrue();
        var root = JsonNode.Parse(result.Value!)!.AsObject();
        PrepReportTicket133Tests.QuestionTexts(root, "tech-qa").Should()
            .NotContain(PrepReportTicket133Tests.StuckQ);
        PrepReportTicket133Tests.QuestionTexts(root, "weakness-plan").Should()
            .Contain(PrepReportTicket133Tests.StuckQ);
    }
}
