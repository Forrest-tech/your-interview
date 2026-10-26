using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
// 票 152：相关文章 / 博客模块切除（R-152-1 / R-152-2 / R-152-3 三层共用）。
//
// 信号定义唯一出处：提纯层(JobPageFetcher)、分节边界层(JdPasteParser)、
// AI 材料预切(JdAiEnricher)三处一律引用本类，不得各写一份漂移。
//
// 信号一律是通用文本信号，不依赖任何站点域名、class、HTML 结构。
// 切除语义：从模块开始行起截断到文末（模块后的页面元数据同样不要，
// 它是页面 chrome，不是 JD 内容）。
// ============================================================================
public static class RelatedModuleCut
{
    private static readonly Regex RelatedTitle = new(
        @"Related\s+(stories|articles|jobs)|You may also like|Recommended(\s+for\s+you|\s+jobs)?|More\s+(from|stories)|相关文章|推荐阅读|猜你喜欢",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>单行是否为模块起始信号（§2.1）。</summary>
    public static bool IsSignalLine(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return false;
        // (a) 整行内容类型标签行
        if (string.Equals(t, "Blog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Article", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "Video", StringComparison.OrdinalIgnoreCase))
            return true;
        // (b) 相关内容标题行（≤60 字）
        return t.Length <= 60 && RelatedTitle.IsMatch(t);
    }

    /// <summary>
    /// 找模块开始行下标：信号行出现后 5 行内须再出现一次信号行才判定；
    /// 单个孤立标签行不触发（防误伤正文里偶然出现的 "Blog" 一词）。
    /// 无模块返回 -1。
    /// </summary>
    public static int FindModuleStart(IList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsSignalLine(lines[i])) continue;
            var end = Math.Min(i + 5, lines.Count - 1);
            for (var j = i + 1; j <= end; j++)
                if (IsSignalLine(lines[j])) return i;
        }
        return -1;
    }

    /// <summary>从模块开始行起截断到文末；无模块时原样返回。</summary>
    public static string Cut(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Split('\n');
        var start = FindModuleStart(lines);
        return start < 0 ? text : string.Join("\n", lines.Take(start)).TrimEnd();
    }
}
