using YourInterview.BuildingBlocks.Security;

namespace YourInterview.Services.Knowledge.Tests;

/// <summary>
/// 票16:DbContext 全局过滤器按当前用户收口(票03 口径)。测试里以指定用户
/// 构造上下文模拟该用户的请求上下文。形态同 Services.Jobs.Tests 先例。
/// </summary>
public sealed class TestCurrentUser(Guid userId) : ICurrentUser
{
    public Guid? UserId => userId;
    public string? Email => "test@local";
    public bool IsAuthenticated => true;
    public string? IpAddress => null;
    public string? UserAgent => null;
    public IReadOnlyList<string> Roles => [];
}
