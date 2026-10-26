using System.Text.Json;
using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain.JsonFields;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票19 ARC-06 JsonFieldAdapter 单元测试。
/// boundary 类别:空/null、空对象/空数组、损坏 JSON、Unicode/emoji、超大 payload、
/// 未知/更高版本信封、旧形状变体(近信封形)、序列化口径对拍(Web vs 默认)。
/// </summary>
public sealed class JsonFieldAdapterTests
{
    // ---------- Wrap/Unwrap 往返 ----------

    [Theory] // boundary:空/null、空白、空数组/对象、损坏 JSON、Unicode/emoji、引号/换行/反斜杠
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{oops")]
    [InlineData("{\"text\":\"复盘 🎉 éè\"}")]
    [InlineData("quote \" and \\ backslash and \n newline")]
    [InlineData("{\"v\":1,\"data\":\"x\",\"extra\":1}")] // 多一键 = 非信封形载荷,往返仍逐字
    public void WrapUnwrap_RoundTripsVerbatim(string? payload)
    {
        JsonFieldAdapter.Unwrap(JsonFieldAdapter.Wrap(payload)).Should().Be(payload);
    }

    [Fact] // boundary:超大 payload(约 200KB)
    public void WrapUnwrap_LargePayload()
    {
        var big = "[{" + string.Join(",", Enumerable.Range(0, 5000).Select(i => $"\"idx\":{i}")) + "}]";
        JsonFieldAdapter.Unwrap(JsonFieldAdapter.Wrap(big)).Should().Be(big);
    }

    [Fact] // 信封存储形:ASCII 载荷逐字可核
    public void Wrap_ProducesVersionedEnvelope()
    {
        JsonFieldAdapter.Wrap("[]").Should().Be("{\"v\":1,\"data\":\"[]\"}");
        JsonFieldAdapter.Wrap("").Should().Be("{\"v\":1,\"data\":\"\"}");
        JsonFieldAdapter.Wrap(null).Should().BeNull();
        JsonFieldAdapter.IsEnveloped(JsonFieldAdapter.Wrap("[]")).Should().BeTrue();
    }

    // ---------- 旧数据(无信封)兼容读 ----------

    [Theory] // 旧形状变体:数组、对象、纯文本、损坏 JSON、近信封形(v 非数字 / data 非字符串 / 多键 / 少键)
    [InlineData("[{\"text\":\"x\"}]")]
    [InlineData("{\"text\":\"old debrief\"}")]
    [InlineData("plain text not json")]
    [InlineData("{oops")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("{\"v\":\"1\",\"data\":\"x\"}")]
    [InlineData("{\"v\":1,\"data\":123}")]
    [InlineData("{\"v\":1,\"data\":\"x\",\"extra\":1}")]
    [InlineData("{\"data\":\"x\"}")]
    [InlineData("{\"v\":1}")]
    [InlineData("  {\"text\":\"leading space object\"}")]
    public void Unwrap_LegacyShapes_ReturnedAsIs(string stored)
    {
        JsonFieldAdapter.Unwrap(stored).Should().Be(stored);
        JsonFieldAdapter.IsEnveloped(stored).Should().BeFalse();
    }

    [Fact]
    public void Unwrap_Null_ReturnsNull()
        => JsonFieldAdapter.Unwrap(null).Should().BeNull();

    [Fact] // 已知残余风险(报告登记):旧载荷恰好长成信封形会被解封 —— 现存字段形状无此形态
    public void Unwrap_EnvelopeShapedLegacy_IsUnwrappedByDesign()
    {
        JsonFieldAdapter.Unwrap("{\"v\":1,\"data\":\"x\"}").Should().Be("x");
    }

    // ---------- 未知/更高版本:走失败路径,不静默吞 ----------

    [Theory] // boundary:未知版本(更高 2/99、更低 0、负数)
    [InlineData("{\"v\":2,\"data\":\"x\"}")]
    [InlineData("{\"v\":99,\"data\":\"future\"}")]
    [InlineData("{\"v\":0,\"data\":\"x\"}")]
    [InlineData("{\"v\":-1,\"data\":\"x\"}")]
    public void Unwrap_UnknownVersion_Throws(string stored)
    {
        var act = () => JsonFieldAdapter.Unwrap(stored);
        act.Should().Throw<JsonFieldException>().WithMessage("*信封版本*");
        JsonFieldAdapter.IsEnveloped(stored).Should().BeTrue();
    }

    // ---------- 强类型读写:与迁移前 JsonSerializer 口径对拍 ----------

    [Fact] // Web 口径:RoundEmailItem 列表序列化与迁移前 JsonOpts(Web) 逐字一致
    public void Serialize_WebParity_WithLegacyJsonOpts()
    {
        var emails = new List<RoundEmailItem>
        {
            new("khalil@corpay.com", "Interview invite", "2026-10-10", "Hi Forrest, …"),
            new("forrest@example.com", "Re: 邀请", "2026-10-11", "Thanks!")
        };
        var legacy = JsonSerializer.Serialize(emails, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JsonFieldAdapter.Serialize(emails).Should().Be(legacy);
        JsonFieldAdapter.Read<List<RoundEmailItem>>(legacy).Should().BeEquivalentTo(emails);
    }

    [Fact] // 默认口径:与无 options 的 JsonSerializer 逐字一致(JobPayload/AI 字典/本地报告场景)
    public void SerializeRaw_DefaultParity()
    {
        var dict = new Dictionary<string, object?> { ["verdict"] = "solid", ["overall"] = 72 };
        JsonFieldAdapter.SerializeRaw(dict).Should().Be(JsonSerializer.Serialize(dict));
        var payload = new { AssetId = Guid.NewGuid(), StoragePath = "/tmp/a.mp3", UserId = (Guid?)null };
        JsonFieldAdapter.SerializeRaw(payload).Should().Be(JsonSerializer.Serialize(payload));
        JsonFieldAdapter.ReadRaw<Dictionary<string, JsonElement>>("{\"a\":1}")!["a"].GetInt32().Should().Be(1);
        JsonFieldAdapter.ReadRaw<Dictionary<string, JsonElement>>(null).Should().BeNull();
    }

    [Fact] // boundary:Read null → default;损坏 JSON → JsonException(调用方既有容错接住)
    public void Read_NullAndCorrupt()
    {
        JsonFieldAdapter.Read<List<string>>(null).Should().BeNull();
        var act = () => JsonFieldAdapter.Read<List<string>>("{oops");
        act.Should().Throw<JsonException>();
    }

    // ---------- 类型化形状判定:与迁移前字符串口径逐项一致 ----------

    [Theory] // "[ ]" 带空格按现状口径 = 非空(不做解析归一化)
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("[]", true)]
    [InlineData("[ ]", false)]
    [InlineData("[{\"text\":\"x\"}]", false)]
    [InlineData("{}", false)]
    public void IsEmptyArray_MatchesLegacyStringSemantics(string? raw, bool expected)
    {
        JsonFieldAdapter.IsEmptyArray(raw).Should().Be(expected);
    }
}
