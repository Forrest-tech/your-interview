using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Identity.Domain;

/// <summary>
/// 用户聚合根。身份与访问上下文的核心。
/// 设计要点(面试可讲):
///  - 密码永远只存 Argon2id 哈希,绝不存明文/可逆加密
///  - SecurityStamp 变更即让所有已签发 token 失效(改密/踢下线)
///  - 角色 → 权限 在聚合内维护,保证"权限校验只读聚合,不散落各处"
/// </summary>
public sealed class AppUser : AuditableAggregateRoot
{
    private readonly List<UserRole> _roles = new();

    private AppUser() { } // EF Core

    public AppUser(string email, string displayName, string passwordHash, string? avatarUrl = null)
    {
        Email = Normalize(email);
        DisplayName = displayName;
        PasswordHash = passwordHash;
        AvatarUrl = avatarUrl;
        IsActive = true;
        SecurityStamp = Guid.NewGuid().ToString("N");
        LastPasswordChangedAt = DateTimeOffset.UtcNow;
    }

    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string PasswordHash { get; private set; } = string.Empty;
    public string? AvatarUrl { get; private set; }
    public string SecurityStamp { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public bool MustChangePassword { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset? LastPasswordChangedAt { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public string? PreferredLanguage { get; private set; } = "en";
    public string? TimeZone { get; private set; } = "America/Toronto";

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    // ---------- 行为(所有状态变更都走方法,保证不变式) ----------

    public void ChangePassword(string newHash)
    {
        PasswordHash = newHash;
        SecurityStamp = Guid.NewGuid().ToString("N"); // 踢掉所有旧 token
        LastPasswordChangedAt = DateTimeOffset.UtcNow;
        MustChangePassword = false;
        RaiseDomainEvent(new UserPasswordChanged(Id, Email));
    }

    public void Rename(string displayName)
    {
        DisplayName = displayName;
        Touch();
    }

    public void SetAvatar(string? url)
    {
        AvatarUrl = url;
        Touch();
    }

    public void SetPreferences(string? language, string? timeZone)
    {
        PreferredLanguage = language ?? PreferredLanguage;
        TimeZone = timeZone ?? TimeZone;
        Touch();
    }

    public void RecordSuccessfulLogin()
    {
        LastLoginAt = DateTimeOffset.UtcNow;
        FailedLoginAttempts = 0;
        LockedUntil = null;
        Touch();
    }

    public void RecordFailedLogin(int maxAttempts = 8, int lockMinutes = 15)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= maxAttempts)
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(lockMinutes);
        Touch();
    }

    public bool IsLockedOut() => LockedUntil.HasValue && LockedUntil > DateTimeOffset.UtcNow;

    public void Deactivate()
    {
        IsActive = false;
        SecurityStamp = Guid.NewGuid().ToString("N");
        RaiseDomainEvent(new UserDeactivated(Id, Email));
    }

    public void Activate()
    {
        IsActive = true;
        Touch();
    }

    public void RequirePasswordChange()
    {
        MustChangePassword = true;
        Touch();
    }

    // ---------- 角色管理 ----------

    public void AssignRole(Guid roleId, Guid? assignedBy = null)
    {
        if (_roles.Any(r => r.RoleId == roleId)) return;
        _roles.Add(new UserRole(Id, roleId, assignedBy));
        RaiseDomainEvent(new UserRoleAssigned(Id, roleId, assignedBy));
    }

    public void RemoveRole(Guid roleId)
    {
        var existing = _roles.FirstOrDefault(r => r.RoleId == roleId);
        if (existing is null) return;
        _roles.Remove(existing);
        RaiseDomainEvent(new UserRoleRemoved(Id, roleId));
    }

    public void ReplaceRoles(IEnumerable<Guid> roleIds, Guid? assignedBy = null)
    {
        var target = roleIds.Distinct().ToHashSet();
        foreach (var r in _roles.Where(r => !target.Contains(r.RoleId)).ToList()) RemoveRole(r.RoleId);
        foreach (var id in target) AssignRole(id, assignedBy);
    }
}

/// <summary>用户-角色关联(关联实体,归属 AppUser 聚合)。</summary>
public sealed class UserRole
{
    private UserRole() { }
    internal UserRole(Guid userId, Guid roleId, Guid? assignedBy)
    {
        UserId = userId;
        RoleId = roleId;
        AssignedAt = DateTimeOffset.UtcNow;
        AssignedBy = assignedBy;
    }

    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }
    public Guid? AssignedBy { get; private set; }

