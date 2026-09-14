using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace YourInterview.BuildingBlocks.Security;

/// <summary>
/// 当前请求的用户身份。
///
/// 为什么放在 BuildingBlocks 而不是 Identity 服务里:
/// Tracker 要按用户过滤自己的投递、实战机经要看自己的面试记录、模拟练习更是纯私有的 ——
/// 每个服务都需要它。放在 Identity 会让其他服务为了拿个用户 ID 去引用整个身份服务,
/// 那是用错了方向的依赖。
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    /// <summary>角色列表(用于前端显隐管理入口)。</summary>
    IReadOnlyList<string> Roles { get; }
}

/// <summary>基于 HttpContext 的实现 —— 从 JWT 的 claims 里读。</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? UserId
    {
        get
        {
            var raw = Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? Principal?.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email)
                            ?? Principal?.FindFirstValue("email");

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    public IReadOnlyList<string> Roles => Principal?
        .FindAll(ClaimTypes.Role).Select(c => c.Value).ToList() ?? [];
}

/// <summary>给没有 HTTP 上下文场景(后台 Worker、单元测试)用的空实现。</summary>
public sealed class NullCurrentUser : ICurrentUser
{
    public Guid? UserId => null;
    public string? Email => null;
    public bool IsAuthenticated => false;
    public string? IpAddress => null;
    public string? UserAgent => null;
    public IReadOnlyList<string> Roles => [];
}

public static class CurrentUserExtensions
{
    /// <summary>拿到当前用户 ID,没登录直接抛 —— 用于"这条路径必须已认证"的地方。</summary>
    public static Guid RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw new UnauthorizedAccessException("该操作需要已认证的用户身份");

    /// <summary>注册 ICurrentUser(Web 服务在 AddServiceDefaults 之外按需调用)。</summary>
    public static IServiceCollection AddCurrentUser(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        return services;
    }
}
