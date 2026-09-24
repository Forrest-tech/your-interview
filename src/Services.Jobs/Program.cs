using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Jobs.Application;
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

    // ---------- AiGateway 内部客户端(2026-09-18) ----------
    // 求职信生成等 AI 功能经此调 AiGateway —— 凭据只存网关一处,Jobs 不碰 key。
    // 地址优先取配置 AiGateway:BaseUrl(容器内 http://aigateway:8080/),
    // 宿主机直跑时回落到 5268。
    services.AddHttpContextAccessor();
    services.AddHttpClient<IAiGatewayClient, AiGatewayClient>(http =>
    {
        var baseUrl = builder.Configuration["AiGateway:BaseUrl"] ?? "http://127.0.0.1:5268/";
        http.BaseAddress = new Uri(baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/");
        // 生成是长请求:LLM 输出 300-400 词通常 20-60 秒。
        // 给到 3 分钟 —— 短于上游真实耗时会让用户看到"超时"而其实已经成功了。
        http.Timeout = TimeSpan.FromMinutes(3);
    });

    services.AddSingleton(TimeProvider.System);
    services.AddHealthChecks().AddDbContextCheck<JobsDbContext>("postgres");

    // ---------- MassTransit:把领域事件转发成跨服务集成事件到 RabbitMQ ----------
    services.AddMassTransitWithRabbitMq(builder.Configuration, x =>
    {
        // M1.5:实战机经条目上填了结果 → 回写 Tracker 轮次 Outcome(被拒自动转 Rejected)
        x.AddConsumer<InterviewOutcomeRecordedConsumer>();
    });
});

// ---------- 当前用户(简历/求职信是用户级私有数据,按 UserId 隔离) ----------
// ⚠️ AddCurrentUser 不在 AddServiceDefaults 里 —— 按需显式调用(见其注释)。
//    JobsController 构造注入 ICurrentUser,不注册会让控制器激活直接抛
//    "Unable to resolve service for type ICurrentUser" → 500。
builder.Services.AddCurrentUser();

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
