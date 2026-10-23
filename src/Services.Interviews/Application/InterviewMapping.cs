using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Domain.JsonFields;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.Services.Interviews.Infrastructure.Services;
using YourInterview.Services.Interviews.Infrastructure.Storage;
using YourInterview.SharedContracts.Ai;

namespace YourInterview.Services.Interviews.Application;

// ============================ 映射(扩展方法,和 Jobs 服务保持一致风格) ============================

public static class InterviewMappingExtensions
{
    public static AssetDto ToDto(this InterviewAsset a, uint version = 0) => new(
        a.Id, a.Kind.ToString(), a.FileName, a.ContentType, a.SizeBytes, a.BlobUrl,
        a.DurationSeconds, a.SourceLanguage, a.HasTranscript,
        a.TranscriptText?.Length ?? 0, a.UploadedAt,
        a.StoragePath, a.Sha256, a.FileExists,
        a.IntegrityStatus?.ToString(), a.LastVerifiedAt,
        a.RoundId, a.Phase?.ToString(), a.MetaJson, version);

    public static QuestionDto ToDto(this InterviewQuestion q, uint version = 0) => new(
        q.Id, q.Sequence, q.QuestionText, q.MyAnswerText, q.Category.ToString(), q.Difficulty,
        q.Assessment, q.GotStuck, q.StuckReason, q.RecommendedAnswer, q.AskedAtSeconds,
        q.WeaknessTagsJson, q.FollowUpQuestionsJson, q.MissedPointsJson, q.Mastery,
        q.RoundId, q.AskedBy, q.ReviewJson, version);

    public static ActionDto ToDto(this InterviewAction a) => new(
        a.Id, a.Title, a.Detail, a.DueDate, a.IsDone, a.CreatedAt);

    public static WeaknessDto ToDto(this InterviewWeakness w) => new(
        w.Id, w.Category.ToString(), w.Title, w.Detail, w.Evidence, w.Severity, w.Suggestion,
        w.OccurrenceCount, w.SourceType.ToString(), w.CreatedAt);

    public static RoundDto ToDto(this InterviewRound r, uint version = 0) => new(
        r.Id, r.Order, r.Stage, r.ScheduledDate, r.Interviewers, r.Format, r.Location,
        r.Outcome.ToString(), r.Notes, r.Feedback,
        r.MeetingLink, r.ScheduledTime, r.EmailsJson, r.Transcript,
        r.PrepQuestionsJson, r.RecordingUrl, r.ChecklistJson,
        r.ReviewJson, r.PrepReportJson, version);

    public static GuidanceMaterialDto ToDto(this GeneratedMaterial m) => new(
        m.Id, m.Version, m.ContentMarkdown, m.Model, m.GeneratedAt,
        m.PromptTokens, m.CompletionTokens);

    public static QuestionCandidateDto ToCandidateDto(this InterviewQuestion q) => new(
        q.Id, q.QuestionText, q.MyAnswerText, q.RecommendedAnswer,
        q.Category.ToString(), q.Difficulty, q.GotStuck, q.Assessment);

    public static InterviewEntryDto ToDto(this InterviewEntry e, uint version = 0,
        IReadOnlyDictionary<Guid, uint>? questionVersions = null,
        IReadOnlyDictionary<Guid, uint>? roundVersions = null,
        IReadOnlyDictionary<Guid, uint>? assetVersions = null) => new(
        e.Id, e.CompanyId, e.CompanyName, e.JobApplicationId, e.Role,
        e.CompanyProfile, e.JdText, e.JdSummary,
        e.RoundNo, e.InterviewDate, e.InterviewFormat, e.Interviewers, e.Location, e.Result, e.Notes,
        e.Status.ToString(), e.FailureReason, e.TranscribedAt, e.AnalyzedAt,
        e.OverallScore, e.PronunciationScore, e.FluencyScore, e.StructureScore,
        e.TechnicalDepthScore, e.RelevanceScore, e.AnalysisSummary,
        ParseSpeechMetrics(e.SpeechMetricsJson), e.CandidateTranscript,
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count, e.Rounds.Count,
        e.Assets.OrderBy(x => x.UploadedAt).Select(x => x.ToDto(
            assetVersions is not null && assetVersions.TryGetValue(x.Id, out var av) ? av : 0)).ToList(),
        e.Questions.OrderBy(x => x.Sequence).Select(x => x.ToDto(
            questionVersions is not null && questionVersions.TryGetValue(x.Id, out var qv) ? qv : 0)).ToList(),
        e.Weaknesses.OrderByDescending(x => x.Severity).Select(x => x.ToDto()).ToList(),
        e.Rounds.OrderBy(x => x.Order).Select(x => x.ToDto(
            roundVersions is not null && roundVersions.TryGetValue(x.Id, out var rv) ? rv : 0)).ToList(),
        e.CreatedAt, e.UpdatedAt,
        e.QuestionsToAskJson, e.Actions.Count,
        e.Actions.OrderBy(x => x.IsDone).ThenBy(x => x.CreatedAt)
            .Select(x => x.ToDto()).ToList(), e.DebriefJson,
        DeriveOutcome(e)?.ToString(), version);

