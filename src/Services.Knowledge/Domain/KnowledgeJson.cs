using System.Text.Json;

namespace YourInterview.Services.Knowledge.Domain;

/// <summary>
/// 出处引用(MS Learn / RFC / 官方文档)。
/// 为什么单独建个 record 而不是裸存 URL:前端要展示"这条知识的依据是什么",
/// 需要 (url, title, kind) 三件套 —— 光有 URL 与光有标题都不够用。
/// </summary>
public sealed record KnowledgeReference(string Url, string? Title = null, string? Kind = null);

/// <summary>
/// 知识点里那些 JSON 数组字段的统一序列化/反序列化入口。
///
/// 设计取舍(为什么用 JSON 列而不是关联表):
///   KeyPoints / CommonMistakes / FollowUps / References 都是"跟着知识点一起读、一起写"的小集合,
///   从不单独按其中某一项做跨行查询。为它们各建一张表,只会让详情查询多三次 join、
///   让写入多三次 SaveChanges,而换不来任何查询能力。
///   这正是 DDD 里"值对象优先"的思路:它们不是有独立生命周期的实体,就是知识点的一部分。
///
/// 容错原则:解析失败一律当作空集合 —— 历史数据里一个手误的 JSON 不该让整个详情接口 500。
/// </summary>
public static class KnowledgeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyList<string> ParseStringList(string? json)
        => Parse(json, static () => new List<string>());

    public static string? Serialize(IReadOnlyList<string>? list)
        => list is null || list.Count == 0 ? null : JsonSerializer.Serialize(list, Options);

    public static IReadOnlyList<KnowledgeReference> ParseReferenceList(string? json)
        => Parse(json, static () => new List<KnowledgeReference>());

    public static List<KnowledgeReference> ParseReferenceListMutable(string? json)
        => Parse(json, static () => new List<KnowledgeReference>()).ToList();

    public static string? Serialize(IReadOnlyList<KnowledgeReference>? list)
        => list is null || list.Count == 0 ? null : JsonSerializer.Serialize(list, Options);

    private static IReadOnlyList<T> Parse<T>(string? json, Func<List<T>> empty)
    {
        if (string.IsNullOrWhiteSpace(json)) return empty();
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? empty();
        }
        catch (JsonException)
        {
            // 坏数据不放大:记不记日志都行,但绝不让它影响读接口可用性。
            return empty();
        }
    }
}
