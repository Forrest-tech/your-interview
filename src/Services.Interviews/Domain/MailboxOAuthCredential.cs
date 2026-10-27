using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Interviews.Domain;

/// <summary>
/// 票 245：邮箱 OAuth 应用凭据（部署级，非用户级）。
/// OAuth 应用注册是部署维度的配置（一个部署对应一套 Google/Azure 应用），因此一行 = 一个
/// provider，不挂 UserId、不进用户级查询过滤器；Mailbox 页的凭据卡直接维护它。
/// ClientSecret 只存 DataProtection 加密后的密文（purpose 见 MailboxProtection），绝不进日志、
/// 不出现在任何报错回显与任何 GET 响应里。
/// 有效性语义（解析器唯一口径）：行存在 = 整组 DB 值生效；行不存在 = 环境变量兜底。
/// 保存时 ClientId + RedirectUri 必填；Secret 首次保存必填、后续留空表示沿用已存密文。
/// </summary>
public sealed class MailboxOAuthCredential : Entity
{
    private MailboxOAuthCredential() { }

    public MailboxOAuthCredential(MailboxProvider provider, string clientId,
        string encryptedClientSecret, string redirectUri)
    {
        Provider = provider;
        ClientId = clientId;
        EncryptedClientSecret = encryptedClientSecret;
        RedirectUri = redirectUri;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public MailboxProvider Provider { get; private set; }

    public string ClientId { get; private set; } = string.Empty;

    /// <summary>DataProtection 加密后的 client secret（密文）。</summary>
    public string EncryptedClientSecret { get; private set; } = string.Empty;

    public string RedirectUri { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string clientId, string encryptedClientSecret, string redirectUri)
    {
        ClientId = clientId;
        EncryptedClientSecret = encryptedClientSecret;
        RedirectUri = redirectUri;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
