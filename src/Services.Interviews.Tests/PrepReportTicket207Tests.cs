using System.Text.Json.Nodes;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票207：真增量合并 + 版本快照 + 机经 Snippet 透传 + 增量提示词。
/// 全部离线断言（DTO / JsonObject 直测，不碰数据库）；文本一律为机制占位内容。
/// </summary>
public sealed class PrepReportTicket207Tests
{
    private static JsonObject Stored() => JsonNode.Parse("""
        {
          "generatedAt": "2026-10-20T00:00:00Z",
          "sections": [
            { "id": "round-brief", "title": "Round brief",
              "blocks": [{ "type": "points", "items": ["Acme sells widgets"] }],
              "questions": [] },
            { "id": "tech-qa", "title": "Technical Q&A",
              "blocks": [{ "type": "points", "items": ["Know hash maps"], "userText": "My edited note" }],
              "questions": [{ "question": "What is a hash map?", "category": "Technical" }] }
          ]
        }
        """)!.AsObject();

    private static JsonObject Delta() => JsonNode.Parse("""
        {
          "sections": [
            { "id": "round-brief", "points": ["Acme sells widgets", "Acme is expanding to Europe"] },
            { "id": "tech-qa",
              "points": ["  know   HASH maps "],
              "questions": [
                { "question": "What is a hash map?", "category": "Technical" },
                { "question": "Explain CAP theorem.", "category": "Technical",
                  "stdSpoken": "Spoken CAP.", "stdTechnical": "Technical CAP." }
              ] },
            { "id": "weakness-plan", "title": "Weakness plan",
              "points": ["Study hash maps weekly"] }
          ]
        }
        """)!.AsObject();

    [Fact]
    public void Merge_KeepsExistingBlocksVerbatim_AppendsOnlyNewPoints()
    {
        var stored = Stored();
        var before = stored.ToJsonString();
        var result = PrepReportIncremental.Merge(stored, Delta(), "generate", "2026-10-26T00:00:00Z");

        // 已有块一字不改（含用户编辑版 userText）。
        var brief = stored["sections"]![0]!.AsObject();
        brief["blocks"]![0]!["items"]![0]!.GetValue<string>().Should().Be("Acme sells widgets");
        var techQa = stored["sections"]![1]!.AsObject();
        techQa["blocks"]![0]!["userText"]!.GetValue<string>().Should().Be("My edited note");

        // 重复要点（原文、大小写/空白变体）均被归一化去重，只追加真正的新要点。
        result.PointsAdded.Should().Be(2); // Europe 要点 + weakness-plan 要点
        var briefBlocks = brief["blocks"]!.AsArray();
        briefBlocks.Count.Should().Be(2);
        var appended = briefBlocks[1]!.AsObject();
        appended["items"]![0]!.GetValue<string>().Should().Be("Acme is expanding to Europe");
        appended["provenance"]!["at"]!.GetValue<string>().Should().Be("2026-10-26T00:00:00Z");
        appended["provenance"]!["source"]!.GetValue<string>().Should().Be("generate");
        _ = before; // 存量 JSON 本身未被合并改写以外的路径动过（断言见下：题数）。
    }

    [Fact]
    public void Merge_DedupesQuestionsGlobally_CreatesMissingSections()
    {
        var stored = Stored();
        var result = PrepReportIncremental.Merge(stored, Delta(), "generate", "2026-10-26T00:00:00Z");

        result.QuestionsAdded.Should().Be(1);
        result.SectionsCreated.Should().Be(1);
        var techQa = stored["sections"]![1]!.AsObject();
        techQa["questions"]!.AsArray().Count.Should().Be(2);
        techQa["questions"]![1]!["question"]!.GetValue<string>().Should().Be("Explain CAP theorem.");
        techQa["questions"]![1]!["provenance"]!["source"]!.GetValue<string>().Should().Be("generate");
        var plan = stored["sections"]![2]!.AsObject();
        plan["id"]!.GetValue<string>().Should().Be("weakness-plan");
        plan["title"]!.GetValue<string>().Should().Be("Weakness plan");
    }

