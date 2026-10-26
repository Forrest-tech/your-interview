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

// ============================ 详情页 v2(2026-10-20 定稿) ============================
// 统一入口归属 / 逐题复盘 / 结构化备考报告 / 转写抽题 / 说话人改名。
// JSON 结构由前后端共同约定(见 replica/playbook-redesign-spec.md),后端负责生成与存储。

/// <summary>改材料归属:RoundId=null → 条目级(沟通记录)。</summary>
public sealed record ReassignAssetCommand(Guid UserId, Guid EntryId, Guid AssetId, Guid? RoundId,
    uint? Version = null) : IRequest<Result>; // 票101 Version:材料行 xmin

/// <summary>删除材料(2026-10-20 v2 补齐)。</summary>
public sealed record DeleteAssetCommand(Guid UserId, Guid EntryId, Guid AssetId) : IRequest<Result>;

public sealed class DeleteAssetCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteAssetCommand, Result>
{
    public async Task<Result> Handle(DeleteAssetCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try { e.RemoveAsset(request.AssetId); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Asset.Delete", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ReassignAssetCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ReassignAssetCommand, Result>
{
    public async Task<Result> Handle(ReassignAssetCommand request, CancellationToken ct)
    {
        var e = await LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        // 票101:钉版对象是材料行本身。
        if (request.Version is { } av)
        {
            var a0 = e.Assets.FirstOrDefault(x => x.Id == request.AssetId);
            if (a0 is not null) ConcurrencyPin.Pin(db, a0, av);
        }
        try { e.ReassignAsset(request.AssetId, request.RoundId); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Asset.Reassign", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    internal static async Task<InterviewEntry?> LoadFull(InterviewsDbContext db, Guid id, Guid userId, CancellationToken ct)
        => await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
}

/// <summary>更新材料元数据 JSON(解析字段/AI 摘要/提取安排/处理状态)。</summary>
public sealed record UpdateAssetMetaCommand(Guid UserId, Guid EntryId, Guid AssetId, string? MetaJson,
    uint? Version = null) : IRequest<Result>; // 票101 Version:材料行 xmin

public sealed class UpdateAssetMetaCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateAssetMetaCommand, Result>
{
    public async Task<Result> Handle(UpdateAssetMetaCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        if (request.Version is { } av)
        {
            var x0 = e.Assets.FirstOrDefault(x => x.Id == request.AssetId);
            if (x0 is not null) ConcurrencyPin.Pin(db, x0, av);
        }
        try { e.UpdateAssetMeta(request.AssetId, request.MetaJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Asset.Meta", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>写入逐题复盘 JSON(含标准答案用户编辑版)。</summary>
public sealed record SetQuestionReviewCommand(Guid UserId, Guid EntryId, Guid QuestionId, string? ReviewJson,
    uint? Version = null) : IRequest<Result>; // 票101 Version:题目行 xmin

public sealed class SetQuestionReviewCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SetQuestionReviewCommand, Result>
{
    public async Task<Result> Handle(SetQuestionReviewCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        if (request.Version is { } qv)
        {
            var x0 = e.Questions.FirstOrDefault(x => x.Id == request.QuestionId);
            if (x0 is not null) ConcurrencyPin.Pin(db, x0, qv);
        }
        try { e.SetQuestionReview(request.QuestionId, request.ReviewJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Question.Review", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>写入整轮复盘汇总 JSON。</summary>
public sealed record SetRoundReviewCommand(Guid UserId, Guid EntryId, Guid RoundId, string? ReviewJson,
    uint? Version = null) : IRequest<Result>; // 票101 Version:轮次行 xmin

public sealed class SetRoundReviewCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SetRoundReviewCommand, Result>
{
    public async Task<Result> Handle(SetRoundReviewCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        if (request.Version is { } rv)
        {
            var x0 = e.Rounds.FirstOrDefault(x => x.Id == request.RoundId);
            if (x0 is not null) ConcurrencyPin.Pin(db, x0, rv);
        }
        try { e.SetRoundReview(request.RoundId, request.ReviewJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Round.Review", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>票207：恢复上一版——弹出最近一条 Generate 前快照（无快照则失败）。</summary>
public sealed record RestorePrepReportCommand(Guid UserId, Guid EntryId, Guid RoundId)
    : IRequest<Result<string>>;

public sealed class RestorePrepReportCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RestorePrepReportCommand, Result<string>>
{
    public async Task<Result<string>> Handle(RestorePrepReportCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<string>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<string>(Error.NotFound("轮次"));
        var restored = PrepReportHistory.RestoreLatest(round.PrepReportJson ?? "");
        if (restored is null) return Result.Failure<string>(Error.Validation("PrepReport.History", "没有可恢复的上一版"));
        try { e.SetRoundPrepReport(request.RoundId, restored); }
        catch (InvalidOperationException ex) { return Result.Failure<string>(Error.Conflict("Round.PrepReport", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success(restored);
    }
}

/// <summary>写入结构化备考报告 JSON(前端编辑 Tech Stack 关联后回存也走这里)。</summary>
public sealed record SetRoundPrepReportCommand(Guid UserId, Guid EntryId, Guid RoundId, string? PrepReportJson,
    uint? Version = null) : IRequest<Result>; // 票101 Version:轮次行 xmin

public sealed class SetRoundPrepReportCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SetRoundPrepReportCommand, Result>
{
    public async Task<Result> Handle(SetRoundPrepReportCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        if (request.Version is { } rv)
        {
            var x0 = e.Rounds.FirstOrDefault(x => x.Id == request.RoundId);
            if (x0 is not null) ConcurrencyPin.Pin(db, x0, rv);
        }
        try { e.SetRoundPrepReport(request.RoundId, request.PrepReportJson); }
        catch (InvalidOperationException ex) { return Result.Failure(Error.Conflict("Round.PrepReport", ex.Message)); }
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>说话人批量改名(本轮分段 + 题目 AskedBy 全篇生效)。返回受影响处数。</summary>
public sealed record RenameSpeakerCommand(Guid UserId, Guid EntryId, Guid RoundId, string From, string To)
    : IRequest<Result<int>>;

public sealed class RenameSpeakerCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RenameSpeakerCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RenameSpeakerCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<int>(Error.NotFound("面试条目"));
        try
        {
            var n = e.RenameSpeakerInRound(request.RoundId, request.From, request.To);
            await db.SaveChangesAsync(ct);
            return Result.Success(n);
        }
        catch (InvalidOperationException ex) { return Result.Failure<int>(Error.Conflict("Speaker.Rename", ex.Message)); }
    }
}

/// <summary>生成整轮复盘:逐题四维分析 + 整轮汇总(含通过概率,AI 估计)。</summary>
public sealed record GenerateRoundReviewCommand(Guid UserId, Guid EntryId, Guid RoundId) : IRequest<Result<string>>;

public sealed class GenerateRoundReviewCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GenerateRoundReviewCommand, Result<string>>
{
    public async Task<Result<string>> Handle(GenerateRoundReviewCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<string>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<string>(Error.NotFound("轮次"));

        var questions = e.Questions.Where(q => q.RoundId == round.Id).OrderBy(q => q.Sequence).ToList();
        if (questions.Count == 0)
            return Result.Failure<string>(Error.Validation("Review.NoQuestions",
                "本轮还没有题目,请先从转写抽取问答或手工添加题目。"));

        var hasAudio = e.Assets.Any(a => a.RoundId == round.Id && a.Kind == AssetKind.Audio);
        var transcript = RoundTranscript(e, round);

        var sb = new StringBuilder();
        sb.AppendLine($"公司:{e.CompanyName} 岗位:{e.Role} 轮次:Round {round.Order}({round.Stage})");
        sb.AppendLine($"面试官:{round.Interviewers}");
        sb.AppendLine(hasAudio
            ? "本轮有录音:发音维度可评分(依据原声)。"
            : "★本轮无录音(转写来自粘贴文本):发音维度必须输出 null,禁止猜测发音。");
        if (!string.IsNullOrWhiteSpace(transcript))
            sb.AppendLine($"## 转写原文\n{Trunc(transcript, 12000)}");
        sb.AppendLine("## 逐题(序号|提问人|问题|我的回答)");
        foreach (var q in questions)
            sb.AppendLine($"{q.Sequence}|{q.AskedBy ?? "面试官"}|{q.QuestionText}|{q.MyAnswerText ?? "(无记录)"}");

        const string system =
            "你是北美科技公司面试复盘教练。只输出一个 JSON 对象,不要任何额外文字,不要 Markdown 代码块。\n" +
            "硬规矩:每个维度的 comment 里每句判断都必须给出依据——引用候选人原话片段、指出时间点、或写明『全段未出现 X』;没有依据的话不要写。\n" +
            "输出结构:{\"round\":{\"overall\":0-100整数,\"passProbability\":0-100整数(AI估计),\"passBasis\":\"依据\",\"strongest\":\"\",\"weakest\":\"\",\"summary\":\"\"}," +
            "\"questions\":[{\"sequence\":题序,\"overall\":0-10一位小数,\"verdict\":\"一句话总评\"," +
            "\"dims\":{\"relevance\":{\"score\":0-10,\"comment\":\"\",\"fix\":\"\"},\"depth\":{同},\"expression\":{同}," +
            "\"pronunciation\":{\"score\":0-10或null,\"comment\":\"\",\"fix\":\"\"}}," +
            "\"stdSpoken\":\"北美口语版标准回答(第一人称,可直接朗读)\",\"stdTechnical\":\"技术版标准回答(要点式)\"}]}\n" +
            "无录音时 pronunciation.score 必须为 null,comment 说明『无录音,不评分』。";

        AiCompletionResult completion;
        try
        {
            completion = await ai.CompleteAsync("round-review", sb.ToString(), system, 0.4, 6000, ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            return Result.Failure<string>(Error.Validation("Review.AiNotConfigured",
                "AI 未配置:请先在设置里配置模型凭据。"));
        }
        catch (AiGatewayException ex)
        {
            return Result.Failure<string>(Error.Validation("Review.AiFailed", ex.Message));
        }

        var json = ExtractJson(completion.Text);
        if (json is null)
            return Result.Failure<string>(Error.Validation("Review.BadAiJson", "AI 返回内容无法解析为 JSON,请重试。"));

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("round", out var roundEl))
                e.SetRoundReview(round.Id, roundEl.GetRawText());
            if (root.TryGetProperty("questions", out var qs))
            {
                foreach (var qEl in qs.EnumerateArray())
                {
                    if (!qEl.TryGetProperty("sequence", out var seqEl)) continue;
                    var q = questions.FirstOrDefault(x => x.Sequence == seqEl.GetInt32());
                    if (q is null) continue;
                    var review = new Dictionary<string, object?>();
                    foreach (var p in qEl.EnumerateObject())
                        if (p.Name != "sequence") review[p.Name] = p.Value.Clone();
                    e.SetQuestionReview(q.Id, JsonFieldAdapter.SerializeRaw(review));
                    // 兼容旧字段:RecommendedAnswer 同步技术版,Assessment 同步总评
                    var stdTech = qEl.TryGetProperty("stdTechnical", out var st) ? st.GetString() : null;
                    var verdict = qEl.TryGetProperty("verdict", out var vd) ? vd.GetString() : null;
                    q.UpdateFromAnalysis(q.QuestionText, q.MyAnswerText, verdict ?? q.Assessment,
                        q.Category, q.Difficulty, q.GotStuck, q.StuckReason,
                        stdTech ?? q.RecommendedAnswer, q.AskedAtSeconds);
                }
            }
        }
        catch (JsonException)
        {
            return Result.Failure<string>(Error.Validation("Review.BadAiJson", "AI 返回 JSON 结构不完整,请重试。"));
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(round.ReviewJson ?? "{}");
    }

    internal static string RoundTranscript(InterviewEntry e, InterviewRound round)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(round.Transcript)) parts.Add(round.Transcript!);
        foreach (var a in e.Assets.Where(a => a.RoundId == round.Id
                     && (a.Kind is AssetKind.Transcript or AssetKind.Text or AssetKind.Notes)
                     && !string.IsNullOrWhiteSpace(a.TranscriptText)).OrderBy(a => a.UploadedAt))
            parts.Add(a.TranscriptText!);
        return string.Join("\n\n", parts);
    }

    internal static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// 票227：抽取结果的会议元数据过滤（纯函数，公开静态以便测试直接断言——Interviews 程序集无 InternalsVisibleTo）。
/// 判定对象是抽取出的“题目”本身：纯会议元数据（会议链接/会议号/密码/拨入号/Join 参会指令）或提问人
/// 为会议标识（Join 等）时返回 true（应丢弃）；真实题目一律返回 false。
/// 只认结构性物流标识，不因单个词误杀真实提问——“Have you used Zoom meetings before?”、
/// “How do I join the team?”、无问号的中文真实提问均须通过。
/// </summary>
public static class QuestionExtractFilter
{
    // 会议平台域名：题目含此类链接即为会议元数据（文档/代码链接不在此列）。
    private static readonly string[] MeetingHosts =
        ["teams.microsoft.com", "teams.live.com", "zoom.us", "meet.google.com",
         "webex.com", "gotomeeting.com", "chime.aws", "skype.com", "whereby.com"];

    // 强物流标识：出现即为会议元数据（会议号/密码/拨入/参会链接类，不含通用问句）。
    // 注意：“passcode”不作裸子串命中——“How do you handle passcodes in production?”是真实安全提问，
    // 只有“Passcode: 000000”这类标签+取值形态才算物流；故 passcode 走第 3b 条正则，不进本表。
    private static readonly string[] LogisticsMarkers =
        ["meeting id", "meeting passcode", "dial-in", "dial in",
         "会议号", "会议密码", "参会密码", "入会密码", "电话拨入", "参会链接", "会议链接", "join link"];

    public static bool IsMeetingMetadata(string question, string? askedBy)
    {
        var q = question.Trim();
        if (q.Length == 0) return false;

        // 1）提问人是会议标识而非真人：精确为 Join/加入会议（含后缀），或提问人本身含链接。
        var asker = (askedBy ?? "").Trim();
        if (asker.Length > 0)
        {
            var lowerAsker = asker.ToLowerInvariant();
            if (lowerAsker is "join" or "加入会议" || lowerAsker.StartsWith("加入会议")) return true;
            if (lowerAsker.StartsWith("join ", StringComparison.Ordinal) &&
                (lowerAsker.Contains("meeting") || lowerAsker.Contains("teams") ||
                 lowerAsker.Contains("zoom") || lowerAsker.Contains("会议"))) return true;
            if (asker.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                asker.Contains("https://", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var lower = q.ToLowerInvariant();

        // 2）题目含会议平台链接。MatchCollection 是非泛型枚举，须显式写 Match，否则 m 被推断为 object。
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(q, @"https?://[^\s]+"))
        {
            var url = m.Value.ToLowerInvariant();
            if (MeetingHosts.Any(h => url.Contains(h))) return true;
        }

        // 3）强物流标识。
        if (LogisticsMarkers.Any(m => lower.Contains(m))) return true;

        // 3b）passcode 仅标签+取值形态算物流（“Passcode: 000000”/“passcode 123456”）；
        // 裸词出现在真实提问中（如 passcode 安全策略）不得误杀。
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"passcode\s*[:：]\s*\d") ||
            System.Text.RegularExpressions.Regex.IsMatch(lower, @"passcode\s+\d{4,}")) return true;

        // 4）以 Join 开头的参会指令（“Join Microsoft Teams Meeting”类）；“How do I join …”等问句不在此列。
        if (System.Text.RegularExpressions.Regex.IsMatch(q, @"^\s*join\b.*(meeting|teams|zoom|会议)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return true;

        return false;
    }
}

/// <summary>从本轮转写抽取问答对 → 生成本轮题目(提问人逐人标注)。</summary>
public sealed record ExtractRoundQuestionsCommand(Guid UserId, Guid EntryId, Guid RoundId) : IRequest<Result<int>>;

public sealed class ExtractRoundQuestionsCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<ExtractRoundQuestionsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ExtractRoundQuestionsCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<int>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<int>(Error.NotFound("轮次"));

        var transcript = GenerateRoundReviewCommandHandler.RoundTranscript(e, round);
        if (string.IsNullOrWhiteSpace(transcript))
            return Result.Failure<int>(Error.Validation("Extract.NoTranscript", "本轮还没有转写材料可抽取。"));

        // 已有本轮题目时不重复抽取(幂等保护)
        if (e.Questions.Any(q => q.RoundId == round.Id))
            return Result.Success(0);

        List<(string Question, string? Answer, string? AskedBy, string Category)> extracted;
        try
        {
            var prompt =
                $"下面是 {e.CompanyName} {e.Role} Round {round.Order}({round.Stage})的面试转写,面试官:{round.Interviewers}。\n" +
                "抽取全部问答对,只输出 JSON 数组:[{\"question\":\"面试官原话\",\"answer\":\"候选人回答原话\",\"askedBy\":\"提问人真名,分不清写 面试官 1/面试官 2\",\"category\":\"Technical|SystemDesign|Behavioral|Coding|Culture|Other\"}]\n" +
                "闲聊寒暄不要抽。会议邀约/会议元数据（Join、会议链接、会议号、密码、时间地点、参会方式）不要抽，只抽面试官向候选人提出的真实问题。转写:\n" + (transcript.Length > 12000 ? transcript[..12000] : transcript);
            var completion = await ai.CompleteAsync("round-qa-extract", prompt,
                "只输出 JSON 数组,不要其它文字。", 0.2, 6000, ct);
            extracted = ParseExtracted(completion.Text);
        }
        catch (AiGatewayException)
        {
            // AI 不可用时用本地分段规则兜底(按说话人轮次配对),保证功能可用
            extracted = FallbackExtract(transcript, round.Interviewers);
        }

        var created = 0;
        foreach (var (question, answer, askedBy, category) in extracted)
        {
            if (string.IsNullOrWhiteSpace(question)) continue;
            // 票227：抽取后统一过滤（AI 与规则两路同过）——纯会议元数据不得成题。
            if (QuestionExtractFilter.IsMeetingMetadata(question, askedBy)) continue;
            var cat = Enum.TryParse<QuestionCategory>(category, true, out var c) ? c : QuestionCategory.Technical;
            e.AddQuestion(question.Trim(), answer, cat, 3, round.Id, askedBy);
            created++;
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(created);
    }

    private static List<(string, string?, string?, string)> ParseExtracted(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        var list = new List<(string, string?, string?, string)>();
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var q = el.TryGetProperty("question", out var qe) ? qe.GetString() : null;
                if (string.IsNullOrWhiteSpace(q)) continue;
                list.Add((q!,
                    el.TryGetProperty("answer", out var ae) ? ae.GetString() : null,
                    el.TryGetProperty("askedBy", out var be) ? be.GetString() : null,
                    el.TryGetProperty("category", out var ce) ? ce.GetString() ?? "Technical" : "Technical"));
            }
        }
        catch (JsonException) { /* 坏 JSON → 返回空,调用方可见 0 条 */ }
        return list;
    }

    /// <summary>无 AI 时的规则抽取:识别 "Name: 内容" 轮次行,面试官的问句 + 紧随的候选人回答配对。</summary>
    internal static List<(string, string?, string?, string)> FallbackExtract(string transcript, string? interviewers)
    {
        var turns = new List<(string Speaker, string Text)>();
        foreach (var rawLine in transcript.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^([^:：]{1,40})[:：]\s*(.+)$");
            if (m.Success) turns.Add((m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
        }
        var interviewerNames = (interviewers ?? "")
            .Split(new[] { ',', '，', ';', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => System.Text.RegularExpressions.Regex.Replace(s, @"\(.*?\)", "").Trim())
            .Where(s => s.Length > 0).ToList();
        bool IsCandidate(string speaker) =>
            speaker.Contains("Forrest") || speaker.Equals("You", StringComparison.OrdinalIgnoreCase)
            || speaker.Contains("我") || speaker.Contains("Candidate", StringComparison.OrdinalIgnoreCase);
        string? MatchInterviewer(string speaker)
        {
            foreach (var n in interviewerNames)
                if (speaker.Contains(n) || n.Contains(speaker)) return n;
            return null;
        }

        var result = new List<(string, string?, string?, string)>();
        for (var i = 0; i < turns.Count; i++)
        {
            var (speaker, text) = turns[i];
            if (IsCandidate(speaker)) continue;
            var askedBy = MatchInterviewer(speaker);
            if (askedBy is null && interviewerNames.Count > 0 && !speaker.Contains("面试官")) continue;
            if (!text.Contains('?') && !text.Contains('？')) continue;
            string? answer = null;
            if (i + 1 < turns.Count && IsCandidate(turns[i + 1].Speaker)) answer = turns[i + 1].Text;
            result.Add((text, answer, askedBy ?? speaker, "Technical"));
        }
        return result;
    }
}

/// <summary>生成结构化备考报告(AI 优先;AI 未配置时本地聚合兜底并标注 generatedBy)。
/// 2026-10-21:生成语言跟随账号语言(Language 参数);数据源按 AiGateway 的用户级
/// 「备考数据源」配置接入 —— 题库/公司背景开关决定内部输入,网络面经/Glassdoor
/// 经网关检索后才进提示词,状态如实写进报告 sources.details,绝不编造面经。</summary>
public sealed record GeneratePrepReportCommand(
    Guid UserId, Guid EntryId, Guid RoundId, string? JdText = null, string? ResumeText = null,
    string? Experiences = null, string? Language = null,
    // 票207：沟通邮件摘录（前端按 thread 分组、原文截断）+ 读取状态 + 开关（默认开）。
    string? Communications = null, string? CommunicationsStatus = null, int CommunicationsCount = 0,
    bool IncludeCommunications = true) : IRequest<Result<string>>;

/// <summary>一次生成实际用到的数据源上下文(配置开关 + 外部检索结果)。</summary>
internal sealed record PrepSourceContext(
    bool QuestionBankEnabled, bool CompanyBackgroundEnabled,
    PrepSourceSearchDto? Web, PrepSourceSearchDto? Glassdoor);

public sealed class GeneratePrepReportCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GeneratePrepReportCommand, Result<string>>
{
    public async Task<Result<string>> Handle(GeneratePrepReportCommand request, CancellationToken ct)
    {
        var e = await ReassignAssetCommandHandler.LoadFull(db, request.EntryId, request.UserId, ct);
        if (e is null) return Result.Failure<string>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<string>(Error.NotFound("轮次"));

        var lang = NormalizeLanguage(request.Language);
        var ctx = await LoadSourceContext(e, round, ct);
        var sources = BuildSources(e, request, ctx);
        var oldRaw = round.PrepReportJson;
        var stored = TryStoredReport(oldRaw);

        string reportJson;
        if (stored is null)
        {
            // 首轮 Generate = 创建（整份 AI 新写、结构规范；框架要点只作显示 fallback，
            // 从不带入存储）——与旧行为一致。
            try
            {
                var data = BuildSourceData(e, round, request, ctx);
                var cls = PrepReportClassifier.Classify(round.Stage);
                var t = PrepReportLocalTexts.For(lang);
                var prompt = PrepReportPrompts.UserPrompt(data, lang, cls);
                var completion = await ai.CompleteAsync("round-prep-report", prompt, PrepReportPrompts.SystemPrompt(lang), 0.5, 16000, ct);
                var json = GenerateRoundReviewCommandHandler.ExtractJson(completion.Text);
                if (json is null) throw new AiGatewayException("AI 返回内容无法解析为 JSON");
                using var doc = JsonDocument.Parse(json); // 校验合法性
                if (JsonNode.Parse(json) is not JsonObject root)
                    throw new JsonException("AI 返回 JSON 不是对象");
                root = PrepReportNormalizer.Normalize(root, data, lang, t, cls);
                root["generatedAt"] = DateTimeOffset.UtcNow.ToString("o");
                root["generatedBy"] = completion.Model;
                root["sources"] = sources;
                reportJson = root.ToJsonString();
            }
            catch (AiGatewayException)
            {
                // AI 未配置/失败:本地聚合兜底(JD/简历/历史原题/短板都是真实输入,只是不经 LLM 组织)
                reportJson = BuildLocalReport(e, round, request, ctx, lang, sources);
            }
            catch (JsonException)
            {
                reportJson = BuildLocalReport(e, round, request, ctx, lang, sources);
            }
        }
        else
        {
            // 票207：已有报告 → 真增量追加。已有节与已生成块一字不改；只填充空节、
            // 向 Q&A 节新增题目、向已有节追加要点（归一化去重）；追加块打 provenance。
            reportJson = await GenerateIncremental(e, round, request, ctx, lang, sources, stored, ct);
        }

        // 票207：落库前包上快照（旧根非空即入栈；首轮创建无旧根，不包）。
        reportJson = PrepReportHistory.WithHistory(reportJson, oldRaw);
        e.SetRoundPrepReport(round.Id, reportJson);
        await db.SaveChangesAsync(ct);
        return Result.Success(reportJson);
    }

    /// <summary>已存报告判定：可解析为带 sections 数组的对象才算已存；框架轮次无存储，返回 null。</summary>
    private static JsonObject? TryStoredReport(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            if (JsonNode.Parse(raw) is JsonObject root && root["sections"] is JsonArray)
                return root;
        }
        catch { /* 损坏的存量按未生成处理（快照仍会原样保存旧串，供恢复） */ }
        return null;
    }

    /// <summary>票207：增量 Generate。AI 路输出增量 JSON 后合并；AI 失败/增量不可解析时
    /// 本地全量报告转增量形态后合并（同一合并口径，绝不整份覆盖）。</summary>
    private async Task<string> GenerateIncremental(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand request, PrepSourceContext ctx, string lang, JsonObject sources,
        JsonObject stored, CancellationToken ct)
    {
        var data = BuildSourceData(e, round, request, ctx);
        var cls = PrepReportClassifier.Classify(round.Stage);
        var merged = stored.DeepClone().AsObject();
        var at = DateTimeOffset.UtcNow.ToString("o");
        JsonObject? delta = null;
        string provenance = "generate";
        try
        {
            var prompt = PrepReportPrompts.IncrementalPrompt(data, lang, cls, stored);
            var completion = await ai.CompleteAsync("round-prep-report", prompt, PrepReportPrompts.SystemPrompt(lang), 0.5, 16000, ct);
            var json = GenerateRoundReviewCommandHandler.ExtractJson(completion.Text);
            if (json is null) throw new AiGatewayException("AI 返回内容无法解析为 JSON");
            using var doc = JsonDocument.Parse(json); // 校验合法性
            if (JsonNode.Parse(json) is not JsonObject root)
                throw new JsonException("AI 返回增量 JSON 不是对象");
            delta = root;
            merged["generatedBy"] = completion.Model;
        }
        catch (Exception ex) when (ex is AiGatewayException or JsonException)
        {
            // 本地兜底：全量本地报告转增量形态再合并（script 叙事块不进追加）。
            var localJson = BuildLocalReport(e, round, request, ctx, lang, sources);
            if (JsonNode.Parse(localJson) is JsonObject localRoot)
                delta = PrepReportIncremental.ToDelta(localRoot);
            provenance = "local";
            merged["generatedBy"] = "local-aggregation";
        }
        if (delta is not null)
            PrepReportIncremental.Merge(merged, delta, provenance, at);
        merged["generatedAt"] = at;
        merged["sources"] = sources;
        return merged.ToJsonString();
    }

    // ---------------- 语言 ----------------

    /// <summary>生成语言归一化:只认 zh/en/fr,其余(含空)按 en —— 与前端三语口径一致。</summary>
    internal static string NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch { "zh" => "zh", "fr" => "fr", _ => "en" };

    // ---------------- 生成输入映射(票133):领域对象 → 公共 DTO,测试可直接构造 ----------------

    private static PrepReportSourceData BuildSourceData(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand req, PrepSourceContext ctx)
    {
        var questions = ctx.QuestionBankEnabled
            ? e.Questions.Select(q => new PrepQuestionInput(q.QuestionText, q.Category,
                q.GotStuck, q.StuckReason, q.RecommendedAnswer, q.Sequence)).ToList()
            : new List<PrepQuestionInput>();
        var weaknesses = ctx.QuestionBankEnabled
            ? e.Weaknesses.Select(w => new PrepWeaknessInput(
                w.Category, w.Title, w.Detail, w.Suggestion, w.Severity)).ToList()
            : new List<PrepWeaknessInput>();
        var fetched = new List<(string Title, string Url, string Snippet)>();
        foreach (var s in new[] { ctx.Web, ctx.Glassdoor })
        {
            if (s is null || s.Status != "fetched") continue;
            // 票207：机经 Snippet 透传进提示词（只透传搜索摘要，绝不抓正文）。
            foreach (var r in s.Results.Take(5)) fetched.Add((r.Title, r.Url, r.Snippet ?? ""));
        }
        // 票207：沟通邮件只在开关开时进提示词（默认开）；关时按无沟通邮件处理。
        var commText = req.IncludeCommunications ? req.Communications : null;
        var commStatus = req.IncludeCommunications ? (req.CommunicationsStatus ?? "none") : "disabled";
        var commCount = req.IncludeCommunications ? req.CommunicationsCount : 0;
        return new PrepReportSourceData(
            e.CompanyName, e.Role, e.CompanyProfile,
            round.Order, round.Stage, round.Interviewers, round.Format, round.ScheduledDate,
            req.JdText, e.JdText, e.JdSummary, req.ResumeText, req.Experiences,
            ctx.QuestionBankEnabled, ctx.CompanyBackgroundEnabled,
            fetched, questions, weaknesses,
            commText, commStatus, commCount);
    }

    // ---------------- 数据源上下文 ----------------

    /// <summary>
    /// 读 AiGateway 的备考数据源配置;已启用的外部源当场检索。
    /// 网关不可达时内部源按默认口径、外部源标 failed —— 不编造、不阻断生成。
    /// </summary>
    private async Task<PrepSourceContext> LoadSourceContext(InterviewEntry e, InterviewRound round, CancellationToken ct)
    {
        try
        {
            var s = await ai.GetPrepSourceSettingsAsync(ct);
            PrepSourceSearchDto? web = null, glassdoor = null;
            if (s.WebExperiencesEnabled) web = await SafeSearch("web", e, round, ct);
            if (s.GlassdoorEnabled) glassdoor = await SafeSearch("glassdoor", e, round, ct);
            return new PrepSourceContext(s.QuestionBankEnabled, s.CompanyBackgroundEnabled, web, glassdoor);
        }
        catch (AiGatewayException)
        {
            return new PrepSourceContext(true, true,
                new PrepSourceSearchDto("web", "failed", null, [], null),
                new PrepSourceSearchDto("glassdoor", "failed", null, [], null));
        }
    }

    private async Task<PrepSourceSearchDto> SafeSearch(string source, InterviewEntry e, InterviewRound round, CancellationToken ct)
    {
        try { return await ai.SearchPrepSourcesAsync(source, e.CompanyName, e.Role, round.Stage, ct); }
        catch (AiGatewayException) { return new PrepSourceSearchDto(source, "failed", null, [], null); }
    }

    /// <summary>
    /// 报告 sources:旧字段(jd/company/experiences/resume/pastQuestions/weaknesses)
    /// 保持兼容;details 逐项给真实状态(fetched/disabled/notConfigured/noData/failed)
    /// 与外部来源链接 —— 前端 Sources 行按它展示,不许猜。
    /// </summary>
    private static JsonObject BuildSources(InterviewEntry e, GeneratePrepReportCommand req, PrepSourceContext ctx)
    {
        var pastCount = ctx.QuestionBankEnabled ? e.Questions.Count : 0;
        var companyUsed = ctx.CompanyBackgroundEnabled && !string.IsNullOrWhiteSpace(e.CompanyProfile);
        // 票207：沟通邮件明细（每源一条，失败/未关联如实标注，绝不静默缺失）。
        var (commStatus, commNote) = !req.IncludeCommunications
            ? ("disabled", "未启用")
            : (req.CommunicationsStatus ?? "none") switch
            {
                "failed" => ("failed", "读取失败，已用其余来源继续"),
                "none" => ("noData", "未关联投递"),
                _ => (req.CommunicationsCount > 0 ? "fetched" : "noData",
                    req.CommunicationsCount > 0 ? "仅本条目" : null),
            };
        var details = new JsonArray
        {
            Detail("questionBank",
                !ctx.QuestionBankEnabled ? "disabled" : pastCount > 0 ? "fetched" : "noData", pastCount),
            Detail("company",
                !ctx.CompanyBackgroundEnabled ? "disabled" : companyUsed ? "fetched" : "noData", null),
            Detail("web", ctx.Web?.Status ?? "disabled", ctx.Web?.Results.Count,
                ctx.Web?.Results.Select(r => r.Url), ctx.Web?.Message),
            Detail("glassdoor", ctx.Glassdoor?.Status ?? "disabled", ctx.Glassdoor?.Results.Count,
                ctx.Glassdoor?.Results.Select(r => r.Url), ctx.Glassdoor?.Message),
            Detail("manualExperiences",
                !string.IsNullOrWhiteSpace(req.Experiences) ? "fetched" : "noData", null),
            Detail("communications", commStatus,
                req.IncludeCommunications ? req.CommunicationsCount : null, null, commNote)
        };
        return new JsonObject
        {
            ["jd"] = !string.IsNullOrWhiteSpace(req.JdText) || !string.IsNullOrWhiteSpace(e.JdText) || !string.IsNullOrWhiteSpace(e.JdSummary),
            ["company"] = companyUsed,
            ["experiences"] = ctx.Web?.Status == "fetched" || ctx.Glassdoor?.Status == "fetched"
                || !string.IsNullOrWhiteSpace(req.Experiences),
            ["resume"] = !string.IsNullOrWhiteSpace(req.ResumeText),
            ["pastQuestions"] = pastCount,
            ["weaknesses"] = e.Weaknesses.Count,
            ["details"] = details
        };
    }

    /// <summary>票207：details 机制扩展——每源一条 {type, status, count, note}（type 与 key 同值）。</summary>
    private static JsonObject Detail(string key, string status, int? count, IEnumerable<string>? links = null, string? note = null)
    {
        var o = new JsonObject { ["key"] = key, ["type"] = key, ["status"] = status };
        if (count is not null) o["count"] = count.Value;
        if (links is not null)
        {
            var arr = new JsonArray();
            foreach (var l in links) arr.Add(l);
            o["links"] = arr;
        }
        if (!string.IsNullOrWhiteSpace(note)) o["note"] = note;
        return o;
    }

    // ---------------- 本地兜底(票133):委托公共 builder,与归一化脚手架同一组代码 ----------------

    /// <summary>本地聚合报告:结构与 AI 版一致;题库关 → 不带原题,公司背景关 → sources 如实标 disabled。</summary>
    internal static string BuildLocalReport(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand req, PrepSourceContext ctx, string lang, JsonObject sources)
    {
        var data = BuildSourceData(e, round, req, ctx);
        var cls = PrepReportClassifier.Classify(round.Stage);
        var t = PrepReportLocalTexts.For(lang);
        var routed = data.QuestionBankEnabled
            ? PrepReportRouting.RouteQuestions(data.Questions, cls)
            : new RoutedPrepQuestions([], [], [], [], []);
        return PrepReportBuilders.BuildLocalReport(data, lang, t, cls, routed, sources).ToJsonString();
    }
}