using Microsoft.EntityFrameworkCore;
using Serilog;
using YourInterview.BuildingBlocks.Ai;
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

// M2.3:LLM 凭据仍只存 AiGateway;票95 起 Assessment 经票38 单一 AiGatewayClient
// 编排 Mock 出题/评分(凭据不落本服务,JWT 透传由客户端处理)。
builder.Services.AddHttpContextAccessor();
builder.Services.AddAiGatewayClient(builder.Configuration["AiGateway:BaseUrl"]);
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration);

// ---------- 鉴权 ----------
// ---------- JWT 认证(校验参数唯一定义在 ServiceDefaults,ARC-09/票18) ----------
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

// ---------- 票119 词典模块（静态数据：嵌入资源 + 本地数据目录，不落库） ----------
builder.Services.Configure<YourInterview.Services.Assessment.Infrastructure.Dictionary.DictionaryOptions>(
    builder.Configuration.GetSection(YourInterview.Services.Assessment.Infrastructure.Dictionary.DictionaryOptions.SectionName));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
        YourInterview.Services.Assessment.Infrastructure.Dictionary.DictionaryOptions>>().Value);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new Lazy<YourInterview.Services.Assessment.Infrastructure.Dictionary.OpenLayerStore>(
    YourInterview.Services.Assessment.Infrastructure.Dictionary.OpenLayerStore.LoadFromEmbeddedResources));
builder.Services.AddSingleton(sp => new YourInterview.Services.Assessment.Infrastructure.Dictionary.OxfordPackStore(
    sp.GetRequiredService<YourInterview.Services.Assessment.Infrastructure.Dictionary.DictionaryOptions>().Oxford.PackDir));
// Wikimedia 等源站按 UA 礼仪拒绝无 UA/库默认 UA（403）：统一带描述性 UA。
builder.Services.AddHttpClient("dictionary-audio", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("YourInterview-Dictionary/1.0 (personal vocabulary learning)");
});
builder.Services.AddSingleton<YourInterview.Services.Assessment.Infrastructure.Dictionary.AudioResolver>();
builder.Services.AddSingleton<YourInterview.Services.Assessment.Infrastructure.Dictionary.DictionaryService>();

var app = builder.Build();

app.UseServiceDefaults("assessment-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync("assessment-api", AssessmentDbSeeder.MigrateAndSeedAsync);

app.Run();
