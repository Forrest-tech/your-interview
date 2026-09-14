using YourInterview.BuildingBlocks.Security;
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

// 当前请求用户上下文 ICurrentUser 已提升到 BuildingBlocks.Security
// (Tracker / 实战机经 / 模拟练习都要用,统一一份抽象)。
