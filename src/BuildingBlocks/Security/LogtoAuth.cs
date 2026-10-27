using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using YourInterview.SharedContracts.Security;

namespace YourInterview.BuildingBlocks.Security;

/// <summary>
/// 票241:Logto 发行方配置(双轨期)。
/// 未配 Logto:Issuer 时双轨关闭,服务行为与票241 之前一字不变(单发行方旧口径)。
/// </summary>
public sealed class LogtoOptions
{
    public const string SectionName = "Logto";

    /// <summary>Logto OIDC issuer,例如 http://localhost:3001/oidc。空 = 未启用双轨。</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Logto access token 的 audience(配成 Logto 里建的 API resource identifier)。</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// 收敛开关(默认 false):true 时选择器永远路由到 Logto,旧发行方用户 token 被拒绝。
    /// 例外:Service 角色的旧发行方 token 仍路由到 Legacy(服务回写链路的现有凭证
    /// 不走 Logto,见 LogtoSchemeSelector 注释;收敛检查里服务凭证单独验证)。
    /// </summary>
    public bool RejectOldIssuer { get; set; }

    public bool Enabled => !string.IsNullOrWhiteSpace(Issuer);
}

/// <summary>
/// 票241:双 scheme 选择器(AddPolicyScheme + ForwardDefaultSelector,唯一路由机制)。
/// 机制:先从**未验证**的 JWT payload 里读 iss 声明做路由,选中 scheme 再做完整校验
/// (签名 + issuer + 有效期)。伪造的 iss 只能把 token 误路由到另一个 scheme,
/// 那个 scheme 的完整校验随即失败 —— 路由绝不等于放行。
/// 明确**不做**顺序回落(“先试 Logto,失败再试旧发行方”):回落会把校验失败
/// 语义搅浑,且与本选择器机制重复;本选择器是唯一的双轨路由。
/// </summary>
public static class LogtoSchemeSelector
{
    public const string PolicyScheme = "LogtoOrLegacy";
    public const string LogtoScheme = "Logto";
    public const string LegacyScheme = "Legacy";

    /// <summary>供 ForwardDefaultSelector 直接挂接的版本(从请求头取 Bearer token)。</summary>
    public static string SelectScheme(HttpContext context, LogtoOptions options)
    {
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        return SelectScheme(header, options.Issuer, options.RejectOldIssuer);
    }

    public static string SelectScheme(string? authorizationHeader, string logtoIssuer, bool rejectOldIssuer)
    {
        var token = Parse(authorizationHeader);
        if (token is null) return LegacyScheme;
        // 收敛日例外:Service 角色旧 token 仍走 Legacy —— 服务账号不进 Logto,
        // 其现有凭证在收敛后必须继续工作(收敛检查里服务凭证单独验证,用户 token 则被拒)。
        if (rejectOldIssuer && HasServiceRole(token)) return LegacyScheme;
        // 收敛日:除 Service 外一律走 Logto —— 旧用户 token 在 Logto scheme 下
        // 因 issuer 不符被拒(这正是收敛的含义);Logto token 正常走 Logto。
        // 注意:不能只靠 iss 匹配 + 默认 Legacy,否则收敛日旧 token 仍在 Legacy
        // scheme 下验过,收敛等于没收。
        if (rejectOldIssuer) return LogtoScheme;
        var iss = Normalize(token.Issuer);
        if (!string.IsNullOrEmpty(iss) && string.Equals(iss, Normalize(logtoIssuer), StringComparison.Ordinal))
            return LogtoScheme;
        return LegacyScheme;
    }

    private static JwtSecurityToken? Parse(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        var value = authorizationHeader.Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value["Bearer ".Length..].Trim();
        if (value.Length == 0) return null;
        try { return new JwtSecurityTokenHandler().ReadJwtToken(value); }
        catch { return null; } // 无法解析 → Legacy;Legacy scheme 的完整校验会拒绝它
    }

    private static bool HasServiceRole(JwtSecurityToken token) =>
        token.Claims.Any(c => (c.Type == "role" || c.Type == ClaimTypes.Role)
            && string.Equals(c.Value, Roles.Service, StringComparison.Ordinal));

    private static string Normalize(string? issuer) => (issuer ?? string.Empty).Trim().TrimEnd('/');
}

