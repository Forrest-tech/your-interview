using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Xunit;
using YourInterview.BuildingBlocks.Hosting;

namespace YourInterview.BuildingBlocks.Tests;

/// <summary>
/// COD-05 / 票07 seam:共享管线异常→状态码映射的公开契约 ——
/// DbUpdateConcurrencyException(xmin 乐观并发冲突)必须映射 409 而非默认 500,
/// 且其余既有映射(Validation→400 / 401 / 404 / Argument→400 / 未知→500)保持不变。
/// UseServiceDefaults 的全局异常处理器直接调用 ServiceDefaults.MapExceptionToStatusCode,
/// 本测试即管线层映射成立的证明;真实端点并发实测见 evidence-07。
/// </summary>
public class ExceptionStatusMappingTests
{
    [Fact]
    public void ConcurrencyException_MapsTo409Conflict()
    {
        ServiceDefaults.MapExceptionToStatusCode(new DbUpdateConcurrencyException())
            .Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public void ValidationException_StillMapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new ValidationException("bad input"))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void UnauthorizedAccess_StillMapsTo401()
    {
        ServiceDefaults.MapExceptionToStatusCode(new UnauthorizedAccessException())
            .Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void KeyNotFound_StillMapsTo404()
    {
        ServiceDefaults.MapExceptionToStatusCode(new KeyNotFoundException())
            .Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public void ArgumentException_StillMapsTo400()
    {
        ServiceDefaults.MapExceptionToStatusCode(new ArgumentException("bad arg"))
            .Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void UnknownException_StillMapsTo500()
    {
        ServiceDefaults.MapExceptionToStatusCode(new InvalidOperationException("boom"))
            .Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public void NullException_MapsTo500()
    {
        ServiceDefaults.MapExceptionToStatusCode(null)
            .Should().Be(StatusCodes.Status500InternalServerError);
    }
}
