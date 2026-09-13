using MediatR;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Identity.Application.Abstractions;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using YourInterview.Services.Identity.Infrastructure.Security;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Application.Auth;

// ============================ 契约 ============================

public sealed record AuthTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);
public sealed record UserProfile(
    Guid Id, string Email, string DisplayName, string? AvatarUrl,
    bool MustChangePassword, string[] Roles, string[] Permissions,
    DateTimeOffset? LastLoginAt, string? PreferredLanguage, string? TimeZone);

public sealed record AuthResult(AuthTokens Tokens, UserProfile Profile);

public sealed record RegisterUserCommand(string Email, string DisplayName, string Password)
    : IRequest<Result<AuthResult>>;

public sealed record LoginCommand(string Email, string Password, string? Device = null)
    : IRequest<Result<AuthResult>>;

public sealed record RefreshTokenCommand(string RefreshToken, string? Device = null)
    : IRequest<Result<AuthResult>>;

public sealed record LogoutCommand(string RefreshToken) : IRequest<Result>;

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword)
    : IRequest<Result>;

public sealed record UpdateProfileCommand(Guid UserId, string DisplayName, string? AvatarUrl, string? PreferredLanguage, string? TimeZone)
    : IRequest<Result<UserProfile>>;

public sealed record GetCurrentUserQuery(Guid UserId) : IRequest<Result<UserProfile>>;

// ============================ 校验 ============================

public sealed class RegisterUserCommandValidator : AbstractValidator<RegisterUserCommand>
{
    public RegisterUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320)
            .WithMessage("请输入合法邮箱地址");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200)
            .WithMessage("显示名称不能为空");
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(12).WithMessage("密码至少 12 位(OWASP 建议)")
            .Matches("[A-Z]").WithMessage("密码需包含大写字母")
            .Matches("[a-z]").WithMessage("密码需包含小写字母")
            .Matches("[0-9]").WithMessage("密码需包含数字")
            .Matches("[^a-zA-Z0-9]").WithMessage("密码需包含特殊字符");
    }
}

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordCommandValidator()
    {
        RuleFor(x => x.NewPassword)
            .NotEmpty().MinimumLength(12)
            .Matches("[A-Z]").Matches("[a-z]").Matches("[0-9]").Matches("[^a-zA-Z0-9]");
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword)
            .WithMessage("新密码不能与当前密码相同");
    }
}

// ============================ 共享逻辑 ============================

/// <summary>
/// 权限解析:用户 → 角色 → 权限集合。缓存友好(可后续加 Redis 二级缓存)。
/// </summary>
public sealed class UserPermissionResolver(IdentityDbContext db)
{
    public async Task<(string[] Roles, string[] Permissions)> ResolveAsync(Guid userId, CancellationToken ct)
    {
        var roleIds = await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct);
        if (roleIds.Count == 0) return ([], []);

        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id))
            .Select(r => r.Name).ToListAsync(ct);

        var permissions = await db.RolePermissions.Where(rp => roleIds.Contains(rp.RoleId))
            .Select(rp => rp.Permission).Distinct().ToListAsync(ct);

        return (roles.ToArray(), permissions.ToArray());
    }
}

/// <summary>把 token + 权限组装成前端要的响应。</summary>
public sealed class AuthResponseBuilder(
    IdentityDbContext db,
    IJwtTokenService jwt,
    UserPermissionResolver resolver,
    Microsoft.Extensions.Options.IOptions<JwtOptions> jwtOptions,
    TimeProvider clock)
{
    private readonly JwtOptions _o = jwtOptions.Value;

    public async Task<AuthResult> BuildAsync(AppUser user, string? device, string? ip, CancellationToken ct)
    {
        var (roles, permissions) = await resolver.ResolveAsync(user.Id, ct);
        var (access, accessExp) = jwt.CreateAccessToken(user, roles, permissions);

        var rawRefresh = jwt.CreateRawRefreshToken();
        var refreshExpires = clock.GetUtcNow().AddDays(_o.RefreshTokenDays);
        var refresh = new RefreshToken(user.Id, TokenHasher.Hash(rawRefresh), refreshExpires, device, ip);
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(ct);

        var profile = new UserProfile(user.Id, user.Email, user.DisplayName, user.AvatarUrl,
            user.MustChangePassword, roles, permissions, user.LastLoginAt, user.PreferredLanguage, user.TimeZone);

        return new AuthResult(
            new AuthTokens(access, accessExp, rawRefresh, refreshExpires),
            profile);
    }
}

