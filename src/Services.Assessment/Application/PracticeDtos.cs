using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// DTO —— 与前端 MaterialNode / Recording 形状对齐,序列化零转换
// ============================================================

/// <summary>素材节点(树形,children 递归)。MarkColor:null/none/orange/red/green(第四十三轮)。</summary>
public sealed record MaterialNodeDto(
    Guid Id, string Name, bool Folder, int SortOrder, bool Expanded,
    string? Content, IReadOnlyList<MaterialNodeDto> Children, string? MarkColor = null,
    Guid? CategoryId = null);

/// <summary>一条录音 + 可选评分。</summary>
public sealed record RecordingDto(
    Guid Id, Guid MaterialId, double DurationSeconds, long SizeBytes,
    string ContentType, DateTimeOffset CreatedAt, RecordingScoreDto? Score,
    // ★ 票111 段3(A11):录音行自身读时 xmin(区别于 Score.Version 评分行)——
    //   DELETE recordings/{id} 时 If-Match 回传钉版用。
    uint? Version = null,
    // ★ 票258 S1:服务端跟读标识。前端只见 bool,不见 NULL(NULL 映射为 false)。
    bool IsShadow = false);

/// <summary>
/// 发音评分结果。
/// ⚠️ 可空字段拿不到就是 null —— 前端据此显示 "—",绝不用 0 冒充。
/// </summary>
public sealed record RecordingScoreDto(
    double? PronScore, double? AccuracyScore, double? FluencyScore,
    double? CompletenessScore, double? ProsodyScore,
    string Recognized, IReadOnlyList<WordScoreDto> Words,
    string ReferenceText, DateTimeOffset AssessedAt,
    // ★ 第三十四轮:计费口径(音频秒数),读库与新建都会带上。
    //   拿不到为 null —— 不编造。
    double? BilledSeconds = null, int? BilledBytes = null,
    // ★ 票108 A10:评分行读时 xmin —— 重评分保存时回传钉版,过期得 409。
    uint? Version = null);

/// <summary>逐词评分读出 DTO。★ 票122:补 OffsetMs/DurationMs(默认值参数)——
/// 旧 WordsJson 无此两键 → STJ 绑定得 0,前端分发据真值走弹层(与旧行为逐字
/// 相同,不假 seek);新落库记录原值读出,重载后点填充词可 seek。</summary>
public sealed record WordScoreDto(string Word, double Accuracy, string ErrorType,
    double OffsetMs = 0, double DurationMs = 0);

