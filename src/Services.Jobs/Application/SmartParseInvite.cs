using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;
// ============================================================================
//  AI 精修:JD

// ============================================================================
//  确定性解析器:邀请邮件
// ============================================================================

public static class InvitePasteParser
{
    public static ParsedInviteDto Parse(string raw)
    {
        var text = raw.Trim();
        var conf = new Dictionary<string, int>();

        var roundNo = GuessRound(text);
        if (roundNo is not null) conf["roundNo"] = 70;

        var stage = GuessStage(text);
        if (stage is not null) conf["stage"] = 70;

        var format = GuessFormat(text);
        if (format is not null) conf["format"] = 75;

        var at = GuessTime(text);
        if (at is not null) conf["scheduledAt"] = 75;

        var interviewer = Labeled(text, "面试官", "Interviewer", "Interviewer(s)", "面试人");
        if (interviewer is not null) conf["interviewer"] = 80;

        var location = Labeled(text, "地点", "Location", "地址", "面试地点")
                    ?? (format == "Video" ? null : GuessAddress(text));
        if (location is not null) conf["location"] = 60;

        var link = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase) is { Success: true } m
            ? m.Value.TrimEnd(')', '】', '」', ',', '。') : null;

        var company = Labeled(text, "公司", "Company")
                   ?? CompanyFromEmail(text)
                   ?? CompanyAfterAt(text);
        var role = Labeled(text, "职位", "岗位", "Role", "Position");

