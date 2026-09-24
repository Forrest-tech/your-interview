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

public sealed record AdminUserListItem(
    Guid Id, string Email, string DisplayName, bool IsActive, bool MustChangePassword,
    DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt, string[] Roles, string? AvatarUrl);

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
                u.LastLoginAt, u.CreatedAt, u.AvatarUrl,
                Roles = (from ur in db.UserRoles
                         join r in db.Roles on ur.RoleId equals r.Id
                         where ur.UserId == u.Id
                         select r.Name).ToArray()
            })
            .ToListAsync(ct);

        var items = users.Select(u => new AdminUserListItem(
            u.Id, u.Email, u.DisplayName, u.IsActive, u.MustChangePassword,
            u.LastLoginAt, u.CreatedAt, u.Roles, u.AvatarUrl)).ToList();

        return Result.Success(new PagedResult<AdminUserListItem>(items, total, page, size));
    }
}

public sealed class AdminCreateUserCommandHandler(
    IdentityDbContext db, IPasswordHasher hasher)
    : IRequestHandler<AdminCreateUserCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AdminCreateUserCommand request, CancellationToken ct)
    {
        var email = AppUser.Normalize(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Result.Failure<Guid>(Error.Conflict("Auth.EmailTaken", "该邮箱已存在"));

        var validRoles = await db.Roles.Where(r => request.Roles.Contains(r.Name)).ToListAsync(ct);
        if (validRoles.Count == 0)
            return Result.Failure<Guid>(Error.Validation("Role.NotFound", "指定的角色不存在"));

        var user = new AppUser(email, request.DisplayName, hasher.Hash(request.Password));
        user.RequirePasswordChange(); // 管理员代建的账号,首次登录必须改密
        foreach (var r in validRoles) user.AssignRole(r.Id);

        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return Result.Success(user.Id);
    }
}

public sealed class AdminUpdateUserCommandHandler(IdentityDbContext db)
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

        user.Rename(request.DisplayName);

        if (request.IsActive && !user.IsActive) user.Activate();
        if (!request.IsActive && user.IsActive) user.Deactivate();

        if (request.RequirePasswordChange) user.RequirePasswordChange();
        else user.ClearPasswordChangeRequirement();   // 开关语义:关掉=解除(M2.2)

        var roles = await db.Roles.Where(r => request.Roles.Contains(r.Name)).ToListAsync(ct);
        user.ReplaceRoles(roles.Select(r => r.Id));

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AdminResetPasswordCommandHandler(IdentityDbContext db, IPasswordHasher hasher)
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

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AdminDeleteUserCommandHandler(IdentityDbContext db)
    : IRequestHandler<AdminDeleteUserCommand, Result>
{
    public async Task<Result> Handle(AdminDeleteUserCommand request, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound("用户"));

        // 软删除(审计要求:记录保留,账号失效)
        user.Deactivate();
        user.MarkDeleted();
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

public sealed class AdminUpsertRoleCommandHandler(IdentityDbContext db)
    : IRequestHandler<AdminUpsertRoleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AdminUpsertRoleCommand request, CancellationToken ct)
    {
        AppRole role;

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
        }
        else
        {
            if (await db.Roles.AnyAsync(r => r.Name == request.Name, ct))
                return Result.Failure<Guid>(Error.Conflict("Role.Exists", "同名角色已存在"));

            role = new AppRole(request.Name, request.Description);
            db.Roles.Add(role);
        }

        if (!string.IsNullOrWhiteSpace(request.Description) && !role.IsSystemRole)
        {
            // 通过 SetPermissions 之外的途径更新描述
        }

        role.SetPermissions(request.Permissions);
        await db.SaveChangesAsync(ct);
        return Result.Success(role.Id);
    }
}

public sealed class AdminDeleteRoleCommandHandler(IdentityDbContext db)
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
