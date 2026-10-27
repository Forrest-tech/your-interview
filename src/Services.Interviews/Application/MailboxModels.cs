using YourInterview.Services.Interviews.Domain;

namespace YourInterview.Services.Interviews.Application;

/// <summary>票 240：邮箱直连导入 —— provider 无关的邮件模型（Gmail / Graph 统一映射到此）。</summary>
public sealed record MailboxAttachment(
    string FileName,
    string ContentType,
    long SizeBytes,
    string? ProviderAttachmentId,
    byte[]? Bytes);

/// <summary>
/// provider 拉回的单封邮件。
/// RawMimeBase64Url = 原始 MIME 全文的 base64url（Gmail format=raw / Graph $value / IMAP BODY[]），
/// 原样入库的唯一依据；RawMimeSha256 由刷新 Handler 对解码后字节算出（hash 比对验收用）。
/// </summary>
public sealed record MailboxMessage(
    string ProviderMessageId,
    string? ProviderThreadId,
    string FromEmail,
    string? FromName,
    IReadOnlyList<string> ToEmails,
    IReadOnlyList<string> CcEmails,
    string Subject,
    DateTimeOffset ReceivedAt,
    string BodyText,
    string? BodyHtml,
    string? RawMimeBase64Url,
    IReadOnlyList<MailboxAttachment> Attachments,
    string? MessageIdHeader);

/// <summary>一次拉取的结果：本页邮件 + 下一页游标（null = 拉完）。CursorExpired = 增量游标过期（须回落日期窗口）。</summary>
public sealed record MailboxFetchResult(
    IReadOnlyList<MailboxMessage> Messages,
    string? NextCursor,
    bool CursorExpired);

/// <summary>OAuth token 对：access token 只放内存；refresh token 经 DataProtection 加密后落库。</summary>
public sealed record MailboxTokens(
    string AccessToken,
    string? RefreshToken,
    string Scope,
    DateTimeOffset? ExpiresAt);

/// <summary>provider 抛出的分层异常：401/失效、429 限流、其他 provider 错误——Handler 据此分流（重连提示 / 退避 / 失败）。</summary>
public sealed class MailboxAuthException(string message) : Exception(message);

public sealed class MailboxRateLimitedException(string message, TimeSpan? retryAfter) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class MailboxProviderException(string message) : Exception(message);

/// <summary>
/// 票 240：邮箱 provider 抽象（方案 §1）。
/// Gmail / Graph 双实现；IMAP 仅兜底（本轮未落地：MailKit 不在本地 NuGet 缓存、沙盒无出站，
/// 强行手写 MIME 解析不干净——见票 240 剩余项，connect 端点对 Imap 返回 400）。
/// </summary>
public interface IMailboxProvider
{
    MailboxProvider Kind { get; }

    /// <summary>发起 OAuth：返回用户浏览器应跳转的授权 URL（state 由调用方生成并校验）。</summary>
    string GetAuthorizationUrl(string clientId, string redirectUri, string state);

    /// <summary>授权码换 token（access + refresh）。</summary>
    Task<MailboxTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code,
        string redirectUri, CancellationToken ct);

    /// <summary>refresh token 换 access token。</summary>
    Task<MailboxTokens> RefreshAccessTokenAsync(string clientId, string clientSecret,
        string refreshToken, CancellationToken ct);

    /// <summary>撤销授权（尽力而为：失败不抛，由调用方忽略）。</summary>
    Task RevokeAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct);

    /// <summary>
    /// 拉取新增邮件。
    /// cursor 有值 = 增量（Gmail historyId / Graph deltaLink）；null = 日期窗口（sinceUtc 起）。
    /// 返回的 NextCursor 只有整轮成功才由调用方落库；CursorExpired 为真时调用方须回落日期窗口重拉。
    /// </summary>
    Task<MailboxFetchResult> ListNewAsync(string accessToken, string? cursor,
        DateTimeOffset sinceUtc, CancellationToken ct);

    /// <summary>按 provider message id 重取单封（待确认队列确认时用）。</summary>
    Task<MailboxMessage?> GetMessageAsync(string accessToken, string providerMessageId,
        CancellationToken ct);

    /// <summary>取附件字节（provider 侧附件 id；超 25MB 的附件调用方不取、只记名）。</summary>
    Task<byte[]?> GetAttachmentBytesAsync(string accessToken, string providerMessageId,
        string providerAttachmentId, CancellationToken ct);
}

/// <summary>provider 工厂：按枚举取实现；Imap 抛 NotSupported（本轮未落地）。</summary>
public sealed class MailboxProviderFactory(
    GmailMailboxProvider gmail,
    GraphMailboxProvider graph) : IMailboxProviderFactory
{
    public IMailboxProvider Get(MailboxProvider kind) => kind switch
    {
        MailboxProvider.Gmail => gmail,
        MailboxProvider.Graph => graph,
        _ => throw new NotSupportedException($"IMAP 兜底尚未落地（票 240 剩余项），暂不支持 provider={kind}。")
    };
}

public interface IMailboxProviderFactory
{
    IMailboxProvider Get(MailboxProvider kind);
}