/// <summary>Logto 校验参数(Logto scheme 专用:issuer + audience + 签名 + 有效期;密钥走 JWKS)。</summary>
public static class LogtoTokenValidation
{
    public static TokenValidationParameters Build(LogtoOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = !string.IsNullOrWhiteSpace(options.Audience),
        ValidAudience = string.IsNullOrWhiteSpace(options.Audience) ? null : options.Audience,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        RoleClaimType = ClaimTypes.Role
    };
}

/// <summary>
/// 映射表镜像读行(共享门唯一事实源)。LogtoUserId = Logto 用户 ID;
/// LegacyUserId = 业务 Guid(迁移用户的原 Guid / 新注册用户由审批门分配的 Guid)。
/// </summary>
public sealed record LogtoMirrorEntry(
    string LogtoUserId,
    Guid LegacyUserId,
    string Email,
    string Approval,
    bool IsSuspended,
    bool SuspendedByGate,
    DateTimeOffset? LastEventAt);

/// <summary>映射表 + 旧侧 IsActive 的只读查询(非 Identity 服务直接查同一 Postgres 库)。</summary>
public interface ILogtoMirrorReader
{
    Task<LogtoMirrorEntry?> GetByLogtoIdAsync(string logtoUserId, CancellationToken ct);
    Task<LogtoMirrorEntry?> GetByLegacyIdAsync(Guid legacyUserId, CancellationToken ct);

    /// <summary>旧侧用户 IsActive;null = 用户不存在或已删除。</summary>
    Task<bool?> GetOldUserIsActiveAsync(Guid legacyUserId, CancellationToken ct);
}

/// <summary>双轨关闭时的空实现(门在 !Enabled 时直接返回,本实现永远不会被调用)。</summary>
public sealed class NullMirrorReader : ILogtoMirrorReader
{
    public Task<LogtoMirrorEntry?> GetByLogtoIdAsync(string logtoUserId, CancellationToken ct) =>
        Task.FromResult<LogtoMirrorEntry?>(null);
    public Task<LogtoMirrorEntry?> GetByLegacyIdAsync(Guid legacyUserId, CancellationToken ct) =>
        Task.FromResult<LogtoMirrorEntry?>(null);
    public Task<bool?> GetOldUserIsActiveAsync(Guid legacyUserId, CancellationToken ct) =>
        Task.FromResult<bool?>(null);
}

public sealed class MirrorRowRead
{
    public string LogtoUserId { get; set; } = string.Empty;
    public Guid LegacyUserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Approval { get; set; } = string.Empty;
    public bool IsSuspended { get; set; }
    public bool SuspendedByGate { get; set; }
    public DateTimeOffset? LastEventAt { get; set; }
}

public sealed class OldUserRead
{
    public Guid Id { get; set; }
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
}

/// <summary>
/// 映射表只读上下文(票241 round-3 决议:直接跨 schema 只读查询,不是 Identity API 调用)。
/// 只读查询专用:本上下文永远不跑迁移(迁移归 Identity 服务的 identity.logto_user_mapping 迁移管),
/// 表名/列名与 Identity 的 LogtoUserMapping 配置逐字对齐,漂移会在首个带 Logto token 的请求暴露。
/// </summary>
public sealed class LogtoMappingReadContext : DbContext
{
    private readonly string _connectionString;

    public LogtoMappingReadContext(string connectionString) => _connectionString = connectionString;

    public DbSet<MirrorRowRead> MirrorRows => Set<MirrorRowRead>();
    public DbSet<OldUserRead> OldUsers => Set<OldUserRead>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseNpgsql(_connectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MirrorRowRead>(e =>
        {
            e.ToTable("logto_user_mapping", "identity");
            e.HasKey(m => m.LogtoUserId);
            e.Property(m => m.LogtoUserId).HasColumnName("LogtoUserId").HasMaxLength(64);
            e.Property(m => m.LegacyUserId).HasColumnName("LegacyUserId");
            e.Property(m => m.Email).HasColumnName("Email").HasMaxLength(320);
            e.Property(m => m.Approval).HasColumnName("Approval").HasMaxLength(32);
            e.Property(m => m.IsSuspended).HasColumnName("IsSuspended");
            e.Property(m => m.SuspendedByGate).HasColumnName("SuspendedByGate");
            e.Property(m => m.LastEventAt).HasColumnName("LastEventAt");
        });
        modelBuilder.Entity<OldUserRead>(e =>
        {
            e.ToTable("users", "identity");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("Id");
            e.Property(u => u.IsActive).HasColumnName("IsActive");
            e.Property(u => u.IsDeleted).HasColumnName("IsDeleted");
        });
    }
}

