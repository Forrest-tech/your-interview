using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// 票 240：匹配规则纯函数（方案 §2.1/§2.2）。全部是确定性规则、无 AI、无外部调用——可单测。
/// 规则口径与前端 intake 一字对齐：classifyEmail、normalizeThreadSubject、extractMeetingLink、
/// extractInterviewWhen 均为前端同名函数的 C# 直译（见 web/src/app/features/playbook）。
/// </summary>
public static class MailboxMatching
{
    /// <summary>命中结果：Signal 为空 = 无信号（不导入不提示）。</summary>
    public sealed record MatchResult(
        string? Signal,
        string MatchedRule,
        bool IsStrong,
        Guid? EntryId,
        IReadOnlyList<Guid> CandidateEntryIds);

    /// <summary>条目规则快照：从 MailMatchRule 的四列 JSON 解出（全部小写归一化后比较）。</summary>
    public sealed record EntryRules(
        Guid EntryId,
        IReadOnlyList<string> Domains,
        IReadOnlyList<string> Contacts,
        IReadOnlyList<string> Keywords,
        IReadOnlyList<string> Exclusions);

    public static EntryRules ParseRules(Guid entryId, string domainsJson, string contactsJson,
        string keywordsJson, string exclusionsJson) =>
        new(entryId,
            ReadStrings(domainsJson).Select(NormalizeDomain).Where(x => x.Length > 0).ToList(),
            ReadStrings(contactsJson).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList(),
            ReadStrings(keywordsJson).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList(),
            ReadStrings(exclusionsJson).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList());

    private static IReadOnlyList<string> ReadStrings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 匹配一封邮件（方案 §2.1 顺序，精度优先、宁漏勿错）：
    /// 1) 排除列表：发件地址在任一条目排除列表里 → 无信号（永不导入、不提示）；
    /// 2) 强 A：发件域或任一收件域 ∈ 任一条目域名 → 自动导入；命中多条目 → 歧义（不猜）；
    /// 3) 强 B：发件地址 ∈ 往来人映射（用户确认过的 recruiter/面试官）→ 自动导入该条目；
    /// 4) 弱 C：主题或正文含公司名关键词 → 待确认队列；
    /// 5) 弱 D：发件域为招聘平台（indeed/linkedin 等）且主题或正文含公司名 → 待确认队列；
    /// 6) 无信号：不导入不提示。
    /// </summary>
    public static MatchResult Match(
        string fromEmail, IReadOnlyList<string> toEmails,
        string subject, string bodyText,
        IReadOnlyList<EntryRules> allRules,
        IReadOnlyDictionary<string, Guid> contactMaps)
    {
        var from = fromEmail.Trim().ToLowerInvariant();
        var fromDomain = DomainOf(from);

        // 排除优先：用户在待确认队列点过“忽略”的发件地址，对所有条目都不再出现。
        if (allRules.Any(r => r.Exclusions.Contains(from)))
            return new(null, "已排除发件人", false, null, []);

        // 强 A：公司域（发件域或任一收件域）。
        var domains = toEmails.Select(DomainOf).Append(fromDomain).Where(d => d.Length > 0).ToHashSet();
        var domainHits = allRules.Where(r => r.Domains.Any(d => domains.Contains(d))).ToList();
        if (domainHits.Count == 1)
            return new("StrongDomain", $"公司域 {domainHits[0].Domains.First(d => domains.Contains(d))}",
                true, domainHits[0].EntryId, []);
        if (domainHits.Count > 1)
            return new("Ambiguous", "多条目同域，待用户选择",
                false, null, domainHits.Select(r => r.EntryId).ToList());

        // 强 B：已知往来人（用户确认过的地址 → 条目映射）。
        if (contactMaps.TryGetValue(from, out var mappedEntry))
            return new("StrongContact", $"已知往来人 {from}", true, mappedEntry, []);

        // 弱 C/D：公司名关键词（主题或正文）。
        var haystack = $"{subject}\n{bodyText}".ToLowerInvariant();
        var platform = IsPlatformDomain(fromDomain);
        var keywordHits = allRules.Where(r => r.Keywords.Any(k => haystack.Contains(k))).ToList();
        if (keywordHits.Count == 1)
            return new(platform ? "WeakPlatform" : "WeakCompanyName",
                platform ? "招聘平台通知 + 公司名" : "主题/正文含公司名",
                false, keywordHits[0].EntryId, []);
        if (keywordHits.Count > 1)
            return new("Ambiguous", "多条目关键词命中，待用户选择",
                false, null, keywordHits.Select(r => r.EntryId).ToList());

        return new(null, "无信号", false, null, []);
    }

    public static string DomainOf(string email)
    {
        var at = email.LastIndexOf('@');
        return at >= 0 ? NormalizeDomain(email[(at + 1)..]) : string.Empty;
    }

