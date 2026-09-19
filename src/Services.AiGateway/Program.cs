using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.AiGateway.Infrastructure.Persistence;
using YourInterview.Services.AiGateway.Infrastructure.Storage;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("aigateway-api");

// ---------- 数据库:独占 schema "aigateway" ----------
builder.Services.AddDbContext<AiGatewayDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("AiGatewayDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AiGatewayDbContext.Schema));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AiGatewayDbContext>("postgres");

// ---------- CQRS 管道 ----------
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(System.Reflection.Assembly.GetExecutingAssembly());
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.PerformanceBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.UnhandledExceptionBehavior<,>));
});

// ---------- 当前用户(凭据是用户级数据,按 UserId 隔离) ----------
builder.Services.AddCurrentUser();

// ---------- LLM 客户端 ----------
// 独立 http client:生成 Cover Letter / 准备包可能要跑几十秒(取决于模型),给 3 分钟。
builder.Services.AddHttpClient("llm", c =>
{
    c.Timeout = TimeSpan.FromMinutes(3);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(20),
});

// 客户端工厂:按协议(openai-compatible / azure-openai / anthropic)派发。
// 单例安全 —— 只持有 IHttpClientFactory 与 ILoggerFactory。
builder.Services.AddSingleton<YourInterview.SharedContracts.Ai.ILlmClientFactory,
    YourInterview.SharedContracts.Ai.LlmClientFactory>();

// LLM 凭据解析(数据库 > 环境变量 > appsettings)。
// 单例:内部用 IServiceScopeFactory 读 Scoped 的 DbContext,查完即释放。
builder.Services.AddSingleton<IAiKeyProvider, AiKeyProvider>();
builder.Services.AddSingleton<YourInterview.Services.AiGateway.Application.AiKeyProviderHolder>();

builder.Services.AddMassTransitWithRabbitMq(builder.Configuration);

// ---------- 鉴权 ----------
var jwtSection = builder.Configuration.GetSection("Jwt");
var signingKey = jwtSection["SigningKey"] ?? throw new InvalidOperationException("缺少 Jwt:SigningKey");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

var app = builder.Build();

app.UseServiceDefaults("aigateway-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try { await AiGatewayDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger); }
catch (Exception ex) { app.Logger.LogError(ex, "AiGateway 数据库初始化失败 —— 服务继续启动"); }

app.Run();
