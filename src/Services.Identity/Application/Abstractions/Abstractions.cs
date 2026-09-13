using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Identity.Domain;
using YourInterview.Services.Identity.Infrastructure.Persistence;

namespace YourInterview.Services.Identity.Application.Abstractions;

/// <summary>Identity 服务应用层共用的查询/仓储接口(实现放 Infrastructure)。</summary>
public interface IIdentityUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

/// <summary>当前请求用户上下文(IHttpContextAccessor 的抽象,便于单测)。</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    string? Email { get; }
    bool IsAuthenticated { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
}
