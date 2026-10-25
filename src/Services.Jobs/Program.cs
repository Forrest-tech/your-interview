using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using YourInterview.BuildingBlocks.Ai;
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
    // 票38:注册口径下沉 BuildingBlocks.AddAiGatewayClient(回落 5268、超时 3 分钟,原口径不变)。
    services.AddAiGatewayClient(builder.Configuration["AiGateway:BaseUrl"]);

    // ---------- 招聘页面抓取(Smart paste 粘贴链接链路) ----------
    // 浏览器 UA:很多 ATS 站点对默认 HttpClient UA 返回 403。
    services.AddHttpClient(YourInterview.Services.Jobs.Infrastructure.Services.JobPageFetcherOptions.ClientName, http =>
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.Timeout = TimeSpan.FromSeconds(20);
    });
    services.AddSingleton<YourInterview.Services.Jobs.Infrastructure.Services.IJobPageFetcher,
        YourInterview.Services.Jobs.Infrastructure.Services.JobPageFetcher>();

    // 票142 R-142-11:公司简介抓取(Wikipedia + 官网 About)。
    // 描述性 UA(Wikimedia 规范要求可识别的 UA,不冒充浏览器);单腿 15 秒由抓取器内 CTS 控制。
    services.AddHttpClient(YourInterview.Services.Jobs.Infrastructure.Services.CompanyProfileFetcher.ClientName, http =>
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("your-interview/1.0 (company-profile-fetch)");
        http.Timeout = TimeSpan.FromSeconds(20);
    });
    services.AddSingleton<YourInterview.Services.Jobs.Infrastructure.Services.ICompanyProfileFetcher,
        YourInterview.Services.Jobs.Infrastructure.Services.CompanyProfileFetcher>();

    // 2026-10-19:LinkedIn 职位页抓取(Playwright，带用户登录态)
    services.AddSingleton<YourInterview.Services.Jobs.Application.LinkedInFetcher>();

    services.AddSingleton(TimeProvider.System);
    services.AddHealthChecks().AddDbContextCheck<JobsDbContext>("postgres");

    // ---------- MassTransit:把领域事件转发成跨服务集成事件到 RabbitMQ ----------
    services.AddMassTransitWithRabbitMq(builder.Configuration, x =>
    {
        // M1.5:实战机经条目上填了结果 → 回写 Tracker 轮次 Outcome(被拒自动转 Rejected)
        x.AddConsumer<InterviewOutcomeRecordedConsumer>();
        // 票114:实战机经轮次排期落库 → 回写 Tracker 投影排期 + 推进投递状态(CONF-DASH-1)
        x.AddConsumer<InterviewRoundScheduledConsumer>();
    });
});

// ---------- 当前用户(简历/求职信是用户级私有数据,按 UserId 隔离) ----------
// ⚠️ AddCurrentUser 不在 AddServiceDefaults 里 —— 按需显式调用(见其注释)。
//    JobsController 构造注入 ICurrentUser,不注册会让控制器激活直接抛
//    "Unable to resolve service for type ICurrentUser" → 500。
builder.Services.AddCurrentUser();

// ---------- JWT 认证(校验 Identity 签发的令牌,只验签不查库) ----------
// 校验参数唯一定义在 ServiceDefaults(ARC-09/票18);本服务旧有的 issuer/audience 兜底已并入统一口径。
// RequireHttpsMetadata 为本服务保留项,经 configure 回调设置(无 Authority 配置,实际不拉 OIDC 元数据)。
builder.Services.AddJwtAuthentication(builder.Configuration, options =>
{
    options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
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

// COD-14/票02:迁移失败即启动失败(fail-fast,共享处置见 ServiceDefaults)—— 不再带病启动假健康
await app.RunStartupMigrationsOrFailAsync(ServiceName, JobsDbSeeder.MigrateAndSeedAsync);

app.Run();

public partial class Program;
