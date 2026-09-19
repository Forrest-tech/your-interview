using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.AiGateway.Domain;

/// <summary>
/// 用户级 LLM 连接配置(2026-09-18 从 Assessment 抽出)。
///
/// 为什么独立成服务:
///   1. 凭据只存一处 —— 换 key 只改一个地方,不会出现"Jobs 用的还是旧 key"。
///   2. 调用方(Jobs/Interviews/Knowledge)只发"把这段 prompt 发给 LLM",
///      不关心协议/端点/回退顺序 —— 这些是实现细节,不该泄漏到业务服务。
///   3. 加新 AI 功能不再复制凭据解析逻辑(否则每加一个用途抄一遍)。
///
/// 与 SpeechSetting 分开建表而不是复用一张:
///   Speech 是"厂商固定 + 区域"模型,LLM 是"协议 + 端点 + 模型"模型,
///   字段集不同;硬塞一张表会到处是 NULL 列,查询与校验都变脏。
///
/// 主键是 UserId(一人一条)。
/// </summary>
public sealed class AiSetting
{
    private AiSetting() { }

    public AiSetting(Guid userId, string protocol, string apiKey, string? baseUrl, string model,
        string? endpoint = null, string? apiVersion = null, string? displayName = null)
    {
        UserId = userId;
        Protocol = protocol;
        ApiKey = apiKey;
        BaseUrl = baseUrl;
        Model = model;
        Endpoint = endpoint;
        ApiVersion = apiVersion;
        DisplayName = displayName;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>一人一条配置。</summary>
    public Guid UserId { get; private set; }

    /// <summary>协议类型:openai-compatible / azure-openai / anthropic。
    ///  决定用哪个客户端实现 —— 不是厂商名(DeepSeek 与 Qwen 同属 openai-compatible)。</summary>
    public string Protocol { get; private set; } = LlmProviders.OpenAiCompatible;

    /// <summary>API Key。⚠️ 只存服务端,任何接口都不返回明文。</summary>
    public string ApiKey { get; private set; } = string.Empty;

    /// <summary>协议地址(openai-compatible / anthropic 用)。不含 /chat/completions。</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>模型名(openai-compatible / anthropic)或部署名(azure-openai)。</summary>
    public string Model { get; private set; } = string.Empty;

    /// <summary>Azure OpenAI 资源端点。</summary>
    public string? Endpoint { get; private set; }

    /// <summary>Azure OpenAI api-version。</summary>
    public string? ApiVersion { get; private set; }

    /// <summary>显示名(如 "DeepSeek"),仅用于界面回显,不参与调用。</summary>
    public string? DisplayName { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CreatedAt { get; private set; }

    /// <summary>覆盖配置(用户重新保存时)。</summary>
    public void Update(string protocol, string apiKey, string? baseUrl, string model,
        string? endpoint, string? apiVersion, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(protocol)) throw new ArgumentException("protocol 不能为空", nameof(protocol));
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("model 不能为空", nameof(model));

        Protocol = protocol;
        ApiKey = apiKey;
        BaseUrl = baseUrl;
        Model = model;
        Endpoint = endpoint;
        ApiVersion = apiVersion;
        DisplayName = displayName;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>转成调用用的连接参数。key 为空时返回 null(调用方必须处理)。</summary>
    public LlmConnection? ToConnection()
        => string.IsNullOrWhiteSpace(ApiKey)
            ? null
            : new LlmConnection(Protocol, ApiKey, BaseUrl, Model, Endpoint, ApiVersion);
}
