using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;
using YourInterview.Services.Gateway.Middleware;

var builder = WebApplication.CreateBuilder(args);

// ---------- 日志 ----------
builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [gateway] {Message:lj}{NewLine}{Exception}"));

// =====================================================================================
//  YARP 反向代理 —— 全平台的唯一入口
//
//  路由表来自 appsettings.json 的 ReverseProxy 节点(配置驱动)。
//  选配置驱动而不是代码里硬编码:改路由/加服务不需要重新编译部署,
//  这对"微服务数量还会继续增长"的项目是刚需。
// =====================================================================================
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// ---------- 健康检查:网关同时盯着所有下游 ----------
var health = builder.Services.AddHealthChecks();
var clusterUrls = builder.Configuration
    .GetSection("ReverseProxy:Clusters")
    .GetChildren()
    .SelectMany(c => c.GetSection("Destinations").GetChildren())
    .Select(d => d["Address"])
    .Where(a => !string.IsNullOrWhiteSpace(a))
    .Select(a => a!)
    .Distinct()
    .ToList();

foreach (var url in clusterUrls)
{
    // 探活路径统一用 /health/live —— 每个服务都实现了它
    var probe = url.TrimEnd('/') + "/health/live";
    health.AddUrlGroup(new Uri(probe), name: new Uri(url).Host + ":" + new Uri(url).Port);
}

// ---------- CORS:前端 SPA 与网关不同源 ----------
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? ["http://localhost:4200", "http://localhost:5100"];

builder.Services.AddCors(o => o.AddPolicy("spa", p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// =====================================================================================
//  限流 —— 网关的第一道防线
//
//  按用户(已认证)或 IP(未认证)分区限流。
//  为什么不按路径:攻击者换路径就行;按身份/来源限制才真正控制住单点消耗。
// =====================================================================================
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
              ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous"
            : ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 300,              // 每 10 秒 300 次 —— 正常使用远低于此
            Window = TimeSpan.FromSeconds(10),
            QueueLimit = 0,                 // 不排队,超了直接 429(排队会让延迟失控)
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });

    // 认证端点单独更严:防暴力破解
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});

var app = builder.Build();

// ---------- 链路追踪头:进站生成/沿用 correlation id,出站透传 ----------
app.UseMiddleware<CorrelationIdMiddleware>();

app.UseSerilogRequestLogging(o =>
{
    o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} → {StatusCode} in {Elapsed:0.0}ms";
});

app.UseCors("spa");
app.UseRateLimiter();

// ---------- 透传认证:网关校验 JWT 后把身份传给下游 ----------
// 注意:下游服务**仍然自己验签**(不信任网关注入的头),
// 这是纵深防御 —— 万一内网被绕过,下游不会变成完全不设防。
app.UseMiddleware<GatewayAuthMiddleware>();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");

// 网关自己的信息端点(前端用来探测后端可用性)
app.MapGet("/api/gateway/info", (IConfiguration cfg) => Results.Ok(new
{
    service = "your-interview-gateway",
    version = "1.0.0",
    proxy = "YARP",
    routes = cfg.GetSection("ReverseProxy:Routes").GetChildren().Select(r => new
    {
        id = r["RouteId"],
        path = r["Match:Path"]
    }).ToList()
}));

app.MapReverseProxy();

app.Run();
