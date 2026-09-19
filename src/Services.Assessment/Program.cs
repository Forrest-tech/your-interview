using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("assessment-api");

// ---------- 数据库:独占 schema "assessment" ----------
builder.Services.AddDbContext<AssessmentDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("AssessmentDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AssessmentDbContext.Schema));
    options.AddInterceptors(new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
});

builder.Services.AddHealthChecks().AddDbContextCheck<AssessmentDbContext>("postgres");

// ---------- CQRS 管道 ----------
builder.Services.AddMediatR(cfg =>
{
    cfg.RegisterServicesFromAssembly(System.Reflection.Assembly.GetExecutingAssembly());
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.LoggingBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.ValidationBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.PerformanceBehavior<,>));
    cfg.AddOpenBehavior(typeof(YourInterview.BuildingBlocks.Behaviors.UnhandledExceptionBehavior<,>));
});

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

// ---------- LLM 接入(2026-09-18:面试前准备包) ----------
// 独立的 http client:LLM 与语音服务的超时/连接策略不同 ——
// 生成预测问题可能要跑几十秒(取决于模型),给 3 分钟。
// 不共用 azure-speech 那个(它的 5 分钟与 v1.1 强制是为语音流式场景定的)。
builder.Services.AddHttpClient("llm", c =>
{
    c.Timeout = TimeSpan.FromMinutes(3);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(20),
});

// 客户端工厂:按协议(openai-compatible / azure-openai / anthropic)派发。
// 单例安全 —— 它只持有 IHttpClientFactory 与 ILoggerFactory,都是单例安全的。
builder.Services.AddSingleton<YourInterview.SharedContracts.Ai.ILlmClientFactory,
    YourInterview.SharedContracts.Ai.LlmClientFactory>();

// LLM 凭据解析(数据库 > 环境变量 > appsettings)。
// 与 ISpeechKeyProvider 严格同构,同样用 IServiceScopeFactory 读 Scoped 的 DbContext。
builder.Services.AddSingleton<YourInterview.Services.Assessment.Infrastructure.Storage.IAiKeyProvider,
    YourInterview.Services.Assessment.Infrastructure.Storage.AiKeyProvider>();

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

app.UseServiceDefaults("assessment-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

try { await AssessmentDbSeeder.MigrateAndSeedAsync(app.Services, app.Configuration, app.Logger); }
catch (Exception ex) { app.Logger.LogError(ex, "Assessment 数据库初始化失败 —— 服务继续启动"); }

app.Run();
