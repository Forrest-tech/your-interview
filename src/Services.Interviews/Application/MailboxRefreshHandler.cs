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
        // 票 303：任务 scope 无 HTTP 用户，全局过滤器会 fail-closed 匹配零行；任务路径查询一律
        // IgnoreQueryFilters，用户隔离靠各查询已有的显式 UserId 谓词（绝不改过滤器定义本身）。
        var entriesQuery = db.Entries.IgnoreQueryFilters().Include(e => e.Assets).Include(e => e.Rounds)
            .Where(e => e.UserId == request.UserId);
        if (request.EntryId is { } scopedId) entriesQuery = entriesQuery.Where(e => e.Id == scopedId);
        var entries = await entriesQuery.ToListAsync(ct);
        if (request.EntryId is not null && entries.Count == 0)
            return Result.Failure<RefreshMailboxResult>(Error.NotFound("面试条目"));
        var entryById = entries.ToDictionary(e => e.Id);

        var rulesQuery = db.MailMatchRules.IgnoreQueryFilters().Where(r => r.UserId == request.UserId);
        if (request.EntryId is { } rid) rulesQuery = rulesQuery.Where(r => r.EntryId == rid);
        var rules = (await rulesQuery.ToListAsync(ct))
            .Select(r => MailboxMatching.ParseRules(r.EntryId, r.DomainsJson, r.ContactsJson,
                r.KeywordsJson, r.ExclusionsJson))
            .ToList();
        var contactMaps = await db.MailContactMaps.IgnoreQueryFilters().Where(m => m.UserId == request.UserId).ToListAsync(ct);
        var mapBySender = contactMaps.ToDictionary(
            m => m.SenderEmailLower, m => m.EntryId, StringComparer.OrdinalIgnoreCase);

        // 票 298：条目级日期界线（只算一次，贯穿整轮；全局刷新不设界线）。
        // 定义 = 该条目已导入邮件的 max MailReceivedAt；待确认队列不计入（还不是该条目的邮件）。
        DateTimeOffset? entryLatest = null;
        if (request.EntryId is { } latestEntryId)
        {
            entryLatest = await db.MailboxImportRecords.IgnoreQueryFilters()
                .Where(r => r.UserId == request.UserId && r.EntryId == latestEntryId
                    && r.MailReceivedAt != null)
                .MaxAsync(r => (DateTimeOffset?)r.MailReceivedAt, ct);
        }

        // 票 294：回填断点。续跑（起跑时已有进度行）不清 mailboxNew，否则会抹掉上一段已导入的 new 标记；
        // 新起跑与稳态跑（无进度行）清标行为与今日一致。
        var progress = await db.MailboxBackfillProgress.IgnoreQueryFilters().FirstOrDefaultAsync(
            p => p.UserId == request.UserId && p.Provider == conn.Provider
                && p.AccountEmail == conn.AccountEmail, ct);
        if (progress is null)
        {
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
        }

        if (provider is IIncrementalMailboxProvider stream)
        {
            return await HandleStreamingAsync(request, conn, stream, access, entryById, rules,
                mapBySender, progress, opts, entryLatest, ct);
        }

        // 拉取：增量游标优先；过期 → 回落窗口重拉（Gmail 404 / Graph 410 已在 provider 内转 CursorExpired）。
        // 票 298：条目级且无 latest 时强制全窗口（cursor 置 null），否则新条目锚点前的旧邮件永远捞不到；
        // 有 latest 时首轮与回落窗口同样用界线起点。
        var legacySince = entryLatest is { } lb ? BoundedWindowStart(lb)
            : DateTimeOffset.UtcNow.AddDays(-opts.FirstImportDays);
        var legacyCursor = request.EntryId is not null && entryLatest is null ? null : conn.Cursor;
        MailboxFetchResult fetch;
        try
        {
            fetch = await provider.ListNewAsync(access, legacyCursor, legacySince, ct);
            if (fetch.CursorExpired)
            {
                fetch = await provider.ListNewAsync(access, null,
                    entryLatest is { } fb ? BoundedWindowStart(fb)
                        : DateTimeOffset.UtcNow.AddDays(-opts.HistoryFallbackDays), ct);
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

        var counters = new RefreshCounters();
        var cancelled = false;

        try
        {
            foreach (var message in fetch.Messages)
            {
                ct.ThrowIfCancellationRequested();
                var r = await ProcessMessageAsync(message, fetchRaw: null, entryById, rules,
                    mapBySender, conn, provider, access, opts, request.UserId, ct);
                counters.Added += r.Added;
                counters.Pending += r.Pending;
                counters.Skipped += r.Skipped;
                counters.Failed += r.Failed;
                counters.Reasons.AddRange(r.Reasons);
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
            return Result.Success(new RefreshMailboxResult(counters.Added, counters.Pending, counters.Skipped,
                counters.Failed, [.. counters.Reasons, ex.RetryAfter is { } ra
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
        return Result.Success(new RefreshMailboxResult(counters.Added, counters.Pending, counters.Skipped,
            counters.Failed, counters.Reasons, cancelled, false, false, cursorAdvanced));
    }

    /// <summary>票 294：流式刷新计数（跨页/跨段累积；异常中断时保留已计部分）。</summary>
    private sealed class RefreshCounters
    {
        public int Added;
        public int Pending;
        public int Skipped;
        public int Failed;
        public List<string> Reasons { get; } = [];
    }

    /// <summary>
    /// 票 298：日期界线起点 = 最新邮件 UTC 日期 00:00 再减 1 天。
    /// 减 1 天覆盖两处边界不确定性：Gmail after: 的当日包容性官方未明文定义；
    /// after: 按账号时区解释日期，而本系统按 UTC 取日，时区差可达 1 天。
    /// 多取 1 天无害：导入记录去重键保证不重复导入。
    /// </summary>
    private static DateTimeOffset BoundedWindowStart(DateTimeOffset latest)
        => new(latest.UtcDateTime.Date.AddDays(-1), TimeSpan.Zero);

    private static List<string> ReadIdList(string json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    /// <summary>
    /// 票 294：单封处理（legacy 与流式共用，唯一事实源）。
    /// 匹配 → 作用域外跳过 → 强信号（fetchRaw 非空时先取原文再导入）→ 弱/歧义进待确认队列。
    /// fetchRaw 为 null = legacy（原文已在 message 上）；非 null = 流式（强信号命中时才取原文）。
    /// </summary>
    private async Task<(int Added, int Pending, int Skipped, int Failed, List<string> Reasons)> ProcessMessageAsync(
        MailboxMessage message,
        Func<string, CancellationToken, Task<string?>>? fetchRaw,
        IReadOnlyDictionary<Guid, InterviewEntry> entryById,
        IReadOnlyList<MailboxMatching.EntryRules> rules,
        IReadOnlyDictionary<string, Guid> mapBySender,
        MailboxConnection conn, IMailboxProvider provider, string access, MailboxOptions opts,
        Guid userId, CancellationToken ct)
    {
        var match = MailboxMatching.Match(message.FromEmail, message.ToEmails,
            message.Subject, message.BodyText, rules, mapBySender);
        if (match.Signal is null) return (0, 0, 0, 0, []);

        // 作用域外命中（条目级刷新时映射指向其他条目）→ 不导入不提示。
        if (match.EntryId is { } targetId && !entryById.ContainsKey(targetId)) return (0, 0, 0, 0, []);
        if (match.CandidateEntryIds.Count > 0
            && !match.CandidateEntryIds.Any(id => entryById.ContainsKey(id))) return (0, 0, 0, 0, []);

        if (match.IsStrong && match.EntryId is { } strongEntryId)
        {
            var msg = message;
            if (fetchRaw is not null)
            {
                var raw = await fetchRaw(message.ProviderMessageId, ct);
                msg = message with { RawMimeBase64Url = raw };
            }
            var entry = entryById[strongEntryId];
            var imported = await ImportStrongAsync(entry, msg, match, provider, access, opts, userId, ct);
            return (imported.Added, 0, imported.Skipped, imported.Failed, imported.Reasons);
        }

        // 弱信号 / 歧义：只进待确认队列（本行不存原文；确认时重取）。
        var alreadyPending = await db.MailboxPendingItems.IgnoreQueryFilters().AnyAsync(p =>
            p.UserId == userId && p.Provider == conn.Provider
            && p.ProviderMessageId == message.ProviderMessageId
            && p.Status == MailboxPendingStatus.Pending, ct);
        if (alreadyPending) return (0, 0, 1, 0, []);
        var alreadyImported = await db.MailboxImportRecords.IgnoreQueryFilters().AnyAsync(r =>
            r.UserId == userId && r.Provider == conn.Provider
            && r.AccountEmail == conn.AccountEmail
            && r.ProviderMessageId == message.ProviderMessageId, ct);
        if (alreadyImported) return (0, 0, 1, 0, []);
        var candidates = match.CandidateEntryIds
            .Where(id => entryById.ContainsKey(id)).ToList();
        db.MailboxPendingItems.Add(new MailboxPendingItem(userId, conn.Provider,
            conn.AccountEmail, message.ProviderMessageId, message.ProviderThreadId,
            message.FromEmail, message.FromName, message.Subject, message.ReceivedAt,
            match.Signal, match.MatchedRule,
            match.Signal == "Ambiguous" ? null : match.EntryId,
            match.Signal == "Ambiguous"
                ? JsonSerializer.Serialize(candidates) : null,
            MailboxMatching.Snippet(message.BodyText)));
        return (0, 1, 0, 0, []);
    }

    /// <summary>
    /// 票 294：流式刷新总入口。稳态（有锚点且无未完成回填）→ history deltas；
    /// 否则 → 回填（新起跑 / 断点续跑）。限流与取消返回已计计数 + 进度保留。
    /// 票 298：条目级且无 latest 时不走稳态（否则新条目锚点前的旧邮件永远捞不到），强制回填全窗口；
    /// 有 latest 时稳态 deltas 已比日期界线更窄，照旧。
    /// </summary>
    private async Task<Result<RefreshMailboxResult>> HandleStreamingAsync(
        RefreshMailboxCommand request, MailboxConnection conn, IIncrementalMailboxProvider stream,
        string access, IReadOnlyDictionary<Guid, InterviewEntry> entryById,
        IReadOnlyList<MailboxMatching.EntryRules> rules, IReadOnlyDictionary<string, Guid> mapBySender,
        MailboxBackfillProgress? progress, MailboxOptions opts, DateTimeOffset? entryLatest,
        CancellationToken ct)
    {
        var counters = new RefreshCounters();
        try
        {
            if (conn.Cursor is not null && progress is null
                && (request.EntryId is null || entryLatest is not null))
            {
                try
                {
                    return await RunSteadyAsync(request, conn, stream, access, entryById, rules,
                        mapBySender, opts, counters, ct);
                }
                catch (MailboxCursorExpiredException)
                {
                    // 锚点过期：清锚 + 回落回填（有 latest 时新起跑用日期界线，否则沿用 30 天窗口）。
                    conn.AdvanceCursor(null, DateTimeOffset.UtcNow);
                    return await RunBackfillAsync(request, conn, stream, access, entryById, rules,
                        mapBySender, null, opts.HistoryFallbackDays, opts, counters, entryLatest, ct);
                }
            }
            return await RunBackfillAsync(request, conn, stream, access, entryById, rules,
                mapBySender, progress, opts.FirstImportDays, opts, counters, entryLatest, ct);
        }
        catch (OperationCanceledException)
        {
            // 取消不阻塞已挣状态落库（逐封保存已覆盖绝大多数；此处补 token 轮换等残余）。
            await db.SaveChangesAsync(CancellationToken.None);
            return Result.Success(new RefreshMailboxResult(counters.Added, counters.Pending, counters.Skipped,
                counters.Failed, counters.Reasons, true, false, false, false));
        }
        catch (MailboxRateLimitedException ex)
        {
            await db.SaveChangesAsync(CancellationToken.None);
            return Result.Success(new RefreshMailboxResult(counters.Added, counters.Pending, counters.Skipped,
                counters.Failed, [.. counters.Reasons, ex.RetryAfter is { } ra
                    ? $"邮箱限流，请 {ra.TotalSeconds:0} 秒后重试。"
                    : "邮箱限流，请稍后重试。"],
                false, true, false, false));
        }
    }

    /// <summary>
    /// 票 294：稳态增量。history 分页收齐 deltas 再逐封处理；成功后以响应 historyId 推进 cursor；
    /// 响应无 historyId 时不推进（与今日“仅成功推进”一致）。锚点过期由上层捕获回落回填。
    /// </summary>
    private async Task<Result<RefreshMailboxResult>> RunSteadyAsync(
        RefreshMailboxCommand request, MailboxConnection conn, IIncrementalMailboxProvider stream,
        string access, IReadOnlyDictionary<Guid, InterviewEntry> entryById,
        IReadOnlyList<MailboxMatching.EntryRules> rules, IReadOnlyDictionary<string, Guid> mapBySender,
        MailboxOptions opts, RefreshCounters counters, CancellationToken ct)
    {
        var ids = new List<string>();
        string? pageToken = null;
        string? newHistoryId = null;
        do
        {
            var page = await stream.ListHistoryPageAsync(access, conn.Cursor!, pageToken, ct);
            ids.AddRange(page.Ids);
            pageToken = page.NextPageToken;
            newHistoryId = page.NewHistoryId;
        } while (pageToken is not null);

        await ProcessIdBatchAsync(ids, 0, onAfterMessage: null, request, conn, stream, access,
            entryById, rules, mapBySender, opts, counters, ct);

        var cursorAdvanced = false;
        if (newHistoryId is not null)
        {
            conn.AdvanceCursor(newHistoryId, DateTimeOffset.UtcNow);
            cursorAdvanced = true;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(new RefreshMailboxResult(counters.Added, counters.Pending, counters.Skipped,
            counters.Failed, counters.Reasons, false, false, false, cursorAdvanced));
    }

    /// <summary>
    /// 票 294：回填（新起跑 / 断点续跑 / 作用域切换重起）。
    /// 页循环：页内下标跑满 → 进下一页；token 为空 → 回填完成（anchor0 兜底 + 定锚 + 删进度行）。
    /// 完成逻辑幂等：兜底中段失败时进度行仍在，下轮从已完成页边界重进完成段（去重前置跳过已导入）。
    /// </summary>
    private async Task<Result<RefreshMailboxResult>> RunBackfillAsync(
        RefreshMailboxCommand request, MailboxConnection conn, IIncrementalMailboxProvider stream,
        string access, IReadOnlyDictionary<Guid, InterviewEntry> entryById,
        IReadOnlyList<MailboxMatching.EntryRules> rules, IReadOnlyDictionary<string, Guid> mapBySender,
        MailboxBackfillProgress? progress, int windowDays, MailboxOptions opts,
        RefreshCounters counters, DateTimeOffset? entryLatest, CancellationToken ct)
    {
        var userId = request.UserId;
        // 作用域切换：stored.Scope 非空且与本轮不同 → 丢弃重起（贵但正确；全量跑 stored.Scope == null 可续跑任何作用域）。
        if (progress is not null && progress.ScopeEntryId is not null && progress.ScopeEntryId != request.EntryId)
        {
            db.MailboxBackfillProgress.Remove(progress);
            await db.SaveChangesAsync(ct);
            progress = null;
        }
        if (progress is null)
        {
            // 票 298：有 latest 时新起跑只拉 [最新日期, 今天]（减 1 天安全垫见 BoundedWindowStart），
            // 不再全窗口重扫；无 latest 时沿用既有窗口（首次 180 天 / 锚点过期回落 30 天）。
            var windowStart = entryLatest is { } latest
                ? BoundedWindowStart(latest)
                : DateTimeOffset.UtcNow.AddDays(-windowDays);
            var startAnchor = await stream.GetProfileHistoryIdAsync(access, ct);
            progress = new MailboxBackfillProgress(userId, conn.Provider, conn.AccountEmail,
                request.EntryId, windowStart, startAnchor, "[]", 0, null);
            db.MailboxBackfillProgress.Add(progress);
            var first = await stream.ListIdsPageAsync(access, null, windowStart, ct);
            progress.StartPage(JsonSerializer.Serialize(first.Ids), first.NextPageToken);
            await db.SaveChangesAsync(ct);
        }

        while (true)
        {
            var ids = ReadIdList(progress.CurrentPageIdsJson);
            if (progress.CurrentPageIndex < ids.Count)
            {
                await ProcessIdBatchAsync(ids, progress.CurrentPageIndex,
                    nextIndex =>
                    {
                        progress.AdvanceIndex(nextIndex);
                        return Task.CompletedTask;
                    },
                    request, conn, stream, access, entryById, rules, mapBySender, opts, counters, ct);
                continue;
            }
            if (progress.NextPageToken is null) break;
            var next = await stream.ListIdsPageAsync(access, progress.NextPageToken, progress.WindowStartUtc, ct);
            progress.StartPage(JsonSerializer.Serialize(next.Ids), next.NextPageToken);
            await db.SaveChangesAsync(ct);
        }

        // 完成：anchor0 兜底（回填期间到达、页已列过的新信），再定锚、删进度行。
        var anchor = progress.StartHistoryId;
        if (anchor is not null)
        {
            try
            {
                string? catchPageToken = null;
                string? latestHistoryId = null;
                var catchIds = new List<string>();
                do
                {
                    var page = await stream.ListHistoryPageAsync(access, anchor, catchPageToken, ct);
                    catchIds.AddRange(page.Ids);
                    catchPageToken = page.NextPageToken;
                    latestHistoryId = page.NewHistoryId;
                } while (catchPageToken is not null);
                await ProcessIdBatchAsync(catchIds, 0, onAfterMessage: null,
                    request, conn, stream, access, entryById, rules, mapBySender, opts, counters, ct);
                anchor = latestHistoryId ?? await stream.GetProfileHistoryIdAsync(access, ct);
            }
            catch (MailboxCursorExpiredException)
            {
                // anchor0 极罕见过期：放弃兜底，改取 fresh 锚点定锚（回填页本身已完整）。
                anchor = await stream.GetProfileHistoryIdAsync(access, ct);
            }
        }
        else
        {
            anchor = await stream.GetProfileHistoryIdAsync(access, ct);
        }

        db.MailboxBackfillProgress.Remove(progress);
        var cursorAdvanced = false;
        if (anchor is not null)
        {
            conn.AdvanceCursor(anchor, DateTimeOffset.UtcNow);
            cursorAdvanced = true;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(new RefreshMailboxResult(counters.Added, counters.Pending, counters.Skipped,
            counters.Failed, counters.Reasons, false, false, false, cursorAdvanced));
    }

    /// <summary>
    /// 票 294：逐封批处理（回填页内段与稳态 deltas 共用）。
    /// 每封：导入记录前置去重（仅导入记录；不查待确认项，与强信号路径今日语义一致）
    /// → 取全文（404 = 信已不在，静默跳过，与旧取数后过滤一致）
    /// → 共享 ProcessMessageAsync（强信号命中时取原文）→ 逐封落库。
    /// 票 300：单封落库失败不中断整轮 —— 每封保存前快照计数与新增跟踪项；
    /// 保存失败时只摘除本封新增实体（Added），Modified 的断点推进、游标/轮换类状态刻意保留，
    /// 随下一封保存落库；计数回滚后记 failed + 内层异常原因，继续下一封。
    /// 限流与取消不捕获，由上层按已计计数返回；onAfterMessage 在每次落库前推进断点下标。
    /// </summary>
    private async Task ProcessIdBatchAsync(
        IReadOnlyList<string> ids, int startIndex, Func<int, Task>? onAfterMessage,
        RefreshMailboxCommand request, MailboxConnection conn, IIncrementalMailboxProvider stream,
        string access, IReadOnlyDictionary<Guid, InterviewEntry> entryById,
        IReadOnlyList<MailboxMatching.EntryRules> rules, IReadOnlyDictionary<string, Guid> mapBySender,
        MailboxOptions opts, RefreshCounters counters, CancellationToken ct)
    {
        for (var i = startIndex; i < ids.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var id = ids[i];
            // 票 300 快照：保存失败时只回滚本封的计数与新增实体。
            var trackedAddedBefore = db.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToHashSet();
            var (addedBefore, pendingBefore, skippedBefore, failedBefore, reasonsBefore) =
                (counters.Added, counters.Pending, counters.Skipped, counters.Failed, counters.Reasons.Count);
            string? messageSubject = null;
            var alreadyImported = await db.MailboxImportRecords.IgnoreQueryFilters().AnyAsync(r =>
                r.UserId == request.UserId && r.Provider == conn.Provider
                && r.AccountEmail == conn.AccountEmail
                && r.ProviderMessageId == id, ct);
            if (alreadyImported)
            {
                counters.Skipped++;
            }
            else
            {
                var full = await stream.GetMessageFullAsync(access, id, ct);
                if (full is not null)
                {
                    messageSubject = full.Subject;
                    var r = await ProcessMessageAsync(full,
                        (id, token) => stream.GetMessageRawAsync(access, id, token),
                        entryById, rules,
                        mapBySender, conn, stream, access, opts, request.UserId, ct);
                    counters.Added += r.Added;
                    counters.Pending += r.Pending;
                    counters.Skipped += r.Skipped;
                    counters.Failed += r.Failed;
                    counters.Reasons.AddRange(r.Reasons);
                }
            }
            if (onAfterMessage is not null) await onAfterMessage(i + 1);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 保存失败的实体仍被跟踪：不摘除则下一封保存会重抛同一插入错误。
                // 只摘本封新增（Added）；Modified 的断点推进等状态保留，随下一封保存落库。
                foreach (var tracked in db.ChangeTracker.Entries()
                    .Where(e => e.State == EntityState.Added && !trackedAddedBefore.Contains(e.Entity))
                    .ToList())
                    tracked.State = EntityState.Detached;
                counters.Added = addedBefore;
                counters.Pending = pendingBefore;
                counters.Skipped = skippedBefore;
                counters.Failed = failedBefore;
                counters.Reasons.RemoveRange(reasonsBefore, counters.Reasons.Count - reasonsBefore);
                counters.Failed++;
                var detail = ex.InnerException?.Message ?? ex.Message;
                if (detail.Length > 300) detail = detail[..300];
                var label = string.IsNullOrWhiteSpace(messageSubject) ? id : Truncate(messageSubject);
                counters.Reasons.Add($"《{label}》保存失败：{detail}");
            }
        }
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
        var entryRule = await db.MailMatchRules.IgnoreQueryFilters().FirstOrDefaultAsync(
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
        var conn = await db.MailboxConnections.IgnoreQueryFilters()
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
        var conn = await db.MailboxConnections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Status == MailboxConnectionStatus.Active, ct);
        var accountEmail = conn?.AccountEmail ?? string.Empty;
        var providerKind = conn?.Provider ?? provider.Kind;
        var already = await db.MailboxImportRecords.IgnoreQueryFilters().AnyAsync(r =>
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
            ruleLabel, sha, rawBytes.LongLength, null, message.ReceivedAt));
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
