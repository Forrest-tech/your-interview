using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using YourInterview.Services.Identity.Domain;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "your-interview.identity";
    public string Audience { get; set; } = "your-interview.api";
    /// <summary>生产必须从 Key Vault / 环境变量注入,长度 ≥ 32 字节(HS256 要求)。</summary>
    public string SigningKey { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 30;
    public int RefreshTokenDays { get; set; } = 14;
}

public interface IJwtTokenService
{
    (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(AppUser user, IEnumerable<string> roles, IEnumerable<string> permissions);
    string CreateRawRefreshToken();
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock) : IJwtTokenService
{
    private readonly JwtOptions _o = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(
        AppUser user, IEnumerable<string> roles, IEnumerable<string> permissions)
    {
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(_o.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(CustomClaims.FullName, user.DisplayName),
            new(CustomClaims.SecurityStamp, user.SecurityStamp),
            new(ClaimTypes.NameIdentifier, user.Id.ToString())
        };

        if (!string.IsNullOrEmpty(user.AvatarUrl)) claims.Add(new Claim(CustomClaims.Avatar, user.AvatarUrl));

        // 角色 → 标准 Role claim(配合 [Authorize(Roles=...)] 开箱可用)
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        // 权限点 → 自定义 claim(配合权限策略 HasPermission("jobs.write"))
        foreach (var perm in permissions.Distinct()) claims.Add(new Claim(CustomClaims.Permission, perm));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _o.Issuer,
            audience: _o.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public string CreateRawRefreshToken() => TokenHasher.CreateRaw();
}
