using System.Net;
using System.Net.Sockets;
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

    // 票 150:公司 logo 取数 —— 禁自动重定向(3xx 由抓取循环手动判定跟随,
    // 防重定向绕过 SSRF 校验),超时 20 秒与 JobPageFetcher 同口径;取数失败一律返回 null 不抛异常。
    // 票 249:底层再加 resolve-then-fetch pinning —— ConnectCallback 内解析 host
    // 并全量校验地址(任一内网/保留 IP 即拒),只连校验过的公网 IP,杜绝解析与
    // 连接之间的 DNS 重绑定;SNI/证书校验仍以原始 host 为准,不受 pinning 影响。
    services.AddHttpClient(YourInterview.Services.Jobs.Infrastructure.Services.CompanyLogoFetcherOptions.ClientName, http =>
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        http.Timeout = TimeSpan.FromSeconds(20);
    }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                if (YourInterview.Services.Jobs.Application.CompanyLogoRules.IsPrivateOrReservedIp(literal))
                    throw new InvalidOperationException("票249:SSRF 拒绝内网字面 IP");
                addresses = [literal];
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
                if (addresses.Length == 0
                    || addresses.Any(YourInterview.Services.Jobs.Application.CompanyLogoRules.IsPrivateOrReservedIp))
                    throw new InvalidOperationException("票249:SSRF 拒绝非公网解析结果");
            }
            var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), ct);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
            return new NetworkStream(socket, ownsSocket: true);
        },
    });
    services.AddScoped<YourInterview.Services.Jobs.Infrastructure.Services.ICompanyLogoFetcher,
        YourInterview.Services.Jobs.Infrastructure.Services.CompanyLogoFetcher>();

    // 票 249 R1:本地开源 logo 层(EmbeddedResource,懒加载一次,单例)。
    services.AddSingleton<YourInterview.Services.Jobs.Infrastructure.Services.ILocalLogoCatalog,
        YourInterview.Services.Jobs.Infrastructure.Services.LocalLogoCatalog>();

    // 票 249 R2:Wikidata 域名解析(描述性 UA,遵循 Wikimedia 礼仪,不冒充浏览器)。
    services.AddHttpClient(YourInterview.Services.Jobs.Infrastructure.Services.WikidataDomainResolverOptions.ClientName, http =>
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("your-interview/1.0 (company-logo-domain-resolution)");
        http.Timeout = TimeSpan.FromSeconds(20);
    });
    services.AddScoped<YourInterview.Services.Jobs.Infrastructure.Services.IWikidataDomainResolver,
        YourInterview.Services.Jobs.Infrastructure.Services.WikidataDomainResolver>();

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
