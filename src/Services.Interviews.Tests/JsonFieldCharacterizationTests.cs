using System.Text;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票19 ARC-06 特征测试(characterization):迁移**前**在基线 cb37c52 上写就并全绿,
/// 锁定 16 个 *Json 大字段在**聚合属性 / DTO 层**的现状行为 —— 写入什么原文,
/// 读回什么原文(逐字相等),与存储层是否加版本信封无关。迁移后必须保持全绿。
///
/// boundary 类别标注:空/null、空对象/空数组、损坏 JSON、Unicode/emoji、超大 payload。
/// </summary>
public sealed class JsonFieldCharacterizationTests
{
    // ---- 真实形态样本(形状依据见 evidence-19/inventory.md)----
    private const string Debrief = "{\"text\":\"复盘:系统设计答得一般 😅 下次先画图再开口\"}";
    private const string QuestionsToAsk = "[{\"text\":\"What does success look like in 6 months?\",\"checked\":true},{\"text\":\"团队规模?\",\"checked\":false}]";
    private const string SpeechMetrics = "{\"durationSeconds\":612.5,\"wordCount\":1450,\"wordsPerMinute\":142.0,\"averageSentenceLength\":14.2,\"shortSentenceCount\":9,\"fillerWordCount\":23,\"fillerWordBreakdown\":{\"um\":15,\"uh\":8},\"selfRepetitionCount\":3,\"longPauseCount\":4,\"totalSilenceSeconds\":21.5,\"pronunciationAccuracy\":0.87,\"lowScoreWordRatio\":0.06,\"problemWords\":[{\"word\":\"throughput\",\"accuracyScore\":0.42,\"fluencyScore\":null,\"startSeconds\":31.0,\"endSeconds\":31.6}],\"structureMarkers\":null}";
    private const string Segments = "[{\"idx\":0,\"speaker\":\"Interviewer\",\"start\":0.0,\"end\":2.5,\"text\":\"Tell me about yourself.\",\"pitchHz\":120.5},{\"idx\":1,\"speaker\":\"Forrest\",\"start\":2.6,\"end\":9.1,\"text\":\"Sure — I led the DealTrax migration…\",\"pitchHz\":98.2}]";
    private const string Meta = "{\"kind\":\"email\",\"from\":\"khalil@corpay.com\",\"subject\":\"Interview invite\",\"summary\":\"约周四上午视频面\"}";
    private const string QuestionReview = "{\"overall\":72,\"verdict\":\"solid\",\"dims\":{\"relevance\":{\"score\":80,\"comment\":\"on point\",\"fix\":\"\"}},\"stdSpoken\":{\"ai\":\"I would start with…\",\"user\":null},\"stdTechnical\":{\"ai\":\"Key points: …\",\"user\":\"我的版本\"}}";
    private const string RoundReview = "{\"overall\":78,\"passProbability\":0.65,\"passBasis\":\"两轮技术面均通过\",\"strongest\":\"系统设计\",\"weakest\":\"发音\",\"summary\":\"整体稳\"}";
    private const string PrepReport = "{\"generatedAt\":\"2026-10-20T00:00:00.0000000+00:00\",\"generatedBy\":\"local-aggregation\",\"sources\":{\"jd\":{\"key\":\"jd\",\"status\":\"ok\"}},\"sections\":[],\"questions\":[]}";

    private static InterviewEntry NewEntry() => new(Guid.NewGuid(), "Corpay", "Senior .NET Developer");

    // ---------- InterviewEntry 三字段 ----------

    [Fact] // round-trip:真实形态
    public void Debrief_RoundTripsVerbatim()
    {
        var e = NewEntry();
        e.SetDebrief(Debrief);
        e.DebriefJson.Should().Be(Debrief);
        e.ToDto().DebriefJson.Should().Be(Debrief);
    }

    [Fact] // boundary:空/null、空对象、损坏 JSON、Unicode —— Debrief 全透传不校验
    public void Debrief_BoundarySamples_PassThroughVerbatim()
    {
        var e = NewEntry();
        foreach (var s in new string?[] { null, "", " ", "{}", "{oops", "{\"text\":\"🎉 éè 中文\"}" })
        {
            e.SetDebrief(s);
            e.DebriefJson.Should().Be(s);
        }
    }

    [Fact] // boundary:超大 payload(5000 字复盘)
    public void Debrief_LargePayload_RoundTrips()
    {
        var big = "{\"text\":\"" + new string('复', 5000) + "\"}";
        var e = NewEntry();
        e.SetDebrief(big);
        e.DebriefJson.Should().Be(big);
    }

    [Fact] // round-trip + 可选字段协议:null 不覆盖已有值
    public void QuestionsToAsk_RoundTrips_AndNullDoesNotOverwrite()
    {
        var e = NewEntry();
        e.UpdateBasicInfo("Corpay", "SDE", null, null, null, 1, null, null, null, null, null, null,
            QuestionsToAsk);
        e.QuestionsToAskJson.Should().Be(QuestionsToAsk);
        e.ToDto().QuestionsToAskJson.Should().Be(QuestionsToAsk);

        e.UpdateBasicInfo("Corpay", "SDE", null, null, null, 1, null, null, null, null, null, null,
            null);
        e.QuestionsToAskJson.Should().Be(QuestionsToAsk);
    }

