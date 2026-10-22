using YourInterview.BuildingBlocks.Security;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// COD-01/票03:DbContext 全局过滤器按当前用户收口。测试里:
/// 种子上下文用条目主人 id;消费者上下文用 Guid.Empty 模拟「无 HTTP 上下文」
/// (过滤器 fail-closed,消费者必须 IgnoreQueryFilters + 事件 UserId 显式校验)。
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