    /// <summary>指标 JSON 反序列化(票19:经 JsonFieldAdapter 强类型读):坏数据/缺字段时返回 null,前端按"无指标"处理。</summary>
    private static SpeechMetricsDto? ParseSpeechMetrics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonFieldAdapter.Read<SpeechMetricsDto>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>列表项只带聚合的标量字段(不带子集合),避免列表页把大文本拖出来。</summary>
    public static InterviewEntryListItemDto ToListItemDto(this InterviewEntry e) => new(
        e.Id, e.CompanyId, e.CompanyName, e.Role, e.RoundNo, e.InterviewDate, e.InterviewFormat,
        e.Status.ToString(), e.Result,
        e.OverallScore, e.PronunciationScore, e.FluencyScore, e.StructureScore,
        e.TechnicalDepthScore, e.RelevanceScore,
        e.Assets.Count, e.Questions.Count, e.Weaknesses.Count,
        e.Assets.Any(a => a.TranscriptText != null),
        e.CreatedAt,
        // Tracker 联动(M1.4):邀请登记自动建的草稿带申请 Id,前端可回链
        e.JobApplicationId,
        DeriveOutcome(e)?.ToString());

    // ===================== 条目结果派生(票05 / ARC-14 单一事实源) =====================
    //
    // 背景:结果判读此前 TS/C# 各一份且口径不同(前端 v2.helpers 用中英关键词判
    // 分组,后端 Jobs 消费者用另一组关键词回写 Tracker)。票04 已把跨服务回写
    // 收归结构化枚举(轮次 InterviewRoundOutcome → SharedContracts
    // InterviewOutcome,枚举为准、文本只作展示);本方法把「条目展示用结果」
    // 也收归同一事实源:列表/详情 DTO 的 Outcome 只许由这里派生,前端只消费
    // 枚举做分组(In Progress/Completed)与徽章映射,不再判读 Result 原文。
    //
    // 派生规则(顺序即优先级,上者胜):
    //  1. 结构化轮次优先(与票04 UpdateBasicInfo 发事件的口径完全一致):
    //     条目当前轮(RoundNo 对应的轮次)已有定论(Outcome 非 Pending)时
    //     直接取轮次枚举 —— 哪怕 Result 原文与之矛盾也以枚举为准
    //     (同 Jobs 消费者 EnumWinsOverFreeText 的语义)。只看当前轮:
    //     早先轮次通过不等于整场结束,后续轮次未定论时条目仍在进行中。
    //  2. Result 原文恰为受控词表(域约定 Passed|Rejected|Pending|Ghosted|
    //     Cancelled|NoShow)时按枚举名精确解析 —— 这是结构化值,不是关键词猜测。
    //  3. 其余自由文本才走关键词回落,且与票04 Jobs 侧
    //     LegacyOutcomeTextAdapter 的口径逐项对照(见下),保证同一原文在
    //     列表分组与 Tracker 回写的结论同向:
    //       · 未落定优先:含 pending/waiting/awaiting/in progress/等回复/等待/
    //         进行中 → Pending(留 In Progress;Legacy 对「等回复」同样不回写)。
    //       · 拒向:Legacy 的 未通过/不通过/拒/挂 → Failed 与前端旧判读的
    //         reject/denied/declin/fail/没过/淘汰/黄了 并集 → Rejected。
    //         拒向必须先于通过向判定(Legacy 明注「未通过先于通过」:
    //         未通过/不通过都含「通过」二字,顺序反了结论就反了)。
    //       · 通过向:Legacy 的 通过 → Passed 与旧判读的 offer/hired/accepted/
    //         录用/录取/成功/\bpass 并集 → Passed。
    //       · 中性终局(旧判读的 closed 组):ghost/失联/无回复 → Ghosted;
    //         cancel/withdraw/closed/ended/done/放弃/取消/结束/完结 → Cancelled
    //         (枚举内无通用 Closed 值,取中性终局 Cancelled 承接;方向同为已结束)。
    //  4. 都不命中 → null(无结构化结论,留 In Progress —— 宁可漏分,不可错分,
    //     与 Legacy「识别不了就不回写」同向)。
    //
    // 与改前前端判读的已知差异(均为向票04口径靠拢,非回归):
    //  · 含「不通过」的原文:旧判读误入 positive(含「通过」),现按 Legacy 改判 Rejected;
    //  · 含「挂」的原文(如「挂了」):旧判读不命中留 In Progress,现按 Legacy 判 Rejected;
    //  · 当前轮已有定论而 Result 原文写未落定词(如「等后续轮次」):旧判读留
    //    In Progress,现以轮次枚举为准判终局 —— 与 Tracker 回写(票04 事件带枚举)一致。
    /// <summary>条目结构化结果派生(票05 单一事实源;规则见上方注释块)。</summary>
    public static InterviewRoundOutcome? DeriveOutcome(InterviewEntry e)
    {
        // 1. 当前轮的结构化定论优先(票04 口径)。
        var round = e.Rounds.FirstOrDefault(r => r.Order == e.RoundNo);
        if (round is not null && round.Outcome != InterviewRoundOutcome.Pending)
            return round.Outcome;

        var raw = (e.Result ?? string.Empty).Trim();
        if (raw.Length == 0) return null;

        // 2. 受控词表精确解析(名字对上才算,防 "1" 之类数字串被 Enum.TryParse 误收)。
        //    票98(源票65 D1):TryParse 还会把越界数字串("-1"/"99")收为未定义枚举值,
        //    其 ToString() 恰等于原文、单靠名字校验挡不住 —— 须再过 Enum.IsDefined,
        //    未定义一律不认、回落规则 3(与票58 adapter 的防数字串口径对齐)。
        if (Enum.TryParse<InterviewRoundOutcome>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && string.Equals(parsed.ToString(), raw, StringComparison.OrdinalIgnoreCase))
            return parsed;

        // 3. 自由文本关键词回落(与 LegacyOutcomeTextAdapter 对照,见注释块)。
        var s = raw.ToLowerInvariant();
        if (ContainsAny(s, "pending", "waiting", "awaiting", "in progress", "等回复", "等待", "进行中"))
            return InterviewRoundOutcome.Pending;
        if (ContainsAny(s, "未通过", "不通过", "拒", "挂",
                "reject", "denied", "declin", "fail", "没过", "淘汰", "黄了"))
            return InterviewRoundOutcome.Rejected;
        if (ContainsAny(s, "通过", "offer", "hired", "accepted", "录用", "录取", "成功")
            || Regex.IsMatch(s, @"\bpass"))
            return InterviewRoundOutcome.Passed;
        if (ContainsAny(s, "ghost", "失联", "无回复"))
            return InterviewRoundOutcome.Ghosted;
        if (ContainsAny(s, "cancel", "withdraw", "closed", "ended", "done", "放弃", "取消", "结束", "完结"))
            return InterviewRoundOutcome.Cancelled;
        return null;
    }