        return new ParsedInviteDto(
            ParseOutcome.Partial, Array.Empty<string>(),
            company, role, roundNo, stage, at, location, format, interviewer, link,
            conf, false, false);
    }

    public static int CountFilled(ParsedInviteDto d)
    {
        var n = 0;
        if (d.RoundNo is not null) n++;
        if (!string.IsNullOrWhiteSpace(d.Stage)) n++;
        if (d.ScheduledAt is not null) n++;
        if (!string.IsNullOrWhiteSpace(d.Format)) n++;
        if (!string.IsNullOrWhiteSpace(d.Interviewer)) n++;
        if (!string.IsNullOrWhiteSpace(d.Location)) n++;
        return n;
    }

    private static string? Labeled(string text, params string[] labels)
    {
        foreach (var label in labels)
        {
            var m = Regex.Match(text, $@"{Regex.Escape(label)}\s*[:：]\s*(.+)",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var v = m.Groups[1].Value.Trim();
                if (v.Length is > 0 and <= 120) return v;
            }
        }
        return null;
    }

    private static int? GuessRound(string text)
    {
        var cn = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["一"] = 1, ["二"] = 2, ["三"] = 3, ["四"] = 4, ["五"] = 5,
            ["两"] = 2, ["首"] = 1, ["终"] = 99
        };

        var m = Regex.Match(text, @"第\s*([0-9一二三四五两首]+)\s*(?:轮|面)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var v = m.Groups[1].Value;
            if (int.TryParse(v, out var n)) return n;
            if (cn.TryGetValue(v, out var c)) return c == 99 ? null : c;
        }

        m = Regex.Match(text, @"round\s*#?\s*([1-9])", RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

        // "终面 / final round" 不给具体轮次 —— 猜错轮次比留空更糟
        return null;
    }

    private static string? GuessStage(string text)
    {
        if (Regex.IsMatch(text, @"(system\s*design|系统设计|架构设计)", RegexOptions.IgnoreCase)) return "SystemDesign";
        if (Regex.IsMatch(text, @"(coding|algorithm|算法|白板|技术面|technical|机考|oa\b)", RegexOptions.IgnoreCase)) return "Technical";
        if (Regex.IsMatch(text, @"(behavioral|behavioural|行为面|bq|hr面|文化|价值观)", RegexOptions.IgnoreCase)) return "Behavioral";
        if (Regex.IsMatch(text, @"(final|终面|onsite|on-site|现场面|总监面|hiring manager)", RegexOptions.IgnoreCase)) return "Final";
        if (Regex.IsMatch(text, @"(phone\s*screen|screening|电面|初筛|电话面|hr\s*screen)", RegexOptions.IgnoreCase)) return "Screen";
        return null;
    }

    private static string? GuessFormat(string text)
    {
        if (Regex.IsMatch(text, @"(zoom|google\s*meet|microsoft\s*teams|teams\b|webex|视频|线上|video)", RegexOptions.IgnoreCase)) return "Video";
        if (Regex.IsMatch(text, @"(phone|电话|call\s*you)", RegexOptions.IgnoreCase)) return "Phone";
        if (Regex.IsMatch(text, @"(on-?site|现场|到园区|到公司|in\s*person)", RegexOptions.IgnoreCase)) return "Onsite";
        return null;
    }

    private static string? GuessAddress(string text)
    {
        var m = Regex.Match(text, @"\b(\d{1,5}\s+[A-Z][A-Za-z]+\s+(?:Street|St|Avenue|Ave|Road|Rd|Boulevard|Blvd|Drive|Dr)\b[^,\n]{0,40})");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    // 票85:英文日期显式格式表(配 InvariantCulture 使用,不依赖宿主 culture)。
    // 时刻形态由上方正则限定:时:分[ AM/PM] / 时:分AM / 时 AM / 时AM / 时:分(可 24h)。
    private static readonly string[] EnglishMonthDateFormats =
    {
        "MMM d, yyyy h:mm tt", "MMM d, yyyy h:mmtt",
        "MMM d, yyyy H:mm tt", "MMM d, yyyy H:mmtt",
        "MMM d, yyyy h tt", "MMM d, yyyy htt",
        "MMM d, yyyy H:mm",
    };

    private static readonly string[] SlashDateFormats =
    {
        "M/d/yyyy h:mm tt", "M/d/yyyy h:mmtt",
        "M/d/yyyy H:mm tt", "M/d/yyyy H:mmtt",
        "M/d/yyyy h tt", "M/d/yyyy htt",
        "M/d/yyyy H:mm",
    };

    private static DateTimeOffset? GuessTime(string text)
    {
        // 1) 2026-10-05 14:30 / 2026/10/05 14:30
        // ⚠️ 日/月的可选项必须"两位在前":0?[1-9] 会把 "18" 截成 "1",
        //    而后面的时间是可选组,整条正则照样成功 → 得到 10/1 凌晨(踩过)。
        var m = Regex.Match(text,
            @"(?<d>(?:20\d{2})[-/.](?:1[0-2]|0?[1-9])[-/.](?:[12]\d|3[01]|0?[1-9]))\s*(?:at\s*)?(?<t>(?:[01]?\d|2[0-3]):[0-5]\d(?:\s*(?:AM|PM|am|pm))?)?");
        if (m.Success)
        {
            var datePart = m.Groups["d"].Value;
            var timePart = m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value.Trim() : "09:00";
            if (DateTimeOffset.TryParse($"{datePart} {timePart}", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dto))
                return dto;
        }

        // 2) Oct 5, 2026 at 2:30 PM (EST)
        // ⚠️ 票85:原实现用 GetCultureInfo("en-US")——生产 runtimeconfig 开 invariant
        //    globalization(Directory.Build.props),该调用直接抛 CultureNotFoundException,
        //    英文日期整支失效(票28双向断言锁定)。改显式格式表 + InvariantCulture:
        //    invariant 的月份缩写/AM-PM 标记即英文,且 InvariantCulture 本身在
        //    invariant 宿主下可用。格式表逐项与旧 en-US TryParse 行为对齐过
        //    (evidence-85 探针):含无后缀 24h 时刻(H:mm)、"14:30 PM" 这类
        //    旧解析能解出的怪形态(H:mm tt),以及纯小时无分钟("…2026 2")旧解析
        //    解不出——故表中不放裸 h/H 格式,保持解不出。
        m = Regex.Match(text,
            @"(?<mon>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<y>20\d{2})\s*(?:at\s*)?(?<t>\d{1,2}(?::[0-5]\d)?\s*(?:AM|PM|am|pm)?)?");
        if (m.Success)
        {
            var s = $"{m.Groups["mon"].Value} {m.Groups["day"].Value}, {m.Groups["y"].Value} "
                    + (m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value.Trim() : "9:00 AM");
            if (DateTimeOffset.TryParseExact(s, EnglishMonthDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dto2))
                return dto2;
        }

        // 3) 10/05/2026 2:00pm (同样两位优先;月日序按美式 MM/dd,歧义口径同旧 en-US)
        m = Regex.Match(text,
            @"(?<d>(?:1[0-2]|0?[1-9])/(?:[12]\d|3[01]|0?[1-9])/(?:20\d{2}))\s*(?:at\s*)?(?<t>\d{1,2}(?::[0-5]\d)?\s*(?:AM|PM|am|pm)?)?");
        if (m.Success)
        {
            var s = m.Groups["d"].Value + " "
                    + (m.Groups["t"].Success && m.Groups["t"].Length > 0 ? m.Groups["t"].Value.Trim() : "9:00 AM");
            if (DateTimeOffset.TryParseExact(s, SlashDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var dto3))
                return dto3;
        }

        // 4) 中文:10月5日 14:30
        m = Regex.Match(text, @"(?<mo>\d{1,2})月(?<d>\d{1,2})日\s*(?<t>(?:[01]?\d|2[0-3]):[0-5]\d)?");
        if (m.Success)
        {
            var year = DateTimeOffset.Now.Year;
            var mo = int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture);
            var dd = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
            var time = m.Groups["t"].Success && m.Groups["t"].Length > 0
                ? m.Groups["t"].Value : "09:00";
            var (hh, mm) = SplitTime(time);
            try
            {
                var local = new DateTime(year, mo, dd, hh, mm, 0, DateTimeKind.Local);
                // 跨年兜底:解析出比今天早一个月的日期,多半是明年年初的面试
                if (local < DateTime.Now.AddMonths(-1)) local = local.AddYears(1);
                return new DateTimeOffset(local);
            }
            catch (ArgumentOutOfRangeException) { /* 日期非法就当没解析到 */ }
        }

        return null;
    }

    private static (int H, int M) SplitTime(string t)
    {
        var parts = t.Split(':');
        var h = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var m = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        return (h, m);
    }

    private static string? CompanyFromEmail(string text)
    {
        var m = Regex.Match(text, @"@(?<d>[a-z0-9\-]+)\.(?:com|io|ai|co|ca|net|org)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var d = m.Groups["d"].Value;
            if (d.Length > 1 && !FreeMail.Contains(d.ToLowerInvariant()))
                return char.ToUpperInvariant(d[0]) + d[1..];
        }
        return null;
    }

    // "You have an interview at Shopify..." —— 没有公司标签也没有邮箱域名时的兜底。
    // 只接受大写开头的词,小写词(on/the/with)自然截断;会议软件名一律跳过。
    private static string? CompanyAfterAt(string text)
    {
        foreach (var m in Regex.Matches(text,
                     @"\bat\s+((?:[A-Z][A-Za-z0-9&.\-']+\s+){0,2}[A-Z][A-Za-z0-9&.\-']+)",
                     RegexOptions.Compiled).Cast<Match>())
        {
            var v = m.Groups[1].Value.Trim();
            if (v.Length is < 2 or > 60) continue;
            if (VideoWords.IsMatch(v)) continue;
            return v;
        }
        return null;
    }

    private static readonly Regex VideoWords =
        new(@"(?i)meet|zoom|teams|hangouts|webex|call|link", RegexOptions.Compiled);

    private static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail", "outlook", "hotmail", "yahoo", "icloud", "qq", "163", "126", "foxmail", "protonmail"
    };
}

// ============================================================================
//  AI 精修:邀请邮件
