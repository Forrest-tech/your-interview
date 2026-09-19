using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Assessment.Domain;

/// <summary>
/// 用户级 LLM 配置(面试前准备包用)。
///
/// 与 SpeechSetting 并列,理由相同:
///   · key 绝不下发浏览器 —— 前端提交给后端保管,后端只回掩码。
///   · 生效优先级:数据库(本表,用户在设置页保存的) &gt; 环境变量 &gt; appsettings。
///   · 保存前必须真测一次(由 Handler 把关),不通过不入库。
///
/// 与 SpeechSetting 分开建表而不是复用一张:
///   Speech 是"厂商固定 + 区域"模型,LLM 是"协议 + 端点 + 模型"模型,
///   字段集不同;硬塞一张表会到处是 NULL 列,查询与校验都变脏。
///
/// 主键是 UserId(一人一条),与 SpeechSetting 一致。
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
    public string Protocol { get; private set; } = LlmProtocols.OpenAiCompatible;

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

    public void Update(string protocol, string apiKey, string? baseUrl, string model,
        string? endpoint, string? apiVersion, string? displayName)
    {
        Protocol = protocol;
        ApiKey = apiKey;
        BaseUrl = baseUrl;
        Model = model;
        Endpoint = endpoint;
        ApiVersion = apiVersion;
        DisplayName = displayName;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>协议常量。用常量而非 enum:HTTP 请求体里直接传字符串,省一层转换。</summary>
public static class LlmProtocols
{
    public const string OpenAiCompatible = "openai-compatible";
    public const string AzureOpenAi = "azure-openai";
    public const string Anthropic = "anthropic";
}
