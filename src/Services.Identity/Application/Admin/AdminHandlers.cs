using YourInterview.BuildingBlocks.Security;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using YourInterview.Services.Identity.Infrastructure.Security;
using YourInterview.SharedContracts.Security;

namespace YourInterview.Services.Identity.Application.Admin;

// ============================ 契约 ============================

/// <summary>
/// 用户列表项。
/// ★ M2.4:补 LockedUntil —— 管理员得先看见"谁被锁了",才谈得上解锁;
///   之前统计里有 lockedAccounts,列表里却没有,管理员只能干瞪眼。
/// </summary>
public sealed record AdminUserListItem(
    Guid Id, string Email, string DisplayName, bool IsActive, bool MustChangePassword,
    DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, string[] Roles, string? AvatarUrl,
    DateTimeOffset? LockedUntil);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(Total / (double)PageSize);
}

public sealed record AdminUserQuery(string? Search, string? Role, bool? IsActive, int Page = 1, int PageSize = 20)
    : IRequest<Result<PagedResult<AdminUserListItem>>>;

public sealed record AdminCreateUserCommand(string Email, string DisplayName, string Password, string[] Roles)
    : IRequest<Result<Guid>>;

public sealed record AdminUpdateUserCommand(Guid UserId, string DisplayName, bool IsActive, bool RequirePasswordChange, string[] Roles)
    : IRequest<Result>;

public sealed record AdminResetPasswordCommand(Guid UserId, string NewPassword) : IRequest<Result>;

/// <summary>M2.4:连续登录失败锁定的账号,管理员人工解锁。</summary>
public sealed record AdminUnlockUserCommand(Guid UserId) : IRequest<Result>;

public sealed record AdminDeleteUserCommand(Guid UserId) : IRequest<Result>;

public sealed record RoleListItem(Guid Id, string Name, string? Description, bool IsSystemRole, string[] Permissions, int UserCount);

public sealed record AdminRolesQuery : IRequest<Result<IReadOnlyList<RoleListItem>>>;

public sealed record AdminUpsertRoleCommand(Guid? RoleId, string Name, string? Description, string[] Permissions)
    : IRequest<Result<Guid>>;

public sealed record AdminDeleteRoleCommand(Guid RoleId) : IRequest<Result>;

public sealed record PermissionCatalogItem(string Key, string Resource, string Action, string Description);

public sealed record AdminPermissionCatalogQuery : IRequest<Result<IReadOnlyList<PermissionCatalogItem>>>;

public sealed record AuditLogItem(Guid Id, Guid? UserId, string? UserEmail, string Action, string Resource,
    string? ResourceId, string? Detail, string? IpAddress, bool Success, DateTimeOffset OccurredAt);

public sealed record AdminAuditLogQuery(string? Action, Guid? UserId, DateTimeOffset? From, DateTimeOffset? To,
    int Page = 1, int PageSize = 50) : IRequest<Result<PagedResult<AuditLogItem>>>;

public sealed record AdminStatsQuery : IRequest<Result<AdminStats>>;

public sealed record AdminStats(
    int TotalUsers, int ActiveUsers, int NewUsersLast7Days, int TotalRoles, int TotalAuditEvents,
    int FailedLoginsLast24h, int LockedAccounts, IReadOnlyList<DailyCount> SignupsTrend);

public sealed record DailyCount(DateOnly Date, int Count);

// ============================ 校验 ============================

public sealed class AdminCreateUserCommandValidator : AbstractValidator<AdminCreateUserCommand>
{
    public AdminCreateUserCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12)
            .Matches("[A-Z]").Matches("[a-z]").Matches("[0-9]").Matches("[^a-zA-Z0-9]");
        RuleFor(x => x.Roles).NotEmpty().WithMessage("必须指定至少一个角色");
    }
}

public sealed class AdminUpsertRoleCommandValidator : AbstractValidator<AdminUpsertRoleCommand>
{
    public AdminUpsertRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Permissions).NotNull();
        RuleForEach(x => x.Permissions).Must(p => Permissions.All.Contains(p))
            .WithMessage("存在未知权限点: {PropertyValue}");
    }
}

