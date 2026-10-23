
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