// ============================ 处理器 ============================

public sealed class RegisterUserCommandHandler(
    IdentityDbContext db,
    IPasswordHasher hasher,
    AuthResponseBuilder authBuilder,
    ICurrentUser currentUser,
    ILogger<RegisterUserCommandHandler> logger)
    : IRequestHandler<RegisterUserCommand, Result<AuthResult>>
{
    public async Task<Result<AuthResult>> Handle(RegisterUserCommand request, CancellationToken ct)
    {
        var email = AppUser.Normalize(request.Email);

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<AuthResult>(Error.Conflict("Auth.EmailTaken", "该邮箱已被注册"));

        var user = new AppUser(email, request.DisplayName, hasher.Hash(request.Password));
        db.Users.Add(user);

        // 新用户默认给 User 角色
        var defaultRole = await db.Roles.FirstOrDefaultAsync(r => r.Name == Roles.User, ct);
        if (defaultRole is not null) user.AssignRole(defaultRole.Id);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("新用户注册 {Email}", email);

        var result = await authBuilder.BuildAsync(user, null, currentUser.IpAddress, ct);
        return Result.Success(result);
    }
}

public sealed class LoginCommandHandler(
    IdentityDbContext db,
    IPasswordHasher hasher,
    AuthResponseBuilder authBuilder,
    ICurrentUser currentUser,
    ILogger<LoginCommandHandler> logger)
    : IRequestHandler<LoginCommand, Result<AuthResult>>
{
    public async Task<Result<AuthResult>> Handle(LoginCommand request, CancellationToken ct)
    {
        var email = AppUser.Normalize(request.Email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // 统一错误信息:不泄露"邮箱是否存在"(防用户枚举)
        if (user is null)
        {
            logger.LogWarning("登录失败(用户不存在) {Email} from {Ip}", email, currentUser.IpAddress);
            return Result.Failure<AuthResult>(Error.Unauthorized("邮箱或密码不正确"));
        }

        if (user.IsLockedOut())
            return Result.Failure<AuthResult>(Error.Forbidden("账号因多次登录失败被暂时锁定,请稍后再试"));

        if (!user.IsActive)
            return Result.Failure<AuthResult>(Error.Forbidden("账号已停用,请联系管理员"));

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin();
            await db.SaveChangesAsync(ct);
            logger.LogWarning("登录失败(密码错误) {Email} 第 {Count} 次", email, user.FailedLoginAttempts);
            return Result.Failure<AuthResult>(Error.Unauthorized("邮箱或密码不正确"));
        }

        // 参数升级:老哈希强度不够时,登录成功顺手重哈希
        if (hasher.NeedsRehash(user.PasswordHash)) user.ChangePassword(hasher.Hash(request.Password));

        user.RecordSuccessfulLogin();
        await db.SaveChangesAsync(ct);

        var result = await authBuilder.BuildAsync(user, request.Device, currentUser.IpAddress, ct);
        return Result.Success(result);
    }
}

