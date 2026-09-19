using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace YourInterview.SharedContracts.Ai;

/// <summary>
/// 按协议类型构造对应的 LLM 客户端。
///
/// 为什么用工厂而不是 DI 直接注入:连接参数是**用户级运行时数据**
/// (用户在设置页填的 key/model),不是启动期配置 —— 无法在 DI 容器里单例化。
/// 工厂按需创建,HttpClient 由 IHttpClientFactory 托管连接池。
/// </summary>
public interface ILlmClientFactory
{
    /// <summary>按连接参数建客户端。参数不合法时抛 LlmCallException(IsConfigurationError=true)。</summary>
    ILlmClient Create(LlmConnection connection);
}

public sealed class LlmClientFactory(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory) : ILlmClientFactory
{
    public ILlmClient Create(LlmConnection connection)
    {
        var http = httpFactory.CreateClient("llm");
        var logger = loggerFactory.CreateLogger<OpenAiCompatibleClient>();

        return connection.Protocol switch
        {
            LlmProviders.AzureOpenAi => new AzureOpenAiClient(http, connection),
            LlmProviders.Anthropic => new AnthropicClient(http, connection),
            // 默认走 OpenAI 兼容协议 —— DeepSeek / Qwen / Ollama / 自定义端点都落这里
            _ => new OpenAiCompatibleClient(http, connection, logger)
        };
    }
}