    private static bool ContainsAny(string s, params string[] keywords)
        => keywords.Any(k => s.Contains(k, StringComparison.Ordinal));
}

// ============================ 归属加载(COD-01/票03) ============================

/// <summary>
/// 条目归属加载的统一入口(双保险第二层):
/// · 普通请求路径:按 (Id, UserId) 谓词加载(全局过滤器之外再显式一道);
/// · Worker 回写路径(OwnerUserId 非空,裁决 B):IgnoreQueryFilters 按 Id 加载后
///   校验 entry.UserId == OwnerUserId —— 不给服务账号全量豁免,主人不对一律视为不存在(404)。
/// </summary>
internal static class OwnedEntry
{
    public static async Task<InterviewEntry?> LoadAsync(
        InterviewsDbContext db, Guid entryId, Guid userId, Guid? ownerUserId,
        Func<IQueryable<InterviewEntry>, IQueryable<InterviewEntry>>? include = null,
        CancellationToken ct = default)
    {
        IQueryable<InterviewEntry> q = ownerUserId.HasValue
            ? db.Entries.IgnoreQueryFilters()
            : db.Entries;
        if (include is not null) q = include(q);
        var e = await q.FirstOrDefaultAsync(x => x.Id == entryId, ct);
        if (e is null) return null;
        var expected = ownerUserId ?? userId;
        return e.UserId == expected ? e : null;
    }

    public static async Task<InterviewEntry?> LoadNoTrackingAsync(
        InterviewsDbContext db, Guid entryId, Guid userId, Guid? ownerUserId,
        Func<IQueryable<InterviewEntry>, IQueryable<InterviewEntry>>? include = null,
        CancellationToken ct = default)
    {
        IQueryable<InterviewEntry> q = ownerUserId.HasValue
            ? db.Entries.IgnoreQueryFilters().AsNoTracking()
            : db.Entries.AsNoTracking();
        if (include is not null) q = include(q);
        var e = await q.FirstOrDefaultAsync(x => x.Id == entryId, ct);
        if (e is null) return null;
        var expected = ownerUserId ?? userId;
        return e.UserId == expected ? e : null;
    }

    /// <summary>子表直查前的归属存在性校验:条目不属于当前用户 → 按不存在处理(404)。</summary>
    public static Task<bool> ExistsAsync(
        InterviewsDbContext db, Guid entryId, Guid userId, CancellationToken ct = default)
        => db.Entries.AnyAsync(x => x.Id == entryId && x.UserId == userId, ct);
}

