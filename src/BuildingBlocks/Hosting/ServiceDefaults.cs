using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using MediatR;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Events;
using YourInterview.BuildingBlocks.Behaviors;
using YourInterview.BuildingBlocks.Persistence;

namespace YourInterview.BuildingBlocks.Hosting;

/// <summary>
/// 每个微服务共用的启动基座:日志 / 可观测 / 异常处理 / OpenAPI / CORS / 健康检查 / MediatR。
/// 保持各服务 Program.cs 极薄 —— 这正是 eShop 的做法(面试可讲的"服务模板统一")。
/// </summary>
public static class ServiceDefaults
{
    public const string CorsPolicy = "your-interview-cors";

    /// <summary>
    /// 全服务统一的 Kestrel 请求体上限:64MB(ARC-09/票18)。
    /// 与网关(Services.Gateway 自有同值一行,网关不引用 BuildingBlocks)口径一致;
    /// 此前只有 Assessment 与网关配了 64MB,其余服务吃 Kestrel 默认 30,000,000B,
    /// 无 [RequestSizeLimit] 的端点收到 &gt;30MB 请求体即被拒。端点级属性口径不动(按端点覆盖本值)。
    /// </summary>
    public const long MaxRequestBodySizeBytes = 64L * 1024 * 1024;

    /// <summary>JWT Issuer 兜底(ARC-09/票18:以 Jobs 旧口径为准的统一默认值)。</summary>
    public const string DefaultJwtIssuer = "your-interview.identity";

    /// <summary>JWT Audience 兜底(ARC-09/票18:以 Jobs 旧口径为准的统一默认值)。</summary>
    public const string DefaultJwtAudience = "your-interview.api";

