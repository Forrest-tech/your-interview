using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using YourInterview.BuildingBlocks.Results;

namespace YourInterview.BuildingBlocks.Web;

/// <summary>
/// Result → HTTP 的映射。RFC 9457 ProblemDetails 标准错误体。
/// </summary>
public static class ResultExtensions
{
    public static IResult ToProblemDetails(this Result result)
    {
        if (result.IsSuccess) throw new InvalidOperationException("成功的结果不需要转 ProblemDetails");

        return Microsoft.AspNetCore.Http.Results.Problem(
            statusCode: GetStatusCode(result.Error.Type),
            title: GetTitle(result.Error.Type),
            type: GetTypeUri(result.Error.Type),
            extensions: new Dictionary<string, object?>
            {
                ["code"] = result.Error.Code,
                ["traceId"] = Activity.Current?.Id
            });
    }

    private static int GetStatusCode(ErrorType t) => t switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError
    };

    private static string GetTitle(ErrorType t) => t switch
    {
        ErrorType.Validation => "Validation failure",
        ErrorType.NotFound => "Resource not found",
        ErrorType.Conflict => "Conflict",
        ErrorType.Unauthorized => "Unauthorized",
        ErrorType.Forbidden => "Forbidden",
        _ => "Server error"
    };

    private static string GetTypeUri(ErrorType t) => t switch
    {
        ErrorType.Validation => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        ErrorType.NotFound => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        ErrorType.Conflict => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        ErrorType.Unauthorized => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        ErrorType.Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.6.1"
    };
}