    // ---------- 分析回写:SpeechMetrics + 题目三标签 + 快照两字段 ----------

    private static InterviewEntry AnalyzedEntry(string? metrics, string summary = "Overall solid. Watch pacing.")
    {
        var e = NewEntry();
        e.AttachAsset(AssetKind.Transcript, "notes.txt", "text/plain", 10, null, null,
            transcriptText: "Interviewer: hi. Forrest: hello.");
        e.StartAnalysis();
        e.ApplyAnalysis(80, 70, 75, 78, 82, 79, summary,
            questions: [new QuestionDraft(1, "Tell me about yourself", "I led…",
                QuestionCategory.Behavioral, 3, "structured", false, null, "Use STAR", 2.6,
                WeaknessTags: "[\"Structure\",\"Terminology\"]",
                FollowUpQuestionsJson: "[\"How would you scale it?\"]",
                MissedPointsJson: "[\"Did not mention trade-offs\"]")],
            weaknesses: null, speechMetricsJson: metrics);
        return e;
    }

    [Fact] // round-trip:SpeechMetrics 原文保留 + DTO 解析出强类型指标
    public void SpeechMetrics_RoundTrips_AndDtoParses()
    {
        var e = AnalyzedEntry(SpeechMetrics);
        e.SpeechMetricsJson.Should().Be(SpeechMetrics);
        var dto = e.ToDto();
        dto.SpeechMetrics.Should().NotBeNull();
        dto.SpeechMetrics!.WordCount.Should().Be(1450);
        dto.SpeechMetrics.FillerWordBreakdown!["um"].Should().Be(15);
        dto.SpeechMetrics.ProblemWords![0].Word.Should().Be("throughput");
    }

    [Fact] // boundary:损坏 JSON —— 原文照存,DTO 解析失败回 null(现状容错)
    public void SpeechMetrics_Corrupt_KeepsRaw_DtoNull()
    {
        var e = AnalyzedEntry("{oops");
        e.SpeechMetricsJson.Should().Be("{oops");
        e.ToDto().SpeechMetrics.Should().BeNull();
    }

    [Fact] // boundary:空/空白 —— 不覆盖(保持 null)
    public void SpeechMetrics_Blank_NotStored()
    {
        AnalyzedEntry("").SpeechMetricsJson.Should().BeNull();
        AnalyzedEntry(null).SpeechMetricsJson.Should().BeNull();
    }

    [Fact] // round-trip:题目三标签经 ApplyAnalysis→SetTags 落盘读回
    public void QuestionTags_RoundTrip()
    {
        var q = AnalyzedEntry(SpeechMetrics).Questions.Single();
        q.WeaknessTagsJson.Should().Be("[\"Structure\",\"Terminology\"]");
        q.FollowUpQuestionsJson.Should().Be("[\"How would you scale it?\"]");
        q.MissedPointsJson.Should().Be("[\"Did not mention trade-offs\"]");
        var dto = q.ToDto();
        dto.WeaknessTagsJson.Should().Be(q.WeaknessTagsJson);
        dto.FollowUpQuestionsJson.Should().Be(q.FollowUpQuestionsJson);
        dto.MissedPointsJson.Should().Be(q.MissedPointsJson);
    }

    [Fact] // round-trip:快照 Dimensions/SummaryBullets 与 PlaybookText 构建器输出一致
    public void Snapshot_FieldsMatchBuilders()
    {
        // 快照由 ApplyAnalysis 的 Handler 单独记(聚合 ApplyAnalysis 本身不建快照)—— 此处照 Handler 口径补记
        var e = AnalyzedEntry(SpeechMetrics, "整体不错。发音要练。逻辑清晰。");
        e.AddAnalysisSnapshot(80,
            PlaybookText.BuildDimensionsJson(80, 70, 75, 78, 82, 79),
            PlaybookText.SummaryToBulletsJson("整体不错。发音要练。逻辑清晰。"));
        var s = e.Snapshots.Single();
        s.DimensionsJson.Should().Be(
            PlaybookText.BuildDimensionsJson(80, 70, 75, 78, 82, 79));
        s.SummaryBulletsJson.Should().Be(
            PlaybookText.SummaryToBulletsJson("整体不错。发音要练。逻辑清晰。"));
        s.DimensionsJson.Should().Contain("\"pronunciation\":70");
    }

    // ---------- InterviewAsset 两字段 ----------

    [Fact] // round-trip:挂材料时 segments + meta 原文保留
    public void Asset_SegmentsAndMeta_RoundTripOnAttach()
    {
        var e = NewEntry();
        var a = e.AttachAsset(AssetKind.Transcript, "sub.vtt", "text/vtt", 100, null, null,
            transcriptText: "hello", transcriptSegmentsJson: Segments, metaJson: Meta);
        a.TranscriptSegmentsJson.Should().Be(Segments);
        a.MetaJson.Should().Be(Meta);
        a.ToDto().MetaJson.Should().Be(Meta);
    }

