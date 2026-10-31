using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

// ============================ 票 240：邮箱直连导入 —— 连接 / 规则 / 待确认 / 审计 ============================
// 刷新编排（含逐封导入）见 MailboxRefreshHandler.cs；导入单封的共享逻辑在 MailboxImportHelper 内。

public sealed record MailboxConnectionDto(Guid Id, string Provider, string AccountEmail, string Scope,
    string Status, DateTimeOffset ConnectedAt, DateTimeOffset? LastRefreshAt, bool HasCursor);

public sealed record MailboxPendingDto(Guid Id, string Provider, string AccountEmail,
    string FromEmail, string? FromName, string Subject, DateTimeOffset ReceivedAt,
    string Signal, string MatchedRule, Guid? EntryId, IReadOnlyList<Guid> CandidateEntryIds,
    string? Snippet, string Status, DateTimeOffset CreatedAt);

public sealed record MailboxImportDto(Guid Id, Guid EntryId, Guid? AssetId, string Provider,
    string AccountEmail, string ProviderMessageId, string MatchedRule, long RawMimeBytes,
    bool IsNew, DateTimeOffset ImportedAt);

public sealed record MailMatchRulesDto(Guid EntryId, IReadOnlyList<string> Domains,
    IReadOnlyList<string> Contacts, IReadOnlyList<string> Keywords, IReadOnlyList<string> Exclusions);

/// <summary>发起连接：返回授权跳转 URL（前端 window.location 跳转）。IMAP 本轮未落地 → 400。</summary>
public sealed record ConnectMailboxCommand(Guid UserId, string Provider) : IRequest<Result<string>>;

public sealed class ConnectMailboxCommandHandler(
    IMailboxProviderFactory providers,
    MailboxProtection protection,
    MailboxOAuthResolver oauth)
    : IRequestHandler<ConnectMailboxCommand, Result<string>>
{
    public async Task<Result<string>> Handle(ConnectMailboxCommand request, CancellationToken ct)
    {
        if (!Enum.TryParse<MailboxProvider>(request.Provider, ignoreCase: true, out var kind))
            return Result.Failure<string>(Error.Validation("Mailbox.Provider", "邮箱类型无效"));
        if (kind == MailboxProvider.Imap)
            return Result.Failure<string>(Error.Validation("Mailbox.Provider",
                "IMAP 兜底尚未落地（票 240 剩余项），本轮仅支持 Gmail / Outlook。"));
        // 票 245：凭据唯一解析口径（DB 三元组整组优先、env 兜底，绝不混用）。
        var resolved = await oauth.ResolveAsync(kind, ct);
        if (!resolved.IsSuccess) return Result.Failure<string>(resolved.Error);
        var (clientId, _, redirectUri, _) = resolved.Value;
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
            return Result.Failure<string>(Error.Validation("Mailbox.NotConfigured",
                $"未配置 {kind} 的 OAuth 凭据，请先在 Mailbox 页的 OAuth 凭据卡中填写。"));
        var state = protection.CreateState(request.UserId, kind);
        var url = providers.Get(kind).GetAuthorizationUrl(clientId, redirectUri, state);
        return Result.Success(url);
    }
}

/// <summary>OAuth 回调：state 解出用户 + provider → 换 token → 取账号邮箱 → 建连接（v1 一次一链：旧连接置 Inactive）。</summary>
public sealed record MailboxCallbackCommand(string Code, string State) : IRequest<Result<string>>;

