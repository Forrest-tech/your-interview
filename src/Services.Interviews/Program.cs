using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Serilog;
using YourInterview.BuildingBlocks.Ai;
using YourInterview.BuildingBlocks.Hosting;
using YourInterview.BuildingBlocks.Persistence;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;
using YourInterview.SharedContracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("interviews-api");

// ---------- 录音文件存储(本地卷,数据库只存相对路径 + SHA-256) ----------
builder.Services.AddSingleton<IInterviewAudioStore, LocalInterviewAudioStore>();

// ---------- 当前用户(集成事件要带 UserId 给下游 Worker/Analytics) ----------
// ⚠️ AddCurrentUser 不在 AddServiceDefaults 里 —— 按需显式调用(见其注释)。
//    事件发布器 InterviewsIntegrationEventPublisher 构造注入 ICurrentUser,
//    不注册会让 DI 校验直接失败 → 服务起不来。
builder.Services.AddCurrentUser();

// ---------- 数据库:独占 schema "interviews" ----------
builder.Services.AddDbContext<InterviewsDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("InterviewsDb"),
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", InterviewsDbContext.Schema));
    // 拦截器顺序无所谓(职责正交):
    //   AnalysisJobOutboxInterceptor —— SavingChanges:领域事件 → 任务行(同事务);
    //   DomainEventDispatchInterceptor —— SavedChanges:领域事件 → 应用内通知。
    options.AddInterceptors(
        new AnalysisJobOutboxInterceptor(sp.GetRequiredService<ICurrentUser>()),
        new DomainEventDispatchInterceptor(sp.GetRequiredService<MediatR.IPublisher>()));
});

builder.Services.AddHealthChecks().AddDbContextCheck<InterviewsDbContext>("postgres");

// ---------- AiGateway 内部客户端(2026-10-07,照 Jobs 同名客户端模式) ----------
// 面试指导材料生成经此调 AiGateway —— 凭据只存网关一处,Interviews 不碰 key。
builder.Services.AddHttpContextAccessor();
// 票38:注册口径下沉 BuildingBlocks.AddAiGatewayClient(回落 5268、超时 3 分钟,原口径不变)。
builder.Services.AddAiGatewayClient(builder.Configuration["AiGateway:BaseUrl"]);

// ---------- 票 240：邮箱直连导入(OAuth 只读、token 加密存储、手动刷新) ----------
// Secret 一律走配置（appsettings 空占位 + 环境变量注入），绝不硬编码、不进日志。
builder.Services.Configure<MailboxOptions>(builder.Configuration.GetSection(MailboxOptions.Section));
// 票 242：密钥目录解析抽纯函数（见 MailboxKeysDirectory；单服务账号假设见其注释）。
// 显式配置 > INTERVIEW_STORAGE_DIR > 主机可写默认；旧 /app 默认在 CI 非 root runner 上建目录失败。
var mailboxKeysDir = MailboxKeysDirectory.Resolve(
    builder.Configuration["Mailbox:DataProtectionKeysDir"],
    Environment.GetEnvironmentVariable("INTERVIEW_STORAGE_DIR"));
Directory.CreateDirectory(mailboxKeysDir);
builder.Services.AddDataProtection()
    .SetApplicationName("your-interview")
    .PersistKeysToFileSystem(new DirectoryInfo(mailboxKeysDir));
builder.Services.AddHttpClient<GmailMailboxProvider>();
builder.Services.AddHttpClient<GraphMailboxProvider>();
builder.Services.AddSingleton<MailboxProtection>();
builder.Services.AddScoped<MailboxOAuthResolver>();
builder.Services.AddSingleton<IMailboxProviderFactory, MailboxProviderFactory>();

// ---------- 消息总线:转写完成 → 触发分析流水线 ----------
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration, x =>
{
    // 邀请登记 → 自动建 Playbook 草稿(M1.4);消费幂等,总线重试/重投安全
    x.AddConsumer<InterviewInviteRecordedConsumer>();
});

// ---------- 分析任务派发器:台账(Pending)→ RabbitMQ,至少一次投递 + 卡单补发 ----------
builder.Services.AddHostedService<AnalysisJobDispatcher>();

// ---------- 录音完整性巡检:文件在不在/大小/摘要,结论回写供前端警示 ----------
builder.Services.AddHostedService<AudioIntegritySweepService>();

// ---------- 鉴权 ----------
// ---------- JWT 认证(校验参数唯一定义在 ServiceDefaults,ARC-09/票18) ----------
builder.Services.AddJwtAuthentication(builder.Configuration);

builder.Services.AddAuthorization(options => PermissionPolicy.AddPermissionPolicies(options));

var app = builder.Build();

app.UseServiceDefaults("interviews-api");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync("interviews-api", InterviewsDbSeeder.MigrateAndSeedAsync);

app.Run();
