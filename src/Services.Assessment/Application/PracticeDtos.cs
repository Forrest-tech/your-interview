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
    string ContentType, DateTimeOffset CreatedAt, RecordingScoreDto? Score);

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

public sealed record WordScoreDto(string Word, double Accuracy, string ErrorType);

