using System.Text.Json;
using FluentAssertions;
using Xunit;
using YourInterview.SharedContracts.Analysis;

namespace YourInterview.Analysis.Worker.Tests;

/// <summary>
/// 票40(ARC-15)§7 特征锁:分析契约的线缆 JSON 形态逐字段锁定。
///
/// 改前样本(OldWebJson/OldDefaultJson)即合一之前 Worker 本地副本
/// (PipelineContracts.SpeechMetrics 等)序列化出的形态:
///   · Web 口径 = WriteBack 的 JsonContent.Create 默认(Web defaults,camelCase);
///   · Default 口径 = AnalysisPipeline.PersistAsync 的 analysis.json 落盘形(PascalCase)。
/// 合一后契约类型迁 SharedContracts.Analysis —— 本文件届时仅改 using,
/// 样本字符串与逐字段断言一律不动:序列化兼容是本票生命线,任何字段
/// 名/类型/可空性/默认值/顺序漂移都会在此变红。
/// </summary>
public sealed class ContractSerializationLockTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static SpeechMetrics FullMetrics() => new(
        DurationSeconds: 1500.5,
        WordCount: 320,
        WordsPerMinute: 145.5,
        AverageSentenceLength: 12.5,
        ShortSentenceCount: 4,
        FillerWordCount: 5,
        FillerWordBreakdown: new Dictionary<string, int> { ["um"] = 3, ["like"] = 2 },
        SelfRepetitionCount: 2,
        LongPauseCount: 3,
        TotalSilenceSeconds: 30.5,
        PronunciationAccuracy: 88.5,
        LowScoreWordRatio: 12.5,
        ProblemWords:
        [
            new WordPronunciation("kubernetes", 55.5, null, 10.5, 11),
            new WordPronunciation("cache", 62, 90.5, 20, 20.5)
        ],
        StructureMarkers: new Dictionary<string, int> { ["first"] = 2, ["however"] = 1 },
        HasAudio: false);

    // 改前形态(Web/camelCase):WriteBack speechMetrics 载荷的逐字段样本
    private const string OldWebJson =
        """{"durationSeconds":1500.5,"wordCount":320,"wordsPerMinute":145.5,"averageSentenceLength":12.5,"shortSentenceCount":4,"fillerWordCount":5,"fillerWordBreakdown":{"um":3,"like":2},"selfRepetitionCount":2,"longPauseCount":3,"totalSilenceSeconds":30.5,"pronunciationAccuracy":88.5,"lowScoreWordRatio":12.5,"problemWords":[{"word":"kubernetes","accuracyScore":55.5,"fluencyScore":null,"startSeconds":10.5,"endSeconds":11},{"word":"cache","accuracyScore":62,"fluencyScore":90.5,"startSeconds":20,"endSeconds":20.5}],"structureMarkers":{"first":2,"however":1},"hasAudio":false}""";

    // 改前形态(Default/PascalCase):analysis.json 落盘的逐字段样本
    private const string OldDefaultJson =
        """{"DurationSeconds":1500.5,"WordCount":320,"WordsPerMinute":145.5,"AverageSentenceLength":12.5,"ShortSentenceCount":4,"FillerWordCount":5,"FillerWordBreakdown":{"um":3,"like":2},"SelfRepetitionCount":2,"LongPauseCount":3,"TotalSilenceSeconds":30.5,"PronunciationAccuracy":88.5,"LowScoreWordRatio":12.5,"ProblemWords":[{"Word":"kubernetes","AccuracyScore":55.5,"FluencyScore":null,"StartSeconds":10.5,"EndSeconds":11},{"Word":"cache","AccuracyScore":62,"FluencyScore":90.5,"StartSeconds":20,"EndSeconds":20.5}],"StructureMarkers":{"first":2,"however":1},"HasAudio":false}""";

    private static void AssertFullMetrics(SpeechMetrics m)
    {
        m.DurationSeconds.Should().Be(1500.5);
        m.WordCount.Should().Be(320);
        m.WordsPerMinute.Should().Be(145.5);
        m.AverageSentenceLength.Should().Be(12.5);
        m.ShortSentenceCount.Should().Be(4);
        m.FillerWordCount.Should().Be(5);
        m.FillerWordBreakdown.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["um"] = 3, ["like"] = 2 });
        m.SelfRepetitionCount.Should().Be(2);
        m.LongPauseCount.Should().Be(3);
        m.TotalSilenceSeconds.Should().Be(30.5);
        m.PronunciationAccuracy.Should().Be(88.5);
        m.LowScoreWordRatio.Should().Be(12.5);
        m.ProblemWords.Should().HaveCount(2);
        m.ProblemWords[0].Should().Be(new WordPronunciation("kubernetes", 55.5, null, 10.5, 11));
        m.ProblemWords[1].Should().Be(new WordPronunciation("cache", 62, 90.5, 20, 20.5));
        m.StructureMarkers.Should().BeEquivalentTo(
            new Dictionary<string, int> { ["first"] = 2, ["however"] = 1 });
        m.HasAudio.Should().BeFalse();
    }

    [Fact] // 特征锁:Web 口径序列化逐字等于改前形态
    public void Metrics_WebSerialization_MatchesPreMergeShape() =>
        JsonSerializer.Serialize(FullMetrics(), Web).Should().Be(OldWebJson);

    [Fact] // 特征锁:Default 口径序列化逐字等于改前形态
    public void Metrics_DefaultSerialization_MatchesPreMergeShape() =>
        JsonSerializer.Serialize(FullMetrics()).Should().Be(OldDefaultJson);

    [Fact] // 序列化往返:改前 Web JSON → 契约类型逐字段等值
    public void Metrics_OldWebJson_DeserializesFieldByField()
    {
        var m = JsonSerializer.Deserialize<SpeechMetrics>(OldWebJson, Web);
        m.Should().NotBeNull();
        AssertFullMetrics(m!);
    }

    [Fact] // 序列化往返:改前 Default JSON → 契约类型逐字段等值
    public void Metrics_OldDefaultJson_DeserializesFieldByField()
    {
        var m = JsonSerializer.Deserialize<SpeechMetrics>(OldDefaultJson);
        m.Should().NotBeNull();
        AssertFullMetrics(m!);
    }

    [Fact] // boundary:空集合 + null StructureMarkers + HasAudio 缺省(true)往返
    public void Metrics_Boundary_EmptyCollectionsAndDefaults_RoundTrip()
    {
        // 改前形态的极简样本:尾两项带默认值,JSON 中省略 structureMarkers/hasAudio
        const string minimalWeb =
            """{"durationSeconds":0,"wordCount":0,"wordsPerMinute":0,"averageSentenceLength":0,"shortSentenceCount":0,"fillerWordCount":0,"fillerWordBreakdown":{},"selfRepetitionCount":0,"longPauseCount":0,"totalSilenceSeconds":0,"pronunciationAccuracy":0,"lowScoreWordRatio":0,"problemWords":[]}""";
        var m = JsonSerializer.Deserialize<SpeechMetrics>(minimalWeb, Web);
        m.Should().NotBeNull();
        m!.FillerWordBreakdown.Should().BeEmpty();
        m.ProblemWords.Should().BeEmpty();
        m.StructureMarkers.Should().BeNull();   // 默认值 null 不变
        m.HasAudio.Should().BeTrue();           // 默认值 true 不变

        var back = JsonSerializer.Deserialize<SpeechMetrics>(
            JsonSerializer.Serialize(m, Web), Web);
        back!.StructureMarkers.Should().BeNull();
        back.HasAudio.Should().BeTrue();
        back.FillerWordBreakdown.Should().BeEmpty();
        back.ProblemWords.Should().BeEmpty();
    }

    [Fact] // 特征锁:TranscriptSegment Web 序列化逐字 + 往返逐字段(Unicode 文本)
    public void Segment_WebRoundTrip_FieldByField()
    {
        var seg = new TranscriptSegment(1, 6.5, 12, "Résumé 🎉 cache", "CANDIDATE", 0.9);
        var json = JsonSerializer.Serialize(seg, Web);
        json.Should().Be(
            """{"index":1,"startSeconds":6.5,"endSeconds":12,"text":"R\u00E9sum\u00E9 \uD83C\uDF89 cache","speaker":"CANDIDATE","speakerConfidence":0.9}""");

        var back = JsonSerializer.Deserialize<TranscriptSegment>(json, Web);
        back.Should().Be(seg);
    }

    [Fact] // boundary:TranscriptSegment 缺 speakerConfidence → 默认 1.0(改前默认值锁定)
    public void Segment_MissingConfidence_DefaultsToOne()
    {
        const string json =
            """{"index":0,"startSeconds":0,"endSeconds":5,"text":"hello","speaker":"UNKNOWN"}""";
        var seg = JsonSerializer.Deserialize<TranscriptSegment>(json, Web);
        seg.Should().NotBeNull();
        seg!.SpeakerConfidence.Should().Be(1.0);
        seg.Speaker.Should().Be("UNKNOWN");
    }

    [Fact] // boundary:WordPronunciation FluencyScore 可空往返(null 与有值)
    public void WordPronunciation_NullableFluency_RoundTrips()
    {
        var withNull = new WordPronunciation("api", 40, null, 0, 0.5);
        var backNull = JsonSerializer.Deserialize<WordPronunciation>(
            JsonSerializer.Serialize(withNull, Web), Web);
        backNull.Should().Be(withNull);
        backNull!.FluencyScore.Should().BeNull();

        var withValue = new WordPronunciation("api", 40, 77.5, 0, 0.5);
        var backValue = JsonSerializer.Deserialize<WordPronunciation>(
            JsonSerializer.Serialize(withValue, Web), Web);
        backValue.Should().Be(withValue);
    }
}
