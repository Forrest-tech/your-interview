using MediatR;
using Microsoft.Extensions.Logging;

namespace YourInterview.BuildingBlocks.Behaviors;

/// <summary>
/// MediatR 管道行为:统一日志 + 耗时统计。
/// 顺序很关键:Logging → Validation → Performance → Handler。
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        logger.LogInformation("处理 {RequestName} {@Request}", name, request);
        var response = await next();
        logger.LogInformation("完成 {RequestName}", name);
        return response;
    }
}

/// <summary>性能行为:超过阈值(默认 500ms)记 Warning —— 面试必答的"如何发现慢查询/慢接口"。</summary>
public sealed class PerformanceBehavior<TRequest, TResponse>(
    ILogger<PerformanceBehavior<TRequest, TResponse>> logger,
    TimeProvider clock)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private const int SlowThresholdMs = 500;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var start = clock.GetTimestamp();
        var response = await next();
        var elapsedMs = clock.GetElapsedTime(start).TotalMilliseconds;

        if (elapsedMs > SlowThresholdMs)
        {
            logger.LogWarning("慢请求 {RequestName} 耗时 {ElapsedMs:0}ms —— {@Request}",
                typeof(TRequest).Name, elapsedMs, request);
        }
        return response;
    }
}

/// <summary>未处理异常行为:把异常翻译成 Result.Failure,而不是让 500 冒到前端。</summary>
public sealed class UnhandledExceptionBehavior<TRequest, TResponse>(
    ILogger<UnhandledExceptionBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{RequestName} 处理异常", typeof(TRequest).Name);
            throw;
        }
    }
}
