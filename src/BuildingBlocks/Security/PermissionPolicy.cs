using Microsoft.AspNetCore.Authorization;
using YourInterview.SharedContracts.Security;

namespace YourInterview.BuildingBlocks.Security;

/// <summary>
/// 权限策略名 + 策略注册。所有服务共用,保证「权限点字符串」与「策略名」完全一致。
/// 面试要点:把「角色」和「权限」分开 —— 角色只是权限的容器,鉴权只认权限点,
/// 这样新增角色不需要改任何 [Authorize] 注解(开闭原则在安全模型上的落地)。
/// </summary>
public static class PermissionPolicy
{
    /// <summary>策略名前缀。const 才能用于 [Authorize(Policy = ...)] 的编译期拼接。</summary>
    public const string Prefix = "perm:";

    public static string Name(string permission) => Prefix + permission;

    public static void AddPermissionPolicies(AuthorizationOptions options)
    {
        foreach (var permission in Permissions.All)
        {
            options.AddPolicy(Name(permission), policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(CustomClaims.Permission, permission));
        }
    }
}
