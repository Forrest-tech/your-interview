using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;
using YourInterview.Services.Identity.Api;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.Services.Identity.Application.Logto;

namespace YourInterview.Services.Identity.Tests;

/// <summary>
/// 票241 旧发行开关(AuthController 层,控制器直测、不连库):
///   · LegacyRegistrationEnabled=false → POST /api/auth/register 回 410,且绝不调 MediatR;
///   · IssuanceEnabled=false → POST /api/auth/login 与 /refresh 回 410,且绝不调 MediatR。
/// 通过路径(开关 true 时走原有 handler)是既有行为,本文件只锁门分支;
/// IResult 的状态码用 DefaultHttpContext 执行后断言(Results.Problem 的状态码
/// 只在执行时落到 Response 上)。
/// </summary>
public class LogtoAuthSwitchTests
{
    private static (AuthController Controller, ISender Sender) Setup(LegacyAuthOptions opts)
    {
        var sender = Substitute.For<ISender>();
        var controller = new AuthController(sender, Substitute.For<ICurrentUser>(),
            Options.Create(opts));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return (controller, sender);
    }

    // Results.Problem 的状态码在结果对象上直接可读,无需执行
    // (执行需要 RequestServices 配 IProblemDetailsService,直测没必要)。
    private static int StatusOf(IResult result) =>
        result.Should().BeOfType<ProblemHttpResult>().Subject.StatusCode;

    [Fact]
    public async Task Register_GreyStart_Returns410_AndNeverSends()
    {
        var (controller, sender) = Setup(new LegacyAuthOptions { LegacyRegistrationEnabled = false });
        var result = await controller.Register(new RegisterUserCommand("a@b.c", "A", "pw"), default);
        StatusOf(result).Should().Be(410);
        await sender.DidNotReceiveWithAnyArgs().Send(Arg.Any<IRequest<Result<AuthResult>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_Convergence_Returns410_AndNeverSends()
    {
        var (controller, sender) = Setup(new LegacyAuthOptions { IssuanceEnabled = false });
        var result = await controller.Login(new LoginCommand("a@b.c", "pw"), default);
        StatusOf(result).Should().Be(410);
        await sender.DidNotReceiveWithAnyArgs().Send(Arg.Any<IRequest<Result<AuthResult>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_Convergence_Returns410_AndNeverSends()
    {
        var (controller, sender) = Setup(new LegacyAuthOptions { IssuanceEnabled = false });
        var result = await controller.Refresh(new RefreshTokenCommand("rt"), default);
        StatusOf(result).Should().Be(410);
        await sender.DidNotReceiveWithAnyArgs().Send(Arg.Any<IRequest<Result<AuthResult>>>(), Arg.Any<CancellationToken>());
    }
}
