using MassTransit;
using Polly;
using Polly.Extensions.Http;
using Serilog;
using YourInterview.Analysis.Worker.Analysis;
using YourInterview.Analysis.Worker.Consumers;
using YourInterview.SharedContracts;

var builder = Host.CreateApplicationBuilder(args);

// =====================================================================================
//  Analysis.Worker —— 后台分析服务
//
//  它**没有 HTTP 端点**(不需要对外提供服务),只做三件事:
//    1. 监听消息:等"有新的面试录音需要分析"
//    2. 跑管线:音频规整 → 转写 → 指标 → 诊断
//    3. 回写结果:把六维评分与报告写回 Interviews,并刷新 Analytics 读模型
//
//  为什么单独做成 Worker 而不是塞进 Interviews 服务:
//    分析是**重且慢**的操作(几分钟,要烧 Azure 配额),跑在 Web 进程里会
//    ① 拖垮请求响应 ② 无法独立扩缩容 ③ 重启就丢任务。
//    拆出来后:Web 只管快操作,分析可以慢慢跑、失败了重试、峰值时多开副本。
// =====================================================================================

builder.Services.AddSerilog((sp, cfg) => cfg
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] [analysis-worker] {Message:lj}{NewLine}{Exception}"));

// ---------- 管线阶段(顺序由 AnalysisPipeline 显式决定,不依赖注册顺序) ----------
builder.Services.AddSingleton<AzureSpeechClient>();
builder.Services.AddSingleton<IPipelineStage, AudioPrepareStage>();
builder.Services.AddSingleton<IPipelineStage, TranscribeStage>();
builder.Services.AddSingleton<IPipelineStage, MeasureStage>();
builder.Services.AddSingleton<IPipelineStage, DiagnoseStage>();
builder.Services.AddSingleton<AnalysisPipeline>();

// 服务间调用的令牌(Worker 用服务账号正常登录,不绕过权限模型)
builder.Services.AddSingleton<ServiceTokenProvider>();

// ---------- 回写用的 HTTP 客户端(带重试 + 熔断) ----------
// 为什么要重试:分析跑了 5 分钟,回写时对方正好重启 —— 不能因此丢掉整场分析。
builder.Services.AddHttpClient("interviews", c => c.Timeout = TimeSpan.FromSeconds(30))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))));

// Azure Speech 专用客户端:长超时(单片上传+识别可能几十秒),不走 chunked 编码
builder.Services.AddHttpClient("azure-speech", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    c.DefaultRequestVersion = System.Net.HttpVersion.Version11;
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // 连接寿命别太长 —— 长时间跑批时,陈旧连接会在上传阶段被服务端断掉
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    ConnectTimeout = TimeSpan.FromSeconds(30),
});

builder.Services.AddHttpClient("identity", c => c.Timeout = TimeSpan.FromSeconds(15))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))));

builder.Services.AddHttpClient("analytics", c => c.Timeout = TimeSpan.FromSeconds(15))
    .AddPolicyHandler(HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))));

// ---------- 消息总线 ----------
builder.Services.AddMassTransitWithRabbitMq(builder.Configuration, x =>
{
    x.AddConsumer<InterviewAnalysisRequestedConsumer>(cfg =>
    {
        // 分析很慢(几分钟),并发度要限制 —— 同时跑太多会打满 CPU 和 Azure 配额
        cfg.UseConcurrentMessageLimit(2);
    });

    x.AddConsumer<InterviewAnalysisCompletedConsumer>();
});

// ---------- 优雅关闭:分析中途收到 SIGTERM 时,等当前任务收尾 ----------
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMinutes(2));

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");
logger.LogInformation("Analysis.Worker 已启动,等待分析任务…");

await host.RunAsync();
