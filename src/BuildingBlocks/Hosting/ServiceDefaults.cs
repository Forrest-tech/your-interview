using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MediatR;
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

    public static WebApplicationBuilder AddServiceDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        Action<IServiceCollection>? configure = null)
    {
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

            var status = ex switch
            {
                ValidationException => StatusCodes.Status400BadRequest,
                UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
                KeyNotFoundException => StatusCodes.Status404NotFound,
                ArgumentException => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status500InternalServerError
            };

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
