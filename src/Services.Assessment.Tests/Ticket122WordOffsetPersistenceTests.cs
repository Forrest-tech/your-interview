using System.Text.Json;
using FluentAssertions;
using YourInterview.Services.Assessment.Api;
using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票122:逐词时间偏移(OffsetMs/DurationMs)落库契约补测。
/// 缺陷原形:运行时与 assess 响应都带偏移,前端保存路径整组透传 words,
/// 但保存入参 WordScoreIn 与读出 WordScoreDto 只有三字段 —— 偏移在 STJ
/// 绑定时被当未知属性丢弃,重载报告点填充词 seek 结构性不可达。
/// 本组用例全为纯逻辑(无 DB):往返/绑定/旧形状/降级四路钉死新契约。
/// </summary>
public sealed class Ticket122WordOffsetPersistenceTests
{
    [Fact] // (a) 持久化往返:按保存路径同款序列化(Controller 对 body.Words
           // 的 JsonSerializer.Serialize 逐字做法)→ DeserializeWords 读回,
           // 偏移逐词原值、三老字段不变。
    public void RoundTrip_WordsWithOffsets_SurviveSerializeAndDeserialize()
    {
        var words = new List<WordScoreIn>
        {
            new("teenage", 88.0, "None", OffsetMs: 120, DurationMs: 430),
            new("um", 0.0, "Insertion", OffsetMs: 3200, DurationMs: 180),
            new("girls", 91.5, "None"),
        };

        var json = JsonSerializer.Serialize(words);
        json.Should().Contain("\"OffsetMs\":3200"); // 偏移确实进了 WordsJson 文本

        var back = ListRecordingsQueryHandler.DeserializeWords(json);
        back.Should().HaveCount(3);
        back[0].OffsetMs.Should().Be(120);
        back[0].DurationMs.Should().Be(430);
        back[1].Word.Should().Be("um");
        back[1].OffsetMs.Should().Be(3200);
        back[1].DurationMs.Should().Be(180);
        back[1].ErrorType.Should().Be("Insertion");
        back[2].OffsetMs.Should().Be(0); // 未给偏移 = 0 =「无偏移」语义
        back[2].Accuracy.Should().Be(91.5);
    }

    [Fact] // (b1) 保存入参绑定:前端请求体(camelCase)含两字段 → 值完整抵达。
    public void Binding_BodyWithOffsets_KeepsValues()
    {
        const string body = """
            {"pronScore":88,"accuracyScore":87,"fluencyScore":87,"completenessScore":90.6,
             "prosodyScore":null,"recognizedText":"teenage um girls","referenceText":"teenage girls",
             "azureRegion":"canadacentral",
             "words":[{"word":"um","accuracy":0,"errorType":"Insertion","offsetMs":3200,"durationMs":180}]}
            """;
        var bound = JsonSerializer.Deserialize<RecordingScoreBody>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        bound.Should().NotBeNull();
        bound!.Words.Should().ContainSingle();
        bound.Words![0].OffsetMs.Should().Be(3200);
        bound.Words[0].DurationMs.Should().Be(180);
    }

    [Fact] // (b2) 保存入参绑定:旧版请求体不含两字段 → 默认 0,不报错(向后兼容)。
    public void Binding_BodyWithoutOffsets_DefaultsToZero()
    {
        const string body = """
            {"pronScore":90,"words":[{"word":"girls","accuracy":91,"errorType":"None"}]}
            """;
        var bound = JsonSerializer.Deserialize<RecordingScoreBody>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        bound.Should().NotBeNull();
        bound!.Words.Should().ContainSingle();
        bound.Words![0].OffsetMs.Should().Be(0);
        bound.Words[0].DurationMs.Should().Be(0);
    }

    [Fact] // (c) 旧库形状:三字段对象数组(旧版逐字序列化产物,PascalCase 键)
           // → DTO 两新字段得 0,其余逐字无损、无异常 —— 旧记录重载行为
           // 与今天逐字相同(分发据 0 走弹层,绝不假 seek)。
    public void LegacyShape_ThreeFieldJson_ReadsWithZeroOffsets()
    {
        const string legacy = "[{\"Word\":\"girls\",\"Accuracy\":91,\"ErrorType\":\"None\"}," +
                              "{\"Word\":\"um\",\"Accuracy\":0,\"ErrorType\":\"Insertion\"}]";

        var words = ListRecordingsQueryHandler.DeserializeWords(legacy);

        words.Should().HaveCount(2);
        words[0].Word.Should().Be("girls");
        words[0].Accuracy.Should().Be(91);
        words[0].OffsetMs.Should().Be(0);
        words[0].DurationMs.Should().Be(0);
        words[1].OffsetMs.Should().Be(0);
    }

    [Theory] // (d) 降级回归:畸形/异形 WordsJson 仍走既有 catch → 空数组,
             // 不许因 DTO 补字段而破(票120 已知的既有守卫)。
    [InlineData("{\"word\":\"x\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("not json at all")]
    public void MalformedWordsJson_StillDegradesToEmpty(string json)
    {
        ListRecordingsQueryHandler.DeserializeWords(json).Should().BeEmpty();
    }
}
