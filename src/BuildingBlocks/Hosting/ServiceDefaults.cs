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
using Microsoft.AspNetCore.Authentication;
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
using YourInterview.BuildingBlocks.Security;

namespace YourInterview.BuildingBlocks.Hosting;

/// <summary>
/// 每个微服务共用的启动基座:日志 / 可观测 / 异常处理 / OpenAPI / CORS / 健康检查 / MediatR。
/// 保持各服务 Program.cs 极薄 —— 这正是 eShop 的做法(面试可讲的"服务模板统一")。
/// </summary>
public static class ServiceDefaults
{
    public const string CorsPolicy = "your-interview-cors";

    /// <summary>
    /// CORS 白名单的**统一默认口径**(ARC-18/票50):与网关(Services.Gateway/appsettings.json 的
    /// Cors:AllowedOrigins 及网关代码兜底)逐项一致,网关与各服务共读同一配置键
    /// <c>Cors:AllowedOrigins</c>(环境变量 Cors__AllowedOrigins__0... 同样生效)。
    /// 逐项依据:
    ///   · http://localhost:4200 —— Angular dev server(ng serve)的规范 Origin;
    ///     前端既可经 proxy.conf.json 同源转发,也可在排查时直连各服务端口
    ///     (web/src/environments/environment.ts 的 servicePorts 直连模式),两种形态浏览器 Origin 都是它。
    ///   · http://127.0.0.1:4200 —— 同一 dev server 以 127.0.0.1 打开时的 Origin(与 localhost 是不同 Origin,必须分列)。
    ///   · http://localhost:5100 —— 网关 appsettings 既有的 SPA 本地形态,网关侧已放行,服务侧统一跟齐。
    /// 部署形态(生产前端域名)不写死在代码里:上线时以 Cors:AllowedOrigins 配置节/环境变量注入即可,不要改本默认值。
    /// </summary>
    public static readonly string[] DefaultCorsAllowedOrigins =
        ["http://localhost:4200", "http://localhost:5100", "http://127.0.0.1:4200"];

    /// <summary>
    /// ARC-18(票50):CORS 白名单的**唯一解析口径**。读统一配置键 <c>Cors:AllowedOrigins</c>
    /// (与网关同键同值),逐项归一化(去空白、去尾斜杠、去重)并剔除含 <c>*</c> 的通配项
    /// —— 白名单永不接受通配,防止有人把配置写成 * 又回到全放行+凭据的危险组合。
    /// 配置缺失/为空/全被剔除时回落 <see cref="DefaultCorsAllowedOrigins"/>。
    /// 纯函数(只读 <see cref="IConfiguration"/>),口径由 BuildingBlocks.Tests 锁定。
    /// </summary>
    public static IReadOnlyList<string> ResolveCorsAllowedOrigins(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        var normalized = NormalizeCorsOrigins(configured);
        return normalized.Count > 0 ? normalized : NormalizeCorsOrigins(DefaultCorsAllowedOrigins);
    }

