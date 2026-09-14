using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.Services.Jobs.Infrastructure.Services;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);
const string ServiceName = "jobs-api";

builder.AddServiceDefaults(ServiceName, services =>
{
    var conn = builder.Configuration.GetConnectionString("Jobs")!;
    services.AddDbContext<JobsDbContext>((sp, options) =>
    {
        options.UseNpgsql(conn, npg =>
        {
            npg.MigrationsHistoryTable("__ef_migrations_history", JobsDbContext.Schema);
            npg.EnableRetryOnFailure(3);
        });
        options.AddInterceptors(new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
    });

    services.AddSingleton(TimeProvider.System);
    services.AddHealthChecks().AddDbContextCheck<JobsDbContext>("postgres");

    // ---------- MassTransit:把领域事件转发成跨服务集成事件到 RabbitMQ ----------
    services.AddMassTransitWithRabbitMq(builder.Configuration);
});

// ---------- JWT 认证(校验 Identity 签发的令牌,只验签不查库) ----------
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException(
        "缺少 Jwt:SigningKey 配置。请在本地 appsettings.Development.json 或环境变量 Jwt__SigningKey 中配置" +
        "(需与 Services.Identity 签发的密钥一致,至少 32 字符)。");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"] ?? "your-interview.identity",
            ValidAudience = jwtSection["Audience"] ?? "your-interview.api",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
    });

builder.Services.AddAuthorization(options =>
{
    // 注册所有权限策略(与 Identity 完全一致 —— 策略名 = "perm:<权限点>")
    YourInterview.BuildingBlocks.Security.PermissionPolicy.AddPermissionPolicies(options);
});

builder.Services.ConfigureSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer",
        BearerFormat = "JWT", In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();
app.UseServiceDefaults(ServiceName);
app.MapControllers();

try { await JobsDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger); }
catch (Exception ex) { app.Logger.LogError(ex, "Jobs 数据库初始化失败 —— 服务继续启动"); }

app.Run();

public partial class Program;
