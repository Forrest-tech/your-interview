using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 240：邮箱 token 保护（方案 §4）。
/// refresh token 落库前经 DataProtection 加密（purpose "mailbox-refresh-token-v1"），
/// 密钥目录走配置（默认 interviews 卷下 mailbox-keys），绝不进 git、不进日志、不出现在报错回显。
/// OAuth state 自含 userId|provider|nonce|expiryUnix，回调时比对当前用户 + 10 分钟有效期，
/// 防止 CSRF 与跨用户冒用；state 本身也经 DataProtection 保护（独立 purpose）。
/// </summary>
public sealed class MailboxProtection(IDataProtector protector)
{
    private readonly IDataProtector _tokenProtector = protector.CreateProtector("mailbox-refresh-token-v1");
    private readonly IDataProtector _stateProtector = protector.CreateProtector("mailbox-oauth-state-v1");

    public string ProtectRefreshToken(string refreshToken) => _tokenProtector.Protect(refreshToken);

    public string UnprotectRefreshToken(string protectedToken) => _tokenProtector.Unprotect(protectedToken);

    public string CreateState(Guid userId, MailboxProvider provider)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var plain = $"{userId}|{provider}|{nonce}|{expiry}";
        return _stateProtector.Protect(plain);
    }

    /// <summary>校验 state：解密失败、格式不对、过期、用户不符、provider 不符 → null（回调直接 400，不泄露原因细节）。</summary>
    public (Guid UserId, MailboxProvider Provider)? ValidateState(string state, Guid currentUserId)
    {
        string plain;
        try { plain = _stateProtector.Unprotect(state); }
        catch { return null; }
        var parts = plain.Split('|');
        if (parts.Length != 4) return null;
        if (!Guid.TryParse(parts[0], out var userId)) return null;
        if (!Enum.TryParse<MailboxProvider>(parts[1], out var provider)) return null;
        if (!long.TryParse(parts[3], out var expiry)) return null;
        if (userId != currentUserId) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return null;
        return (userId, provider);
    }
}

/// <summary>票 240：Mailbox 配置节（appsettings Mailbox；Secret 一律空占位 + 环境变量注入）。</summary>
public sealed class MailboxOptions
{
    public const string Section = "Mailbox";

    public GoogleOptions Google { get; set; } = new();
    public MicrosoftOptions Microsoft { get; set; } = new();

    /// <summary>OAuth 回调成功后前端重定向基址（默认 http://localhost:4200；compose/Mac 按部署改）。</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:4200";

    /// <summary>DataProtection 密钥目录（默认 interviews 卷下 mailbox-keys；空 = 默认目录）。</summary>
    public string? DataProtectionKeysDir { get; set; }

    /// <summary>首次导入窗口（天，默认 180）。</summary>
    public int FirstImportDays { get; set; } = 180;

    /// <summary>增量游标过期回落窗口（天，默认 30）。</summary>
    public int HistoryFallbackDays { get; set; } = 30;

    /// <summary>附件原样存储上限（字节，默认 25MB；超限只记名）。</summary>
    public long AttachmentMaxBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>附件与原文落盘根目录（默认 INTERVIEW_STORAGE_DIR 下 mail-attachments）。</summary>
    public string? AttachmentRootDir { get; set; }

    public sealed class GoogleOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }

    public sealed class MicrosoftOptions
    {
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
    }
}
