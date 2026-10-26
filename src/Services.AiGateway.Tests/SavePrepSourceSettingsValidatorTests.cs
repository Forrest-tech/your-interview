using YourInterview.Services.AiGateway.Application;
using Xunit;

namespace YourInterview.Services.AiGateway.Tests;

/// <summary>
/// 票112:SavePrepSourceSettingsCommand 此前无校验器,SearchProvider 的 EF 硬限
/// (50)只靠 Handler 的 tavily/serper 白名单顺带拦住——错误是白名单通用文案、
/// 无字段级 errors{}。本组逐字段断言:超限(+1)拒绝且文案为票107 定版长度
/// 文案、整值放行;既有可选语义(null/空 = 未配置)守恒,不加 NotEmpty;
/// SearchApiKey 在 EF 是 text(有意不限长),超长放行。
/// </summary>
public sealed class SavePrepSourceSettingsValidatorTests
{
    private static readonly Guid User = Guid.NewGuid();

    private static SavePrepSourceSettingsCommand Valid() => new(
        User, true, true, true, false, "tavily", "sk-test");

    private static List<FluentValidation.Results.ValidationFailure> Errors(SavePrepSourceSettingsCommand cmd)
        => new SavePrepSourceSettingsCommandValidator().Validate(cmd).Errors.ToList();

    [Fact]
    public void ValidCommand_Passes() => Assert.Empty(Errors(Valid()));

    [Fact] // 守恒: 另一合法服务商同样放行(白名单在 Handler,校验器不复制)
    public void Serper_Passes() => Assert.Empty(Errors(Valid() with { SearchProvider = "serper" }));

    [Fact] // 守恒: null = 未配置(既有可选语义,不得加 NotEmpty)
    public void SearchProvider_Null_Passes() => Assert.Empty(Errors(Valid() with { SearchProvider = null }));

    [Fact] // 守恒: 空串同样按未配置口径放行
    public void SearchProvider_Empty_Passes() => Assert.Empty(Errors(Valid() with { SearchProvider = "" }));

    // ---------- SearchProvider ≤50(EF 硬限) ----------
    [Fact] // boundary: 上限整值
    public void SearchProvider_50_Accepted()
        => Assert.DoesNotContain(Errors(Valid() with { SearchProvider = new string('x', 50) }),
            x => x.PropertyName == nameof(SavePrepSourceSettingsCommand.SearchProvider));

    [Fact] // boundary: 上限 +1,文案逐字
    public void SearchProvider_51_Rejected_WithLengthMessage()
    {
        var e = Assert.Single(Errors(Valid() with { SearchProvider = new string('x', 51) }),
            x => x.PropertyName == nameof(SavePrepSourceSettingsCommand.SearchProvider));
        Assert.Equal("搜索服务商长度不能超过 50 个字符", e.ErrorMessage);
    }

    // ---------- SearchApiKey 无上限(EF text,有意设计 —— 不得误设) ----------
    [Fact]
    public void SearchApiKey_VeryLong_Accepted_NoLengthRule()
        => Assert.Empty(Errors(Valid() with { SearchApiKey = new string('k', 5000) }));
}