// ============================ 特权操作辅助(M2.4) ============================

/// <summary>
/// 显式写一条"管理员操作"审计。
///
/// 为什么不能只靠 AuditSaveChangesInterceptor:
/// 拦截器只知道"哪个实体的哪些字段变了",写出来是
/// `appuser.updated / fields: IsActive, SecurityStamp` ——
/// 合规审计(SOC2 / PIPEDA)要回答的是"**谁**在什么时候把**谁**停用了",
/// 这种语义只有动作发起处知道。所以特权动作必须自己补一条可读记录。
/// </summary>
internal static class AdminAudit
{
    public static void Write(IdentityDbContext db, ICurrentUser currentUser, string action,
        string resource, string? resourceId, string? detail) =>
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, action, resource, resourceId, detail,
            currentUser.IpAddress, currentUser.UserAgent, true));
}

/// <summary>管理员自我保护:防止把系统里最后一个可用的管理员弄没了(M2.4)。</summary>
internal static class AdminGuard
{
    /// <summary>角色名归一化:去空白、去空、去重。手输/前端多选都可能带空格或重复。</summary>
    public static string[] NormalizeRoles(IEnumerable<string> roles) =>
        roles.Select(r => r?.Trim() ?? string.Empty)
             .Where(r => r.Length > 0)
             .Distinct(StringComparer.Ordinal)
             .ToArray();

    /// <summary>当前处于"启用"状态的 Admin 角色用户数量。</summary>
    public static async Task<int> ActiveAdminCountAsync(IdentityDbContext db, CancellationToken ct)
    {
        var adminRoleId = await AdminRoleIdAsync(db, ct);
        if (adminRoleId is null) return 0;

        var ids = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.RoleId == adminRoleId.Value).Select(ur => ur.UserId).ToListAsync(ct);

        // db.Users 带 !IsDeleted 查询过滤器,这里只需再判 IsActive
        return await db.Users.CountAsync(u => u.IsActive && ids.Contains(u.Id), ct);
    }

    public static async Task<Guid?> AdminRoleIdAsync(IdentityDbContext db, CancellationToken ct) =>
        await db.Roles.AsNoTracking().Where(r => r.Name == Roles.Admin).Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);
}

// ============================ 处理器 ============================

public sealed class AdminUserQueryHandler(IdentityDbContext db)
    : IRequestHandler<AdminUserQuery, Result<PagedResult<AdminUserListItem>>>
{
    public async Task<Result<PagedResult<AdminUserListItem>>> Handle(AdminUserQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 200);

        var q = db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(u => u.Email.Contains(s) || u.DisplayName.ToLower().Contains(s));
        }
        if (request.IsActive.HasValue) q = q.Where(u => u.IsActive == request.IsActive.Value);

        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var roleName = request.Role;
            q = q.Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id
                && db.Roles.Any(r => r.Id == ur.RoleId && r.Name == roleName)));
        }

        var total = await q.CountAsync(ct);

        var users = await q.OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .Select(u => new
            {
                u.Id, u.Email, u.DisplayName, u.IsActive, u.MustChangePassword,
                u.LastLoginAt, u.CreatedAt, u.AvatarUrl, u.LockedUntil,
                Roles = (from ur in db.UserRoles
                         join r in db.Roles on ur.RoleId equals r.Id
                         where ur.UserId == u.Id
                         select r.Name).ToArray()
            })
            .ToListAsync(ct);

        var items = users.Select(u => new AdminUserListItem(
            u.Id, u.Email, u.DisplayName, u.IsActive, u.MustChangePassword,
            u.LastLoginAt, u.CreatedAt, u.Roles, u.AvatarUrl, u.LockedUntil)).ToList();

        return Result.Success(new PagedResult<AdminUserListItem>(items, total, page, size));
    }
}

