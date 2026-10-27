namespace YourInterview.Services.Identity.Application.Logto;

/// <summary>
/// 票241:Logto Management API 与审批门配置(配置节 Logto:Management)。
/// 凭据一律来自环境变量注入,代码里只留占位(appsettings.json 不写真实值):
///   Logto__Management__Endpoint / __M2mClientId / __M2mClientSecret / __WebhookSigningKey。
/// 票252(B8-A):Logto 1.45.0 每个 webhook hook 自动生成不同的签名 key 且拒收
/// 客户端指定,故验签须持 key 列表而非一把 key:
///   Logto__Management__WebhookSigningKeys__0 / __1 / …(每 hook 一个,须列全)。
/// 优先级(显式规则):列表中有任一非空项时,列表全量取代单 key(WebhookSigningKey
/// 被忽略,故迁移到列表时必须把全部 hook 的 key 都列入,漏列的 hook 会 401);
/// 列表全空时回落到单 key,旧部署不改配置仍能工作。
/// </summary>
public sealed class LogtoManagementOptions
{
    public const string SectionName = "Logto:Management";

    /// <summary>Logto 核心地址,例如 http://localhost:3001(容器内用服务名,见 compose)。</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// 票251 B1:Management API 的 resource indicator。Logto 1.45.0 只认租户内置的
    /// Management API indicator(默认租户即 https://default.logto.app/api)，由 Endpoint
    /// 派生的 {Endpoint}/api 会被回 400 oidc.invalid_target —— 绝不能再那样拼。
    /// 只有租户非默认时才改配本项；空 = 用默认值。
    /// </summary>
    public string ManagementApiResource { get; set; } = "https://default.logto.app/api";

    /// <summary>Machine-to-Machine 应用的 client id(需在 Console 给它 Management API 权限)。</summary>
    public string M2mClientId { get; set; } = string.Empty;

    public string M2mClientSecret { get; set; } = string.Empty;

    /// <summary>Webhook 详情页的 Signing Key,验签 logto-signature-sha-256 用。</summary>
    public string WebhookSigningKey { get; set; } = string.Empty;

    /// <summary>
    /// 票252(B8-A):全部 webhook hook 的签名 key 列表(Logto 每 hook 一把 key)。
    /// 空白项自动忽略;见类注释的优先级规则(列表非空则全量取代单 key)。
    /// </summary>
    public List<string> WebhookSigningKeys { get; set; } = [];

    /// <summary>
    /// 票252:实际用于验签的 key 列表(已去空白、去前后空格)。
    /// 注意:这不是“按事件选 key”——验签时逐 key 试(见 LogtoWebhookSignature.VerifyAny),
    /// 故单把 key 泄露可伪造任意事件类型(Panel 接受的残留,见 SETUP §5)。
    /// </summary>
    public IReadOnlyList<string> EffectiveWebhookSigningKeys()
    {
        var keys = WebhookSigningKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .ToList();
        if (keys.Count == 0 && !string.IsNullOrWhiteSpace(WebhookSigningKey))
            keys.Add(WebhookSigningKey.Trim());
        return keys;
    }

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

    public bool WebhookConfigured => ManagementConfigured && EffectiveWebhookSigningKeys().Count > 0;
}
