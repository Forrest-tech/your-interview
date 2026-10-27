using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Security;
using YourInterview.SharedContracts.Security;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// 票241 双轨期:单 scheme 多发行方 allowlist + Logto 角色→perm 声明转换。
/// 未配 Logto:Issuer 时 BuildTokenValidationParameters 保持票18 旧口径
/// (见 JwtValidationParametersTests);本文件只锁双轨分支。
/// </summary>
public class LogtoDualIssuerTests
{
    private const string Key = "unit-test-signing-key-at-least-32-chars!!";
    private const string OldIssuer = "your-interview.identity";
    private const string LogtoIssuer = "http://localhost:3001/oidc";

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration DualConfig() => Config(new()
    {
        ["Jwt:Issuer"] = OldIssuer,
        ["Jwt:Audience"] = "your-interview.api",
        ["Jwt:SigningKey"] = Key,
        ["Logto:Issuer"] = LogtoIssuer,
        ["Logto:Audience"] = "your-interview.api.logto"
    });

    [Fact] // 双轨:allowlist 含新旧两发行方与两 audience,且挂了签名解析器
    public void DualConfig_AllowlistContainsBothIssuers()
    {
        var p = ServiceDefaults.BuildTokenValidationParameters(DualConfig());

        p.ValidIssuers.Should().Contain(OldIssuer).And.Contain(LogtoIssuer);
        p.ValidAudiences.Should().Contain("your-interview.api").And.Contain("your-interview.api.logto");
        p.IssuerSigningKeyResolver.Should().NotBeNull();
    }

    [Fact] // 双轨:未配 Logto:Audience 时 audience 不校验(上线清单要求配上后恢复)
    public void DualConfig_WithoutLogtoAudience_DisablesAudienceValidation()
    {
        var cfg = Config(new()
        {
            ["Jwt:Issuer"] = OldIssuer, ["Jwt:Audience"] = "your-interview.api",
            ["Jwt:SigningKey"] = Key, ["Logto:Issuer"] = LogtoIssuer
        });
        var p = ServiceDefaults.BuildTokenValidationParameters(cfg);
        p.ValidateAudience.Should().BeFalse();
        p.ValidIssuers.Should().Contain(LogtoIssuer);
    }

    [Fact] // 单轨:未配 Logto 时解析器为空,旧口径一字不变
    public void SingleIssuer_NoResolver()
    {
        var cfg = Config(new()
        {
            ["Jwt:Issuer"] = OldIssuer, ["Jwt:Audience"] = "your-interview.api",
            ["Jwt:SigningKey"] = Key
        });
        ServiceDefaults.BuildTokenValidationParameters(cfg).IssuerSigningKeyResolver.Should().BeNull();
    }

    [Fact] // 解析器:旧发行方 → 对称密钥;Logto 发行方 → 注入的 JWKS 密钥;其他 → 空
    public void Resolver_RoutesByIssuer()
    {
        using var rsa = RSA.Create(2048);
        var jwksKey = new RsaSecurityKey(rsa) { KeyId = "logto-kid-1" };
        var oldKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key));
        var resolver = new MultiIssuerSigningKeyResolver(oldKey, OldIssuer, LogtoIssuer,
            kid => kid == "logto-kid-1" ? [jwksKey] : []);

        var oldToken = new JwtSecurityToken(issuer: OldIssuer);
        resolver.Resolve("", oldToken, "any", new TokenValidationParameters())
            .Should().ContainSingle().Which.Should().Be(oldKey);

        var logtoToken = new JwtSecurityToken(issuer: LogtoIssuer);
        resolver.Resolve("", logtoToken, "logto-kid-1", new TokenValidationParameters())
            .Should().ContainSingle().Which.Should().Be(jwksKey);
        resolver.Resolve("", logtoToken, "unknown-kid", new TokenValidationParameters())
            .Should().BeEmpty();

        var otherToken = new JwtSecurityToken(issuer: "https://evil.example/oidc");
        resolver.Resolve("", otherToken, "x", new TokenValidationParameters()).Should().BeEmpty();
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact] // 转换:Logto roles 数组 → Role 声明 + perm 展开 + legacy Guid → NameIdentifier
    public async Task Transform_LogtoRoles_ExpandsPermissions()
    {
        var legacy = Guid.NewGuid();
        var principal = Principal(
            new Claim("sub", "abc123"),
            new Claim("roles", "[\"User\"]"),
            new Claim("legacy_user_id", legacy.ToString()));
        var t = new LogtoRoleClaimsTransformation();

        var result = await t.TransformAsync(principal);
        var identity = (ClaimsIdentity)result.Identity!;
        identity.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Contain("User");
        foreach (var perm in Roles.DefaultPermissions["User"])
            identity.HasClaim(CustomClaims.Permission, perm).Should().BeTrue();
        identity.FindFirst(ClaimTypes.NameIdentifier)?.Value.Should().Be(legacy.ToString());
    }

    [Fact] // 转换:旧 token 自带 perm → 原样不动,不二次展开、不补 NameIdentifier
    public async Task Transform_OldTokenWithPerms_Untouched()
    {
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(CustomClaims.Permission, "jobs.read"),
            new Claim("roles", "[\"Admin\"]"));
        var t = new LogtoRoleClaimsTransformation();

        var result = await t.TransformAsync(principal);
        result.Should().BeSameAs(principal);
        ((ClaimsIdentity)result.Identity!).FindAll(CustomClaims.Permission)
            .Should().ContainSingle();
    }

    [Fact] // 转换:Service 角色即使出现在 Logto token 里也被忽略;未知角色忽略
    public async Task Transform_ServiceAndUnknownRoles_Ignored()
    {
        var principal = Principal(
            new Claim("role", "Service"),
            new Claim("role", "NoSuchRole"));
        var t = new LogtoRoleClaimsTransformation();

        var result = await t.TransformAsync(principal);
        result.Should().BeSameAs(principal);
    }
}
