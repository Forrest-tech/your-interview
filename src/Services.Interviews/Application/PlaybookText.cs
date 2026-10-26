using System.Text;
using YourInterview.Services.Interviews.Domain.JsonFields;

namespace YourInterview.Services.Interviews.Application;

/// <summary>
/// Playbook 重做(A1–A7)用的纯文本工具 —— 全部做成 public static,
/// 让单元测试(反射 runner,不连数据库)可以直接覆盖。
/// </summary>
public static class PlaybookText
{
    /// <summary>
    /// A4 题目归一化:ToLowerInvariant + 去标点 + 去空白,然后精确相等匹配。
    ///
    /// 去的是 Unicode 标点(char.IsPunctuation,含 # + ? ! 。,等)与空白。
    /// 注意 "C#"/"C++" 的 #/+ 会被去掉 —— 两边归一化一致,不影响"同一题"的判定;
    /// v1 只做精确匹配,不做语义相似(按 spec)。
    /// </summary>
    public static string NormalizeQuestionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (char.IsPunctuation(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// A3:把分析 Summary(一段话)切成 3~5 条一句话结论,序列化为 JSON 数组。
    /// 无 Summary 返回 null。切分按中文句号优先,英文按 ". " 兜底;
    /// 小数点后无空格(如 12.3)不会被误切。
    /// </summary>
    public static string? SummaryToBulletsJson(string? summary, int maxBullets = 5)
    {
        var bullets = SummaryToBullets(summary, maxBullets);
        return bullets.Count == 0 ? null : JsonFieldAdapter.Serialize(bullets);
    }

    public static List<string> SummaryToBullets(string? summary, int maxBullets = 5)
    {
        if (string.IsNullOrWhiteSpace(summary)) return [];
        // 中文句号优先;没有中文句号时按英文句界(点+空格)切
        var parts = summary.Contains('。')
            ? summary.Split('。', StringSplitOptions.RemoveEmptyEntries)
            : summary.Split(". ", StringSplitOptions.RemoveEmptyEntries);
        return parts
            .Select(p => p.Trim().TrimEnd('。', '.', '!', '！', '?', '？'))
            .Where(p => p.Length > 0)
            .Take(Math.Max(1, maxBullets))
            .ToList();
    }

    /// <summary>
    /// A3:六维分数 JSON:{overall,pronunciation,fluency,structure,technicalDepth,relevance}。
    /// 各维度可空 —— 文本模式下 pronunciation 为 null,前端按"未测量"展示。
    /// </summary>
    public static string BuildDimensionsJson(int? overall, int? pronunciation, int? fluency,
        int? structure, int? technicalDepth, int? relevance)
        => JsonFieldAdapter.Serialize(new
        {
            overall,
            pronunciation,
            fluency,
            structure,
            technicalDepth,
            relevance
        });
}
