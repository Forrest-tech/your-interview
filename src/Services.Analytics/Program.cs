using Microsoft.EntityFrameworkCore;
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
// ---------- JWT 认证(校验参数唯一定义在 ServiceDefaults,ARC-09/票18) ----------
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

var app = builder.Build();

app.UseServiceDefaults("analytics-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync("analytics-api", AnalyticsDbSeeder.MigrateAndSeedAsync);

app.Run();
