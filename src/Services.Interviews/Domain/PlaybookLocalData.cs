using YourInterview.BuildingBlocks.Domain;

namespace YourInterview.Services.Interviews.Domain;

/// <summary>
/// STAR 故事库(COD-10b/票15)—— Playbook 列表页的故事卡,此前只存浏览器
/// localStorage(pb.starStories),清缓存/换设备即丢。本实体是 Interviews 面的落点。
///
/// 为什么独立成表而不是塞进条目:故事库是**用户级全局列表**(不属于任何一场
/// 面试),按 JD 关键词跨条目推荐 —— 与收藏 = (UserId, 目标) 的用户级关系数据
/// 同类(票14 Jobs 面 application_favorites 先例)。
///
/// ClientId = 前端本地 id('s'+Date.now()),是导入去重与前端寻址的稳定键:
/// 后端行 Id 是 Guid,但前端既有数据/交互都以 ClientId 认故事,导入与 upsert
/// 都按 (UserId, ClientId) 收口,避免导入后前端手里的 id 全部失效。
///
/// 归属(票03 口径):带 UserId,查询恒带归属谓词 + 全局过滤器。
/// </summary>
public sealed class StarStory : AuditableAggregateRoot
{
    private StarStory() { }

    public StarStory(Guid userId, string clientId, string title, string? situation,
        string? task, string? action, string? result, string? tags, int sortOrder)
    {
        UserId = userId;
        ClientId = clientId;
        Title = title;
        Situation = situation;
        Task = task;
        Action = action;
        Result = result;
        Tags = tags;
        SortOrder = sortOrder;
    }

    public Guid UserId { get; private set; }

    /// <summary>前端本地 id('s'+时间戳),导入/upsert 的去重键。</summary>
    public string ClientId { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;
    public string? Situation { get; private set; }
    public string? Task { get; private set; }
    public string? Action { get; private set; }
    public string? Result { get; private set; }
    public string? Tags { get; private set; }

    /// <summary>故事库展示顺序(= 本地数组顺序,导入时按输入序续接)。</summary>
    public int SortOrder { get; private set; }

    /// <summary>整卡更新(前端保存即整条提交,与本地 saveStory 的整条替换语义一致)。</summary>
    public void Update(string title, string? situation, string? task, string? action,
        string? result, string? tags)
    {
        Title = title;
        Situation = situation;
        Task = task;
        Action = action;
        Result = result;
        Tags = tags;
        Touch();
    }
}

/// <summary>
/// 历史复盘孤儿数据(COD-10b/票15)—— 旧详情页 #17 曾把面后复盘存
/// localStorage(pb.debrief.&lt;entryId&gt;,值 {questions, rating, stuck})。
/// 复盘功能本身已于 2026-10-18 迁到 entries.DebriefJson(现行形状 {text}),
/// v2 改版后本地键再无读写方,只剩用户浏览器里可能残留的历史数据。
///
/// 本表只做**无损归档**:原始 JSON 逐字保存、后端可查,不映射进 DebriefJson
/// (避免覆盖现行复盘、避免自创字段口径)。
///
/// 票77 起归档键改为 **EntryKey(键后缀原文)**:票15 旧口径以「后缀 = 条目
/// Guid」为前提,后缀匹配不上现存条目的孤儿键(典型成因正是条目已删)被
/// 静默跳过、永不归档。现按 (UserId, EntryKey) 收口、不再校验条目存在性:
/// 后缀能解析为 Guid 时 EntryKey 存其规范形(与旧口径的去重/读回逐字一致),
/// EntryId 列保留原值;孤儿后缀逐字存 EntryKey,EntryId = Guid.Empty 占位
/// (归档语义以 EntryKey 为准)。
/// </summary>
public sealed class LegacyDebrief : AuditableAggregateRoot
{
    private LegacyDebrief() { }

    /// <param name="entryKey">归档键(已归一:Guid 后缀 = 规范 Guid 串,其余 = 后缀原文逐字)。</param>
    public LegacyDebrief(Guid userId, string entryKey, string payloadJson)
    {
        UserId = userId;
        EntryKey = entryKey;
        EntryId = Guid.TryParse(entryKey, out var g) ? g : Guid.Empty;
        PayloadJson = payloadJson;
    }

    public Guid UserId { get; private set; }

    /// <summary>旧列(保留):Guid 后缀时 = 该 Guid,孤儿键时 = Guid.Empty。</summary>
    public Guid EntryId { get; private set; }

    /// <summary>归档键(票77):后缀原文(Guid 时为规范形),去重/读回以此为准。</summary>
    public string EntryKey { get; private set; } = string.Empty;

    /// <summary>本地原值逐字(JSON 字符串,不解析不改写)。</summary>
    public string PayloadJson { get; private set; } = string.Empty;
}

/// <summary>
/// 历史粘贴备战材料孤儿数据(COD-10b/票15)—— 旧详情页曾把粘贴的备战材料存
/// localStorage(pb-pasted-&lt;entryId&gt;,值 = 原始 Markdown 字符串),按条目分键。
/// v2 改版后本地键再无读写方,现行 Guidance 走后端材料。
///
/// 与 LegacyDebrief 同口径:原始字符串逐字无损归档、后端可查,不写现行
/// Guidance 数据。归档键同 LegacyDebrief(票77 EntryKey,见该实体注释)。
/// </summary>
public sealed class LegacyPastedMaterial : AuditableAggregateRoot
{
    private LegacyPastedMaterial() { }

    /// <param name="entryKey">归档键(已归一:Guid 后缀 = 规范 Guid 串,其余 = 后缀原文逐字)。</param>
    public LegacyPastedMaterial(Guid userId, string entryKey, string content)
    {
        UserId = userId;
        EntryKey = entryKey;
        EntryId = Guid.TryParse(entryKey, out var g) ? g : Guid.Empty;
        Content = content;
    }

    public Guid UserId { get; private set; }

    /// <summary>旧列(保留):Guid 后缀时 = 该 Guid,孤儿键时 = Guid.Empty。</summary>
    public Guid EntryId { get; private set; }

    /// <summary>归档键(票77):后缀原文(Guid 时为规范形),去重/读回以此为准。</summary>
    public string EntryKey { get; private set; } = string.Empty;

    /// <summary>本地原值逐字(原始字符串,不加工)。</summary>
    public string Content { get; private set; } = string.Empty;
}