public sealed class AdminCreateUserCommandHandler(
    IdentityDbContext db, IPasswordHasher hasher, ICurrentUser currentUser)
    : IRequestHandler<AdminCreateUserCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AdminCreateUserCommand request, CancellationToken ct)
    {
        var email = AppUser.Normalize(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<Guid>(Error.Conflict("Auth.EmailTaken", "该邮箱已存在"));

        // ★ M2.4:角色名写错要报 400,而不是悄悄忽略。
        //   之前传一个不存在的角色名会静默建出"零角色"账号 ——
        //   管理员以为建好了,用户登录后处处 403,还得反过来查为什么。
        var wanted = AdminGuard.NormalizeRoles(request.Roles);
        var validRoles = await db.Roles.Where(r => wanted.Contains(r.Name)).ToListAsync(ct);
        if (validRoles.Count != wanted.Length)
            return Result.Failure<Guid>(Error.Validation("Role.NotFound",
                $"指定的角色不存在: {string.Join(", ", wanted.Except(validRoles.Select(r => r.Name)))}"));
        if (validRoles.Count == 0)
            return Result.Failure<Guid>(Error.Validation("Role.NotFound", "必须指定至少一个角色"));

        var user = new AppUser(email, request.DisplayName, hasher.Hash(request.Password));
        user.RequirePasswordChange(); // 管理员代建的账号,首次登录必须改密
        foreach (var r in validRoles) user.AssignRole(r.Id);

        db.Users.Add(user);
        AdminAudit.Write(db, currentUser, "admin.user.created", "AppUser", user.Id.ToString(),
            $"email: {email}; roles: {string.Join(", ", validRoles.Select(r => r.Name))}");

        await db.SaveChangesAsync(ct);
        return Result.Success(user.Id);
    }
}

