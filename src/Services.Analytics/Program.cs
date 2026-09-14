using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Analytics.Application;
using YourInterview.Services.Analytics.Infrastructure.Persistence;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("analytics-api");

// ---------- 读模型库:独占 schema "analytics" ----------
builder.Services.AddDbContext<AnalyticsDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("AnalyticsDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AnalyticsDbContext.Schema));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AnalyticsDbContext>("postgres");

builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(System.Reflection.Assembly.GetExecutingAssembly());
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.PerformanceBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.UnhandledExceptionBehavior<,>));
});

builder.Services.AddCurrentUser();

// ---------- 消息总线:Analytics 是纯消费者(读模型由事件驱动) ----------
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration, x =>
{
    // 每个消费者独立队列 —— 一个消费者反复失败(反复重投)不会拖垮其他读模型更新
    x.AddConsumer<MockAnswerSubmittedConsumer>();
    x.AddConsumer<KnowledgeLevelChangedConsumer>();
    x.AddConsumer<JobApplicationStatusChangedConsumer>();
    x.AddConsumer<InterviewAnalysisCompletedConsumer>();
    x.AddConsumer<AuditEventRecordedConsumer>();
});

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

app.UseServiceDefaults("analytics-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try { await AnalyticsDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger); }
catch (Exception ex) { app.Logger.LogError(ex, "Analytics 数据库初始化失败 —— 服务继续启动"); }

app.Run();