    public static WebApplicationBuilder AddServiceDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        Action<IServiceCollection>? configure = null)
    {
        // ---------- Kestrel 请求体上限(ARC-09/票18:全服务统一 64MB,见 MaxRequestBodySizeBytes) ----------
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxRequestBodySizeBytes);

        // ---------- Serilog ----------
        builder.Host.UseSerilog((context, services, cfg) => cfg
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command",
                context.HostingEnvironment.IsDevelopment() ? LogEventLevel.Information : LogEventLevel.Warning)
            .MinimumLevel.Override("MassTransit", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .Enrich.WithMachineName()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] [{service}] {Message:lj}{NewLine}{Exception}")
            .ReadFrom.Configuration(context.Configuration));

        // ---------- 统一 JSON(前端 Angular 需要 camelCase;枚举用字符串) ----------
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        builder.Services.AddControllers().AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        // ---------- FluentValidation ----------
        // ⚠️ 必须显式注册 Validator 的实现类型(程序集扫描 + includeInternalTypes 都不可靠),
        // 否则 IValidator<TRequest> 是空集合,ValidationBehavior 会静默跳过所有校验 ——
        // 这是极易踩的坑:接口写得对、行为写得对,但校验就是不生效。
        RegisterValidators(builder.Services, System.Reflection.Assembly.GetEntryAssembly()!);

        // ---------- MediatR(扫描各服务自己的程序集) ----------
        // ★ 2026-09-24(M1):这里是 MediatR 的**唯一**注册点。
        //   之前 5 个服务在各自 Program.cs 里又 AddMediatR 一遍 ——
        //   同一程序集扫两次 = 每个通知处理器注册两份 = 每条领域事件派发两次。
        //   表现:Interviews 的分析事件双发、Worker 跑两遍(烧双倍 Azure 转写额度),
        //   请求管道的 Logging/Validation 行为也都套了两层。
        //   若某服务需要额外行为,在这里加,不要再在自己的 Program.cs 里 AddMediatR。
        builder.Services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(System.Reflection.Assembly.GetEntryAssembly()!);
            cfg.RegisterServicesFromAssembly(typeof(ServiceDefaults).Assembly);
            cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
            cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            cfg.AddOpenBehavior(typeof(PerformanceBehavior<,>));
            cfg.AddOpenBehavior(typeof(UnhandledExceptionBehavior<,>));
        });

        // ---------- CORS(本地 Angular dev server + Azure 前端) ----------
        builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .SetIsOriginAllowed(_ => true)          // 生产改为白名单(builder.Configuration["Cors:Origins"])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        // ---------- 健康检查 ----------
        builder.Services.AddHealthChecks();

        // ---------- 认证/授权骨架(具体 scheme 由 Identity 配置补上) ----------
        builder.Services.AddAuthorization();

        // ---------- OpenTelemetry ----------
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporterIfConfigured(builder.Configuration))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporterIfConfiguredMetrics(builder.Configuration));

        // ---------- Swagger ----------
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
            {
                Title = $"{serviceName} API",
                Version = "v1",
                Description = "Your Interview —— 个人求职与面试全流程平台"
            });
        });

        // ---------- 各服务追加自己的注册 ----------
        configure?.Invoke(builder.Services);

        return builder;
    }

    /// <summary>
    /// ARC-09(票18):JWT 校验参数的**唯一定义**。此前七个服务在各自 Program.cs 逐字复制本块且已漂移
    /// (仅 Jobs 带 issuer/audience 兜底)——统一口径以 Jobs 兜底为准:
    /// Issuer/Audience 配置缺失(null)时回落默认串;签名密钥缺失/空白即抛(启动失败,不带病运行)。
    /// 纯函数(只读 <see cref="IConfiguration"/>),口径由 BuildingBlocks.Tests 特征测试锁定。
    /// </summary>
    public static TokenValidationParameters BuildTokenValidationParameters(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var signingKey = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException(
                "缺少 Jwt:SigningKey 配置。请在本地 appsettings.Development.json 或环境变量 Jwt__SigningKey 中配置" +
                "(需与 Services.Identity 签发的密钥一致,至少 32 字符)。");

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            // 注:?? 只兜底 null —— 配置为空串时原样保留(Jobs 旧口径,特征测试锁定,不顺手改)。
            ValidIssuer = configuration["Jwt:Issuer"] ?? DefaultJwtIssuer,
            ValidAudience = configuration["Jwt:Audience"] ?? DefaultJwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            // Jobs/Identity 旧显式值;其余五服务未显式设时框架默认同为 ClaimTypes.Role —— 等值显式化。
            RoleClaimType = ClaimTypes.Role
        };
    }

    /// <summary>
    /// ARC-09(票18):JWT Bearer 注册的**唯一入口**。校验参数一律由
    /// <see cref="BuildTokenValidationParameters"/> 决定,调用点不许另改;
    /// <paramref name="configure"/> 只供个别服务保留有意差异
    /// (Identity 的 ProblemDetails 401/403 Events 与 SaveToken、Jobs/Identity 的 RequireHttpsMetadata),
    /// 差异内容仍写在各服务 Program.cs、逐项可盘点。
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services, IConfiguration configuration,
        Action<JwtBearerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = BuildTokenValidationParameters(configuration);
                configure?.Invoke(options);
            });
        return services;
    }

    /// <summary>
    /// 全局异常 → HTTP 状态码的唯一映射表(UseServiceDefaults 的异常处理器与测试共用同一口径)。
    /// COD-05:EF Core 的 <see cref="DbUpdateConcurrencyException"/>(xmin 乐观并发令牌冲突)
    /// 必须映射 409 Conflict —— 此前它落到默认分支冒 500,双标签页/双设备并发编辑同一条目时
    /// 用户只看到「服务器错误」,无从知道自己的改动因版本过期而未保存。
    /// </summary>
    public static int MapExceptionToStatusCode(Exception? ex) => ex switch
    {
        ValidationException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        DbUpdateConcurrencyException => StatusCodes.Status409Conflict,
        ArgumentException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };

    /// <summary>中间件管线(顺序即最佳实践,面试可直接讲)。</summary>
    public static WebApplication UseServiceDefaults(this WebApplication app, string serviceName)
    {
        app.UseSerilogRequestLogging(opt =>
        {
            opt.MessageTemplate = "[{service}] HTTP {RequestMethod} {RequestPath} → {StatusCode} in {Elapsed:0.0}ms";
        });

        // 全局异常 → RFC 9457 ProblemDetails
        app.UseExceptionHandler(a => a.Run(async ctx =>
        {
            var feature = ctx.Features.Get<IExceptionHandlerFeature>();
            var ex = feature?.Error;
            var isDev = app.Environment.IsDevelopment();

            var status = MapExceptionToStatusCode(ex);

            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/problem+json";

            if (ex is ValidationException ve)
            {
                await ctx.Response.WriteAsJsonAsync(new ValidationProblemDetails(
                    ve.Errors.GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
                {
                    Status = status,
                    Title = "One or more validation errors occurred.",
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                    Extensions = { ["traceId"] = ctx.TraceIdentifier }
                });
                return;
            }

            if (ex is DbUpdateConcurrencyException)
            {
                // COD-05:并发冲突给用户可执行的提示(刷新后重试),且该文案不含任何内部细节,
                // 生产环境也照常下发 —— 与 500 的「隐藏 detail」口径不同,409 的 detail 就是给用户看的。
                // code=concurrency_conflict 供前端统一错误处理(ApiClient)识别并给出本地化提示。
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = status,
                    Title = "Concurrency conflict",
                    Detail = "This record was modified by someone else since it was loaded. " +
                             "Your changes were not saved — refresh and try again.",
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                    Extensions = { ["traceId"] = ctx.TraceIdentifier, ["code"] = "concurrency_conflict" }
                });
                return;
            }

            await ctx.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = status,
                Title = ex?.GetType().Name ?? "Server error",
                Detail = isDev ? ex?.ToString() : "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                Extensions = { ["traceId"] = ctx.TraceIdentifier }
            });
        }));

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", serviceName));
        }

        app.UseCors(CorsPolicy);
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapHealthChecks("/health/live");
        app.MapHealthChecks("/health/ready");
        app.MapGet("/", () => Microsoft.AspNetCore.Http.Results.Ok(new
        {
            service = serviceName,
            status = "ok",
            docs = "/swagger",
            time = DateTimeOffset.UtcNow
        }));

        return app;
    }

    /// <summary>
    /// 启动迁移/种子的统一处置(COD-14,票02):失败即启动失败(fail-fast)。
    ///
    /// ★ 为什么选进程退出而不是「记日志继续启动 + /health 报错」:
    ///   REQ-01 已实证旧口径的危害 —— 迁移链断裂后服务带着缺表/缺列的库继续启动,
    ///   /health/live 照常 200,问题延后到用户操作时才以满屏 500 暴露,且无自恢复路径。
    ///   本系统是单机部署,没有编排层做滚动/金丝雀,「半启动」没有任何消费者:
    ///   宁可进程在 app.Run() 之前抛回原异常直接退出,让部署脚本/看门狗第一时间发现。
    ///   七个业务服务一律经此入口,口径一致,不再逐个 Program.cs 各写一份 try/catch。
    /// </summary>
    /// <param name="migrateAndSeed">服务的 MigrateAndSeedAsync 委托(迁移+种子)。</param>
    /// <param name="logger">记录 Error 级失败日志的 logger(通常为 app.Logger)。</param>
    /// <param name="serviceName">服务名,仅用于日志定位。</param>
    public static async Task RunStartupMigrationsOrFailAsync(
        Func<Task> migrateAndSeed, ILogger logger, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(migrateAndSeed);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            await migrateAndSeed();
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{Service} 数据库初始化(迁移/种子)失败 —— 启动中止(fail-fast):" +
                "迁移未完成时带病启动只会让接口以 500 暴露问题,进程直接退出,请修好数据库后重启",
                serviceName);
            throw;
        }
    }

    /// <summary>
    /// <see cref="RunStartupMigrationsOrFailAsync(Func{Task}, ILogger, string)"/> 的 Program.cs 便捷重载:
    /// 各服务只需一行 `await app.RunStartupMigrationsOrFailAsync("xxx-api", XxxDbSeeder.MigrateAndSeedAsync);`
    /// </summary>
    public static Task RunStartupMigrationsOrFailAsync(
        this WebApplication app, string serviceName,
        Func<IServiceProvider, IConfiguration, ILogger, Task> migrateAndSeed)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(migrateAndSeed);
        return RunStartupMigrationsOrFailAsync(
            () => migrateAndSeed(app.Services, app.Configuration, app.Logger),
            app.Logger, serviceName);
    }

    /// <summary>
    /// 扫描程序集里所有 FluentValidation 的 AbstractValidator&lt;T&gt;,并注册为:
    ///   - IValidator&lt;T&gt;
    ///   - IValidator(非泛型基接口,ValidationBehavior 通过 IEnumerable&lt;IValidator&lt;TRequest&gt;&gt; 拿的就是它)
    /// </summary>
    private static void RegisterValidators(IServiceCollection services, System.Reflection.Assembly assembly)
    {
        var validatorTypes = assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false })
            .Select(t => new
            {
                Implementation = t,
                Interfaces = t.GetInterfaces()
                    .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
                    .ToArray()
            })
            .Where(x => x.Interfaces.Length > 0)
            .ToList();

        foreach (var v in validatorTypes)
        {
            foreach (var @interface in v.Interfaces)
                services.AddScoped(@interface, v.Implementation);
            services.AddScoped(v.Implementation);
        }
    }

    private static TracerProviderBuilder AddOtlpExporterIfConfigured(this TracerProviderBuilder t, IConfiguration cfg)
    {
        var endpoint = cfg["Otel:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint)) t.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
        return t;
    }

    private static MeterProviderBuilder AddOtlpExporterIfConfiguredMetrics(this MeterProviderBuilder m, IConfiguration cfg)
    {
        var endpoint = cfg["Otel:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint)) m.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
        return m;
    }
}