/// <summary>基于同一库只读查询的镜像实现(单条索引查询,不缓存:停用必须下一请求即生效)。</summary>
public sealed class EfLogtoMirrorReader(string connectionString) : ILogtoMirrorReader
{
    public async Task<LogtoMirrorEntry?> GetByLogtoIdAsync(string logtoUserId, CancellationToken ct)
    {
        await using var db = new LogtoMappingReadContext(connectionString);
        var row = await db.MirrorRows.AsNoTracking()
            .FirstOrDefaultAsync(m => m.LogtoUserId == logtoUserId, ct);
        return row is null ? null : new LogtoMirrorEntry(
            row.LogtoUserId, row.LegacyUserId, row.Email, row.Approval,
            row.IsSuspended, row.SuspendedByGate, row.LastEventAt);
    }

    public async Task<LogtoMirrorEntry?> GetByLegacyIdAsync(Guid legacyUserId, CancellationToken ct)
    {
        await using var db = new LogtoMappingReadContext(connectionString);
        var row = await db.MirrorRows.AsNoTracking()
            .FirstOrDefaultAsync(m => m.LegacyUserId == legacyUserId, ct);
        return row is null ? null : new LogtoMirrorEntry(
            row.LogtoUserId, row.LegacyUserId, row.Email, row.Approval,
            row.IsSuspended, row.SuspendedByGate, row.LastEventAt);
    }

    public async Task<bool?> GetOldUserIsActiveAsync(Guid legacyUserId, CancellationToken ct)
    {
        await using var db = new LogtoMappingReadContext(connectionString);
        return await db.OldUsers.AsNoTracking()
            .Where(u => u.Id == legacyUserId && !u.IsDeleted)
            .Select(u => (bool?)u.IsActive)
            .FirstOrDefaultAsync(ct);
    }
}

