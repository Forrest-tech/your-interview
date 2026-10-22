using Microsoft.EntityFrameworkCore;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.SharedContracts;   // AddMassTransitWithRabbitMq 扩展所在命名空间

var builder = WebApplication.CreateBuilder(args);

// ★ Kestrel 请求体上限 64MB 已收进 AddServiceDefaults 全服务统一(ARC-09/票18);
//   本服务 2026-09-26 的 413 修复口径不变(长录音 multipart 上传/评分送审,值仍 64MB)。
builder.AddServiceDefaults("assessment-api");

// ---------- 数据库:独占 schema "assessment" ----------
builder.Services.AddDbContext<AssessmentDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("AssessmentDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AssessmentDbContext.Schema));
    options.AddInterceptors(new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AssessmentDbContext>("postgres");

// ---------- 当前用户(练习是纯私有数据,按 UserId 隔离) ----------
builder.Services.AddCurrentUser();

// ---------- Azure 发音评估(服务端持 key,浏览器只上传音频) ----------
builder.Services.AddHttpClient("azure-speech", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    c.DefaultRequestVersion = System.Net.HttpVersion.Version11;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(30),
});
builder.Services.AddSingleton<YourInterview.Services.Assessment.Application.PronunciationAssessor>();

// ---------- 录音存储(2026-09-15 第十七轮) ----------
// 音频落文件系统,元数据落库 —— 见 IAudioStore 注释里的取舍说明。
// 换对象存储只需换这一个注册。
builder.Services.AddSingleton<YourInterview.Services.Assessment.Infrastructure.Storage.IAudioStore,
    YourInterview.Services.Assessment.Infrastructure.Storage.LocalAudioStore>();

// 语音密钥解析(数据库 > 环境变量 > appsettings)。
// 单例评估器靠它读 Scoped 的 DbContext —— 见 ISpeechKeyProvider 注释。
builder.Services.AddSingleton<YourInterview.Services.Assessment.Infrastructure.Storage.ISpeechKeyProvider,
    YourInterview.Services.Assessment.Infrastructure.Storage.SpeechKeyProvider>();

// Azure 神经语音合成(示范朗读走 Azure 时用;key 与评分共用同一份)
builder.Services.AddSingleton<YourInterview.Services.Assessment.Application.SpeechSynthesizer>();

// M2.3:LLM 凭据与补全已收敛到 AiGateway(前端统一走 /api/ai/*),
// Assessment 不再持有 LLM 客户端/凭据栈;历史数据由 RemoveAiSettings 迁移搬走后删表。
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration);

// ---------- 鉴权 ----------
// ---------- JWT 认证(校验参数唯一定义在 ServiceDefaults,ARC-09/票18) ----------
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

var app = builder.Build();

app.UseServiceDefaults("assessment-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync("assessment-api", AssessmentDbSeeder.MigrateAndSeedAsync);

app.Run();
