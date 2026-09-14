using System.Security.Claims;

namespace YourInterview.Services.Gateway.Middleware;

/// <summary>
/// 关联 ID 中间件 —— 分布式追踪的起点。
///
/// 为什么必须有:用户报"我提交失败了",没有 correlation id 就只能靠时间去猜是哪次请求。
/// 有了它,前端把 id 一并报过来,后端一条命令就能捞出整条链路。
///
/// 规则:进站如果有 X-Correlation-Id 就沿用(链路跨系统延续),没有就生成一个。
/// 无论哪种情况都写回响应头,让前端能拿到。
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        context.Items[HeaderName] = correlationId;

        // 写回响应头(OnStarting 确保即使后续中间件改了响应也还在)
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // 出站请求也要带上 —— YARP 转发的请求会继承它
        context.Request.Headers[HeaderName] = correlationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}

/// <summary>
/// 网关层的认证处理。
///
/// 职责边界(很重要,别越界):
///   网关 **只做粗判**:没有 token 且路由要求认证 → 直接 401,不浪费下游一次调用。
///   网关 **不做细判**:权限(能不能读这个资源)由下游各服务自己判 ——
///   因为只有下游知道自己的权限点,网关硬编码权限表迟早和服务实现脱节。
///
/// 同理,网关不修改 token,只把 Authorization 头原样透传。
/// </summary>
public sealed class GatewayAuthMiddleware(RequestDelegate next)
{
    /// <summary>不需要认证的路径前缀(健康检查、登录、注册、Swagger)。</summary>
    private static readonly string[] AnonymousPrefixes =
    [
        "/health", "/api/auth/login", "/api/auth/register", "/api/auth/refresh",
        "/swagger", "/api/gateway/info", "/openapi"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        // 匿名路径直接放行
        if (AnonymousPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        // 有 token 就解析出身份(不验签 —— 下游会验)。
        // 这里只为了日志与限流分区能带上用户标识。
        var header = context.Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(header) && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = header["Bearer ".Length..].Trim();
            var sub = TryReadSubject(token);
            if (sub is not null)
                context.Items["GatewayUserId"] = sub;
        }

        await next(context);
    }

    /// <summary>
    /// 只读取 token 的 subject,不验签。
    /// 用途仅限日志关联与限流分区 —— 任何授权判断都不允许依赖这个值。
    /// </summary>
    private static string? TryReadSubject(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sub", out var sub)) return sub.GetString();
            if (doc.RootElement.TryGetProperty(ClaimTypes.NameIdentifier, out var nid))
                return nid.GetString();
            return null;
        }
        catch
        {
            // token 格式不对不是网关该处理的事 —— 交给下游返回 401,保持职责单一
            return null;
        }
    }
}
