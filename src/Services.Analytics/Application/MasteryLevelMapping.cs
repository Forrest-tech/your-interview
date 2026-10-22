namespace YourInterview.Services.Analytics.Application;

/// <summary>MasteryBreakdown 的三档桶(与读模型字段 Mastered/Learning/Fresh 一一对应)。</summary>
public enum MasteryBucket
{
    Fresh,
    Learning,
    Mastered
}

/// <summary>
/// Knowledge 掌握等级 → Analytics 桶 + 雷达分 的唯一映射(票34/REQ-18 定版)。
///
/// 为什么需要这一层:KnowledgeLevelChanged 事件的 Level 是 MasteryLevel 枚举 int
/// (New=0 / Learning=1 / Familiar=2 / Mastered=3 / NeedsReview=4,发布侧
/// KnowledgeMasteryChangedPublisher 传领域事件的 NewLevel),Mastery 是同枚举的字符串名。
/// 旧消费者按 0-100 分口径直消费(Level>=80 判 Mastered、Level 直写雷达分),
/// 结果 Mastered 恒计 0、technicalDepth 雷达分恒为 0-4 —— 投影分叉的具体成因之一。
/// 事件契约不动(不变项),语义解释收口在本映射:优先认 Mastery 字符串(自描述),
/// 字符串缺失/无法识别时按枚举 int 值域回落,两者皆不可识别 → Fresh/0(不抛错、不污染)。
///
/// 桶口径(与 Knowledge 真源 GetKnowledgeStats 的 ByMastery 对齐):
///   Mastered → Mastered;Learning / Familiar / NeedsReview → Learning;New → Fresh。
///   (Fresh 在读模型注释中定义为「还没开始学的(New)」;NeedsReview 是学过又答错,
///   仍在学习中,不回 Fresh。)
/// 雷达分(technicalDepth,knowledge 来源):New=0 / NeedsReview=20 / Learning=40 /
///   Familiar=65 / Mastered=80 —— 与旧消费者「>=80 判 Mastered」的原始意图衔接,
///   且保证 Mastered 档分数严格高于其余档。
/// </summary>
public static class MasteryLevelMapping
{
    public static (MasteryBucket Bucket, int Score) Classify(string? mastery, int level)
    {
        if (TryParseName(mastery, out var byName))
            return byName;

        // 枚举 int 回落:0=New 1=Learning 2=Familiar 3=Mastered 4=NeedsReview
        return level switch
        {
            0 => (MasteryBucket.Fresh, 0),
            1 => (MasteryBucket.Learning, 40),
            2 => (MasteryBucket.Learning, 65),
            3 => (MasteryBucket.Mastered, 80),
            4 => (MasteryBucket.Learning, 20),
            _ => (MasteryBucket.Fresh, 0)
        };
    }

    /// <summary>只按等级名分类(真源对账/重建用:Knowledge 条目只带 Mastery 名)。</summary>
    public static (MasteryBucket Bucket, int Score) ClassifyName(string? mastery)
        => TryParseName(mastery, out var r) ? r : (MasteryBucket.Fresh, 0);

    private static bool TryParseName(string? mastery, out (MasteryBucket Bucket, int Score) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(mastery)) return false;

        switch (mastery.Trim().ToLowerInvariant())
        {
            case "new": result = (MasteryBucket.Fresh, 0); return true;
            case "learning": result = (MasteryBucket.Learning, 40); return true;
            case "familiar": result = (MasteryBucket.Learning, 65); return true;
            case "mastered": result = (MasteryBucket.Mastered, 80); return true;
            case "needsreview": result = (MasteryBucket.Learning, 20); return true;
            default: return false;
        }
    }

    /// <summary>一组条目的 technicalDepth 代表分 = 各条目映射分的均值(四舍五入);空集返回 null。</summary>
    public static int? AverageScore(IEnumerable<string?> masteries)
    {
        var scores = masteries.Select(m => ClassifyName(m).Score).ToList();
        return scores.Count == 0 ? null : (int)Math.Round(scores.Average());
    }
}
