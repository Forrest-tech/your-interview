using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YourInterview.Services.Jobs.Application;

// ============================================================================
//  Tracker 智能粘贴 —— 确定性解析规则(需求 6.2.2 / 6.2.3)
//
//  这个文件刻意只依赖 BCL,不引用任何项目内类型。两个理由:
//    1. 规则是纯函数:一段文本进、一份草稿出。它不该知道数据库、HTTP 或 AI 网关。
//    2. 可独立验证:零依赖意味着能单独拉出来跑样例,不必起整套服务。
//
//  为什么坚持"先规则、后 AI":
//    薪资、地点、日期这类字段,正则比模型稳定得多(模型会自己改写数字);
//    而摘要、关键词提炼这类"理解活"才轮到模型。分工见 SmartAddHandlers.cs。
// ============================================================================



/// <summary>解析状态。Ok=字段基本齐;Partial=只解出一部分;Failed=完全没解出来(前端回退手动)。</summary>
public static class ParseOutcome
{
    public const string Ok = "Ok";
    public const string Partial = "Partial";
    public const string Failed = "Failed";
}

/// <summary>粘贴内容的类型。</summary>
public static class PasteKind
{
    public const string Url = "Url";
    public const string Text = "Text";
    public const string Image = "Image";
    public const string Html = "Html";
}

/// <summary>
/// 票 154：JD 正文小节（动态结构）。Title 原样显示（去尾冒号后的原文标题），
/// Key 只内部用于投影匹配（归一化标题），Body 为原文正文（行结构保留），
/// Origin: auto（解析产出）/ manual（用户手动增补），Edited: 用户是否改过标题或正文。
/// </summary>
public sealed record JdSection(string Title, string Key, string Body, string Origin, bool Edited);

public sealed record ParsedJdDto(
    string SourceKind, string Status, IReadOnlyList<string> MessageCodes,
    string? Company, string? Role, string? Location, string? Salary, string? WorkMode, string? JobType,
    string? JdSummary, string? MatchKeywords, string? Priority, string? Link,
    string? JdText, string? CompanyDescription, string? Requirements, string? Benefits,
    string? WorkAuthorization,
    DateOnly? Deadline, string? DetectedSource,
    Dictionary<string, int> Confidence, bool UsedAi, bool NeedsManualReview, string? LogoUrl = null,
    // 票 154：通用小节提取结果（原文顺序）。null = 未产出（URL 纯链接模式等）。
    IReadOnlyList<JdSection>? Sections = null);

// ============================ 邀请邮件解析(需求 6.2.3) ============================

public sealed record ParsedInviteDto(
    string Status, IReadOnlyList<string> MessageCodes,
    string? Company, string? Role, int? RoundNo, string? Stage,
    DateTimeOffset? ScheduledAt, string? Location, string? Format, string? Interviewer,
    string? RawLink, Dictionary<string, int> Confidence, bool UsedAi, bool NeedsManualReview, string? LogoUrl = null);