    public AppUser User { get; private set; } = null!;
    public AppRole Role { get; private set; } = null!;
}

/// <summary>
/// 角色聚合根 + 权限集合。Admin 是系统角色(不可删除)。
/// </summary>
public sealed class AppRole : AuditableAggregateRoot
{
    private readonly List<RolePermission> _permissions = new();

    private AppRole() { }

    public AppRole(string name, string? description, bool isSystemRole = false)
    {
        Name = name;
        Description = description;
        IsSystemRole = isSystemRole;
    }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsSystemRole { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public void Grant(string permission)
    {
        if (_permissions.Any(p => p.Permission == permission)) return;
        _permissions.Add(new RolePermission(Id, permission));
        RaiseDomainEvent(new RolePermissionChanged(Id, permission, true));
    }

    public void Revoke(string permission)
    {
        var existing = _permissions.FirstOrDefault(p => p.Permission == permission);
        if (existing is null) return;
        _permissions.Remove(existing);
        RaiseDomainEvent(new RolePermissionChanged(Id, permission, false));
    }

    public void SetPermissions(IEnumerable<string> permissions)
    {
        var target = permissions.Distinct().ToHashSet();
        foreach (var p in _permissions.Where(p => !target.Contains(p.Permission)).ToList()) Revoke(p.Permission);
        foreach (var p in target) Grant(p);
        Touch();
    }

    public void Rename(string name, string? description)
    {
        if (IsSystemRole) throw new InvalidOperationException("系统内置角色不允许重命名");
        Name = name;
        Description = description;
        Touch();
    }
}

public sealed class RolePermission
{
    private RolePermission() { }
    internal RolePermission(Guid roleId, string permission)
    {
        RoleId = roleId;
        Permission = permission;
    }

    public Guid RoleId { get; private set; }
    public string Permission { get; private set; } = string.Empty;
    public AppRole Role { get; private set; } = null!;
}

/// <summary>刷新令牌(轮转 + 复用检测)。</summary>
public sealed class RefreshToken : Entity
{
    private RefreshToken() { }

    public RefreshToken(Guid userId, string tokenHash, DateTimeOffset expiresAt, string? device, string? ip)
    {
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        Device = device;
        CreatedByIp = ip;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokedReason { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }
    public string? Device { get; private set; }
    public string? CreatedByIp { get; private set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;

    public void Revoke(string reason, Guid? replacedBy = null)
    {
        RevokedAt = DateTimeOffset.UtcNow;
        RevokedReason = reason;
        ReplacedByTokenId = replacedBy;
    }
}

/// <summary>审计日志(PIPEDA / SOC2 需要:谁、何时、对什么、做了什么、从哪个 IP)。</summary>
public sealed class AuditLog : Entity
{
    private AuditLog() { }

    public AuditLog(Guid? userId, string action, string resource, string? resourceId,
        string? detail, string? ip, string? userAgent, bool success)
    {
        UserId = userId;
        Action = action;
        Resource = resource;
        ResourceId = resourceId;
        Detail = detail;
        IpAddress = ip;
        UserAgent = userAgent;
        Success = success;
        OccurredAt = DateTimeOffset.UtcNow;
    }

    public Guid? UserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Resource { get; private set; } = string.Empty;
    public string? ResourceId { get; private set; }
    public string? Detail { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public bool Success { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
}

// ---------- 领域事件 ----------
public sealed record UserPasswordChanged(Guid UserId, string Email) : DomainEventBase;
public sealed record UserDeactivated(Guid UserId, string Email) : DomainEventBase;
public sealed record UserRoleAssigned(Guid UserId, Guid RoleId, Guid? AssignedBy) : DomainEventBase;
public sealed record UserRoleRemoved(Guid UserId, Guid RoleId) : DomainEventBase;
public sealed record RolePermissionChanged(Guid RoleId, string Permission, bool Granted) : DomainEventBase;
