using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

// ============================ 票 240：邮箱直连导入 —— 手动刷新编排 ============================
// 语义（方案 §3.2，一字不差）：
// - 入口：Materials 页签级（全部条目规则）+ 条目级（仅该条目规则）；首发只做手动刷新，无后台同步。
// - cursor 只在整轮成功后推进；中途失败、限流、取消 → 保留旧 cursor；游标过期 → 回落窗口重拉。
// - 刷新只 INSERT：强信号自动落库、弱信号与歧义只进待确认队列、去重键命中即跳过；邮箱原样不动。

public sealed record RefreshMailboxResult(
    int Added, int Pending, int SkippedDuplicates, int Failed,
    IReadOnlyList<string> FailedReasons, bool Cancelled, bool RateLimited, bool NeedsReconnect,
    bool CursorAdvanced);

public sealed record RefreshMailboxCommand(Guid UserId, Guid? EntryId)
    : IRequest<Result<RefreshMailboxResult>>;

public sealed class RefreshMailboxCommandHandler(
    InterviewsDbContext db,
    IMailboxProviderFactory providers,
    MailboxProtection protection,
    MailboxOAuthResolver oauth,
    IOptions<MailboxOptions> options)
    : IRequestHandler<RefreshMailboxCommand, Result<RefreshMailboxResult>>
{
    public async Task<Result<RefreshMailboxResult>> Handle(RefreshMailboxCommand request, CancellationToken ct)
    {
        var opts = options.Value;
        MailboxConnection? conn;
        IMailboxProvider? provider;
        string? access;
        try
        {
            (conn, provider, access) = await MailboxImportHelper.GetActiveAccessAsync(
                db, providers, protection, oauth, request.UserId, ct);
        }
        catch (MailboxRateLimitedException ex)
        {
            return Result.Success(new RefreshMailboxResult(0, 0, 0, 0,
                [ex.RetryAfter is { } ra ? $"邮箱限流，请 {ra.TotalSeconds:0} 秒后重试。" : "邮箱限流，请稍后重试。"],
                false, true, false, false));
        }
        catch (MailboxProviderException ex)
        {
            return Result.Failure<RefreshMailboxResult>(Error.Unexpected(ex.Message));
        }
        if (conn is null || provider is null || access is null)
            return Result.Success(new RefreshMailboxResult(0, 0, 0, 0, [], false, false, true, false));

        // 作用域：条目级只认该条目规则；页签级认全部条目规则。
        var entriesQuery = db.Entries.Include(e => e.Assets).Include(e => e.Rounds)
            .Where(e => e.UserId == request.UserId);
        if (request.EntryId is { } scopedId) entriesQuery = entriesQuery.Where(e => e.Id == scopedId);
        var entries = await entriesQuery.ToListAsync(ct);
        if (request.EntryId is not null && entries.Count == 0)
            return Result.Failure<RefreshMailboxResult>(Error.NotFound("面试条目"));
        var entryById = entries.ToDictionary(e => e.Id);

        var rulesQuery = db.MailMatchRules.Where(r => r.UserId == request.UserId);
        if (request.EntryId is { } rid) rulesQuery = rulesQuery.Where(r => r.EntryId == rid);
        var rules = (await rulesQuery.ToListAsync(ct))
            .Select(r => MailboxMatching.ParseRules(r.EntryId, r.DomainsJson, r.ContactsJson,
                r.KeywordsJson, r.ExclusionsJson))
            .ToList();
        var contactMaps = await db.MailContactMaps.Where(m => m.UserId == request.UserId).ToListAsync(ct);
        var mapBySender = contactMaps.ToDictionary(
            m => m.SenderEmailLower, m => m.EntryId, StringComparer.OrdinalIgnoreCase);

        // 下次同范围刷新先清旧“新导入”标记（读 MetaJson 改写；本轮新导入的重新打标）。
        foreach (var entry in entries)
        {
            foreach (var asset in entry.Assets)
            {
                if (asset.MetaJson is null || !asset.MetaJson.Contains("\"mailboxNew\":true")) continue;
                try
                {
                    var node = JsonNode.Parse(asset.MetaJson)?.AsObject();
                    if (node is null) continue;
                    node["mailboxNew"] = false;
                    asset.SetMeta(node.ToJsonString());
                }
                catch { /* 坏 meta 不阻塞刷新 */ }
            }
        }

        // 拉取：增量游标优先；过期 → 回落窗口重拉（Gmail 404 / Graph 410 已在 provider 内转 CursorExpired）。
        MailboxFetchResult fetch;
        try
        {
            fetch = await provider.ListNewAsync(access, conn.Cursor,
                DateTimeOffset.UtcNow.AddDays(-opts.FirstImportDays), ct);
            if (fetch.CursorExpired)
            {
                fetch = await provider.ListNewAsync(access, null,
                    DateTimeOffset.UtcNow.AddDays(-opts.HistoryFallbackDays), ct);
            }
        }
        catch (MailboxAuthException)
        {
            conn.MarkInvalid();
            await db.SaveChangesAsync(ct);
            return Result.Success(new RefreshMailboxResult(0, 0, 0, 0, [], false, false, true, false));
        }
        catch (MailboxRateLimitedException ex)
        {
            await db.SaveChangesAsync(ct);
            return Result.Success(new RefreshMailboxResult(0, 0, 0, 0,
                [ex.RetryAfter is { } ra ? $"邮箱限流，请 {ra.TotalSeconds:0} 秒后重试。" : "邮箱限流，请稍后重试。"],
                false, true, false, false));
        }
        catch (OperationCanceledException)
        {
            await db.SaveChangesAsync(ct);
            return Result.Success(new RefreshMailboxResult(0, 0, 0, 0, ["刷新已取消。"], true, false, false, false));
        }

        var added = 0;
        var pending = 0;
        var skipped = 0;
        var failed = 0;
        var failedReasons = new List<string>();
        var cancelled = false;

        try
        {
            foreach (var message in fetch.Messages)
            {
                ct.ThrowIfCancellationRequested();
                var match = MailboxMatching.Match(message.FromEmail, message.ToEmails,
                    message.Subject, message.BodyText, rules, mapBySender);
                if (match.Signal is null) continue;

                // 作用域外命中（条目级刷新时映射指向其他条目）→ 不导入不提示。
                if (match.EntryId is { } targetId && !entryById.ContainsKey(targetId)) continue;
                if (match.CandidateEntryIds.Count > 0
                    && !match.CandidateEntryIds.Any(id => entryById.ContainsKey(id))) continue;

                if (match.IsStrong && match.EntryId is { } strongEntryId)
                {
                    var entry = entryById[strongEntryId];
                    var imported = await ImportStrongAsync(entry, message, match, provider, access, opts, request.UserId, ct);
                    added += imported.Added;
                    skipped += imported.Skipped;
                    failed += imported.Failed;
                    failedReasons.AddRange(imported.Reasons);
                    continue;
                }

                // 弱信号 / 歧义：只进待确认队列（本行不存原文；确认时重取）。
                var alreadyPending = await db.MailboxPendingItems.AnyAsync(p =>
                    p.UserId == request.UserId && p.Provider == conn.Provider
                    && p.ProviderMessageId == message.ProviderMessageId
                    && p.Status == MailboxPendingStatus.Pending, ct);
                if (alreadyPending) { skipped++; continue; }
                var alreadyImported = await db.MailboxImportRecords.AnyAsync(r =>
                    r.UserId == request.UserId && r.Provider == conn.Provider
                    && r.AccountEmail == conn.AccountEmail
                    && r.ProviderMessageId == message.ProviderMessageId, ct);
                if (alreadyImported) { skipped++; continue; }
                var candidates = match.CandidateEntryIds
                    .Where(id => entryById.ContainsKey(id)).ToList();
                db.MailboxPendingItems.Add(new MailboxPendingItem(request.UserId, conn.Provider,
                    conn.AccountEmail, message.ProviderMessageId, message.ProviderThreadId,
                    message.FromEmail, message.FromName, message.Subject, message.ReceivedAt,
                    match.Signal, match.MatchedRule,
                    match.Signal == "Ambiguous" ? null : match.EntryId,
                    match.Signal == "Ambiguous"
                        ? JsonSerializer.Serialize(candidates) : null,
                    MailboxMatching.Snippet(message.BodyText)));
                pending++;
            }
        }
        catch (OperationCanceledException)
        {
            // 取消 = 停拉新、已落库的保留、cursor 不推进。
            cancelled = true;
        }
        catch (MailboxRateLimitedException ex)
        {
            await db.SaveChangesAsync(ct);
            return Result.Success(new RefreshMailboxResult(added, pending, skipped, failed,
                [.. failedReasons, ex.RetryAfter is { } ra
                    ? $"邮箱限流，请 {ra.TotalSeconds:0} 秒后重试。"
                    : "邮箱限流，请稍后重试。"],
                cancelled, true, false, false));
        }

        // 只有整轮成功（未取消）才推进 cursor；日期窗口路径 NextCursor 为 null → 不推进。
        var cursorAdvanced = false;
        if (!cancelled && fetch.NextCursor is not null)
        {
            conn.AdvanceCursor(fetch.NextCursor, DateTimeOffset.UtcNow);
            cursorAdvanced = true;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(new RefreshMailboxResult(added, pending, skipped, failed,
            failedReasons, cancelled, false, false, cursorAdvanced));
    }

    /// <summary>
    /// 强信号导入：命中封 + 同线程其余封（provider 整组拉取；线程封记 "Thread:&lt;原规则&gt;"，
    /// 命中封本身记原规则）。逐封失败计 failed 继续，不中断整轮。
    /// </summary>
    private async Task<(int Added, int Skipped, int Failed, List<string> Reasons)> ImportStrongAsync(
        InterviewEntry entry, MailboxMessage hit, MailboxMatching.MatchResult match,
        IMailboxProvider provider, string access, MailboxOptions opts, Guid userId, CancellationToken ct)
    {
        var added = 0;
        var skipped = 0;
        var failed = 0;
        var reasons = new List<string>();
        var toImport = new List<(MailboxMessage Message, string Rule)>();
        toImport.Add((hit, match.MatchedRule));
        if (hit.ProviderThreadId is { } threadId)
        {
            try
            {
                var thread = await provider.GetThreadAsync(access, threadId, ct);
                foreach (var member in thread)
                {
                    if (member.ProviderMessageId == hit.ProviderMessageId) continue;
                    toImport.Add((member, $"Thread:{match.MatchedRule}"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 整组拉取失败不阻塞命中封本身 */ }
        }
        var entryRule = await db.MailMatchRules.FirstOrDefaultAsync(
            r => r.UserId == userId && r.EntryId == entry.Id, ct);
        var exclusions = entryRule is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : MailboxImportHelper.ReadStrings(entryRule.ExclusionsJson)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (message, rule) in toImport)
        {
            // 线程成员同样受排除列表约束（逐封检查，不因同组放行）；命中封本身已通过强信号，不重查。
            if (message.ProviderMessageId != hit.ProviderMessageId
                && exclusions.Contains(message.FromEmail.Trim().ToLowerInvariant()))
                continue;
            try
            {
                var assetId = await MailboxImportHelper.ImportMessageAsync(
                    db, provider, access, opts, userId, entry, message, rule, ct);
                if (assetId is null) skipped++;
                else added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                reasons.Add($"《{Truncate(message.Subject)}》导入失败：{ex.Message}");
            }
        }
        return (added, skipped, failed, reasons);
    }

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";
}

/// <summary>票 240：导入单封的共享逻辑（刷新 + 待确认确认共用；只 INSERT，绝不 UPDATE 已有资源）。</summary>
public static class MailboxImportHelper
{
    public static MailMatchRulesDto ToDto(Guid entryId, MailMatchRule rule) => new(entryId,
        ReadStrings(rule.DomainsJson), ReadStrings(rule.ContactsJson),
        ReadStrings(rule.KeywordsJson), ReadStrings(rule.ExclusionsJson));

    public static IReadOnlyList<string> ReadStrings(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    public static IReadOnlyList<Guid> ReadGuidList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch { return []; }
    }

    /// <summary>取当前 Active 连接 + 换 access token（新 refresh token 回落时轮换并落库，由调用方 SaveChanges）。</summary>
    public static async Task<(MailboxConnection? Conn, IMailboxProvider? Provider, string? Access)>
        GetActiveAccessAsync(InterviewsDbContext db, IMailboxProviderFactory providers,
            MailboxProtection protection, MailboxOAuthResolver oauth, Guid userId, CancellationToken ct)
    {
        var conn = await db.MailboxConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active, ct);
        if (conn is null) return (null, null, null);
        var provider = providers.Get(conn.Provider);
        // 票 245：刷新换 token 也走唯一解析口径；解析失败（如已保存 Secret 无法解密）→ 需重新处理，
        // 不改连接状态（token 本身未必失效，坏的是凭据配置）。
        var resolved = await oauth.ResolveAsync(conn.Provider, ct);
        if (!resolved.IsSuccess) return (conn, null, null);
        var (clientId, clientSecret, _, _) = resolved.Value;
        string refresh;
        try { refresh = protection.UnprotectRefreshToken(conn.EncryptedRefreshToken); }
        catch { conn.MarkInvalid(); await db.SaveChangesAsync(ct); return (conn, null, null); }
        try
        {
            var tokens = await provider.RefreshAccessTokenAsync(clientId, clientSecret, refresh, ct);
            if (!string.IsNullOrEmpty(tokens.RefreshToken))
                conn.RotateToken(protection.ProtectRefreshToken(tokens.RefreshToken));
            return (conn, provider, tokens.AccessToken);
        }
        catch (MailboxAuthException)
        {
            conn.MarkInvalid();
            await db.SaveChangesAsync(ct);
            return (conn, null, null);
        }
    }

    /// <summary>
    /// 导入单封（append-only）：去重键命中 → 返回 null（调用方计跳过）；
    /// 原文缺失 → 抛错（调用方计 failed，不存假原文）；
    /// 成功 → 返回新资产 id（资产 + 导入记录已挂进上下文，调用方统一 SaveChanges）。
    /// </summary>
    public static async Task<Guid?> ImportMessageAsync(InterviewsDbContext db, IMailboxProvider provider,
        string access, MailboxOptions opts, Guid userId, InterviewEntry entry, MailboxMessage message,
        string ruleLabel, CancellationToken ct)
    {
        var conn = await db.MailboxConnections
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active, ct);
        var accountEmail = conn?.AccountEmail ?? string.Empty;
        var providerKind = conn?.Provider ?? provider.Kind;
        var already = await db.MailboxImportRecords.AnyAsync(r =>
            r.UserId == userId && r.Provider == providerKind && r.AccountEmail == accountEmail
            && r.ProviderMessageId == message.ProviderMessageId, ct);
        if (already) return null;

        if (string.IsNullOrEmpty(message.RawMimeBase64Url))
            throw new MailboxProviderException("邮箱未返回原文（$value/format=raw 失败），不存假原文。");
        byte[] rawBytes;
        try { rawBytes = GmailMailboxProvider.DecodeBase64Url(message.RawMimeBase64Url); }
        catch { throw new MailboxProviderException("原文解码失败，不存假原文。"); }
        var sha = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

        var body = message.BodyText;
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.BodyHtml))
            body = StripHtml(message.BodyHtml);
        if (string.IsNullOrWhiteSpace(body)) body = message.Subject;
        if (string.IsNullOrWhiteSpace(body))
            throw new MailboxProviderException("邮件无正文内容。");
        body = body.Trim();

        var (roundId, phase) = ResolveRound(entry, message.ReceivedAt);
        var meta = new JsonObject
        {
            ["channel"] = "email",
            ["category"] = MailboxMatching.ClassifyEmail(message.Subject, body),
            ["from"] = message.FromName,
            ["fromEmail"] = message.FromEmail,
            ["subject"] = message.Subject,
            ["date"] = message.ReceivedAt.UtcDateTime.ToString("yyyy-MM-dd"),
            ["threadKey"] = MailboxMatching.NormalizeThreadKey(message.Subject),
            ["mailboxImport"] = true,
            ["mailboxNew"] = true,
            ["mailboxProvider"] = providerKind.ToString(),
            ["mailboxMessageId"] = message.ProviderMessageId,
            ["dedupeKey"] = $"{providerKind}:{accountEmail}:{message.ProviderMessageId}"
        };
        var interviewAt = MailboxMatching.ExtractInterviewWhen(body);
        if (interviewAt is not null) meta["interviewAt"] = interviewAt;
        var meetingLink = MailboxMatching.ExtractMeetingLink(body);
        if (meetingLink is not null) meta["meetingLink"] = meetingLink;
        if (message.ProviderThreadId is not null) meta["mailboxThreadId"] = message.ProviderThreadId;

        var fileName = SanitizeFileName(string.IsNullOrWhiteSpace(message.Subject)
            ? "email" : message.Subject, 100) + ".eml";
        // StoragePath 无事后修改口（private set）：传 null，原文落盘路径按约定记入 meta.rawPath
        //（mail-attachments/{assetId}/raw.eml），前端邮件卡不读 StoragePath，不影响渲染。
        var asset = entry.AttachAsset(AssetKind.Text, fileName, "message/rfc822",
            Encoding.UTF8.GetByteCount(body), null, null,
            body, null, null, "en", sha, roundId, phase, meta.ToJsonString());

        // 原文与附件落盘（asset.Id 在构造时已生成；落盘失败不阻塞入库——原文 hash 已记审计行）。
        var root = ResolveRoot(opts);
        var dir = Path.Combine(root, asset.Id.ToString());
        meta["rawPath"] = $"mail-attachments/{asset.Id}/raw.eml";
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "raw.eml"), rawBytes, ct);
            var attachments = message.Attachments.Count > 0
                ? message.Attachments
                : await provider.ListAttachmentsAsync(access, message.ProviderMessageId, ct);
            var attDir = Path.Combine(dir, "attachments");
            var stored = 0;
            var oversized = new List<string>();
            foreach (var att in attachments)
            {
                if (att.SizeBytes > opts.AttachmentMaxBytes) { oversized.Add(att.FileName); continue; }
                byte[]? bytes = att.Bytes;
                if (bytes is null && att.ProviderAttachmentId is not null)
                    bytes = await provider.GetAttachmentBytesAsync(access, message.ProviderMessageId,
                        att.ProviderAttachmentId, ct);
                if (bytes is null) { oversized.Add(att.FileName); continue; }
                Directory.CreateDirectory(attDir);
                await File.WriteAllBytesAsync(
                    Path.Combine(attDir, SanitizeFileName(att.FileName, 120)), bytes, ct);
                stored++;
            }
            meta["attachmentCount"] = stored;
            if (oversized.Count > 0)
                meta["oversizedAttachments"] = JsonSerializer.SerializeToNode(oversized);
            asset.SetMeta(meta.ToJsonString());
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 落盘失败不阻塞入库：hash 与审计行已保留，原文可随时从邮箱重取 */ }

        db.MailboxImportRecords.Add(new MailboxImportRecord(userId, entry.Id, asset.Id,
            providerKind, accountEmail, message.ProviderMessageId, message.ProviderThreadId,
            ruleLabel, sha, rawBytes.LongLength, null));
        return asset.Id;
    }

    /// <summary>轮次归属（方案 §2.2 既有 before/after 规则）：收件日最近的 ScheduledDate ≥ 收件日的轮次，否则最晚轮次，无轮次则 null。</summary>
    public static (Guid? RoundId, AssetPhase Phase) ResolveRound(InterviewEntry entry, DateTimeOffset receivedAt)
    {
        var receivedDate = DateOnly.FromDateTime(receivedAt.UtcDateTime);
        var rounds = entry.Rounds
            .Where(r => r.ScheduledDate is not null)
            .OrderBy(r => r.ScheduledDate!.Value)
            .ToList();
        var round = rounds.FirstOrDefault(r => r.ScheduledDate!.Value >= receivedDate)
            ?? rounds.LastOrDefault();
        if (round is null) return (null, AssetPhase.Pre);
        // 前端 derivePhase 同款：收件日 < 轮次日 → Pre；收件日 > 轮次日 → Post；同日邮件默认 Pre。
        var phase = receivedDate < round.ScheduledDate!.Value ? AssetPhase.Pre
            : receivedDate > round.ScheduledDate!.Value ? AssetPhase.Post : AssetPhase.Pre;
        return (round.Id, phase);
    }

    public static string ResolveRoot(MailboxOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.AttachmentRootDir)) return opts.AttachmentRootDir;
        var baseDir = Environment.GetEnvironmentVariable("INTERVIEW_STORAGE_DIR");
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = "/app/storage/interviews";
        return Path.Combine(baseDir, "mail-attachments");
    }

    private static string SanitizeFileName(string name, int maxLen)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (clean.Length == 0) clean = "email";
        return clean.Length <= maxLen ? clean : clean[..maxLen];
    }

    private static string StripHtml(string html)
    {
        var s = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|tr|li|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }
}
