using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Identity.Application.Abstractions;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.Services.Identity.Application.Logto;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Infrastructure.Services;
using YourInterview.Services.Identity.Infrastructure.Security;
var builder = WebApplication.CreateBuilder(args);
const string ServiceName = "identity-api";

// ---------- 统一服务基座(日志/可观测/MediatR/异常/CORS/健康检查/Swagger) ----------
builder.AddServiceDefaults(ServiceName, services =>
{
    // ---------- 数据库 ----------
    var conn = builder.Configuration.GetConnectionString("Identity")
        ?? "Host=localhost;Port=5433;Database=yourinterview;Username=postgres;Password=postgres";
    services.AddDbContext<IdentityDbContext>((sp, options) =>
    {
        options.UseNpgsql(conn, npg =>
        {
            npg.MigrationsHistoryTable("__ef_migrations_history", IdentityDbContext.Schema);
            npg.EnableRetryOnFailure(3);            // 瞬时故障重试
            npg.CommandTimeout(30);
        });
        options.AddInterceptors(
            sp.GetRequiredService<AuditSaveChangesInterceptor>(),
            new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
        if (builder.Environment.IsDevelopment())
        {
            options.EnableDetailedErrors();
            options.EnableSensitiveDataLogging();
        }
    });

    // ---------- 当前用户上下文 ----------
    services.AddHttpContextAccessor();
    services.AddScoped<ICurrentUser, HttpCurrentUser>();
    services.AddScoped<AuditSaveChangesInterceptor>();
    services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();

    // ---------- 安全 ----------
    services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
    services.AddSingleton(TimeProvider.System);
    services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
    services.AddScoped<IJwtTokenService, JwtTokenService>();
    services.AddScoped<UserPermissionResolver>();
    services.AddScoped<AuthResponseBuilder>();

    // ---------- Google 登录(M2.1):凭据在 Google Auth Platform,回调走 /api/auth/google/callback ----------
    services.Configure<GoogleOAuthOptions>(builder.Configuration.GetSection(GoogleOAuthOptions.SectionName));
    services.AddScoped<GoogleOAuthService>();
    services.AddHttpClient(GoogleOAuthService.HttpClientName);

    // ---------- 票241:Logto 审批门(未配凭据时控制器回 503、扫表不启动,旧登录一字不动) ----------
    services.Configure<LogtoManagementOptions>(
        builder.Configuration.GetSection(LogtoManagementOptions.SectionName));
    services.Configure<LegacyAuthOptions>(builder.Configuration.GetSection(LegacyAuthOptions.SectionName));
    services.AddSingleton<LogtoGateHealth>();
    // Management 客户端 10 秒超时:挂住的调用要快速失败成 5xx 走投递方重发,
    // 不能拖着 webhook 连接(投递方重试次数有限,慢 handler 等于丢事件)。
    services.AddHttpClient(LogtoManagementClient.HttpClientName)
        .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
    services.AddScoped<ILogtoManagementClient, LogtoManagementClient>();
    services.AddScoped<ILogtoMappingStore, LogtoMappingStore>();
    services.AddScoped<ILogtoSweepAudit, LogtoSweepAudit>();
    services.AddScoped<LogtoApprovalGate>();
    services.AddHostedService<LogtoApprovalReconciliationService>();

    // ---------- JWT 认证 ----------
    // 校验参数唯一定义在 ServiceDefaults(ARC-09/票18):其 Issuer/Audience 兜底串与 JwtOptions
    // 类默认值逐字相同,校验结果与旧 JwtOptions 绑定等价;签发侧(上方 Configure<JwtOptions>)不动。
    // 本服务有意差异经 configure 回调原样保留:RequireHttpsMetadata、SaveToken=false、
    // NameClaimType 显式化(框架默认同为 ClaimTypes.Name)、401/403 ProblemDetails Events。
    services.AddJwtAuthentication(builder.Configuration, options =>
        {
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.SaveToken = false;
            options.TokenValidationParameters.NameClaimType = System.Security.Claims.ClaimTypes.Name;
            options.Events = new JwtBearerEvents
            {
                OnChallenge = ctx =>
                {
                    // 返回 ProblemDetails 而不是空 401(前端统一处理)
                    ctx.HandleResponse();
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/problem+json";
                    return ctx.Response.WriteAsJsonAsync(new
                    {
                        type = "https://tools.ietf.org/html/rfc9110#section-15.5.2",
                        title = "Unauthorized",
                        status = 401,
                        code = "Auth.Unauthorized",
                        detail = "访问令牌缺失或无效,请重新登录"
                    });
                },
                OnForbidden = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    ctx.Response.ContentType = "application/problem+json";
                    return ctx.Response.WriteAsJsonAsync(new
                    {
                        type = "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                        title = "Forbidden",
                        status = 403,
                        code = "Auth.Forbidden",
                        detail = "当前角色缺少该操作所需权限"
                    });
                }
            };
        });

    // ---------- 权限策略:每个权限点一条 policy ----------
    services.AddAuthorization(options =>
    {
        PermissionPolicy.AddPermissionPolicies(options);
    });

    // ---------- 健康检查:数据库探针 ----------
    services.AddHealthChecks().AddDbContextCheck<IdentityDbContext>("postgres");
});

// ---------- Swagger 增强:Bearer 认证按钮 ----------
builder.Services.ConfigureSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "输入 access token(不需要写 'Bearer ' 前缀)"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

app.UseServiceDefaults(ServiceName);
app.MapControllers();

// ---------- 启动时迁移 + 种子 ----------
// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync(ServiceName, IdentityDbSeeder.MigrateAndSeedAsync);

app.Run();

/// <summary>Program 标记类,供集成测试 WebApplicationFactory 使用。</summary>
public partial class Program;