public sealed class RefreshTokenCommandHandler(
    IdentityDbContext db,
    AuthResponseBuilder authBuilder,
    ICurrentUser currentUser,
    ILogger<RefreshTokenCommandHandler> logger)
    : IRequestHandler<RefreshTokenCommand, Result<AuthResult>>
{
    public async Task<Result<AuthResult>> Handle(RefreshTokenCommand request, CancellationToken ct)
    {
        var hash = TokenHasher.Hash(request.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
            return Result.Failure<AuthResult>(Error.Unauthorized("刷新令牌无效"));

        // ⚠️ 令牌复用检测:已撤销的令牌再次出现 = 可能被窃取 → 撤销该用户全部令牌
        if (token.RevokedAt is not null)
        {
            logger.LogWarning("检测到刷新令牌复用!UserId={UserId} —— 撤销该用户全部令牌", token.UserId);
            var all = await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in all) t.Revoke("检测到令牌复用,安全撤销");
            await db.SaveChangesAsync(ct);
            return Result.Failure<AuthResult>(Error.Unauthorized("安全异常:令牌已被使用过,请重新登录"));
        }

        if (token.ExpiresAt <= DateTimeOffset.UtcNow)
            return Result.Failure<AuthResult>(Error.Unauthorized("刷新令牌已过期,请重新登录"));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, ct);
        if (user is null || !user.IsActive)
            return Result.Failure<AuthResult>(Error.Unauthorized("用户不可用"));

        // 轮转:旧令牌立即失效,签发新令牌
        var result = await authBuilder.BuildAsync(user, request.Device ?? token.Device, currentUser.IpAddress, ct);
        var newHash = TokenHasher.Hash(result.Tokens.RefreshToken);
        var newToken = await db.RefreshTokens.FirstAsync(t => t.TokenHash == newHash, ct);
        token.Revoke("已轮转", newToken.Id);
        await db.SaveChangesAsync(ct);

        return Result.Success(result);
    }
}

public sealed class LogoutCommandHandler(IdentityDbContext db)
    : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken ct)
    {
        var hash = TokenHasher.Hash(request.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return Result.Success();
        token.Revoke("用户登出");
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ChangePasswordCommandHandler(
    IdentityDbContext db,
    IPasswordHasher hasher,
    ILogger<ChangePasswordCommandHandler> logger)
    : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("用户"));

        if (!hasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure(Error.Validation("Auth.WrongPassword", "当前密码不正确"));

        user.ChangePassword(hasher.Hash(request.NewPassword));

        // 改密后撤销所有刷新令牌(强制其它设备重新登录)
        var tokens = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.Revoke("密码已修改");

        await db.SaveChangesAsync(ct);
        logger.LogInformation("用户 {UserId} 修改密码,已撤销 {Count} 个令牌", user.Id, tokens.Count);
        return Result.Success();
    }
}

public sealed class UpdateProfileCommandHandler(IdentityDbContext db, UserPermissionResolver resolver)
    : IRequestHandler<UpdateProfileCommand, Result<UserProfile>>
{
    public async Task<Result<UserProfile>> Handle(UpdateProfileCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure<UserProfile>(Error.NotFound("用户"));

        user.Rename(request.DisplayName);
        user.SetAvatar(request.AvatarUrl);
        user.SetPreferences(request.PreferredLanguage, request.TimeZone);
        await db.SaveChangesAsync(ct);

        var (roles, permissions) = await resolver.ResolveAsync(user.Id, ct);
        return Result.Success(new UserProfile(user.Id, user.Email, user.DisplayName, user.AvatarUrl,
            user.MustChangePassword, roles, permissions, user.LastLoginAt, user.PreferredLanguage, user.TimeZone));
    }
}

public sealed class GetCurrentUserQueryHandler(IdentityDbContext db, UserPermissionResolver resolver)
    : IRequestHandler<GetCurrentUserQuery, Result<UserProfile>>
{
    public async Task<Result<UserProfile>> Handle(GetCurrentUserQuery request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure<UserProfile>(Error.NotFound("用户"));

        var (roles, permissions) = await resolver.ResolveAsync(user.Id, ct);
        return Result.Success(new UserProfile(user.Id, user.Email, user.DisplayName, user.AvatarUrl,
            user.MustChangePassword, roles, permissions, user.LastLoginAt, user.PreferredLanguage, user.TimeZone));
    }
}
