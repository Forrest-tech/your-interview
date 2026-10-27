using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

// ============================ 票 245：邮箱 OAuth 凭据卡 —— 读 / 保存 / 清除 ===========================
// 凭据是部署级配置（一行 = 一个 provider，不挂用户），Mailbox 页的凭据卡直接维护它。
// Secret 写-only：任何 GET 永不返回 Secret 本身，只给 secretSaved / secretUnreadable 两个状态位。

public sealed record MailboxOAuthSettingsDto(
    string Provider, string Source, string ClientId, string RedirectUri,
    bool SecretSaved, bool SecretUnreadable);

public sealed record GetMailboxOAuthSettingsQuery
    : IRequest<Result<IReadOnlyList<MailboxOAuthSettingsDto>>>;

public sealed class GetMailboxOAuthSettingsQueryHandler(
    InterviewsDbContext db,
    MailboxOAuthResolver resolver)
    : IRequestHandler<GetMailboxOAuthSettingsQuery, Result<IReadOnlyList<MailboxOAuthSettingsDto>>>
{
    public async Task<Result<IReadOnlyList<MailboxOAuthSettingsDto>>> Handle(
        GetMailboxOAuthSettingsQuery request, CancellationToken ct)
    {
        var list = new List<MailboxOAuthSettingsDto>();
        foreach (var provider in new[] { MailboxProvider.Gmail, MailboxProvider.Graph })
        {
            var row = await db.MailboxOAuthCredentials
                .FirstOrDefaultAsync(c => c.Provider == provider, ct);
            var resolved = await resolver.ResolveAsync(provider, ct);
            if (resolved.IsSuccess)
            {
                var v = resolved.Value;
                list.Add(new MailboxOAuthSettingsDto(provider.ToString(),
                    v.Source == MailboxOAuthSource.Saved ? "saved" : "environment",
                    v.ClientId, v.RedirectUri, row is not null, false));
            }
            else if (row is not null)
            {
                // 已保存但 Secret 解密失败：仍展示行内 ClientId/RedirectUri 并标 unreadable，
                // 前端照常可 Clear（Clear 不解密），不清掉就永远修不好。
                list.Add(new MailboxOAuthSettingsDto(provider.ToString(), "saved",
                    row.ClientId, row.RedirectUri, true, true));
            }
            else
            {
                return Result.Failure<IReadOnlyList<MailboxOAuthSettingsDto>>(resolved.Error);
            }
        }
        return Result.Success<IReadOnlyList<MailboxOAuthSettingsDto>>(list);
    }
}

public sealed record SaveMailboxOAuthSettingsCommand(
    string Provider, string ClientId, string? ClientSecret, string RedirectUri)
    : IRequest<Result<MailboxOAuthSettingsDto>>;

public sealed class SaveMailboxOAuthSettingsCommandHandler(
    InterviewsDbContext db,
    MailboxProtection protection)
    : IRequestHandler<SaveMailboxOAuthSettingsCommand, Result<MailboxOAuthSettingsDto>>
{
    public async Task<Result<MailboxOAuthSettingsDto>> Handle(
        SaveMailboxOAuthSettingsCommand request, CancellationToken ct)
    {
        if (!Enum.TryParse<MailboxProvider>(request.Provider, ignoreCase: true, out var provider)
            || provider == MailboxProvider.Imap)
            return Result.Failure<MailboxOAuthSettingsDto>(
                Error.Validation("Mailbox.Provider", "邮箱类型无效"));
        if (string.IsNullOrWhiteSpace(request.ClientId)
            || string.IsNullOrWhiteSpace(request.RedirectUri))
            return Result.Failure<MailboxOAuthSettingsDto>(Error.Validation(
                "Mailbox.OAuthSettings", "Client ID 与 Redirect URI 均不能为空。"));

        var row = await db.MailboxOAuthCredentials
            .FirstOrDefaultAsync(c => c.Provider == provider, ct);
        string encrypted;
        if (!string.IsNullOrWhiteSpace(request.ClientSecret))
        {
            encrypted = protection.ProtectClientSecret(request.ClientSecret);
        }
        else if (row is not null)
        {
            // Secret 留空 = 沿用已存密文（写-only，GET 永不回填，沿用 LLM 卡模式）。
            encrypted = row.EncryptedClientSecret;
        }
        else
        {
            return Result.Failure<MailboxOAuthSettingsDto>(Error.Validation(
                "Mailbox.OAuthSettings", "首次保存必须填写 Client Secret。"));
        }

        if (row is null)
        {
            row = new MailboxOAuthCredential(provider,
                request.ClientId.Trim(), encrypted, request.RedirectUri.Trim());
            db.MailboxOAuthCredentials.Add(row);
        }
        else
        {
            row.Update(request.ClientId.Trim(), encrypted, request.RedirectUri.Trim());
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(new MailboxOAuthSettingsDto(provider.ToString(), "saved",
            row.ClientId, row.RedirectUri, true, false));
    }
}

public sealed record ClearMailboxOAuthSettingsCommand(string Provider) : IRequest<Result>;

public sealed class ClearMailboxOAuthSettingsCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ClearMailboxOAuthSettingsCommand, Result>
{
    public async Task<Result> Handle(ClearMailboxOAuthSettingsCommand request, CancellationToken ct)
    {
        if (!Enum.TryParse<MailboxProvider>(request.Provider, ignoreCase: true, out var provider)
            || provider == MailboxProvider.Imap)
            return Result.Failure(Error.Validation("Mailbox.Provider", "邮箱类型无效"));
        var row = await db.MailboxOAuthCredentials
            .FirstOrDefaultAsync(c => c.Provider == provider, ct);
        if (row is not null)
        {
            db.MailboxOAuthCredentials.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        return Result.Success();
    }
}
