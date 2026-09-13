using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Identity.Domain;

namespace YourInterview.Services.Identity.Infrastructure.Persistence;

/// <summary>
/// Identity 服务的 DbContext(Clean Architecture 的 Infrastructure 层)。
/// 独立 schema:identity —— 对应"database-per-service"思路,服务间不共享表。
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public const string Schema = "identity";

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppRole> Roles => Set<AppRole>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.AvatarUrl).HasMaxLength(1024);
            e.Property(x => x.SecurityStamp).HasMaxLength(64).IsRequired();
            e.Property(x => x.PreferredLanguage).HasMaxLength(16);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            // 乐观并发:PostgreSQL 的 xmin 系统列,零额外字段成本
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasMany(x => x.Roles).WithOne(r => r.User).HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Metadata.FindNavigation(nameof(AppUser.Roles))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<AppRole>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
            e.UseXminAsConcurrencyToken();
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasMany(x => x.Permissions).WithOne(p => p.Role).HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.Metadata.FindNavigation(nameof(AppRole.Permissions))!.SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne(x => x.User).WithMany(x => x.Roles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(100);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Property(x => x.Device).HasMaxLength(256);
            e.Property(x => x.CreatedByIp).HasMaxLength(64);
            e.Property(x => x.RevokedReason).HasMaxLength(200);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.Resource).HasMaxLength(100).IsRequired();
            e.Property(x => x.ResourceId).HasMaxLength(128);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.HasIndex(x => new { x.UserId, x.OccurredAt });
            e.HasIndex(x => x.OccurredAt);
        });
    }
}