public sealed class AdminUpdateUserCommandHandler(IdentityDbContext db, ICurrentUser currentUser)
    : IRequestHandler<AdminUpdateUserCommand, Result>
{
    public async Task<Result> Handle(AdminUpdateUserCommand request, CancellationToken ct)
    {
        // ⚠️ 必须带 Roles:ReplaceRoles 的幂等判断(Any(RoleId==))读的是聚合内的
        // _roles 集合 —— 不 Include 就是空集,已有角色关系会被当新关系重复 INSERT,
        // 撞 user_roles 主键 500(M2.2 修复;与 M1.5 Jobs/Interviews 的子实体问题同类,
        // 但方向相反:那边是"该 Added 被当 Modified",这边是"该跳过被当新增")。
        var user = await db.Users.Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("用户"));

        var isSelf = currentUser.UserId == request.UserId;

        // ★ M2.4 防自锁:管理员停用自己 / 摘掉自己的 Admin 角色,会当场失去管理后台入口,
        //   只能靠改库救回来。Azure AD、AWS IAM、Keycloak 都把这条挡在 API 层。
        if (isSelf && !request.IsActive)
            return Result.Failure(Error.Conflict("Admin.SelfDeactivate", "不能停用自己的账号"));

        var wanted = AdminGuard.NormalizeRoles(request.Roles);
        var roles = await db.Roles.Where(r => wanted.Contains(r.Name)).ToListAsync(ct);
        if (roles.Count != wanted.Length)
            return Result.Failure(Error.Validation("Role.NotFound",
                $"指定的角色不存在: {string.Join(", ", wanted.Except(roles.Select(r => r.Name)))}"));

        var adminRoleId = await AdminGuard.AdminRoleIdAsync(db, ct);
        var hadAdmin = adminRoleId is not null && user.Roles.Any(ur => ur.RoleId == adminRoleId.Value);
        var willHaveAdmin = roles.Any(r => r.Name == Roles.Admin);

        if (isSelf && hadAdmin && !willHaveAdmin)
            return Result.Failure(Error.Conflict("Admin.SelfDemote", "不能移除自己的管理员角色"));

        // ★ 最后一个管理员保护:把唯一的 Admin 降级或停用,系统从此没人进得了管理后台。
        if (hadAdmin && (!willHaveAdmin || !request.IsActive)
            && await AdminGuard.ActiveAdminCountAsync(db, ct) <= 1)
            return Result.Failure(Error.Conflict("Admin.LastAdmin", "系统必须保留至少一个启用状态的管理员"));

        user.Rename(request.DisplayName);

        if (request.IsActive && !user.IsActive) user.Activate();
        if (!request.IsActive && user.IsActive) user.Deactivate();

        if (request.RequirePasswordChange) user.RequirePasswordChange();
        else user.ClearPasswordChangeRequirement();   // 开关语义:关掉=解除(M2.2)

        user.ReplaceRoles(roles.Select(r => r.Id));

        AdminAudit.Write(db, currentUser, "admin.user.updated", "AppUser", user.Id.ToString(),
            $"target: {user.Email}; active: {request.IsActive}; " +
            $"requirePasswordChange: {request.RequirePasswordChange}; roles: {string.Join(", ", wanted)}");

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AdminResetPasswordCommandHandler(
    IdentityDbContext db, IPasswordHasher hasher, ICurrentUser currentUser)
    : IRequestHandler<AdminResetPasswordCommand, Result>
{
    public async Task<Result> Handle(AdminResetPasswordCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("用户"));

        user.ChangePassword(hasher.Hash(request.NewPassword));
        user.RequirePasswordChange();

        var tokens = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in tokens) t.Revoke("管理员重置了密码");

        AdminAudit.Write(db, currentUser, "admin.user.password_reset", "AppUser", user.Id.ToString(),
            $"target: {user.Email}");

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>M2.4:解锁因连续登录失败被锁的账号。</summary>
public sealed class AdminUnlockUserCommandHandler(IdentityDbContext db, ICurrentUser currentUser)
    : IRequestHandler<AdminUnlockUserCommand, Result>
{
    public async Task<Result> Handle(AdminUnlockUserCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("用户"));

        user.Unlock();

        AdminAudit.Write(db, currentUser, "admin.user.unlocked", "AppUser", user.Id.ToString(),
            $"target: {user.Email}");

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AdminDeleteUserCommandHandler(IdentityDbContext db, ICurrentUser currentUser)
    : IRequestHandler<AdminDeleteUserCommand, Result>
{
    public async Task<Result> Handle(AdminDeleteUserCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("用户"));

        if (currentUser.UserId == request.UserId)
            return Result.Failure(Error.Conflict("Admin.SelfDelete", "不能删除自己的账号"));

        var adminRoleId = await AdminGuard.AdminRoleIdAsync(db, ct);
        if (adminRoleId is not null
            && await db.UserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == adminRoleId.Value, ct)
            && await AdminGuard.ActiveAdminCountAsync(db, ct) <= 1)
            return Result.Failure(Error.Conflict("Admin.LastAdmin", "系统必须保留至少一个启用状态的管理员"));

        // 软删除(审计要求:记录保留,账号失效)
        user.Deactivate();
        user.MarkDeleted();

        AdminAudit.Write(db, currentUser, "admin.user.deleted", "AppUser", user.Id.ToString(),
            $"target: {user.Email}");

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AdminRolesQueryHandler(IdentityDbContext db)
    : IRequestHandler<AdminRolesQuery, Result<IReadOnlyList<RoleListItem>>>
{
    public async Task<Result<IReadOnlyList<RoleListItem>>> Handle(AdminRolesQuery request, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking()
            .Select(r => new RoleListItem(
                r.Id, r.Name, r.Description, r.IsSystemRole,
                db.RolePermissions.Where(p => p.RoleId == r.Id).Select(p => p.Permission).ToArray(),
                db.UserRoles.Count(ur => ur.RoleId == r.Id)))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<RoleListItem>>(roles);
    }
}

public sealed class AdminUpsertRoleCommandHandler(IdentityDbContext db, ICurrentUser currentUser)
    : IRequestHandler<AdminUpsertRoleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AdminUpsertRoleCommand request, CancellationToken ct)
    {
        AppRole role;
        var isNew = !request.RoleId.HasValue;

        if (request.RoleId.HasValue)
        {
            var found = await db.Roles.Include(r => r.Permissions)
                .FirstOrDefaultAsync(r => r.Id == request.RoleId.Value, ct);
            if (found is null) return Result.Failure<Guid>(Error.NotFound("角色"));
            role = found;

            if (role.IsSystemRole && role.Name != request.Name)
                return Result.Failure<Guid>(Error.Conflict("Role.System", "系统内置角色不允许改名"));

            if (role.Name == Roles.Admin && !request.Permissions.Contains(Permissions.AdminUsersWrite))
                return Result.Failure<Guid>(Error.Conflict("Role.AdminLock", "Admin 角色必须保留用户管理权限"));

            // ★ M2.4:别把自己脚下的梯子抽掉 ——
            //   管理员改掉"自己所属角色"的用户/审计权限,保存完立刻进不去管理后台。
            if (currentUser.UserId is { } me
                && await db.UserRoles.AnyAsync(ur => ur.UserId == me && ur.RoleId == role.Id, ct))
            {
                var lost = new[] { Permissions.AdminUsersRead, Permissions.AdminAuditRead }
                    .Where(p => role.Permissions.Any(rp => rp.Permission == p) && !request.Permissions.Contains(p))
                    .ToArray();
                if (lost.Length > 0)
                    return Result.Failure<Guid>(Error.Conflict("Role.SelfLockout",
                        $"不能移除自己所属角色上的 {string.Join(", ", lost)} —— 否则你将无法进入管理后台"));
            }
        }
        else
        {
            if (await db.Roles.AnyAsync(r => r.Name == request.Name, ct))
                return Result.Failure<Guid>(Error.Conflict("Role.Exists", "同名角色已存在"));

            role = new AppRole(request.Name, request.Description);
            db.Roles.Add(role);
        }

        // 系统角色名字锁死但描述可维护;自定义角色整体改名 + 改描述
        if (role.IsSystemRole) role.SetDescription(request.Description);
        else role.Rename(request.Name, request.Description);

        role.SetPermissions(request.Permissions);

        AdminAudit.Write(db, currentUser, isNew ? "admin.role.created" : "admin.role.updated",
            "AppRole", role.Id.ToString(),
            $"name: {role.Name}; permissions: {string.Join(", ", request.Permissions)}");

        await db.SaveChangesAsync(ct);
        return Result.Success(role.Id);
    }
}

