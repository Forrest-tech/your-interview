using YourInterview.BuildingBlocks.Security;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// COD-01/票03:DbContext 全局过滤器按当前用户收口后,直连库的集成测试需要一个
/// 固定的当前用户。测试条目以 userId = Guid.Empty 创建,命令也以 Guid.Empty 发起,
/// 故这里恒返回 Guid.Empty,使过滤器与 Handler 谓词口径一致。
/// </summary>
public sealed class TestCurrentUser : ICurrentUser
{
    public Guid? UserId => Guid.Empty;
    public string? Email => "test@local";
    public bool IsAuthenticated => true;
    public string? IpAddress => null;
    public string? UserAgent => null;
    public IReadOnlyList<string> Roles => [];
}
