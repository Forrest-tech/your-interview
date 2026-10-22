using Microsoft.EntityFrameworkCore;
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

// 备考数据源外部检索(Tavily / Serper):短请求,20 秒足够。
builder.Services.AddHttpClient("prep-search", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});

// 截图视觉 OCR:图片 + 模型推理,给 90 秒。
builder.Services.AddHttpClient("vision", c =>
{
    c.Timeout = TimeSpan.FromSeconds(90);
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
// ---------- JWT 认证(校验参数唯一定义在 ServiceDefaults,ARC-09/票18) ----------
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

var app = builder.Build();

app.UseServiceDefaults("aigateway-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync("aigateway-api", AiGatewayDbSeeder.MigrateAndSeedAsync);

app.Run();
