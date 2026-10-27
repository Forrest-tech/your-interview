using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

/// <summary>OAuth 凭据有效来源：环境变量兜底 / 已保存的 DB 值。</summary>
public enum MailboxOAuthSource
{
    Environment,
    Saved
}

/// <summary>解析出的整组 OAuth 凭据（原子三元组，绝不跨源混用）。</summary>
public sealed record ResolvedMailboxOAuth(
    string ClientId, string ClientSecret, string RedirectUri, MailboxOAuthSource Source);

/// <summary>
/// 票 245：邮箱 OAuth 凭据唯一解析口径。
/// Connect（取授权 URL）、OAuth 回调（换 token）、断开（撤销）、刷新（换 access token）
/// 四处共用本解析，绝不各自读配置：
/// - DB 行存在 → 整组 DB 值生效；Secret 解密失败 → 失败（不回落 env、不与 env 混用）；
/// - DB 行不存在 → 环境变量三元组原样兜底（未动过的部署行为一字不变）。
/// </summary>
public sealed class MailboxOAuthResolver(
    InterviewsDbContext db,
    IOptions<MailboxOptions> options,
    MailboxProtection protection)
{
    public async Task<Result<ResolvedMailboxOAuth>> ResolveAsync(
        MailboxProvider provider, CancellationToken ct)
    {
        if (provider == MailboxProvider.Imap)
            return Result.Failure<ResolvedMailboxOAuth>(
                Error.Validation("Mailbox.Provider", "IMAP 兜底尚未落地。"));

        var row = await db.MailboxOAuthCredentials
            .FirstOrDefaultAsync(c => c.Provider == provider, ct);
        if (row is not null)
        {
            string secret;
            try { secret = protection.UnprotectClientSecret(row.EncryptedClientSecret); }
            catch
            {
                return Result.Failure<ResolvedMailboxOAuth>(Error.Validation(
                    "Mailbox.OAuthSecretUnreadable",
                    "已保存的 OAuth 凭据无法解密，请清除后重新保存。"));
            }
            return Result.Success(new ResolvedMailboxOAuth(
                row.ClientId, secret, row.RedirectUri, MailboxOAuthSource.Saved));
        }

        var opts = options.Value;
        var (clientId, clientSecret, redirectUri) = provider switch
        {
            MailboxProvider.Gmail => (opts.Google.ClientId, opts.Google.ClientSecret, opts.Google.RedirectUri),
            _ => (opts.Microsoft.ClientId, opts.Microsoft.ClientSecret, opts.Microsoft.RedirectUri)
        };
        return Result.Success(new ResolvedMailboxOAuth(
            clientId, clientSecret, redirectUri, MailboxOAuthSource.Environment));
    }
}
