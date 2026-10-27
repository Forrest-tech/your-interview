using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Security;
using YourInterview.SharedContracts.Security;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// 票241 双轨期:双 scheme 选择器(AddPolicyScheme + ForwardDefaultSelector,唯一路由机制)。
/// 选择器只按未验证 payload 的 iss 路由,选中 scheme 再做完整校验 —— 本文件只锁路由,
/// 不测签名(签名是各 scheme 自己的 TokenValidationParameters,见 JwtValidationParametersTests
/// 的旧口径与 LogtoTokenValidation.Build)。
/// 明确不测顺序回落:回落机制已被 round-9 从方案里删掉,本选择器是唯一机制。
/// </summary>
public class LogtoDualSchemeTests
{
    private const string OldIssuer = "your-interview.identity";
    private const string LogtoIssuer = "http://localhost:3001/oidc";

    private static string Token(string issuer, params (string Type, string Value)[] claims)
    {
        var jwt = new JwtSecurityToken(issuer: issuer,
            claims: claims.Select(c => new Claim(c.Type, c.Value)));
        return "Bearer " + new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    [Fact] // 旧发行方 → Legacy
    public void OldIssuer_RoutesToLegacy()
    {
        LogtoSchemeSelector.SelectScheme(Token(OldIssuer), LogtoIssuer, false)
            .Should().Be(LogtoSchemeSelector.LegacyScheme);
    }

    [Fact] // Logto 发行方 → Logto
    public void LogtoIssuer_RoutesToLogto()
    {
        LogtoSchemeSelector.SelectScheme(Token(LogtoIssuer), LogtoIssuer, false)
            .Should().Be(LogtoSchemeSelector.LogtoScheme);
    }

    [Fact] // issuer 尾斜杠归一化(配置写没写 /oidc/ 都一样)
    public void LogtoIssuer_TrailingSlash_Normalized()
    {
        LogtoSchemeSelector.SelectScheme(Token(LogtoIssuer + "/"), LogtoIssuer, false)
            .Should().Be(LogtoSchemeSelector.LogtoScheme);
        LogtoSchemeSelector.SelectScheme(Token(LogtoIssuer), LogtoIssuer + "/", false)
            .Should().Be(LogtoSchemeSelector.LogtoScheme);
    }

    [Fact] // 无法解析/无头 → Legacy(后续完整校验会拒绝它,不是静默放行)
    public void UnparsableOrMissing_RoutesToLegacy()
    {
        LogtoSchemeSelector.SelectScheme("Bearer not-a-jwt", LogtoIssuer, false)
            .Should().Be(LogtoSchemeSelector.LegacyScheme);
        LogtoSchemeSelector.SelectScheme(null, LogtoIssuer, false)
            .Should().Be(LogtoSchemeSelector.LegacyScheme);
    }

    [Fact] // 收敛日:旧用户 token 一律 Logto(随后因 issuer 不符被拒)
    public void Convergence_OldUserToken_RoutesToLogto()
    {
        LogtoSchemeSelector.SelectScheme(Token(OldIssuer, ("role", Roles.User)), LogtoIssuer, true)
            .Should().Be(LogtoSchemeSelector.LogtoScheme);
    }

    [Fact] // 收敛日例外:Service 角色旧 token 仍走 Legacy(服务回写凭证不走 Logto)
    public void Convergence_ServiceToken_StillRoutesToLegacy()
    {
        LogtoSchemeSelector.SelectScheme(Token(OldIssuer, ("role", Roles.Service)), LogtoIssuer, true)
            .Should().Be(LogtoSchemeSelector.LegacyScheme);
        LogtoSchemeSelector.SelectScheme(
                Token(OldIssuer, (ClaimTypes.Role, Roles.Service)), LogtoIssuer, true)
            .Should().Be(LogtoSchemeSelector.LegacyScheme);
    }

    [Fact] // 灰度期 Service 角色不影响路由(按 issuer 走;门层再处理角色)
    public void GreyPeriod_ServiceToken_RoutesByIssuer()
    {
        LogtoSchemeSelector.SelectScheme(Token(OldIssuer, ("role", Roles.Service)), LogtoIssuer, false)
            .Should().Be(LogtoSchemeSelector.LegacyScheme);
    }

    [Fact] // Logto 校验参数:issuer + audience(未配 audience 时不校验 audience)
    public void LogtoValidationParameters_IssuerAndAudience()
    {
        var p = LogtoTokenValidation.Build(new LogtoOptions { Issuer = LogtoIssuer, Audience = "api.logto" });
        p.ValidIssuer.Should().Be(LogtoIssuer);
        p.ValidAudience.Should().Be("api.logto");
        p.ValidateAudience.Should().BeTrue();
        p.ValidateLifetime.Should().BeTrue();
        p.ValidateIssuerSigningKey.Should().BeTrue();

        var noAud = LogtoTokenValidation.Build(new LogtoOptions { Issuer = LogtoIssuer });
        noAud.ValidateAudience.Should().BeFalse();
    }

    [Fact] // 旧口径在双轨配置下仍纯净(BuildTokenValidationParameters 不再含 Logto 分支)
    public void OldParameters_IgnoreLogtoConfig()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = OldIssuer,
            ["Jwt:Audience"] = "your-interview.api",
            ["Jwt:SigningKey"] = "unit-test-signing-key-at-least-32-chars!!",
            ["Logto:Issuer"] = LogtoIssuer
        }).Build();
        var p = ServiceDefaults.BuildTokenValidationParameters(cfg);
        p.ValidIssuer.Should().Be(OldIssuer);
        // 本版本 TokenValidationParameters 的 ValidIssuer 与 ValidIssuers 不联动:
        // 只设 ValidIssuer 时 ValidIssuers 保持 null(旧口径特征测试同断言)。
        p.ValidIssuers.Should().BeNull();
        p.IssuerSigningKeyResolver.Should().BeNull();
    }
}