public sealed class AdminDeleteRoleCommandHandler(IdentityDbContext db, ICurrentUser currentUser)
    : IRequestHandler<AdminDeleteRoleCommand, Result>
{
    public async Task<Result> Handle(AdminDeleteRoleCommand request, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == request.RoleId, ct);
        if (role is null) return Result.Failure(Error.NotFound("角色"));
        if (role.IsSystemRole) return Result.Failure(Error.Conflict("Role.System", "系统内置角色不允许删除"));

        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == role.Id, ct))
            return Result.Failure(Error.Conflict("Role.InUse", "该角色仍被用户使用,请先调整用户角色"));

        role.MarkDeleted();

        AdminAudit.Write(db, currentUser, "admin.role.deleted", "AppRole", role.Id.ToString(),
            $"name: {role.Name}");

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AdminPermissionCatalogQueryHandler : IRequestHandler<AdminPermissionCatalogQuery, Result<IReadOnlyList<PermissionCatalogItem>>>
{
    private static readonly Dictionary<string, string> Descriptions = new()
    {
        [Permissions.JobsRead] = "查看求职跟踪数据",
        [Permissions.JobsWrite] = "新建/修改投递记录",
        [Permissions.JobsDelete] = "删除投递记录",
        [Permissions.InterviewsRead] = "查看实战机经",
        [Permissions.InterviewsWrite] = "新增/编辑机经条目与素材",
        [Permissions.InterviewsAnalyze] = "触发录音/文本分析",
        [Permissions.InterviewsDelete] = "删除机经条目",
        [Permissions.KnowledgeRead] = "查看技术栈知识库",
        [Permissions.KnowledgeWrite] = "编辑技术条目",
        [Permissions.KnowledgeDelete] = "删除技术条目",
        [Permissions.MockRead] = "查看 AI 实战模拟",
        [Permissions.MockAnswer] = "参与模拟答题",
        [Permissions.MockManage] = "管理模拟题库与会话",
        [Permissions.AdminUsersRead] = "查看用户列表",
        [Permissions.AdminUsersWrite] = "增删改用户",
        [Permissions.AdminRolesWrite] = "管理角色与权限",
        [Permissions.AdminContentModerate] = "内容审核",
        [Permissions.AdminAuditRead] = "查看审计日志",
        [Permissions.AdminSystemWrite] = "修改系统配置"
    };

    public Task<Result<IReadOnlyList<PermissionCatalogItem>>> Handle(AdminPermissionCatalogQuery request, CancellationToken ct)
    {
        var items = Permissions.All.Select(p =>
        {
            var parts = p.Split('.', 2);
            var resource = parts[0];
            var action = parts.Length > 1 ? parts[1].Replace("write", "管理").Replace("read", "查看") : "";
            return new PermissionCatalogItem(p, resource, action,
                Descriptions.TryGetValue(p, out var d) ? d : p);
        }).ToList();

        return Task.FromResult(Result.Success<IReadOnlyList<PermissionCatalogItem>>(items));
    }
}

