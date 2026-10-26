using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// ARC-18 / 票50 seam:CORS 白名单唯一口径 ServiceDefaults.ResolveCorsAllowedOrigins /
/// IsCorsOriginAllowed 的特征+boundary 测试。
/// 改前 ServiceDefaults 是 SetIsOriginAllowed(_ => true)+AllowCredentials 全放行+凭据,
/// 网关则读 Cors:AllowedOrigins 白名单——统一口径 = 同键、同默认三项、精确匹配、无通配。
/// boundary 类别:空/null、字面量 "null"、大小写、尾斜杠、端口差异、子域冒充、通配剔除、空集合回落。
/// </summary>
public class CorsOriginPolicyTests
{
    private static readonly string[] Defaults = ServiceDefaults.DefaultCorsAllowedOrigins;

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IConfiguration CorsConfig(params string[] origins)
    {
        var values = new Dictionary<string, string?>();
        for (var i = 0; i < origins.Length; i++)
            values[$"Cors:AllowedOrigins:{i}"] = origins[i];
        return Config(values);
    }

    // ---------- ResolveCorsAllowedOrigins ----------

    [Fact] // 基准:默认三项逐项锁定(与网关 appsettings.json / 网关代码兜底一致)
    public void DefaultOrigins_AreGatewayWhitelistExactly()
    {
        Defaults.Should().Equal(
            "http://localhost:4200", "http://localhost:5100", "http://127.0.0.1:4200");
    }

    [Fact] // 基准:配了白名单就用配置值(部署形态注入生产域名走这里)
    public void Resolve_WithConfig_UsesConfiguredOrigins()
    {
        var resolved = ServiceDefaults.ResolveCorsAllowedOrigins(
            CorsConfig("https://app.example.com", "http://localhost:4200"));

        resolved.Should().Equal("https://app.example.com", "http://localhost:4200");
    }

    [Fact] // boundary:配置缺失(null 节)—— 回落统一默认三项
    public void Resolve_MissingSection_FallsBackToDefaults()
    {
        ServiceDefaults.ResolveCorsAllowedOrigins(Config(new()))
            .Should().Equal(Defaults);
    }

    [Fact] // boundary:配置为空集合 —— 回落统一默认三项(空集合不等于"全关",更不等于"全放行")
    public void Resolve_EmptyArray_FallsBackToDefaults()
    {
        ServiceDefaults.ResolveCorsAllowedOrigins(CorsConfig())
            .Should().Equal(Defaults);
    }

    [Fact] // boundary:配置全是空白/通配(剔除后为空)—— 回落默认,通配永不进白名单
    public void Resolve_OnlyBlankAndWildcard_FallsBackToDefaults()
    {
        ServiceDefaults.ResolveCorsAllowedOrigins(CorsConfig(" ", "*", "http://*.example.com"))
            .Should().Equal(Defaults);
    }

    [Fact] // boundary:归一化 —— 去空白、去尾斜杠、去重(大小写不敏感),通配项剔除但其余保留
    public void Resolve_NormalizesEntries()
    {
        var resolved = ServiceDefaults.ResolveCorsAllowedOrigins(CorsConfig(
            "  https://app.example.com/ ", "https://APP.example.com", "*", "http://localhost:4200"));

        resolved.Should().Equal("https://app.example.com", "http://localhost:4200");
    }

    [Fact] // boundary:null 入参 —— 抛 ArgumentNullException(与 BuildTokenValidationParameters 同口径)
    public void Resolve_NullConfiguration_Throws()
    {
        var act = () => ServiceDefaults.ResolveCorsAllowedOrigins(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- IsCorsOriginAllowed ----------

    [Theory] // 基准:白名单内逐项放行
    [InlineData("http://localhost:4200")]
    [InlineData("http://localhost:5100")]
    [InlineData("http://127.0.0.1:4200")]
    public void IsAllowed_WhitelistedOrigin_ReturnsTrue(string origin)
    {
        ServiceDefaults.IsCorsOriginAllowed(origin, Defaults).Should().BeTrue();
    }

    [Fact] // boundary:大小写 —— scheme/host 大小写不敏感,归一后放行
    public void IsAllowed_CaseDifference_ReturnsTrue()
    {
        ServiceDefaults.IsCorsOriginAllowed("HTTP://LOCALHOST:4200", Defaults).Should().BeTrue();
    }

    [Fact] // boundary:尾斜杠 —— 来访 Origin 带尾斜杠归一后放行
    public void IsAllowed_TrailingSlash_ReturnsTrue()
    {
        ServiceDefaults.IsCorsOriginAllowed("http://localhost:4200/", Defaults).Should().BeTrue();
    }

    [Theory] // boundary:子域冒充/后缀冒充 —— 精确匹配,一律拒绝(全放行时代这些全过)
    [InlineData("http://sub.localhost:4200")]        // 子域前缀
    [InlineData("http://evil-localhost:4200")]       // evil- 前缀冒充
    [InlineData("http://localhost:4200.evil.com")]   // 后缀冒充(非法 Origin 形态也必须拒)
    [InlineData("http://localhost.evil.com:4200")]   // 中缀冒充
    public void IsAllowed_SpoofedOrigin_ReturnsFalse(string origin)
    {
        ServiceDefaults.IsCorsOriginAllowed(origin, Defaults).Should().BeFalse();
    }

    [Theory] // boundary:端口差异 / scheme 差异 —— 精确匹配拒绝
    [InlineData("http://localhost:4201")]
    [InlineData("http://localhost:5200")]
    [InlineData("https://localhost:4200")]
    public void IsAllowed_PortOrSchemeDifference_ReturnsFalse(string origin)
    {
        ServiceDefaults.IsCorsOriginAllowed(origin, Defaults).Should().BeFalse();
    }

    [Theory] // boundary:空/null/空白/字面量 "null"(沙盒 iframe Origin)/任意外站 —— 拒绝
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("https://evil.example.com")]
    [InlineData("*")]
    public void IsAllowed_EmptyOrForeign_ReturnsFalse(string? origin)
    {
        ServiceDefaults.IsCorsOriginAllowed(origin, Defaults).Should().BeFalse();
    }

    [Fact] // boundary:白名单为 null/空集合 —— 一律拒绝(fail-closed,不是回落放行)
    public void IsAllowed_NullOrEmptyWhitelist_ReturnsFalse()
    {
        ServiceDefaults.IsCorsOriginAllowed("http://localhost:4200", null).Should().BeFalse();
        ServiceDefaults.IsCorsOriginAllowed("http://localhost:4200", []).Should().BeFalse();
    }

    [Fact] // boundary:白名单含通配项 —— 通配项不命中任何 Origin(防配置写 * 回到全放行)
    public void IsAllowed_WildcardWhitelistEntry_NeverMatches()
    {
        ServiceDefaults.IsCorsOriginAllowed("https://evil.example.com", ["*"]).Should().BeFalse();
        ServiceDefaults.IsCorsOriginAllowed("https://evil.example.com", ["http://*"]).Should().BeFalse();
    }

    [Fact] // boundary:特殊字符/Unicode —— 不抛异常且不误放行
    public void IsAllowed_UnicodeOrigin_ReturnsFalse()
    {
        ServiceDefaults.IsCorsOriginAllowed("http://本地主机:4200", Defaults).Should().BeFalse();
        ServiceDefaults.IsCorsOriginAllowed("http://localhost:4200/路径", Defaults).Should().BeFalse();
    }
}
