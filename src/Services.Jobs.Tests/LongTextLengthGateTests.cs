using FluentValidation.Results;
using YourInterview.Services.Jobs.Application;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票62:SmartAdd 四段长文本 (JdSummary / Requirements / Benefits / CompanyDescription)
/// 长度闸门的回归钉。四列由 varchar(20000) 扩为 text 后,与 JdText (票11) 共用
/// JdTextLimits.MaxLength (200,000) 统一口径:三条写路径 (Create / Update / SmartAdd)
/// 上限内一律放行、超限一律校验失败 (端点层映射为 400),不许再出现某条路径落库 500。
/// 本组只测校验器 (纯函数,无 DB)。计数口径 = string.Length (UTF-16 码元),
/// 与票11 JdText 闸门及 FluentValidation MaximumLength 一致。
/// </summary>
public sealed class LongTextLengthGateTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Id = Guid.NewGuid();

    public static IEnumerable<object[]> Fields()
    {
        yield return new object[] { nameof(CreateApplicationCommand.JdSummary) };
        yield return new object[] { nameof(CreateApplicationCommand.Requirements) };
        yield return new object[] { nameof(CreateApplicationCommand.Benefits) };
        yield return new object[] { nameof(CreateApplicationCommand.CompanyDescription) };
    }

    private static CreateApplicationCommand Create(string field, string? value) => field switch
    {
        nameof(CreateApplicationCommand.JdSummary) => new(User, Guid.NewGuid(), "Senior .NET Developer",
            null, null, null, null, null, value),
        nameof(CreateApplicationCommand.Requirements) => new(User, Guid.NewGuid(), "Senior .NET Developer",
            null, null, null, null, null, null, Requirements: value),
        nameof(CreateApplicationCommand.Benefits) => new(User, Guid.NewGuid(), "Senior .NET Developer",
            null, null, null, null, null, null, Benefits: value),
        nameof(CreateApplicationCommand.CompanyDescription) => new(User, Guid.NewGuid(), "Senior .NET Developer",
            null, null, null, null, null, null, CompanyDescription: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static UpdateApplicationCommand Update(string field, string? value) => field switch
    {
        nameof(UpdateApplicationCommand.JdSummary) => new(User, Id, "Senior .NET Developer",
            null, null, null, null, null, value, null, null, null),
        nameof(UpdateApplicationCommand.Requirements) => new(User, Id, "Senior .NET Developer",
            null, null, null, null, null, null, null, null, null, Requirements: value),
        nameof(UpdateApplicationCommand.Benefits) => new(User, Id, "Senior .NET Developer",
            null, null, null, null, null, null, null, null, null, Benefits: value),
        nameof(UpdateApplicationCommand.CompanyDescription) => new(User, Id, "Senior .NET Developer",
            null, null, null, null, null, null, null, null, null, CompanyDescription: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static SmartAddApplicationCommand SmartAdd(string field, string? value) => field switch
    {
        nameof(SmartAddApplicationCommand.JdSummary) => new(User, "Acme", "Senior .NET Developer",
            null, null, null, null, value, null, null, null, null, null, null),
        nameof(SmartAddApplicationCommand.Requirements) => new(User, "Acme", "Senior .NET Developer",
            null, null, null, null, null, null, null, null, null, null, null, Requirements: value),
        nameof(SmartAddApplicationCommand.Benefits) => new(User, "Acme", "Senior .NET Developer",
            null, null, null, null, null, null, null, null, null, null, null, Benefits: value),
        nameof(SmartAddApplicationCommand.CompanyDescription) => new(User, "Acme", "Senior .NET Developer",
            null, null, null, null, null, null, null, null, null, null, null, CompanyDescription: value),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static IEnumerable<ValidationResult> ValidateAll(string field, string? value)
    {
        yield return new CreateApplicationCommandValidator().Validate(Create(field, value));
        yield return new UpdateApplicationCommandValidator().Validate(Update(field, value));
        yield return new SmartAddApplicationCommandValidator().Validate(SmartAdd(field, value));
    }

    // boundary: 超旧列上限 (回归钉) + 恰统一上限 —— 三条写路径全部放行
    [Theory]
    [MemberData(nameof(Fields))]
    public void AllWritePaths_Accept_LongText_OverOldColumnLimit_And_AtUnifiedLimit(string field)
    {
        foreach (var len in new[] { 21_000, JdTextLimits.MaxLength })
        {
            var value = new string('x', len);
            foreach (var result in ValidateAll(field, value))
                Assert.Empty(result.Errors);
        }
    }

    // boundary: 上限 + 1 —— 三条写路径全部拒绝,且错误落在字段本身 (端点回 400 字段提示)
    [Theory]
    [MemberData(nameof(Fields))]
    public void AllWritePaths_Reject_LongText_OverUnifiedLimit(string field)
    {
        var value = new string('x', JdTextLimits.MaxLength + 1);
        foreach (var result in ValidateAll(field, value))
            Assert.Contains(result.Errors, e => e.PropertyName == field);
    }

    // boundary: null / 空串 —— 未提供或清空,不该被长度校验拦 (Update 的 null = 保持原值语义)
    [Theory]
    [MemberData(nameof(Fields))]
    public void AllWritePaths_Accept_NullAndEmpty_LongText(string field)
    {
        foreach (var value in new string?[] { null, string.Empty })
            foreach (var result in ValidateAll(field, value))
                Assert.Empty(result.Errors);
    }

    // boundary: Unicode —— CJK 按 1 字符计,恰上限放行、上限 + 1 拒绝
    [Theory]
    [MemberData(nameof(Fields))]
    public void AllWritePaths_UnicodeCjk_CountsByCharacter(string field)
    {
        var atLimit = new string('岗', JdTextLimits.MaxLength);
        foreach (var result in ValidateAll(field, atLimit))
            Assert.Empty(result.Errors);

        var overLimit = new string('岗', JdTextLimits.MaxLength + 1);
        foreach (var result in ValidateAll(field, overLimit))
            Assert.Contains(result.Errors, e => e.PropertyName == field);
    }

    // boundary: Unicode 代理对 —— 计数口径为 UTF-16 码元 (与 JdText 闸门一致):
    // 100,000 个 🚀 = 200,000 码元,恰在上限内放行;多一个即 200,002 码元被拒。
    [Fact]
    public void AllWritePaths_UnicodeSurrogatePair_CountsUtf16Units()
    {
        var atLimit = string.Concat(Enumerable.Repeat("🚀", 100_000));
        Assert.Equal(JdTextLimits.MaxLength, atLimit.Length);
        foreach (var result in ValidateAll(nameof(CreateApplicationCommand.Requirements), atLimit))
            Assert.Empty(result.Errors);

        var overLimit = atLimit + "🚀";
        foreach (var result in ValidateAll(nameof(CreateApplicationCommand.Requirements), overLimit))
            Assert.Contains(result.Errors,
                e => e.PropertyName == nameof(CreateApplicationCommand.Requirements));
    }
}