/// <summary>
/// 票241:共享审批/停用门 —— 全服务唯一的门实现(claims 转换层,各服务只引用,不许各自抄分支)。
/// 灰度期两个 scheme 都过本门:
///   · Logto token(Logto-sub 查镜像):无行 → 零权限;approval 非 approved/migrated → 零权限
///     (但 NameIdentifier 一律翻译成业务 Guid,令 /me 能返回待审批态供前端显示);
///     IsSuspended → 零权限;通过时 scope 声明(空格分隔)过滤为 Permissions.All 已知值后打成
///     perm 声明,未知 scope 忽略;Logto token 里的 Service 角色声明直接丢弃(服务账号不走 Logto)。
///   · 旧发行方 token(业务 Guid 即 subject):有镜像行 → 镜像门(approval + 停用,严格 allowlist,
///     approval 为空也拒绝);无镜像行 → 回落旧侧 IsActive(未迁移用户灰度期旧登录不断;
///     IsActive=false 或用户不存在/已删 → 剥夺 perm)。
///   · 查库失败不对称(已定):Logto token 查库失败 → fail-closed 零权限;
///     旧 token 查库失败 → 原样放行(旧 token 的权威是旧 Identity,停用传播靠双写 + 扫表)。
/// 门只剥夺/翻译声明,从不补发权限:缺 scope、scope 未映射、无业务 Guid 一律零权限(default-deny)。
/// </summary>
public sealed class LogtoClaimsGate(
    ILogtoMirrorReader reader,
    IOptions<LogtoOptions> options,
    ILogger<LogtoClaimsGate> logger) : IClaimsTransformation
{
    private static readonly HashSet<string> ApprovedMarks = new(StringComparer.Ordinal) { "approved", "migrated" };

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated) return principal;
        var opts = options.Value;
        if (!opts.Enabled) return principal; // 双轨关闭:旧口径一字不变

        var authType = identity.AuthenticationType ?? string.Empty;
        var isLogto = string.Equals(authType, LogtoSchemeSelector.LogtoScheme, StringComparison.Ordinal)
            || string.Equals(identity.FindFirst("iss")?.Value?.Trim().TrimEnd('/'),
                opts.Issuer.Trim().TrimEnd('/'), StringComparison.Ordinal);
        if (isLogto) return await TransformLogtoAsync(principal, identity, ct: default);
        return await TransformOldAsync(principal, identity);
    }

    private async Task<ClaimsPrincipal> TransformLogtoAsync(ClaimsPrincipal principal, ClaimsIdentity identity, CancellationToken ct)
    {
        // Logto token 的权威 = 镜像 + scope,绝不信任 token 自带的 perm 声明:先全剥掉。
        var kept = identity.Claims
            .Where(c => c.Type != CustomClaims.Permission
                && !(c.Type == ClaimTypes.Role && string.Equals(c.Value, Roles.Service, StringComparison.Ordinal)))
            .ToList();
        var sub = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? identity.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return Rebuild(principal, identity, kept, []);

        LogtoMirrorEntry? row;
        try { row = await reader.GetByLogtoIdAsync(sub, ct); }
        catch (Exception ex)
        {
            // fail-closed:查库失败的 Logto token 一律零权限(不翻译、不补 perm)。
            logger.LogWarning(ex, "Logto 镜像查询失败,token 按零权限处理 sub={Sub}", sub);
            return Rebuild(principal, identity, kept, []);
        }
        if (row is null) return Rebuild(principal, identity, kept, []);

        // NameIdentifier 一律翻译成业务 Guid(待审批用户也能拿到 /me,前端据此显示待审批态)。
        kept = kept.Where(c => c.Type != ClaimTypes.NameIdentifier).ToList();
        var extra = new List<Claim> { new(ClaimTypes.NameIdentifier, row.LegacyUserId.ToString()) };
        if (!ApprovedMarks.Contains(row.Approval) || row.IsSuspended)
            return Rebuild(principal, identity, kept, extra);

        var known = new HashSet<string>(Permissions.All, StringComparer.Ordinal);
        var perms = identity.FindAll("scope")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(known.Contains)
            .Distinct()
            .Select(p => new Claim(CustomClaims.Permission, p));
        return Rebuild(principal, identity, kept, extra.Concat(perms).ToList());
    }

    private async Task<ClaimsPrincipal> TransformOldAsync(ClaimsPrincipal principal, ClaimsIdentity identity)
    {
        if (!identity.HasClaim(c => c.Type == CustomClaims.Permission)) return principal;
        var nameId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (nameId is null || !Guid.TryParse(nameId, out var guid)) return principal;

        LogtoMirrorEntry? row;
        try { row = await reader.GetByLegacyIdAsync(guid, default); }
        catch (Exception ex)
        {
            // 可用性规则:旧 token 查库失败 → 原样放行(旧 token 的权威是旧 Identity)。
            logger.LogWarning(ex, "旧 token 镜像查询失败,按旧口径放行 user={UserId}", guid);
            return principal;
        }
        if (row is not null)
        {
            // 镜像门(严格 allowlist):approval 非 approved/migrated 或已停用 → 剥夺全部 perm。
            if (!ApprovedMarks.Contains(row.Approval) || row.IsSuspended)
                return StripPerms(principal, identity);
            return principal;
        }

        // 无镜像行 = 未迁移用户:回落旧侧 IsActive(灰度期旧登录不断;停用/不存在 → 剥夺)。
        bool? active;
        try { active = await reader.GetOldUserIsActiveAsync(guid, default); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "旧侧 IsActive 查询失败,按旧口径放行 user={UserId}", guid);
            return principal;
        }
        return active == true ? principal : StripPerms(principal, identity);
    }

    private static ClaimsPrincipal StripPerms(ClaimsPrincipal principal, ClaimsIdentity identity) =>
        Rebuild(principal, identity,
            identity.Claims.Where(c => c.Type != CustomClaims.Permission).ToList(), []);

    private static ClaimsPrincipal Rebuild(ClaimsPrincipal principal, ClaimsIdentity identity,
        List<Claim> kept, List<Claim> extra)
    {
        var merged = new ClaimsIdentity(kept.Concat(extra), identity.AuthenticationType,
            identity.NameClaimType, identity.RoleClaimType);
        return new ClaimsPrincipal(merged);
    }
}