    /// <summary>
    /// ARC-18(票50):Origin 是否在白名单内 —— **精确匹配**(归一化后 OrdinalIgnoreCase 全等),
    /// 不做后缀/子域匹配:http://sub.localhost:4200、http://evil-localhost:4200、
    /// 端口不同、scheme 不同一律拒绝。null/空白/字面量 "null"(沙盒 iframe 的 Origin 序列化值)一律拒绝。
    /// 白名单侧含 * 的项被归一化剔除,不会命中任何 Origin。纯函数,口径由 BuildingBlocks.Tests 锁定。
    /// </summary>
    public static bool IsCorsOriginAllowed(string? origin, IEnumerable<string?>? allowedOrigins)
    {
        var normalizedOrigin = NormalizeCorsOrigin(origin);
        if (normalizedOrigin is null || allowedOrigins is null)
            return false;

        foreach (var allowed in allowedOrigins)
        {
            var normalizedAllowed = NormalizeCorsOrigin(allowed);
            if (normalizedAllowed is not null &&
                string.Equals(normalizedOrigin, normalizedAllowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static List<string> NormalizeCorsOrigins(IEnumerable<string?>? origins)
    {
        var result = new List<string>();
        if (origins is null) return result;
        foreach (var origin in origins)
        {
            var normalized = NormalizeCorsOrigin(origin);
            if (normalized is not null &&
                !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }
        return result;
    }

    private static string? NormalizeCorsOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        var value = origin.Trim();
        // 通配项一律作废:白名单口径下 "*" 不是合法条目,含 * 的条目也不参与匹配。
        if (value.Contains('*')) return null;
        // Origin 序列化本身不带尾斜杠;配置/来访值带了尾斜杠时归一掉,避免 "http://localhost:4200/" 误拒。
        value = value.TrimEnd('/');
        return value.Length == 0 ? null : value;
    }

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

        // ---------- CORS(ARC-18/票50:与网关同一白名单口径) ----------
        // 此前 SetIsOriginAllowed(_ => true)+AllowCredentials = 任意站点带凭据跨源,直连服务端口即全放行。
        // 现白名单自统一配置 Cors:AllowedOrigins 读取(解析口径见 ResolveCorsAllowedOrigins),
        // 与网关逐项一致;白名单外 Origin 的预检/实际请求均无 Access-Control-Allow-Origin 回头。
        // 无 Origin 头的请求(服务间调用/健康检查/curl)不经 CORS 判定,不受影响。
        var corsAllowedOrigins = ResolveCorsAllowedOrigins(builder.Configuration);
        builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
            .SetIsOriginAllowed(origin => IsCorsOriginAllowed(origin, corsAllowedOrigins))
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

        var parameters = new TokenValidationParameters
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

        // 票241 修订:本函数永远只返回票18 旧单发行方口径。双轨期的 Logto
        // scheme 另有 LogtoTokenValidation.Build(见 AddJwtAuthentication),
        // 单 scheme 多发行方 allowlist 已废弃(一个 JwtBearer scheme 只有一个
        // metadata 文档/一套密钥,ValidIssuers  alone 拉不到两家 JWKS)。

        return parameters;
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
        services.Configure<LogtoOptions>(configuration.GetSection(LogtoOptions.SectionName));
        var logto = configuration.GetSection(LogtoOptions.SectionName).Get<LogtoOptions>() ?? new LogtoOptions();

        if (!logto.Enabled)
        {
            // 双轨关闭:与票241 之前一字不变的单 scheme 旧口径 + 空镜像读(门直接返回)。
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = BuildTokenValidationParameters(configuration);
                    configure?.Invoke(options);
                });
            services.AddScoped<ILogtoMirrorReader, NullMirrorReader>();
            services.AddScoped<IClaimsTransformation, LogtoClaimsGate>();
            return services;
        }

        // 票241 双轨:双 scheme + AddPolicyScheme 选择器(唯一路由机制,不做顺序回落)。
        // 选择器按未验证 payload 的 iss 路由,选中 scheme 再做完整校验。
        // 镜像读是跨 schema 直查(七服务共用同一 yourinterview 库,键名各异故取首个非空连接串);
        // 未配连接串时启动即失败 —— 门缺了事实源绝不能带病运行。
        var connectionString = configuration.GetSection("ConnectionStrings").GetChildren()
            .Select(c => c.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "已配 Logto:Issuer 但未找到 ConnectionStrings,共享审批门无法读取映射表,拒绝启动。");

        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = LogtoSchemeSelector.PolicyScheme;
                options.DefaultChallengeScheme = LogtoSchemeSelector.PolicyScheme;
                options.DefaultForbidScheme = LogtoSchemeSelector.PolicyScheme;
            })
            .AddPolicyScheme(LogtoSchemeSelector.PolicyScheme, "LogtoOrLegacy", options =>
            {
                options.ForwardDefaultSelector = ctx => LogtoSchemeSelector.SelectScheme(ctx, logto);
            })
            .AddJwtBearer(LogtoSchemeSelector.LogtoScheme, options =>
            {
                // 发现地址 = issuer + /.well-known/openid-configuration(Logto issuer 自带 /oidc 后缀)。
                options.MetadataAddress = logto.Issuer.TrimEnd('/') + "/.well-known/openid-configuration";
                // http issuer(本地开发)不强制 HTTPS 元数据;https issuer 保持默认强制。
                options.RequireHttpsMetadata = logto.Issuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                options.TokenValidationParameters = LogtoTokenValidation.Build(logto);
                configure?.Invoke(options);
            })
            .AddJwtBearer(LogtoSchemeSelector.LegacyScheme, options =>
            {
                options.TokenValidationParameters = BuildTokenValidationParameters(configuration);
                configure?.Invoke(options);
            });
        // 各服务的 configure 回调(如 Identity 的 401/403 Events、Jobs 的 RequireHttpsMetadata)
        // 对两个 scheme 各跑一次,两边口径一致。
        services.AddScoped<ILogtoMirrorReader>(_ => new EfLogtoMirrorReader(connectionString!));
        services.AddScoped<IClaimsTransformation, LogtoClaimsGate>();
        return services;
    }

    /// <summary>
    /// 全局异常 → HTTP 状态码的唯一映射表(UseServiceDefaults 的异常处理器与测试共用同一口径)。
    /// COD-05:EF Core 的 <see cref="DbUpdateConcurrencyException"/>(xmin 乐观并发令牌冲突)
    /// 必须映射 409 Conflict —— 此前它落到默认分支冒 500,双标签页/双设备并发编辑同一条目时
    /// 用户只看到「服务器错误」,无从知道自己的改动因版本过期而未保存。
    /// COD-13:<see cref="JsonException"/> 必须映射 400 BadRequest —— 直读请求体的端点
    /// (Interviews 的 AttachAsset 用 HttpRequest.ReadFromJsonAsync)收到畸形 JSON 时 STJ 抛
    /// JsonException 且无模型绑定层兜底,此前它落到默认分支冒 500,把客户端错误报成服务器错误。
    /// 票76:<see cref="BadHttpRequestException"/> 必须透传其框架 StatusCode —— Kestrel 在请求体
    /// 超过上限(MaxRequestBodySizeBytes)时抛此异常且 StatusCode 本即 413 Payload Too Large,
    /// 此前它落到默认分支冒 500;透传而非硬编码 413,是因为框架的其他细分(请求头过大 431、
    /// 读体超时 408、默认 400)同样应按框架原值返回。Kestrel.Core 的实现为其子类,类型模式一并命中。
    /// 票90:<see cref="DbUpdateException"/> 必须映射 409 Conflict —— 唯一约束竞态是真实可达路径
    /// (Assessment practice_categories (UserId, Name) 唯一索引;Create/Rename 先查后写,两路同名
    /// 并发时一方 SaveChanges 撞约束逃逸,此前冒 500)。口径边界:应用层去重检查命中走
    /// Validation→400(既有口径);xmin 乐观并发走 <see cref="DbUpdateConcurrencyException"/>→409
    /// (票07,子类臂必须在前);其余 DB 层约束冲突归本票。不按 Pg 错误码细分:BuildingBlocks
    /// 无 Npgsql 引用,无法干净辨别 23505 与其他 DB 故障;DB 宕机时同臂亦 409 属可接受误报
    /// (重试建议本身合理),不为此引入反射取 SqlState 的脆弱实现。
    /// 票90:<see cref="OperationCanceledException"/> 必须映射 504 Gateway Timeout —— 唯一可观测
    /// 情形是上游超时以 <see cref="TaskCanceledException"/> 逃逸(HttpClient 超时即此形态,如
    /// Analytics 真源调用未捕获 TaskCanceled);纯客户端断连(RequestAborted)无可交付响应,
    /// 本臂对其无害。TaskCanceledException 是其子类,类型模式一并命中。
    /// </summary>
    public static int MapExceptionToStatusCode(Exception? ex) => ex switch
    {
        ValidationException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        DbUpdateConcurrencyException => StatusCodes.Status409Conflict,
        DbUpdateException => StatusCodes.Status409Conflict,
        OperationCanceledException => StatusCodes.Status504GatewayTimeout,
        JsonException => StatusCodes.Status400BadRequest,
        BadHttpRequestException badHttp => badHttp.StatusCode,
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

            if (ex is DbUpdateException)
            {
                // 票90:DB 层约束冲突(唯一索引竞态等)。与并发臂的区别:这里不是版本过期,
                // 而是写入内容与库内现状冲突(典型如两路同名分类并发创建)。detail 同样只给
                // 用户可执行提示,不含任何内部细节;code=data_conflict 供前端区分于并发冲突。
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = status,
                    Title = "Data conflict",
                    Detail = "The data conflicts with the current state — it may already exist " +
                             "or have been changed by someone else. Refresh and try again.",
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                    Extensions = { ["traceId"] = ctx.TraceIdentifier, ["code"] = "data_conflict" }
                });
                return;
            }

            if (ex is OperationCanceledException)
            {
                // 票90:上游超时(TaskCanceledException 是其子类)。纯客户端断连无可交付响应,
                // 本分支只在响应可交付时生效,即上游未按时响应。detail 不含内部细节。
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = status,
                    Title = "Gateway Timeout",
                    Detail = "The upstream service did not respond in time. Please try again later.",
                    Type = "https://tools.ietf.org/html/rfc9110#section-15.6.5",
                    Extensions = { ["traceId"] = ctx.TraceIdentifier, ["code"] = "upstream_timeout" }
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