    [Fact] // boundary:meta 空白不写入(现状守卫);UpdateAssetMeta 可清空/改写
    public void Asset_Meta_BlankGuard_AndUpdate()
    {
        var e = NewEntry();
        var a = e.AttachAsset(AssetKind.Email, "mail.txt", "text/plain", 10, null, null,
            transcriptText: "body", metaJson: "   ");
        a.MetaJson.Should().BeNull();

        e.UpdateAssetMeta(a.Id, Meta);
        a.MetaJson.Should().Be(Meta);
        e.UpdateAssetMeta(a.Id, null);
        a.MetaJson.Should().BeNull();
    }

    [Fact] // round-trip + 转写完成回写 segments;boundary:超大 payload(1500 段)
    public void Asset_CompleteTranscription_LargeSegments()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < 1500; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"idx\":").Append(i).Append(",\"speaker\":\"S").Append(i % 2)
              .Append("\",\"start\":").Append(i).Append(".0,\"end\":").Append(i + 1)
              .Append(".0,\"text\":\"段落 ").Append(i).Append(" 🎙\"}");
        }
        var big = sb.Append(']').ToString();

        var e = NewEntry();
        var a = e.AttachAsset(AssetKind.Audio, "rec.mp3", "audio/mpeg", 1000, "/tmp/rec.mp3", null);
        e.CompleteTranscription(a.Id, "full text", big);
        a.TranscriptSegmentsJson.Should().Be(big);
    }

    [Fact] // 说话人改名:正则只改 speaker 值,产物格式即现状("speaker": "X" 带空格)
    public void RenameSpeaker_OnlySpeakerValuesChange()
    {
        var e = NewEntry();
        var r = e.AddRound("Technical");
        var segs = "[{\"idx\":0,\"speaker\":\"Speaker 1\",\"start\":0,\"end\":1,\"text\":\"hi\"}," +
                   "{\"idx\":1,\"speaker\":\"Speaker 2\",\"start\":1,\"end\":2,\"text\":\"hello\"}]";
        e.AttachAsset(AssetKind.Transcript, "t.txt", "text/plain", 10, null, null,
            transcriptText: "hi hello", transcriptSegmentsJson: segs, roundId: r.Id);

        e.RenameSpeakerInRound(r.Id, "Speaker 1", "Khalil").Should().Be(1);
        e.Assets.Single().TranscriptSegmentsJson.Should().Be(
            "[{\"idx\":0,\"speaker\": \"Khalil\",\"start\":0,\"end\":1,\"text\":\"hi\"}," +
            "{\"idx\":1,\"speaker\":\"Speaker 2\",\"start\":1,\"end\":2,\"text\":\"hello\"}]");
    }

    // ---------- InterviewQuestion.ReviewJson / InterviewRound 五字段 ----------

    [Fact] // round-trip:逐题复盘 + 整轮复盘 + 备考报告,DTO 透传一致
    public void Reviews_AndPrepReport_RoundTrip()
    {
        var e = NewEntry();
        var r = e.AddRound("Technical");
        var q = e.AddQuestion("Design a cache", "I would…", roundId: r.Id);

        e.SetQuestionReview(q.Id, QuestionReview);
        e.SetRoundReview(r.Id, RoundReview);
        e.SetRoundPrepReport(r.Id, PrepReport);

        q.ReviewJson.Should().Be(QuestionReview);
        r.ReviewJson.Should().Be(RoundReview);
        r.PrepReportJson.Should().Be(PrepReport);
        var dto = r.ToDto();
        dto.ReviewJson.Should().Be(RoundReview);
        dto.PrepReportJson.Should().Be(PrepReport);
        q.ToDto().ReviewJson.Should().Be(QuestionReview);
    }

    [Fact] // boundary:复盘类字段 null/空/损坏全透传(后端不校验形状)
    public void Reviews_BoundarySamples_PassThrough()
    {
        var e = NewEntry();
        var r = e.AddRound("Technical");
        foreach (var s in new string?[] { null, "", "{}", "[]", "{bad json", "{\"a\":\"é🎉\"}" })
        {
            e.SetRoundReview(r.Id, s);
            r.ReviewJson.Should().Be(s);
            e.SetRoundPrepReport(r.Id, s);
            r.PrepReportJson.Should().Be(s);
        }
    }

    [Fact] // round-trip:轮次邮件(Handler 序列化形态);备考题/清单走 DB 特征测试(UpdateDetails 协议)
    public void Round_Emails_RoundTrip()
    {
        var e = NewEntry();
        var r = e.AddRound("Technical");
        var emails = "[{\"from\":\"khalil@corpay.com\",\"subject\":\"Interview invite\",\"date\":\"2026-10-10\",\"snippet\":\"Hi Forrest, …\"}]";
        e.SetRoundEmails(r.Id, emails);
        r.EmailsJson.Should().Be(emails);

        // 经聚合公开路径写入其余两字段(UpdateDetails 的部分更新协议由 DB 特征测试覆盖)
        e.SetRoundEmails(r.Id, null);
        r.EmailsJson.Should().BeNull();
    }
}
