using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// FUN-04:JdText 长度闸门全仓统一口径的回归钉。
/// 列扩为 text 后,四条写路径 (Create / Update / SetJdContent / SmartAdd) 共用
/// JdTextLimits.MaxLength:上限内 (含审计复现的 44,181) 一律放行、超限一律校验失败
/// (端点层映射为 400),不许再出现某条路径落库 500 的缝隙。
/// 本组只测校验器 (纯函数,无 DB)。
/// </summary>
public sealed class JdTextLengthGateTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Id = Guid.NewGuid();
    private static string Jd(int n) => new('x', n);

    private static CreateApplicationCommand Create(string? jd) => new(
        User, Guid.NewGuid(), "Senior .NET Developer", null, null, null, null, null, null,
        JdText: jd);

    private static UpdateApplicationCommand Update(string? jd) => new(
        User, Id, "Senior .NET Developer", null, null, null, null, null, null, null, null, null,
        JdText: jd);

    private static SetJdContentCommand SetJd(string? jd) => new(User, Id, jd, null);

    private static SmartAddApplicationCommand SmartAdd(string? jd) => new(
        User, "Acme", "Senior .NET Developer", null, null, null, null, null, null, null, null,
        JdText: jd, Source: null, Status: null);

    [Theory]
    [InlineData(44181)]   // FUN-04 审计复现长度: 修复前 smart-add 整单 500
    [InlineData(200000)]  // 统一上限本身: 放行
    public void AllWritePaths_Accept_JdText_UpToUnifiedLimit(int len)
    {
        var jd = Jd(len);
        Assert.Empty(new CreateApplicationCommandValidator().Validate(Create(jd)).Errors);
        Assert.Empty(new UpdateApplicationCommandValidator().Validate(Update(jd)).Errors);
        Assert.Empty(new SetJdContentCommandValidator().Validate(SetJd(jd)).Errors);
        Assert.Empty(new SmartAddApplicationCommandValidator().Validate(SmartAdd(jd)).Errors);
    }

    [Fact]
    public void AllWritePaths_Reject_JdText_OverUnifiedLimit()
    {
        var jd = Jd(JdTextLimits.MaxLength + 1);
        Assert.Contains(new CreateApplicationCommandValidator().Validate(Create(jd)).Errors,
            e => e.PropertyName == nameof(CreateApplicationCommand.JdText));
        Assert.Contains(new UpdateApplicationCommandValidator().Validate(Update(jd)).Errors,
            e => e.PropertyName == nameof(UpdateApplicationCommand.JdText));
        Assert.Contains(new SetJdContentCommandValidator().Validate(SetJd(jd)).Errors,
            e => e.PropertyName == nameof(SetJdContentCommand.JdText));
        Assert.Contains(new SmartAddApplicationCommandValidator().Validate(SmartAdd(jd)).Errors,
            e => e.PropertyName == nameof(SmartAddApplicationCommand.JdText));
    }

    [Fact]
    public void AllWritePaths_Accept_NullJdText()
    {
        // null = 未提供 (Update 的局部保存语义) / 无 JD,任何路径都不该被长度校验拦。
        Assert.Empty(new CreateApplicationCommandValidator().Validate(Create(null)).Errors);
        Assert.Empty(new UpdateApplicationCommandValidator().Validate(Update(null)).Errors);
        Assert.Empty(new SetJdContentCommandValidator().Validate(SetJd(null)).Errors);
        Assert.Empty(new SmartAddApplicationCommandValidator().Validate(SmartAdd(null)).Errors);
    }
}
