using System.Text.Json;
using System.Text.Json.Serialization;

namespace YourInterview.Services.Interviews.Domain.JsonFields;

/// <summary>
/// Json 大字段信封异常:存储值声称为版本信封、但版本未知/高于当前支持时抛出。
/// 继承 InvalidOperationException —— 与聚合既有失败路径同族(Handler 映射为
/// Conflict/NotFound 或全局 500),绝不静默吞数据或猜形状读取。
/// </summary>
public sealed class JsonFieldException(string message) : InvalidOperationException(message);

/// <summary>
/// 票19 ARC-06:16 个 *Json 大字段的**单一序列化适配层**。
///
/// 存储信封(仅落库形态,列类型/DTO/C# 属性契约全不变):
///   新写入 = {"v":1,"data":"&lt;载荷原文&gt;"} —— 载荷以 **JSON 字符串** 嵌入,
///   不解析、不重排、不规范化:合法 JSON、损坏 JSON、空白、Unicode 任意载荷
///   往返都逐字不变;载荷本身是不是合法 JSON 与信封无关。
///   旧数据(无信封)= 原样即载荷,读路径原样返回(向后兼容,永不要求回填迁移)。
///
/// 收敛点:聚合 16 字段的 backing field 存信封形、属性 getter/setter 过本类
/// Wrap/Unwrap(EF 对这 16 属性用 Field 访问模式直读 backing field);
/// Handler/PlaybookText/拦截器/派发器的一切字段相关 JsonSerializer 直呼
/// 改走本类 Serialize/Read(Web 口径)或 SerializeRaw/ReadRaw(默认口径,
/// 与 AI 网关传输、JobPayload 消息契约等「原文即契约」的场景对齐)。
///
/// 信封识别(保守):根为 JSON 对象、属性**恰好** {v, data} 两项、v 为整数、
/// data 为字符串 —— 四条全满足才算信封;任一不满足按旧形状原文返回。
/// v != 当前版本 → 抛 JsonFieldException(走既有失败路径,不静默吞)。
/// 已知残余风险:旧载荷若恰好长成 {"v":1,"data":"..."} 会被误认信封 ——
/// 现存 16 字段的真实形状(见盘点表)无一如此,登记于交付报告待协调者知悉。
/// </summary>
public static class JsonFieldAdapter
{
    /// <summary>当前信封版本。新写入一律此版;读到更高版抛错。</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions WebOpts = new(JsonSerializerDefaults.Web);

    private sealed record Envelope(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("data")] string Data);

    // ================= 存储层:原始载荷 ⇄ 信封 =================

    /// <summary>原始载荷 → 存储形(加版本信封)。null 透传(字段清空语义不变)。</summary>
    public static string? Wrap(string? rawPayload)
        => rawPayload is null ? null : JsonSerializer.Serialize(new Envelope(CurrentVersion, rawPayload));

    /// <summary>
    /// 存储形 → 原始载荷。无信封(旧数据/非对象/损坏 JSON)原样返回;
    /// 信封且版本受支持 → 返回 data 原文;信封但版本未知 → 抛 JsonFieldException。
    /// </summary>
    public static string? Unwrap(string? stored)
    {
        if (stored is null) return null;
        if (!TryParseEnvelope(stored, out var v, out var data)) return stored;
        if (v != CurrentVersion)
            throw new JsonFieldException(
                $"Json 字段信封版本 v={v} 高于/未知于当前支持版本 v={CurrentVersion},拒绝静默读取。");
        return data;
    }

    /// <summary>信封识别(保守四条件,见类注释)。识别不出 = 不是信封(旧形状),返回 false。</summary>
    private static bool TryParseEnvelope(string stored, out int version, out string? data)
    {
        version = 0;
        data = null;
        var trimmed = stored.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(stored);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var count = 0;
            JsonElement vEl = default, dEl = default;
            var hasV = false;
            var hasD = false;
            foreach (var p in root.EnumerateObject())
            {
                count++;
                if (p.NameEquals("v")) { hasV = true; vEl = p.Value; }
                else if (p.NameEquals("data")) { hasD = true; dEl = p.Value; }
            }
            if (!hasV || !hasD || count != 2) return false;
            if (vEl.ValueKind != JsonValueKind.Number || !vEl.TryGetInt32(out var v)) return false;
            if (dEl.ValueKind != JsonValueKind.String) return false;
            version = v;
            data = dEl.GetString();
            return true;
        }
        catch (JsonException) { return false; } // 损坏 JSON = 旧形状原文,留给既有容错路径
    }

    /// <summary>存储形是否为信封(诊断/测试用)。未知版本也算信封(与 Unwrap 的抛错口径一致)。</summary>
    public static bool IsEnveloped(string? stored)
        => stored is not null && TryParseEnvelope(stored, out _, out _);

    // ================= 载荷层:强类型 ⇄ 原始载荷(Web 口径,与既有 JsonOpts 一致) =================

    /// <summary>强类型值 → 原始载荷(JsonSerializerDefaults.Web,与迁移前各 JsonOpts 逐字一致)。</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, WebOpts);

    /// <summary>原始载荷 → 强类型值(Web 口径)。null 载荷 → default。损坏 JSON 抛 JsonException(调用方保持既有容错)。</summary>
    public static T? Read<T>(string? rawPayload)
        => rawPayload is null ? default : JsonSerializer.Deserialize<T>(rawPayload, WebOpts);

    // ================= 原文契约层:默认口径(迁移前无 options 的直呼点专用) =================

    /// <summary>强类型值 → 原文(默认 JsonSerializer 口径)。仅限原文即契约的场景:JobPayload 消息、AI 复盘字典、本地报告。</summary>
    public static string SerializeRaw<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>原文 → 强类型值(默认口径)。null → default。</summary>
    public static T? ReadRaw<T>(string? raw)
        => raw is null ? default : JsonSerializer.Deserialize<T>(raw);

    // ================= 类型化形状判定(代原始字符串比较) =================

    /// <summary>
    /// 「空 JSON 数组」判定 —— 与迁移前两处 `IsNullOrWhiteSpace(x) || x == "[]"` 口径**逐字一致**
    /// ("[ ]" 等带空格形态按现状视为非空,不做解析归一化,零行为偏差)。
    /// 入参为原始载荷(聚合属性 getter 输出),不接受存储信封形。
    /// </summary>
    public static bool IsEmptyArray(string? rawPayload)
        => string.IsNullOrWhiteSpace(rawPayload) || rawPayload == "[]";
}
