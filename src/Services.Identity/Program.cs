using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Identity.Application.Abstractions;
using YourInterview.Services.Identity.Application.Auth;
using YourInterview.Services.Identity.Infrastructure.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Identity.Infrastructure.Security;
using YourInterview.Services.Identity.Infrastructure.Services;

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

    // ---------- JWT 认证 ----------
    var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
    if (string.IsNullOrWhiteSpace(jwt.SigningKey))
        jwt.SigningKey = "dev-only-signing-key-change-me-in-production-0123456789abcdef";

    services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.SaveToken = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,                       // 关键:过期即拒
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(30),          // 默认 5 分钟太长
                NameClaimType = System.Security.Claims.ClaimTypes.Name,
                RoleClaimType = System.Security.Claims.ClaimTypes.Role
            };
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
try
{
    await IdentityDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Identity 数据库初始化失败(数据库不可用?)—— 服务继续启动,稍后重试");
}

app.Run();

/// <summary>Program 标记类,供集成测试 WebApplicationFactory 使用。</summary>
public partial class Program;