    private static string NormalizeDomain(string domain)
    {
        var d = domain.Trim().ToLowerInvariant();
        return d.StartsWith("www.", StringComparison.Ordinal) ? d[4..] : d;
    }

    /// <summary>招聘平台通知类发件域（弱信号 D 的平台侧判定）：平台方发件 + 公司名 = 待确认，不自动。</summary>
    public static bool IsPlatformDomain(string domain) =>
        domain is "indeed.com" or "linkedin.com" or "glassdoor.com" or "monster.com"
            or "ziprecruiter.com" or "dice.com" or "jobbank.gc.ca" or "workopolis.com"
            || domain.EndsWith(".indeed.com", StringComparison.Ordinal)
            || domain.EndsWith(".linkedin.com", StringComparison.Ordinal)
            || domain.EndsWith(".glassdoor.com", StringComparison.Ordinal);

    /// <summary>前端 normalizeThreadSubject 的 C# 直译：压缩空白、最多剥 6 层回复/转发前缀，key = 小写。</summary>
    public static string NormalizeThreadKey(string? subject)
    {
        var s = Regex.Replace(subject ?? string.Empty, @"\s+", " ").Trim();
        for (var i = 0; i < 6; i++)
        {
            var m = Regex.Match(s, @"^(re|fwd|fw|回复|转发|答复)\s*[:：]\s*", RegexOptions.IgnoreCase);
            if (!m.Success) break;
            s = s[m.Length..].Trim();
        }
        return s.ToLowerInvariant();
    }

    /// <summary>前端 classifyEmail 的 C# 直译（优先级：改期 > 拒绝 > 邀请 > 跟进 > 其他）。
    /// 票 310：邀请分支收紧。旧规则对主题/正文任意一处出现 interview/schedule 即判邀请，
    /// 周报类邮件只因提及面试即被错打邀请标签。新规则（与前端一字对齐）：
    /// 1) 主题为空（粘贴/OCR 正文-only）时走旧正文关键词规则（票 158 体例：参与人口吻亦判邀请），
    ///    但裸 schedule 不再单独触发；空主题不受群发 veto；
    /// 2) 主题非空时算四信号：(a) 主题指示性（interview/面试且无周报标记，或邀约明示词）、
    ///    (b) 正文邀约言语行为、(c) 正文可解析面试时间、(d) 正文可解析会议链接；
    ///    邀约明示主题（(a) 第二款）本身即充分——否则既有断言（"Interview invitation"/"please confirm"）
    ///    仅得 1 分会被新阈值挡掉；其余须总分 ≥ 2 且 (a) 或 (b) 成立；
    /// 3) 群发 veto：主题非指示性且正文含群发标记（unsubscribe/退订/浏览器查看等）时跳过邀请
    ///    分支（继续跟进/其他）；主题指示性邀约永不受 veto；顺序：拒绝 → veto → 邀请阈值；
    /// 4) 裸 schedule 在任何路径均不再单独触发邀请。改期/拒绝/跟进分支一字未动。
    /// 已知残留见台账票 310 条目（非实现缺口，是本组合的取舍区）。</summary>
    public static string ClassifyEmail(string subject, string body)
    {
        var s = $"{subject}\n{body}".ToLowerInvariant();
        if (Regex.IsMatch(s, @"reschedule|postpone|改期|重新安排|another time")) return "reschedule";
        if (Regex.IsMatch(s, @"regret|unfortunately|not (be )?moving forward|未通过|很抱歉|遗憾地通知|other candidates")) return "rejection";

        var subjectText = (subject ?? string.Empty).Trim();
        if (subjectText.Length == 0)
        {
            // 票 310 fallback：正文-only 时沿用旧正文关键词规则（不含裸 schedule）。
            if (Regex.IsMatch((body ?? string.Empty).ToLowerInvariant(),
                    @"invit|interview|安排面试|邀请您参加|book your|confirm your interview")) return "invitation";
        }
        else
        {
            var subjectLower = subjectText.ToLowerInvariant();
            var bodyLower = (body ?? string.Empty).ToLowerInvariant();
            var roundup = Regex.IsMatch(subjectLower,
                @"this week|upcoming|digest|summary|roundup|newsletter|本周|下周|汇总|周报|upcoming meetings");
            var explicitSubject = Regex.IsMatch(subjectLower,
                @"invit|面邀|面试邀请|邀请您参加|interview invitation|interview request");
            var subjectIndicative = !roundup && (explicitSubject || Regex.IsMatch(subjectLower, @"interview|面试"));
            var explicitInvitationSubject = !roundup && explicitSubject;
            var speechAct = Regex.IsMatch(bodyLower,
                @"we would like to invite you|you are invited to (an? )?interview|we invite you to interview|邀请您参加|诚邀您参加");
            var score = (subjectIndicative ? 1 : 0) + (speechAct ? 1 : 0)
                + (ExtractInterviewWhen(body) is not null ? 1 : 0)
                + (ExtractMeetingLink(body) is not null ? 1 : 0);
            var vetoed = !subjectIndicative && Regex.IsMatch(bodyLower,
                @"unsubscribe|退订|取消订阅|view in browser|在浏览器中查看|you received this (email )?because|您收到此邮件是因为");
            if (!vetoed && (explicitInvitationSubject || (score >= 2 && (subjectIndicative || speechAct))))
                return "invitation";
        }

        if (Regex.IsMatch(s, @"follow ?up|checking in|跟进|想确认|touching base")) return "followup";
        return "other";
    }

