using YourInterview.Services.AiGateway.Application;
using Xunit;

namespace YourInterview.Services.AiGateway.Tests;

/// <summary>
/// 票109:SaveAiSettingCommandValidator 补长度规则的回归钉。此前校验器只有
/// NotEmpty,字段超 EF 硬限(Protocol 50 / Model 200 / BaseUrl 1024 /
/// Endpoint 1024 / ApiVersion 50 / DisplayName 200)时穿透 DB 回 500。
/// 本组逐字段断言:超限(+1)拒绝且文案为票107 定版长度文案、整值放行;
/// 既有 NotEmpty 文案逐字守恒;ApiKey 在 EF 是 text(有意不限长),超长放行。
/// </summary>
public sealed class SaveAiSettingValidatorTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static SaveAiSettingCommand Valid() => new(
        User, "openai-compatible", "sk-test", "https://api.example.com/v1",
        "stub-model", null, null, null);

    private static List<FluentValidation.Results.ValidationFailure> Errors(SaveAiSettingCommand cmd)
        => new SaveAiSettingCommandValidator().Validate(cmd).Errors.ToList();

    private static void AssertLengthError(SaveAiSettingCommand cmd, string property, string message)
    {
        var e = Assert.Single(Errors(cmd), x => x.PropertyName == property);
        Assert.Equal(message, e.ErrorMessage);
    }

    private static void AssertNoError(SaveAiSettingCommand cmd, string property)
        => Assert.DoesNotContain(Errors(cmd), x => x.PropertyName == property);

    [Fact]
    public void ValidCommand_Passes() => Assert.Empty(Errors(Valid()));

    // ---------- Model ≤200(EF 硬限) ----------
    [Fact] // boundary: 上限 +1
    public void Model_201_Rejected_WithLengthMessage()
        => AssertLengthError(Valid() with { Model = new string('m', 201) },
            nameof(SaveAiSettingCommand.Model), "模型名长度不能超过 200 个字符");

    [Fact] // boundary: 上限整值
    public void Model_200_Accepted()
        => AssertNoError(Valid() with { Model = new string('m', 200) },
            nameof(SaveAiSettingCommand.Model));

    [Fact] // 守恒: 既有必填文案逐字不变
    public void Model_Empty_KeepsRequiredMessage()
    {
        var e = Assert.Single(Errors(Valid() with { Model = "" }),
            x => x.PropertyName == nameof(SaveAiSettingCommand.Model));
        Assert.Equal("模型名不能为空。", e.ErrorMessage);
    }

    // ---------- Protocol ≤50 ----------
    [Fact]
    public void Protocol_51_Rejected_WithLengthMessage()
        => AssertLengthError(Valid() with { Protocol = new string('p', 51) },
            nameof(SaveAiSettingCommand.Protocol), "协议类型长度不能超过 50 个字符");

    [Fact]
    public void Protocol_50_Accepted()
        => AssertNoError(Valid() with { Protocol = new string('p', 50) },
            nameof(SaveAiSettingCommand.Protocol));

    [Fact] // 守恒: 既有必填文案逐字不变
    public void Protocol_Empty_KeepsRequiredMessage()
    {
        var e = Assert.Single(Errors(Valid() with { Protocol = "" }),
            x => x.PropertyName == nameof(SaveAiSettingCommand.Protocol));
        Assert.Equal("协议类型不能为空。", e.ErrorMessage);
    }

    // ---------- BaseUrl ≤1024 ----------
    [Fact]
    public void BaseUrl_1025_Rejected_WithLengthMessage()
        => AssertLengthError(Valid() with { BaseUrl = new string('u', 1025) },
            nameof(SaveAiSettingCommand.BaseUrl), "端点地址长度不能超过 1024 个字符");

    [Fact]
    public void BaseUrl_1024_Accepted()
        => AssertNoError(Valid() with { BaseUrl = new string('u', 1024) },
            nameof(SaveAiSettingCommand.BaseUrl));

    // ---------- Endpoint ≤1024 ----------
    [Fact]
    public void Endpoint_1025_Rejected_WithLengthMessage()
        => AssertLengthError(Valid() with { Endpoint = new string('e', 1025) },
            nameof(SaveAiSettingCommand.Endpoint), "Azure 资源端点长度不能超过 1024 个字符");

    [Fact]
    public void Endpoint_1024_Accepted()
        => AssertNoError(Valid() with { Endpoint = new string('e', 1024) },
            nameof(SaveAiSettingCommand.Endpoint));

    // ---------- ApiVersion ≤50 ----------
    [Fact]
    public void ApiVersion_51_Rejected_WithLengthMessage()
        => AssertLengthError(Valid() with { ApiVersion = new string('v', 51) },
            nameof(SaveAiSettingCommand.ApiVersion), "API 版本长度不能超过 50 个字符");

    [Fact]
    public void ApiVersion_50_Accepted()
        => AssertNoError(Valid() with { ApiVersion = new string('v', 50) },
            nameof(SaveAiSettingCommand.ApiVersion));

    // ---------- DisplayName ≤200 ----------
    [Fact]
    public void DisplayName_201_Rejected_WithLengthMessage()
        => AssertLengthError(Valid() with { DisplayName = new string('d', 201) },
            nameof(SaveAiSettingCommand.DisplayName), "显示名称长度不能超过 200 个字符");

    [Fact]
    public void DisplayName_200_Accepted()
        => AssertNoError(Valid() with { DisplayName = new string('d', 200) },
            nameof(SaveAiSettingCommand.DisplayName));

    // ---------- ApiKey 无上限(EF text,有意设计 —— 不得误设) ----------
    [Fact]
    public void ApiKey_VeryLong_Accepted_NoLengthRule()
        => Assert.Empty(Errors(Valid() with { ApiKey = new string('k', 5000) }));

    [Fact] // 守恒: 既有必填文案逐字不变
    public void ApiKey_Empty_KeepsRequiredMessage()
    {
        var e = Assert.Single(Errors(Valid() with { ApiKey = "" }),
            x => x.PropertyName == nameof(SaveAiSettingCommand.ApiKey));
        Assert.Equal("API Key 不能为空。", e.ErrorMessage);
    }
}
