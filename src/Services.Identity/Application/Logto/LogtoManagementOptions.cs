namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票241:Logto Management API 与审批门配置(配置节 Logto:Management)。
/// 凭据一律来自环境变量注入,代码里只留占位(appsettings.json 不写真实值):
///   Logto__Management__Endpoint / __M2mClientId / __M2mClientSecret / __WebhookSigningKey。
/// </summary>
public sealed class LogtoManagementOptions
{
    public const string SectionName = "Logto:Management";

    /// <summary>Logto 核心地址,例如 http://localhost:3001(容器内用服务名,见 compose)。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Machine-to-Machine 应用的 client id(需在 Console 给它 Management API 权限)。</summary>
    public string M2mClientId { get; set; } = string.Empty;

    public string M2mClientSecret { get; set; } = string.Empty;

    /// <summary>Webhook 详情页的 Signing Key,验签 logto-signature-sha-256 用。</summary>
    public string WebhookSigningKey { get; set; } = string.Empty;

    /// <summary>对账扫表间隔分钟数,默认 15(PLAN §4.2a)。</summary>
    public int SweepIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// 审批流上线时间(ISO UTC 字符串,例 2026-10-28T00:00:00Z):扫表只看此时间之后创建的用户。
    /// 空或非法 = 全量(fail-closed:上线前在 Logto 里建的测试账号也会被门一次,管理员批准即可)。
    /// 用字符串而不用 DateTimeOffset?,是避免 compose 空环境变量在配置绑定期抛错拖垮 Identity 启动。
    /// </summary>
    public string GateLaunchedAt { get; set; } = string.Empty;

    public bool ManagementConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(M2mClientId)
        && !string.IsNullOrWhiteSpace(M2mClientSecret);

    public bool WebhookConfigured => ManagementConfigured && !string.IsNullOrWhiteSpace(WebhookSigningKey);
}
