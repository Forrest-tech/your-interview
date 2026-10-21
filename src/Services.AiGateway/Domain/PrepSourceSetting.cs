namespace YourInterview.Services.AiGateway.Domain;

/// <summary>
/// 用户级「备考数据源」配置(2026-10-21)。
///
/// 备考报告生成时按公司 + 岗位自动匹配的数据源开关:
///   · 题库匹配(内部)—— 生成时带入历史原题;
///   · 公司背景(内部)—— 条目/JD 的公司介绍 + AI 整理;
///   · 网络面经 / Glassdoor 面经(外部)—— 经下方配置的搜索服务检索公开页。
///
/// 与 AiSetting 分开建表:那是 LLM 连接,这是数据源策略,字段与生命周期都不同。
/// 搜索密钥只存服务端(与 AiSetting.ApiKey 同一保管方式:本服务独占 schema、
/// 任何接口只回掩码),绝不下发浏览器明文。
/// 主键 UserId,一人一条;没有记录 = 默认口径(内部开、外部关)。
/// </summary>
public sealed class PrepSourceSetting
{
    private PrepSourceSetting() { }

    public PrepSourceSetting(Guid userId)
    {
        UserId = userId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }

    /// <summary>题库匹配(内部历史原题)。默认开。</summary>
    public bool QuestionBankEnabled { get; private set; } = true;

    /// <summary>公司背景(内部 JD/公司介绍 + AI 整理)。默认开。</summary>
    public bool CompanyBackgroundEnabled { get; private set; } = true;

    /// <summary>网络面经(外部搜索)。默认关 —— 要先配搜索服务。</summary>
    public bool WebExperiencesEnabled { get; private set; }

    /// <summary>Glassdoor 面经(外部搜索,限定 glassdoor.com)。默认关。</summary>
    public bool GlassdoorEnabled { get; private set; }

    /// <summary>搜索服务商:tavily | serper。null = 未配置。</summary>
    public string? SearchProvider { get; private set; }

    /// <summary>搜索服务 API Key。⚠️ 只存服务端,任何接口都不返回明文。</summary>
    public string? SearchApiKey { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CreatedAt { get; private set; }

    /// <summary>覆盖配置。apiKey 为 null = 保留原密钥(前端未重填时)。</summary>
    public void Update(bool questionBank, bool companyBackground, bool web, bool glassdoor,
        string? searchProvider, string? apiKey)
    {
        QuestionBankEnabled = questionBank;
        CompanyBackgroundEnabled = companyBackground;
        WebExperiencesEnabled = web;
        GlassdoorEnabled = glassdoor;
        SearchProvider = string.IsNullOrWhiteSpace(searchProvider) ? null : searchProvider.Trim().ToLowerInvariant();
        if (apiKey is not null) SearchApiKey = apiKey;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
