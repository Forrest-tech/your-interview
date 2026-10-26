// 票40(ARC-15):工厂实现原在 SharedContracts/Ai/LlmClientFactory.cs,与接口同文件;
// 现拆分——接口 ILlmClientFactory 留契约,实现迁 BuildingBlocks/Ai(票38 同构先例),方法体逐字不变。
using Microsoft.Extensions.Logging;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.BuildingBlocks.Ai;

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