    /// <summary>前端 extractMeetingLink 的 C# 直译：首个 Teams/Zoom/Meet 链接（末尾标点已剥离）。</summary>
    public static string? ExtractMeetingLink(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (Match m in Regex.Matches(text, @"https?://[^\s<>""'\]\)]+"))
        {
            var url = Regex.Replace(m.Value, @"[.,;:!?'"")\]}>，。；：！？、]+$", string.Empty);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host.ToLowerInvariant();
            var isTeams = host is "teams.microsoft.com" or "teams.live.com"
                || host.EndsWith(".teams.microsoft.com", StringComparison.Ordinal)
                || host.EndsWith(".teams.live.com", StringComparison.Ordinal);
            var isZoom = host is "zoom.us" or "zoom.com"
                || host.EndsWith(".zoom.us", StringComparison.Ordinal)
                || host.EndsWith(".zoom.com", StringComparison.Ordinal);
            var isMeet = host is "meet.google.com" || host.EndsWith(".meet.google.com", StringComparison.Ordinal);
            if (isTeams || isZoom || isMeet) return url;
        }
        return null;
    }

    /// <summary>
    /// 前端 extractInterviewWhen 的 C# 直译（三路：中文年月日 / ISO / 英文月名，全部过日历合法性校验，
    /// 非法日期回落 null）。输出 "yyyy-MM-dd HH:mm"（24 小时制）；无时间或无日期 → null。
    /// </summary>
    public static string? ExtractInterviewWhen(string text)
    {
        var p = (int n) => n.ToString("D2", CultureInfo.InvariantCulture);
        var cn = Regex.Match(text, @"(20\d{2})年(\d{1,2})月(\d{1,2})日\s*(?:(?:星期|周)[一二三四五六日天]\s*)?(\d{1,2})[:：](\d{2})");
        if (cn.Success)
        {
            if (!IsValidDate(int.Parse(cn.Groups[1].Value), int.Parse(cn.Groups[2].Value), int.Parse(cn.Groups[3].Value)))
                return null;
            return $"{cn.Groups[1].Value}-{p(int.Parse(cn.Groups[2].Value))}-{p(int.Parse(cn.Groups[3].Value))} {p(int.Parse(cn.Groups[4].Value))}:{cn.Groups[5].Value}";
        }
        var iso = Regex.Match(text, @"(20\d{2})[-/](\d{1,2})[-/](\d{1,2})(?:[ T]|\s*(?:星期|周)[一二三四五六日天]\s*)(\d{1,2})[:：](\d{2})");
        if (iso.Success)
        {
            if (!IsValidDate(int.Parse(iso.Groups[1].Value), int.Parse(iso.Groups[2].Value), int.Parse(iso.Groups[3].Value)))
                return null;
            return $"{iso.Groups[1].Value}-{p(int.Parse(iso.Groups[2].Value))}-{p(int.Parse(iso.Groups[3].Value))} {p(int.Parse(iso.Groups[4].Value))}:{iso.Groups[5].Value}";
        }
        var months = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
            ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12
        };
        var en = Regex.Match(text, @"(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(20\d{2})[^0-9]{0,12}?(\d{1,2}):(\d{2})\s*(AM|PM)?", RegexOptions.IgnoreCase);
        if (en.Success)
        {
            var mon = months[en.Groups[1].Value[..3]];
            if (!IsValidDate(int.Parse(en.Groups[3].Value), mon, int.Parse(en.Groups[2].Value)))
                return null;
            var hh = int.Parse(en.Groups[4].Value);
            var ampm = en.Groups[6].Value.ToUpperInvariant();
            if (ampm == "PM" && hh < 12) hh += 12;
            if (ampm == "AM" && hh == 12) hh = 0;
            return $"{en.Groups[3].Value}-{p(mon)}-{p(int.Parse(en.Groups[2].Value))} {p(hh)}:{en.Groups[5].Value}";
        }
        return null;
    }

    private static bool IsValidDate(int year, int month, int day) =>
        month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    /// <summary>正文前 500 字摘要（待确认队列展示用，不作原文）。</summary>
    public static string Snippet(string bodyText) =>
        bodyText.Length <= 500 ? bodyText : bodyText[..500];
}
