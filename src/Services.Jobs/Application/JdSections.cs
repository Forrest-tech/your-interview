using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  票 154：JD 小节 JSON 契约与 legacy 投影。
//
//  存储形态：SectionsJson 一列，JSON 数组，元素 {title, key, body, origin, edited}，
//  序列化用 JsonSerializerDefaults.Web（camelCase），前后端同一口径。
//  投影形态：legacy requirements / benefits 仍按今日语义从小节列表派生，
//  供旧消费者（Score legacy 视图、老记录显示）使用；真相是 JdText + 小节列表。
// ============================================================================

/// <summary>小节列表 JSON 的编解码与归一化。BCL + System.Text.Json，无项目内依赖。</summary>
public static class JdSectionJson
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredSection(string? Title, string? Key, string? Body, string? Origin, bool Edited);

    /// <summary>标题归一化（只用于 key 与投影匹配，不改原标题）：小写 + 空白折叠 + 弯引号转直引号 + 去尾冒号/句号。</summary>
    public static string NormalizeTitle(string title)
    {
        var t = title.Trim().Replace('’', '\'').Replace('‘', '\'');
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = t.TrimEnd(':', '：', '.', '。');
        return t.ToLowerInvariant();
    }

    /// <summary>正文归一化（只用于展示去重比对）：全部空白折叠为单空格 + trim。</summary>
    public static string NormalizeBody(string body)
        => Regex.Replace(body, @"\s+", " ").Trim();

    public static string Serialize(IReadOnlyList<JdSection> sections)
        => JsonSerializer.Serialize(sections, Json);

    /// <summary>
    /// 宽容解析：非法 JSON 返回 null（调用方按 legacy 处理，不 500）；
    /// 缺字段补默认（title/body 空串、key 由 title 重算、origin 缺省 auto、edited 缺省 false）。
    /// </summary>
    public static IReadOnlyList<JdSection>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        List<StoredSection>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<StoredSection>>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (raw is null) return null;
        var out_ = new List<JdSection>();
        foreach (var s in raw)
        {
            var title = (s.Title ?? string.Empty).Trim();
            var body = s.Body ?? string.Empty;
            var key = string.IsNullOrWhiteSpace(s.Key) ? NormalizeTitle(title) : s.Key;
            var origin = string.IsNullOrWhiteSpace(s.Origin) ? "auto" : s.Origin;
            out_.Add(new JdSection(title, key, body, origin, s.Edited));
        }
        return out_;
    }

    /// <summary>写路径合法性：空串视为合法（= 无小节）；非法 JSON 返回 false（调用方整单 400）。</summary>
    public static bool IsValid(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            JsonSerializer.Deserialize<List<StoredSection>>(json, Json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 写路径归一：空标题且空正文的节静默丢弃；key 一律由最终标题重算（对话框改标题后 key 不过期）；
    /// origin 只认 auto/manual，其余归一为 auto。返回规范 JSON（空列表则返回 null，语义 = 无小节）。
    /// 注意：非法 JSON 与合法但全空的列表都返回 null，调用方须先用 IsValid 区分。
    /// </summary>
    public static string? Canonicalize(string? json)
    {
        var sections = Parse(json);
        if (sections is null) return null;
        var kept = sections
            .Where(s => !string.IsNullOrWhiteSpace(s.Title) || !string.IsNullOrWhiteSpace(s.Body))
            .Select(s => new JdSection(
                s.Title.Trim(),
                NormalizeTitle(s.Title),
                s.Body,
                s.Origin == "manual" ? "manual" : "auto",
                s.Edited))
            .ToList();
        return kept.Count == 0 ? null : Serialize(kept);
    }
}

/// <summary>
/// legacy 投影：小节列表 → requirements / benefits（与今日语义逐字一致）。
/// 别名表只用于投影与分组，不作捕获关卡（捕获由通用小节判定规则负责）。
/// </summary>
public static class JdSectionProjection
{
    // requirements 族：今日 RequirementsHeaders（13 条）+ 154 新增别名。
    private static readonly HashSet<string> RequirementsFamily = new(StringComparer.Ordinal)
    {
        "requirements", "qualifications", "preferred qualifications", "minimum qualifications",
        "what you'll need", "what you bring", "what you'll bring", "must have", "nice to have",
        "key skills", "skills",
        "required qualifications & experience", "nice to have skills & experience",
        // 154：& 后缀形态的投影匹配（仅匹配时剥离，不改原标题）。
        "required qualifications", "nice to have skills",
        // 今日 RequirementsHeaders 的中文 4 条（154 初版漏抄，致中文 JD 投影为 null —— 回归修补）。
        "任职要求", "任职资格", "岗位要求", "技能要求",
    };

    // benefits 族：今日 BenefitsHeaders（9 条）+ 154 新增别名。
    private static readonly HashSet<string> BenefitsFamily = new(StringComparer.Ordinal)
    {
        "benefits", "what we offer", "perks", "perks & benefits", "compensation and benefits",
        "why join us", "additional information", "advantages",
        "what we'll bring to full-time team members",
        // 今日 BenefitsHeaders 的中文 2 条（同上回归修补）。
        "福利待遇", "薪资福利",
    };

    /// <summary>
    /// 投影匹配用 key：归一化标题 + 剥离 &amp; 后缀（&amp; duties / &amp; experience / &amp; skills / &amp; benefits）。
    /// 仅用于匹配，不改原标题、不改存储 key。
    /// </summary>
    public static string MatchKey(string key)
    {
        var m = Regex.Match(key, @"^(.*?)\s*&\s*(duties|experience|skills|benefits)$");
        return m.Success ? m.Groups[1].Value.Trim() : key;
    }

    /// <summary>
    /// 投影规则（与今日 ExtractSection 语义逐字一致）：
    /// 文档顺序第一个族内小节的正文；该节正文 &lt;10 字时返回 null（旧逻辑首命中不足即 null，不顺延）；
    /// 正文 &gt;8000 字时截断 + "…"（旧逻辑同口径）。
    /// </summary>
    public static string? Project(IReadOnlyList<JdSection> sections, bool benefits)
    {
        var family = benefits ? BenefitsFamily : RequirementsFamily;
        foreach (var s in sections)
        {
            var mk = MatchKey(s.Key);
            if (!family.Contains(mk)) continue;
            var v = s.Body.Trim();
            if (v.Length < 10) return null;
            return v.Length <= 8000 ? v : v[..8000] + "…";
        }
        return null;
    }

    public static string? ProjectRequirements(IReadOnlyList<JdSection> sections) => Project(sections, benefits: false);
    public static string? ProjectBenefits(IReadOnlyList<JdSection> sections) => Project(sections, benefits: true);
}