public sealed class AdminAuditLogQueryHandler(IdentityDbContext db)
    : IRequestHandler<AdminAuditLogQuery, Result<PagedResult<AuditLogItem>>>
{
    public async Task<Result<PagedResult<AuditLogItem>>> Handle(AdminAuditLogQuery request, CancellationToken ct)
    {
        var page = Math.Max(1, request.Page);
        var size = Math.Clamp(request.PageSize, 1, 500);

        var q = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Action)) q = q.Where(a => a.Action == request.Action);
        if (request.UserId.HasValue) q = q.Where(a => a.UserId == request.UserId.Value);
        if (request.From.HasValue) q = q.Where(a => a.OccurredAt >= request.From.Value);
        if (request.To.HasValue) q = q.Where(a => a.OccurredAt <= request.To.Value);

        var total = await q.CountAsync(ct);

        var items = await (from a in q
                           join u in db.Users.IgnoreQueryFilters() on a.UserId equals u.Id into us
                           from u in us.DefaultIfEmpty()
                           orderby a.OccurredAt descending
                           select new AuditLogItem(a.Id, a.UserId, u != null ? u.Email : null,
                               a.Action, a.Resource, a.ResourceId, a.Detail, a.IpAddress, a.Success, a.OccurredAt))
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return Result.Success(new PagedResult<AuditLogItem>(items, total, page, size));
    }
}

public sealed class AdminStatsQueryHandler(IdentityDbContext db, TimeProvider clock)
    : IRequestHandler<AdminStatsQuery, Result<AdminStats>>
{
    public async Task<Result<AdminStats>> Handle(AdminStatsQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since7 = now.AddDays(-7);
        var since24h = now.AddHours(-24);

        var totalUsers = await db.Users.CountAsync(ct);
        var activeUsers = await db.Users.CountAsync(u => u.IsActive, ct);
        var newUsers = await db.Users.CountAsync(u => u.CreatedAt >= since7, ct);
        var totalRoles = await db.Roles.CountAsync(ct);
        var totalAudit = await db.AuditLogs.CountAsync(ct);
        var failedLogins = await db.AuditLogs.CountAsync(a => a.Action == "auth.login.failed" && a.OccurredAt >= since24h, ct);
        var locked = await db.Users.CountAsync(u => u.LockedUntil != null && u.LockedUntil > now, ct);

        var raw = await db.Users.Where(u => u.CreatedAt >= since7)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var trend = Enumerable.Range(0, 7)
            .Select(i => DateOnly.FromDateTime(since7.Date.AddDays(i)))
            .Select(d => new DailyCount(d,
                raw.FirstOrDefault(r => DateOnly.FromDateTime(r.Day) == d)?.Count ?? 0))
            .ToList();

        return Result.Success(new AdminStats(totalUsers, activeUsers, newUsers, totalRoles,
            totalAudit, failedLogins, locked, trend));
    }
}
