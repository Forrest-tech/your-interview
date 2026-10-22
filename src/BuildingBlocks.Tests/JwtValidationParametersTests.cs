using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// ARC-09 / 票18 seam:JWT 校验参数唯一定义 ServiceDefaults.BuildTokenValidationParameters 的特征测试。
/// 改前七个 Program.cs 逐字复制该参数块且已漂移(仅 Jobs 带 issuer/audience 兜底)——
/// 统一口径 = 配置优先、缺失(null)回落 Jobs 兜底串、签名密钥缺失/空白即抛。
/// boundary 类别:配置缺失(null)、空串、空白密钥、null 入参。
/// </summary>
public class JwtValidationParametersTests
{
    private const string Key = "unit-test-signing-key-at-least-32-chars!!";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration Full() => Config(new()
    {
        ["Jwt:Issuer"] = "custom.issuer",
        ["Jwt:Audience"] = "custom.audience",
        ["Jwt:SigningKey"] = Key
    });

    [Fact] // 基准:配置齐全时逐项取配置值
    public void FullConfig_UsesConfiguredValues()
    {
        var p = ServiceDefaults.BuildTokenValidationParameters(Full());

        p.ValidateIssuer.Should().BeTrue();
        p.ValidateAudience.Should().BeTrue();
        p.ValidateLifetime.Should().BeTrue();
        p.ValidateIssuerSigningKey.Should().BeTrue();
        p.ValidIssuer.Should().Be("custom.issuer");
        p.ValidAudience.Should().Be("custom.audience");
        p.ClockSkew.Should().Be(TimeSpan.FromSeconds(30));
        p.RoleClaimType.Should().Be(ClaimTypes.Role);
        var key = p.IssuerSigningKey.Should().BeOfType<SymmetricSecurityKey>().Subject;
        key.Key.Should().Equal(Encoding.UTF8.GetBytes(Key));
    }

    [Fact] // boundary:配置缺失(null)—— Issuer 回落统一兜底(Jobs 旧口径)
    public void MissingIssuer_FallsBackToDefault()
    {
        var cfg = Config(new() { ["Jwt:Audience"] = "a", ["Jwt:SigningKey"] = Key });
        ServiceDefaults.BuildTokenValidationParameters(cfg).ValidIssuer
            .Should().Be(ServiceDefaults.DefaultJwtIssuer);
    }

    [Fact] // boundary:配置缺失(null)—— Audience 回落统一兜底(Jobs 旧口径)
    public void MissingAudience_FallsBackToDefault()
    {
        var cfg = Config(new() { ["Jwt:Issuer"] = "i", ["Jwt:SigningKey"] = Key });
        ServiceDefaults.BuildTokenValidationParameters(cfg).ValidAudience
            .Should().Be(ServiceDefaults.DefaultJwtAudience);
    }

    [Fact] // boundary:空串 —— ?? 只兜底 null,空串原样保留(锁 Jobs 旧口径,不顺手改)
    public void EmptyStringIssuer_IsNotReplacedByFallback()
    {
        var cfg = Config(new()
        {
            ["Jwt:Issuer"] = "", ["Jwt:Audience"] = "", ["Jwt:SigningKey"] = Key
        });
        var p = ServiceDefaults.BuildTokenValidationParameters(cfg);
        p.ValidIssuer.Should().Be("");
        p.ValidAudience.Should().Be("");
    }

    [Fact] // boundary:签名密钥缺失(null)—— 启动期即抛,不带病运行
    public void MissingSigningKey_Throws()
    {
        var cfg = Config(new() { ["Jwt:Issuer"] = "i", ["Jwt:Audience"] = "a" });
        var act = () => ServiceDefaults.BuildTokenValidationParameters(cfg);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:SigningKey*");
    }

    [Fact] // boundary:签名密钥为空白 —— 同样抛(五服务旧口径最终亦在密钥构造/校验期失败,统一为入口即抛)
    public void WhitespaceSigningKey_Throws()
    {
        var cfg = Config(new() { ["Jwt:SigningKey"] = "   " });
        var act = () => ServiceDefaults.BuildTokenValidationParameters(cfg);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:SigningKey*");
    }

    [Fact] // boundary:null 入参
    public void NullConfiguration_Throws()
    {
        var act = () => ServiceDefaults.BuildTokenValidationParameters(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact] // 上限口径:全服务统一 64MB(与网关一致)
    public void MaxRequestBodySize_Is64MB()
    {
        ServiceDefaults.MaxRequestBodySizeBytes.Should().Be(64L * 1024 * 1024);
    }
}