public sealed class MailboxCallbackCommandHandler(
    InterviewsDbContext db,
    IMailboxProviderFactory providers,
    MailboxProtection protection,
    MailboxOAuthResolver oauth)
    : IRequestHandler<MailboxCallbackCommand, Result<string>>
{
    public async Task<Result<string>> Handle(MailboxCallbackCommand request, CancellationToken ct)
    {
        var validated = protection.ValidateState(request.State);
        if (validated is null)
            return Result.Failure<string>(Error.Validation("Mailbox.State", "授权会话已过期或无效，请重新连接。"));
        var (userId, kind) = validated.Value;
        if (kind == MailboxProvider.Imap)
            return Result.Failure<string>(Error.Validation("Mailbox.Provider", "IMAP 兜底尚未落地。"));
        // 票 245：凭据唯一解析口径（与 Connect 同一组值换 token，绝不各自读配置）。
        var resolved = await oauth.ResolveAsync(kind, ct);
        if (!resolved.IsSuccess) return Result.Failure<string>(resolved.Error);
        var (clientId, clientSecret, redirectUri, _) = resolved.Value;
        var provider = providers.Get(kind);
        MailboxTokens tokens;
        try
        {
            tokens = await provider.ExchangeCodeAsync(clientId, clientSecret, request.Code, redirectUri, ct);
        }
        catch (MailboxAuthException ex)
        {
            return Result.Failure<string>(Error.Unauthorized(ex.Message));
        }
        catch (MailboxProviderException ex)
        {
            return Result.Failure<string>(Error.Unexpected(ex.Message));
        }
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            return Result.Failure<string>(Error.Validation("Mailbox.Token",
                "未拿到 refresh token（可能未勾选离线访问）。请断开后重新连接，并在授权页确认允许离线访问。"));
        var accountEmail = await provider.GetAccountEmailAsync(tokens.AccessToken, ct);
        if (string.IsNullOrWhiteSpace(accountEmail))
            return Result.Failure<string>(Error.Validation("Mailbox.Account",
                "无法读取授权账号邮箱，请重新连接。"));
        var encrypted = protection.ProtectRefreshToken(tokens.RefreshToken);
        // 票 303：回调 AllowAnonymous、无 bearer，ambient 用户为 null；同理 IgnoreQueryFilters，
        // 用户隔离靠 state 派生的 userId 显式谓词，否则旧 Active 行查不到、重连越攒越多。
        var existing = await db.MailboxConnections.IgnoreQueryFilters()
            .Where(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active)
            .ToListAsync(ct);
        foreach (var old in existing) old.MarkInactive();
        db.MailboxConnections.Add(new MailboxConnection(userId, kind, accountEmail, tokens.Scope, encrypted));
        await db.SaveChangesAsync(ct);
        return Result.Success(accountEmail);
    }
}

/// <summary>断开：删连接行 + 尽力调 provider 撤销（Graph 无撤销端点，删 token 即失效）；已导入资源保留。</summary>
public sealed record DisconnectMailboxCommand(Guid UserId, Guid ConnectionId) : IRequest<Result>;