    [Fact]
    public void ToDelta_UsesOnlyPointsAndQuestions_SkipsScriptBlocks()
    {
        var local = JsonNode.Parse("""
            { "sections": [
              { "id": "self-intro", "title": "Self-introduction",
                "blocks": [
                  { "type": "script", "text": "Hi, I am a developer." },
                  { "type": "points", "items": ["Present first"] }
                ],
                "questions": [{ "question": "Tell me about yourself." }] }
            ] }
            """)!.AsObject();
        var delta = PrepReportIncremental.ToDelta(local);
        var sec = delta["sections"]![0]!.AsObject();
        sec["points"]!.AsArray().Count.Should().Be(1);
        sec["points"]![0]!.GetValue<string>().Should().Be("Present first");
        sec["questions"]!.AsArray().Count.Should().Be(1);
    }

    [Fact]
    public void History_SnapshotsOldStripsNestedHistory_RestorePopsLatest()
    {
        var v1 = """{"sections": [{"id": "greeting", "title": "Warm-up", "blocks": [], "questions": []}]}""";
        var v2 = """{"sections": [{"id": "greeting", "title": "Warm-up v2", "blocks": [], "questions": []}]}""";
        var v3 = """{"sections": [{"id": "greeting", "title": "Warm-up v3", "blocks": [], "questions": []}]}""";

        var withH1 = PrepReportHistory.WithHistory(v2, v1);
        var withH2 = PrepReportHistory.WithHistory(v3, withH1);
        var restored = PrepReportHistory.RestoreLatest(withH2);
        restored.Should().NotBeNull();
        restored.Should().Contain("Warm-up v2");
        restored.Should().NotContain("Warm-up v3");
        // 再弹一次回到 v1；弹空返回 null。
        var restored2 = PrepReportHistory.RestoreLatest(restored!);
        restored2.Should().Contain("Warm-up");
        restored2.Should().NotContain("v2");
        PrepReportHistory.RestoreLatest(restored2!).Should().BeNull();
        // 首轮创建无旧根：不包 history。
        PrepReportHistory.WithHistory(v1, null).Should().NotContain("history");
    }

    [Fact]
    public void History_CapsAtFiveEntries()
    {
        var current = """{"sections": []}""";
        for (var i = 0; i < 7; i++)
            current = PrepReportHistory.WithHistory($@"{{""sections"": [], ""v"": {i}}}", current);
        var root = JsonNode.Parse(current)!.AsObject();
        (root["history"] as JsonArray)!.Count.Should().Be(5);
    }

    [Fact]
    public void UserPrompt_ContainsSnippetAndCommunications()
    {
        var data = PrepReportTicket133Tests.Sample() with
        {
            FetchedItems = [("Acme interview experience", "https://example.com/exp", "Asked about caching layers")],
            CommunicationsText = "Thread: Onsite invite\n- [Email] Ada @ 2026-10-20 | arrangement: 2026-10-24 Video | Onsite round",
            CommunicationsStatus = "ok",
            CommunicationsCount = 1
        };
        var cls = PrepReportClassifier.Classify("Technical");
        var prompt = PrepReportPrompts.UserPrompt(data, "en", cls);
        prompt.Should().Contain("Asked about caching layers");
        prompt.Should().Contain("Onsite invite");
        prompt.Should().Contain("https://example.com/exp");
    }

    [Fact]
    public void IncrementalPrompt_ContainsExistingReportAndDeltaOnlyRule()
    {
        var data = PrepReportTicket133Tests.Sample();
        var cls = PrepReportClassifier.Classify("Technical");
        var prompt = PrepReportPrompts.IncrementalPrompt(data, "en", cls, Stored());
        prompt.Should().Contain("INCREMENTAL UPDATE ONLY");
        prompt.Should().Contain("Acme sells widgets");
        prompt.Should().Contain("What is a hash map?");
        prompt.Should().Contain("Do NOT rewrite");
    }
}
