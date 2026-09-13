using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