public sealed class DisconnectMailboxCommandHandler(
    InterviewsDbContext db,
    IMailboxProviderFactory providers,
    MailboxProtection protection,
    MailboxOAuthResolver oauth)
    : IRequestHandler<DisconnectMailboxCommand, Result>
{
    public async Task<Result> Handle(DisconnectMailboxCommand request, CancellationToken ct)
    {
        var conn = await db.MailboxConnections
            .FirstOrDefaultAsync(c => c.Id == request.ConnectionId && c.UserId == request.UserId, ct);
        if (conn is null) return Result.Failure(Error.NotFound("邮箱连接"));
        try
        {
            var refresh = protection.UnprotectRefreshToken(conn.EncryptedRefreshToken);
            // 票 245：撤销也走唯一解析口径；解析失败则跳过撤销（删行即断开）。
            var resolved = await oauth.ResolveAsync(conn.Provider, ct);
            if (resolved.IsSuccess)
            {
                var (clientId, clientSecret, _, _) = resolved.Value;
                if (clientId.Length > 0)
                    await providers.Get(conn.Provider).RevokeAsync(clientId, clientSecret, refresh, ct);
            }
        }
        catch { /* 撤销尽力而为：删行即断开 */ }
        db.MailboxConnections.Remove(conn);
        var progresses = await db.MailboxBackfillProgress
            .Where(p => p.UserId == request.UserId && p.Provider == conn.Provider
                && p.AccountEmail == conn.AccountEmail)
            .ToListAsync(ct);
        db.MailboxBackfillProgress.RemoveRange(progresses);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record GetMailboxConnectionsQuery(Guid UserId) : IRequest<Result<IReadOnlyList<MailboxConnectionDto>>>;

public sealed class GetMailboxConnectionsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetMailboxConnectionsQuery, Result<IReadOnlyList<MailboxConnectionDto>>>
{
    public async Task<Result<IReadOnlyList<MailboxConnectionDto>>> Handle(
        GetMailboxConnectionsQuery request, CancellationToken ct)
    {
        var list = await db.MailboxConnections
            .Where(c => c.UserId == request.UserId)
            .OrderByDescending(c => c.ConnectedAt)
            .Select(c => new MailboxConnectionDto(c.Id, c.Provider.ToString(), c.AccountEmail, c.Scope,
                c.Status.ToString(), c.ConnectedAt, c.LastRefreshAt, c.Cursor != null))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<MailboxConnectionDto>>(list);
    }
}

public sealed record GetMailMatchRulesQuery(Guid UserId, Guid EntryId) : IRequest<Result<MailMatchRulesDto>>;

public sealed class GetMailMatchRulesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetMailMatchRulesQuery, Result<MailMatchRulesDto>>
{
    public async Task<Result<MailMatchRulesDto>> Handle(GetMailMatchRulesQuery request, CancellationToken ct)
    {
        var rule = await db.MailMatchRules
            .FirstOrDefaultAsync(r => r.UserId == request.UserId && r.EntryId == request.EntryId, ct);
        if (rule is null)
            return Result.Success(new MailMatchRulesDto(request.EntryId, [], [], [], []));
        return Result.Success(MailboxImportHelper.ToDto(request.EntryId, rule));
    }
}

public sealed record UpdateMailMatchRulesCommand(Guid UserId, Guid EntryId,
    IReadOnlyList<string> Domains, IReadOnlyList<string> Contacts,
    IReadOnlyList<string> Keywords, IReadOnlyList<string> Exclusions) : IRequest<Result<MailMatchRulesDto>>;

public sealed class UpdateMailMatchRulesCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateMailMatchRulesCommand, Result<MailMatchRulesDto>>
{
    public async Task<Result<MailMatchRulesDto>> Handle(UpdateMailMatchRulesCommand request, CancellationToken ct)
    {
        var entry = await db.Entries.FirstOrDefaultAsync(e => e.Id == request.EntryId && e.UserId == request.UserId, ct);
        if (entry is null) return Result.Failure<MailMatchRulesDto>(Error.NotFound("面试条目"));
        var domains = request.Domains.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        var contacts = request.Contacts.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        var keywords = request.Keywords.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        var exclusions = request.Exclusions.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        var rule = await db.MailMatchRules
            .FirstOrDefaultAsync(r => r.UserId == request.UserId && r.EntryId == request.EntryId, ct);
        if (rule is null)
        {
            rule = new MailMatchRule(request.UserId, request.EntryId);
            db.MailMatchRules.Add(rule);
        }
        rule.Update(JsonSerializer.Serialize(domains), JsonSerializer.Serialize(contacts),
            JsonSerializer.Serialize(keywords), JsonSerializer.Serialize(exclusions));
        await db.SaveChangesAsync(ct);
        return Result.Success(MailboxImportHelper.ToDto(request.EntryId, rule));
    }
}

public sealed record GetMailboxPendingQuery(Guid UserId, Guid? EntryId)
    : IRequest<Result<IReadOnlyList<MailboxPendingDto>>>;

public sealed class GetMailboxPendingQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetMailboxPendingQuery, Result<IReadOnlyList<MailboxPendingDto>>>
{
    public async Task<Result<IReadOnlyList<MailboxPendingDto>>> Handle(
        GetMailboxPendingQuery request, CancellationToken ct)
    {
        var q = db.MailboxPendingItems
            .Where(p => p.UserId == request.UserId && p.Status == MailboxPendingStatus.Pending);
        if (request.EntryId is { } entryId)
            q = q.Where(p => p.EntryId == entryId || p.EntryId == null);
        var list = await q.OrderByDescending(p => p.ReceivedAt)
            .Select(p => new MailboxPendingDto(p.Id, p.Provider.ToString(), p.AccountEmail,
                p.FromEmail, p.FromName, p.Subject, p.ReceivedAt, p.Signal, p.MatchedRule, p.EntryId,
                MailboxImportHelper.ReadGuidList(p.CandidateEntryIdsJson), p.Snippet,
                p.Status.ToString(), p.CreatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<MailboxPendingDto>>(list);
    }
}

/// <summary>确认待确认项：重取原文 → 按所选条目导入（规则记 Confirmed）→ 沉淀往来人映射 → 行置 Confirmed。</summary>
public sealed record ConfirmMailboxPendingCommand(Guid UserId, Guid PendingId, Guid? EntryId)
    : IRequest<Result<Guid>>;

public sealed class ConfirmMailboxPendingCommandHandler(
    InterviewsDbContext db,
    IMailboxProviderFactory providers,
    MailboxProtection protection,
    MailboxOAuthResolver oauth,
    IOptions<MailboxOptions> options)
    : IRequestHandler<ConfirmMailboxPendingCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ConfirmMailboxPendingCommand request, CancellationToken ct)
    {
        var pending = await db.MailboxPendingItems
            .FirstOrDefaultAsync(p => p.Id == request.PendingId && p.UserId == request.UserId
                && p.Status == MailboxPendingStatus.Pending, ct);
        if (pending is null) return Result.Failure<Guid>(Error.NotFound("待确认邮件"));
        var entryId = request.EntryId ?? pending.EntryId;
        if (entryId is null)
            return Result.Failure<Guid>(Error.Validation("Mailbox.Entry", "请先选择要归档的面试条目。"));
        var entry = await db.Entries.Include(e => e.Assets).Include(e => e.Rounds)
            .FirstOrDefaultAsync(e => e.Id == entryId.Value && e.UserId == request.UserId, ct);
        if (entry is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));
        var (conn, provider, access) = await MailboxImportHelper.GetActiveAccessAsync(
            db, providers, protection, oauth, request.UserId, ct);
        if (conn is null || provider is null || access is null)
            return Result.Failure<Guid>(Error.Unauthorized("邮箱连接已失效，请重新连接。"));
        var message = await provider.GetMessageAsync(access, pending.ProviderMessageId, ct);
        if (message is null)
            return Result.Failure<Guid>(Error.NotFound("邮件原文（可能已在邮箱中删除）"));
        var result = await MailboxImportHelper.ImportMessageAsync(
            db, provider, access, options.Value, request.UserId, entry, message, "Confirmed", ct);
        if (result is null)
            return Result.Failure<Guid>(Error.Conflict("Mailbox.Duplicate", "该邮件已导入过（去重键命中）。"));
        pending.MarkConfirmed();
        var senderLower = pending.FromEmail.Trim().ToLowerInvariant();
        var map = await db.MailContactMaps
            .FirstOrDefaultAsync(m => m.UserId == request.UserId && m.SenderEmailLower == senderLower, ct);
        if (map is null)
            db.MailContactMaps.Add(new MailContactMap(request.UserId, senderLower, entryId.Value));
        else
            map.Repoint(entryId.Value);
        await db.SaveChangesAsync(ct);
        return Result.Success(result.Value);
    }
}

/// <summary>忽略待确认项：行置 Ignored + 发件地址记入该条目排除列表（已定条目时；歧义项仅忽略本行）。</summary>
public sealed record IgnoreMailboxPendingCommand(Guid UserId, Guid PendingId) : IRequest<Result>;

public sealed class IgnoreMailboxPendingCommandHandler(InterviewsDbContext db)
    : IRequestHandler<IgnoreMailboxPendingCommand, Result>
{
    public async Task<Result> Handle(IgnoreMailboxPendingCommand request, CancellationToken ct)
    {
        var pending = await db.MailboxPendingItems
            .FirstOrDefaultAsync(p => p.Id == request.PendingId && p.UserId == request.UserId
                && p.Status == MailboxPendingStatus.Pending, ct);
        if (pending is null) return Result.Failure(Error.NotFound("待确认邮件"));
        pending.MarkIgnored();
        if (pending.EntryId is { } entryId)
        {
            var rule = await db.MailMatchRules
                .FirstOrDefaultAsync(r => r.UserId == request.UserId && r.EntryId == entryId, ct);
            if (rule is null)
            {
                rule = new MailMatchRule(request.UserId, entryId);
                db.MailMatchRules.Add(rule);
            }
            var exclusions = MailboxImportHelper.ReadStrings(rule.ExclusionsJson).ToList();
            var sender = pending.FromEmail.Trim().ToLowerInvariant();
            if (!exclusions.Contains(sender)) exclusions.Add(sender);
            rule.Update(rule.DomainsJson, rule.ContactsJson, rule.KeywordsJson,
                JsonSerializer.Serialize(exclusions));
        }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record GetMailboxImportsQuery(Guid UserId, Guid? EntryId)
    : IRequest<Result<IReadOnlyList<MailboxImportDto>>>;

public sealed class GetMailboxImportsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetMailboxImportsQuery, Result<IReadOnlyList<MailboxImportDto>>>
{
    public async Task<Result<IReadOnlyList<MailboxImportDto>>> Handle(
        GetMailboxImportsQuery request, CancellationToken ct)
    {
        var q = db.MailboxImportRecords.Where(r => r.UserId == request.UserId);
        if (request.EntryId is { } entryId) q = q.Where(r => r.EntryId == entryId);
        var list = await q.OrderByDescending(r => r.ImportedAt)
            .Select(r => new MailboxImportDto(r.Id, r.EntryId, r.AssetId, r.Provider.ToString(),
                r.AccountEmail, r.ProviderMessageId, r.MatchedRule, r.RawMimeBytes, r.IsNew, r.ImportedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<MailboxImportDto>>(list);
    }
}

/// <summary>
/// 票 298：条目最新邮件日期 = 该条目已导入邮件的 max MailReceivedAt（UTC 日期字符串，与资产 meta date 同口径）。
/// 待确认队列不计入（还不是该条目的邮件）；无已导入邮件 → null。
/// </summary>
public sealed record GetMailboxLatestMailDateQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<string?>>;

public sealed class GetMailboxLatestMailDateQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetMailboxLatestMailDateQuery, Result<string?>>
{
    public async Task<Result<string?>> Handle(
        GetMailboxLatestMailDateQuery request, CancellationToken ct)
    {
        var latest = await db.MailboxImportRecords
            .Where(r => r.UserId == request.UserId && r.EntryId == request.EntryId
                && r.MailReceivedAt != null)
            .MaxAsync(r => (DateTimeOffset?)r.MailReceivedAt, ct);
        return Result.Success<string?>(latest?.UtcDateTime.ToString("yyyy-MM-dd"));
    }
}
