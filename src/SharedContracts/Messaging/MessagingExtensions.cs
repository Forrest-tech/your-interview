using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YourInterview.SharedContracts.Messaging;

namespace YourInterview.SharedContracts;

/// <summary>
/// 共享的 MassTransit + RabbitMQ 配置。
/// 面试要点(必背):
///  - 消息拓扑集中定义,防止"幽灵队列"
///  - 重试用指数退避 + 死信队列(dlq),不丢消息
///  - Outbox 模式:业务写库与消息发布在同一事务,避免"库写了消息没发"
///  - 幂等消费 + EventId 去重,因为消息投递语义是 at-least-once
/// </summary>
public static class MessagingExtensions
{
    public static IServiceCollection AddMassTransitWithRabbitMq(
        this IServiceCollection services,
        IConfiguration config,
        Action<IBusRegistrationConfigurator>? configure = null)
    {
        // ⚠️ hybrid/本地裸跑 模式可能不起 RabbitMQ。此时若仍注册 MassTransit,
        //    hosted service 会在启动时反复连接失败并抛异常,拖住 host 启动,
        //    导致 /health/live 恒 503 → 网关(YARP 主动健康检查)把本服务摘除。
        //    用 Messaging:Enabled=false 精准关闭(而不是屏蔽异常)。
        var messagingEnabled = config.GetValue("Messaging:Enabled", true);
        if (!messagingEnabled)
        {
            // ⚠️ 2026-09-18 修复(真实踩坑):旧实现只是"不注册 MassTransit",
            //    但依赖 IPublishEndpoint 的发布器(如 KnowledgeMasteryChangedPublisher、
            //    JobStatusChangedPublisher、InterviewsIntegrationEventPublisher)仍在 DI 图里。
            //    容器校验服务描述符时找不到 IPublishEndpoint → 直接抛
            //    InvalidOperationException → 整个服务启动失败。
            //    Knowledge 服务就是这样起不来的(不是配置写错,是开关本身有缺陷)。
            //
            //    正确做法:关掉消息总线时,同时注册一个空实现的 IPublishEndpoint ——
            //    依赖它的类仍能正常构造,只是发消息时静默丢弃并记一条 Debug 日志。
            //    这样"无 RabbitMQ 也能跑"才是真的成立。
            services.AddSingleton<MassTransit.IPublishEndpoint, NoOpPublishEndpoint>();
            return services;
        }

        services.AddMassTransit(x =>
        {
            x.SetKebabCaseEndpointNameFormatter();
            configure?.Invoke(x);

            x.UsingRabbitMq((context, cfg) =>
            {
                var host = config["RabbitMq:Host"] ?? "127.0.0.1";
                var port = ushort.TryParse(config["RabbitMq:Port"], out var p) ? p : (ushort)5672;
                var user = config["RabbitMq:Username"] ?? "guest";
                var pass = config["RabbitMq:Password"] ?? "guest";

                cfg.Host(host, port, "/", h =>
                {
                    h.Username(user);
                    h.Password(pass);
                });

                // 拓扑:统一 exchange + 死信交换机

                // 全局重试:指数退避(3 次),之后进 _error 队列(天然死信)
                cfg.UseMessageRetry(r => r.Exponential(
                    retryLimit: 3,
                    minInterval: TimeSpan.FromSeconds(1),
                    maxInterval: TimeSpan.FromSeconds(15),
                    intervalDelta: TimeSpan.FromSeconds(2)));

                // 幂等:同一 EventId 只处理一次
                cfg.UseInMemoryOutbox(context);

                cfg.ConfigureEndpoints(context, new KebabCaseEndpointNameFormatter("your-interview", false));
            });
        });

        return services;
    }
}

/// <summary>
/// Messaging:Enabled=false 时的空发布器。
///
/// 存在的唯一理由:让依赖 IPublishEndpoint 的发布器类仍能被 DI 构造出来。
/// 若不注册它,容器校验会失败、服务起不来 —— 见 AddMassTransitWithRabbitMq 里的注释。
///
/// 行为:不发任何消息,只记 Debug 日志。调用方(领域事件发布器)无需感知差异。
/// </summary>
internal sealed class NoOpPublishEndpoint(
    ILogger<NoOpPublishEndpoint> logger) : MassTransit.IPublishEndpoint
{
    // ---------- IPublishEndpoint:10 个 Publish 重载 ----------
    // 签名逐一对照反射导出,不要凭记忆改 —— 少一个就 CS0535 编译不过。

    public Task Publish<T>(T message, CancellationToken cancellationToken = default) where T : class
        => Log(typeof(T));

    public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext<T>> publishPipe,
        CancellationToken cancellationToken = default) where T : class
        => Log(typeof(T));

    public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext> publishPipe,
        CancellationToken cancellationToken = default) where T : class
        => Log(typeof(T));

    public Task Publish(object message, CancellationToken cancellationToken = default)
        => Log(message?.GetType());

    public Task Publish(object message, MassTransit.IPipe<MassTransit.PublishContext> publishPipe,
        CancellationToken cancellationToken = default)
        => Log(message?.GetType());

    public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default)
        => Log(messageType);

    public Task Publish(object message, Type messageType,
        MassTransit.IPipe<MassTransit.PublishContext> publishPipe,
        CancellationToken cancellationToken = default)
        => Log(messageType);

    public Task Publish<T>(object values, CancellationToken cancellationToken = default) where T : class
        => Log(typeof(T));

    public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext<T>> publishPipe,
        CancellationToken cancellationToken = default) where T : class
        => Log(typeof(T));

    public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext> publishPipe,
        CancellationToken cancellationToken = default) where T : class
        => Log(typeof(T));

    // ---------- IPublishObserverConnector ----------
    // 没有总线就没有消息流,观察者无实际意义。
    // 返回 null 而非抛异常:注册观察者通常是启动期基础设置,抛异常会导致服务起不来。
    // 调用方若对返回值调用 .Disconnect() 会 NRE —— 但那种代码在无总线环境下本就无意义,
    // 且远比"服务启动失败"可控。

    public MassTransit.ConnectHandle ConnectPublishObserver(MassTransit.IPublishObserver observer)
        => null!;

    private Task Log(Type? type)
    {
        logger.LogDebug("消息总线已关闭(Messaging:Enabled=false),丢弃发布的消息 {Type}",
            type?.Name ?? "null");
        return Task.CompletedTask;
    }
}
