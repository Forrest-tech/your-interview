using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Interviews.Domain;

/// <summary>
/// 票 240：邮箱直连导入 —— 邮箱连接的 provider。
/// Gmail / Graph 已实现；Imap 仅保留枚举位（兜底实现另案，见票 240 剩余项），
/// connect 端点对 Imap 返回 400，不静默接受。
/// </summary>
public enum MailboxProvider
{
    Gmail,
    Graph,
    Imap
}

/// <summary>邮箱连接状态：Active 正常 / Inactive 已切换（旧连接保留资源与 cursor）/ Invalid 授权失效。</summary>
public enum MailboxConnectionStatus
{
    Active,
    Inactive,
    Invalid
}

/// <summary>
/// 邮箱连接（票 240）：一行 = 一个邮箱账号的授权。
/// refresh token 只存 DataProtection 加密后的密文，绝不进日志、不出现在任何报错回显里。
/// v1 一次一链：同一用户最多一个 Active 连接；切换账号 = 旧连接置 Inactive + 新连接 Active。
/// </summary>
public sealed class MailboxConnection : Entity
{
    private MailboxConnection() { }

    public MailboxConnection(Guid userId, MailboxProvider provider, string accountEmail,
        string scope, string encryptedRefreshToken)
    {
        UserId = userId;
        Provider = provider;
        AccountEmail = accountEmail;
        Scope = scope;
        EncryptedRefreshToken = encryptedRefreshToken;
        Status = MailboxConnectionStatus.Active;
        ConnectedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public MailboxProvider Provider { get; private set; }
    public string AccountEmail { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;

    /// <summary>DataProtection 加密后的 refresh token（密文）。</summary>
    public string EncryptedRefreshToken { get; private set; } = string.Empty;

    /// <summary>
    /// 增量游标：Gmail = historyId；Graph = @odata.deltaLink；无游标 = null（首次走日期窗口）。
    /// 只有整轮刷新成功才推进（见刷新 Handler），中途失败保留旧值。
    /// </summary>
    public string? Cursor { get; private set; }

    public MailboxConnectionStatus Status { get; private set; }
    public DateTimeOffset ConnectedAt { get; private set; }
    public DateTimeOffset? LastRefreshAt { get; private set; }

    public void MarkInvalid() => Status = MailboxConnectionStatus.Invalid;
    public void MarkInactive() => Status = MailboxConnectionStatus.Inactive;
    public void Reactivate(string encryptedRefreshToken)
    {
        EncryptedRefreshToken = encryptedRefreshToken;
        Status = MailboxConnectionStatus.Active;
    }

    public void RotateToken(string encryptedRefreshToken) => EncryptedRefreshToken = encryptedRefreshToken;
    public void AdvanceCursor(string? cursor, DateTimeOffset at)
    {
        Cursor = cursor;
        LastRefreshAt = at;
    }
}

/// <summary>
/// 票 294：Gmail 回填断点（页边界续跑）。
/// 一行 = 一个账号正在进行的回填；回填完成即删行，稳态由 MailboxConnection.Cursor（historyId）承载。
/// 作用域：ScopeEntryId null = 全量跑；非 null = 条目级跑。续跑安全规则见刷新 Handler：
/// stored.Scope == run.Scope 或 stored.Scope == null 时续跑，否则丢弃重起（贵但正确）。
/// StartHistoryId = 回填起跑时取的锚点；完成时用它跑 history 兜底，闭合“回填期间到达的新信”竞态。
/// </summary>
public sealed class MailboxBackfillProgress : Entity
{
    private MailboxBackfillProgress() { }

    public MailboxBackfillProgress(Guid userId, MailboxProvider provider, string accountEmail,
        Guid? scopeEntryId, DateTimeOffset windowStartUtc, string? startHistoryId,
        string currentPageIdsJson, int currentPageIndex, string? nextPageToken)
    {
        UserId = userId;
        Provider = provider;
        AccountEmail = accountEmail;
        ScopeEntryId = scopeEntryId;
        WindowStartUtc = windowStartUtc;
        StartHistoryId = startHistoryId;
        CurrentPageIdsJson = currentPageIdsJson;
        CurrentPageIndex = currentPageIndex;
        NextPageToken = nextPageToken;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public MailboxProvider Provider { get; private set; }
    public string AccountEmail { get; private set; } = string.Empty;
    public Guid? ScopeEntryId { get; private set; }
    public DateTimeOffset WindowStartUtc { get; private set; }
    public string? StartHistoryId { get; private set; }
    public string CurrentPageIdsJson { get; private set; } = "[]";
    public int CurrentPageIndex { get; private set; }
    public string? NextPageToken { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>进入下一页：id 列表与 token 一起换位，下标归零。</summary>
    public void StartPage(string currentPageIdsJson, string? nextPageToken)
    {
        CurrentPageIdsJson = currentPageIdsJson;
        CurrentPageIndex = 0;
        NextPageToken = nextPageToken;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>页内推进：nextIndex = 下一条未处理下标。</summary>
    public void AdvanceIndex(int nextIndex)
    {
        CurrentPageIndex = nextIndex;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// 条目级邮箱匹配规则（票 240，方案 §2.3）：规则挂在条目上，随条目走，不设全局第二套。
/// 三张表 + 排除列表全部以 JSON 数组存一列；设置区不管规则（只放连接卡）。
/// </summary>
public sealed class MailMatchRule : Entity
{
    private MailMatchRule() { }

    public MailMatchRule(Guid userId, Guid entryId)
    {
        UserId = userId;
        EntryId = entryId;
        DomainsJson = "[]";
        ContactsJson = "[]";
        KeywordsJson = "[]";
        ExclusionsJson = "[]";
    }

    public Guid UserId { get; private set; }
    public Guid EntryId { get; private set; }

    /// <summary>公司域名列表（小写、无 @，如 corpay.com）。</summary>
    public string DomainsJson { get; private set; } = "[]";

    /// <summary>用户确认过的往来人邮箱列表（小写）。</summary>
    public string ContactsJson { get; private set; } = "[]";

    /// <summary>公司名关键词列表（主题/正文弱信号匹配用）。</summary>
    public string KeywordsJson { get; private set; } = "[]";

    /// <summary>条目级排除列表：被用户在待确认队列点“忽略”的发件地址。</summary>
    public string ExclusionsJson { get; private set; } = "[]";

    public void Update(string domainsJson, string contactsJson, string keywordsJson, string exclusionsJson)
    {
        DomainsJson = domainsJson;
        ContactsJson = contactsJson;
        KeywordsJson = keywordsJson;
        ExclusionsJson = exclusionsJson;
    }
}

/// <summary>
/// 往来人→条目映射（票 240，方案 §2.2）：多条目同域时用户选一次即记映射，
/// 下次同往来人自动走该条目。用户级唯一键 (UserId, SenderEmailLower)。
/// </summary>
public sealed class MailContactMap : Entity
{
    private MailContactMap() { }

    public MailContactMap(Guid userId, string senderEmailLower, Guid entryId)
    {
        UserId = userId;
        SenderEmailLower = senderEmailLower;
        EntryId = entryId;
    }

    public Guid UserId { get; private set; }
    public string SenderEmailLower { get; private set; } = string.Empty;
    public Guid EntryId { get; private set; }

    public void Repoint(Guid entryId) => EntryId = entryId;
}

/// <summary>待确认队列行状态：Pending 待确认 / Confirmed 已确认导入 / Ignored 已忽略。</summary>
public enum MailboxPendingStatus
{
    Pending,
    Confirmed,
    Ignored
}

/// <summary>
/// 待确认队列（票 240，方案 §2.1）：弱信号 C/D 与歧义（多条目同域）只进队列、永不自动落库。
/// 本行不存原文：确认时按 ProviderMessageId 向 provider 重取（重取失败则确认报错、提示刷新），
/// 避免待确认表里再存一份大 MIME。
/// </summary>
public sealed class MailboxPendingItem : Entity
{
    private MailboxPendingItem() { }

    public MailboxPendingItem(Guid userId, MailboxProvider provider, string accountEmail,
        string providerMessageId, string? providerThreadId, string fromEmail, string? fromName,
        string subject, DateTimeOffset receivedAt, string signal, string matchedRule,
        Guid? entryId, string? candidateEntryIdsJson, string? snippet)
    {
        UserId = userId;
        Provider = provider;
        AccountEmail = accountEmail;
        ProviderMessageId = providerMessageId;
        ProviderThreadId = providerThreadId;
        FromEmail = fromEmail;
        FromName = fromName;
        Subject = subject;
        ReceivedAt = receivedAt;
        Signal = signal;
        MatchedRule = matchedRule;
        EntryId = entryId;
        CandidateEntryIdsJson = candidateEntryIdsJson;
        Snippet = snippet;
        Status = MailboxPendingStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public MailboxProvider Provider { get; private set; }
    public string AccountEmail { get; private set; } = string.Empty;
    public string ProviderMessageId { get; private set; } = string.Empty;
    public string? ProviderThreadId { get; private set; }
    public string FromEmail { get; private set; } = string.Empty;
    public string? FromName { get; private set; }
    public string Subject { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>命中信号：WeakCompanyName / WeakPlatform / Ambiguous。</summary>
    public string Signal { get; private set; } = string.Empty;

    /// <summary>命中规则的可读标签（如“主题含 Corpay”“平台通知 + 公司名”“多条目同域待选”）。</summary>
    public string MatchedRule { get; private set; } = string.Empty;

    /// <summary>已定条目（弱信号命中单一条目时）；歧义时为 null，确认时由用户选。</summary>
    public Guid? EntryId { get; private set; }

    /// <summary>歧义候选条目 id 数组 JSON；非歧义时为 null。</summary>
    public string? CandidateEntryIdsJson { get; private set; }

    /// <summary>正文前 500 字摘要（仅展示用，不作原文）。</summary>
    public string? Snippet { get; private set; }

    public MailboxPendingStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void MarkConfirmed() => Status = MailboxPendingStatus.Confirmed;
    public void MarkIgnored() => Status = MailboxPendingStatus.Ignored;
}

/// <summary>
/// 导入记录（票 240，方案 §3.3 审计用）：每封成功 append 的邮件记一行。
/// 去重键 (UserId, Provider, AccountEmail, ProviderMessageId) 唯一约束——刷新命中已存在键即跳过。
/// append-only：本行永不 UPDATE/DELETE（资产软删不影响审计行）；AssetId 悬空仅表示资产已被删。
/// </summary>
public sealed class MailboxImportRecord : Entity
{
    private MailboxImportRecord() { }

    public MailboxImportRecord(Guid userId, Guid entryId, Guid? assetId, MailboxProvider provider,
        string accountEmail, string providerMessageId, string? providerThreadId, string matchedRule,
        string rawMimeSha256, long rawMimeBytes, string? cursorSnapshot,
        DateTimeOffset? mailReceivedAt = null)
    {
        UserId = userId;
        EntryId = entryId;
        AssetId = assetId;
        Provider = provider;
        AccountEmail = accountEmail;
        ProviderMessageId = providerMessageId;
        ProviderThreadId = providerThreadId;
        MatchedRule = matchedRule;
        RawMimeSha256 = rawMimeSha256;
        RawMimeBytes = rawMimeBytes;
        CursorSnapshot = cursorSnapshot;
        MailReceivedAt = mailReceivedAt;
        IsNew = true;
        ImportedAt = DateTimeOffset.UtcNow;
    }

    public Guid UserId { get; private set; }
    public Guid EntryId { get; private set; }
    public Guid? AssetId { get; private set; }
    public MailboxProvider Provider { get; private set; }
    public string AccountEmail { get; private set; } = string.Empty;
    public string ProviderMessageId { get; private set; } = string.Empty;
    public string? ProviderThreadId { get; private set; }

    /// <summary>命中规则标签：A 公司域 / B 往来人 / C 公司名 / D 平台通知 / Thread 线程同组 / Confirmed 待确认后确认。</summary>
    public string MatchedRule { get; private set; } = string.Empty;

    /// <summary>原始 MIME 的 SHA-256（十六进制），raw MIME hash 比对验收用。</summary>
    public string RawMimeSha256 { get; private set; } = string.Empty;

    public long RawMimeBytes { get; private set; }
    public string? CursorSnapshot { get; private set; }

    /// <summary>
    /// 票 298：被导入邮件本身的收件时间（MailboxMessage.ReceivedAt：Date 头，缺失时 internalDate 兜底）。
    /// 与导入时间 ImportedAt 不同：最新邮件日期 = 本字段的最大值；旧行 null 不计入。
    /// </summary>
    public DateTimeOffset? MailReceivedAt { get; private set; }

    /// <summary>“新导入”标记：本轮刷新导入的资产在 Materials 里打标；下轮同范围刷新开始时清掉旧标。</summary>
    public bool IsNew { get; private set; }

    public DateTimeOffset ImportedAt { get; private set; }

    public void ClearNew() => IsNew = false;
}
