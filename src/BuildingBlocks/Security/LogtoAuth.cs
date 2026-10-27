using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using YourInterview.SharedContracts.Security;

namespace YourInterview.BuildingBlocks.Security;

/// <summary>
/// 票241:Logto 发行方配置(双轨期)。
/// 灰度期各服务同时接受旧自研 Identity 与 Logto 两个发行方;收敛日把
/// <c>Logto:Issuer</c> 从配置里拿掉(或改回单发行方)即只认 Logto——
/// 注意收敛是“只认 Logto”,不是“只认旧发行方”:收敛配置 = 删掉旧发行方
/// 的签发(Identity 停发)且服务侧 <c>Jwt:Issuer</c> 本身不动、Logto 仍在
/// allowlist 里;真正下线旧发行方那一步是把旧 Identity 停发 + 从
/// allowlist 语义上不再需要它(见 docs/logto/SETUP.md 收敛清单)。
/// 未配 Logto:Issuer 时行为与票241 之前一字不变(单发行方旧口径)。
/// </summary>
public sealed class LogtoOptions
{
    public const string SectionName = "Logto";

    /// <summary>Logto OIDC issuer,例如 http://localhost:3001/oidc。空 = 未启用双轨。</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// Logto access token 的 audience(配成 Logto 里建的 API resource identifier)。
    /// 未配时双轨期 audience 不校验(见 ServiceDefaults 注释)—— 上线前必须配上。
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(Issuer);
}

/// <summary>
/// 票241:双发行方签名密钥解析器(单 scheme、多发行方 allowlist,PLAN §4.4.6 首选方案)。
/// 旧发行方 token → 对称密钥;Logto token → 按 issuer 拉 OIDC discovery 的 JWKS;
/// 其他 issuer → 空(校验失败)。备选的“双 scheme + 回落”没有采用:单 scheme 下
/// 401/403 语义与现有 ProblemDetails 口径保持一致,不引入第二套 scheme 的回落歧义。
/// </summary>
public sealed class MultiIssuerSigningKeyResolver
{
    private readonly SymmetricSecurityKey _oldKey;
    private readonly string _oldIssuer;
    private readonly string _logtoIssuer;
    private readonly Func<string, IEnumerable<SecurityKey>> _logtoKeys;
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> _logtoConfig;

    public MultiIssuerSigningKeyResolver(
        SymmetricSecurityKey oldKey,
        string oldIssuer,
        string logtoIssuer,
        Func<string, IEnumerable<SecurityKey>>? logtoKeys = null)
    {
        _oldKey = oldKey;
        _oldIssuer = oldIssuer;
        _logtoIssuer = logtoIssuer;
        _logtoKeys = logtoKeys ?? DefaultLogtoKeys;
        // Logto issuer 形如 http://localhost:3001/oidc,discovery 在其下挂
        // /.well-known/openid-configuration;ConfigurationManager 自带缓存与刷新。
        _logtoConfig = new Lazy<ConfigurationManager<OpenIdConnectConfiguration>>(() =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                _logtoIssuer.TrimEnd('/') + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever()));
    }

    public IEnumerable<SecurityKey> Resolve(
        string token, SecurityToken securityToken, string kid,
        TokenValidationParameters validationParameters)
    {
        var issuer = (securityToken as JwtSecurityToken)?.Issuer;
        if (string.IsNullOrEmpty(issuer)) return [];
        if (string.Equals(issuer, _oldIssuer, StringComparison.Ordinal)) return [_oldKey];
        if (string.Equals(issuer, _logtoIssuer, StringComparison.Ordinal)) return _logtoKeys(kid);
        return [];
    }

    private IEnumerable<SecurityKey> DefaultLogtoKeys(string kid)
    {
        // 首次调用会出站拉 discovery(沙盒内 .NET 出站被禁,故 Logto token 在沙盒
        // 里验不过是环境限制,不是代码问题;CI/生产走正常网络)。
        // 同步阻塞是 JwtBearer 解析器的既有形态(ConfigurationManager 自带缓存,
        // 命中缓存时不发请求)。
        var config = _logtoConfig.Value.GetConfigurationAsync().GetAwaiter().GetResult();
        var keys = config.SigningKeys;
        return string.IsNullOrEmpty(kid) ? keys : keys.Where(k => k.KeyId == kid);
    }
}

/// <summary>
/// 票241:Logto 角色 → 现有细粒度权限的声明转换(PLAN §4.2 第5点,唯一需要写的一小块接线)。
/// 规则:
///   · 旧发行方/Service token 自带 perm 声明 → 原样不动(绝不二次展开,避免权限膨胀);
///   · Logto token(无 perm 声明)才展开:roles/role 声明里的角色名按
///     <see cref="Roles.DefaultPermissions"/> 展开为 perm 声明,并补标准 Role 声明;
///   · Logto 永远不发 Service 角色:token 里即使出现 Service 也忽略(服务账号只走旧凭证);
///   · 未知角色名忽略,不抛错(控制台拼错角色名时表现为无权限,而不是 500)。
/// 另:迁移/新注册用户在 Logto customData 里带 legacyUserId(原 Guid 或审批门分配的
/// 新 Guid,见 Identity 的 LogtoApprovalGate);JWT customizer 把它打成 legacy_user_id
/// 声明后,本转换把它补成 NameIdentifier,业务服务按 Guid 过滤的旧逻辑无需改动。
/// 未配 JWT customizer 时新用户 NameIdentifier 仍为空,属于部署清单里的必做项
/// (docs/logto/SETUP.md),不是静默降级。
/// </summary>
public sealed class LogtoRoleClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || identity.IsAuthenticated == false) return Task.FromResult(principal);
        if (identity.HasClaim(c => c.Type == CustomClaims.Permission)) return Task.FromResult(principal);

        var roles = ReadRoles(identity)
            .Where(r => Roles.DefaultPermissions.ContainsKey(r) && r != Roles.Service)
            .Distinct()
            .ToList();
        if (roles.Count == 0) return Task.FromResult(principal);

        var extra = new List<Claim>();
        foreach (var role in roles)
        {
            extra.Add(new Claim(ClaimTypes.Role, role));
            foreach (var perm in Roles.DefaultPermissions[role]) extra.Add(new Claim(CustomClaims.Permission, perm));
        }

        var legacyId = identity.FindFirst("legacyUserId")?.Value
            ?? identity.FindFirst("legacy_user_id")?.Value;
        var nameId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (legacyId is not null && Guid.TryParse(legacyId, out _) && !Guid.TryParse(nameId, out _))
            extra.Add(new Claim(ClaimTypes.NameIdentifier, legacyId));

        if (extra.Count == 0) return Task.FromResult(principal);
        var merged = new ClaimsIdentity(
            identity.Claims.Concat(extra), identity.AuthenticationType,
            identity.NameClaimType, identity.RoleClaimType);
        return Task.FromResult(new ClaimsPrincipal(merged));
    }

    private static IEnumerable<string> ReadRoles(ClaimsIdentity identity)
    {
        foreach (var claim in identity.FindAll("roles").Concat(identity.FindAll("role")))
        {
            var value = claim.Value.Trim();
            if (value.StartsWith('['))
            {
                string[]? parsed = null;
                try { parsed = JsonSerializer.Deserialize<string[]>(value); } catch (JsonException) { }
                if (parsed is not null) foreach (var r in parsed) yield return r;
            }
            else if (value.Length > 0) yield return value;
        }
    }
}
