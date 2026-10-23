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
                "闲聊寒暄不要抽。转写:\n" + (transcript.Length > 12000 ? transcript[..12000] : transcript);
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
    string? Experiences = null, string? Language = null) : IRequest<Result<string>>;

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

        string reportJson;
        try
        {
            var prompt = BuildAiPrompt(e, round, request, ctx, lang);
            var completion = await ai.CompleteAsync("round-prep-report", prompt, SystemPromptFor(lang), 0.5, 8000, ct);
            var json = GenerateRoundReviewCommandHandler.ExtractJson(completion.Text);
            if (json is null) throw new AiGatewayException("AI 返回内容无法解析为 JSON");
            using var doc = JsonDocument.Parse(json); // 校验合法性
            var root = JsonNode.Parse(json)!.AsObject();
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

        e.SetRoundPrepReport(round.Id, reportJson);
        await db.SaveChangesAsync(ct);
        return Result.Success(reportJson);
    }

    // ---------------- 语言 ----------------

    /// <summary>生成语言归一化:只认 zh/en/fr,其余(含空)按 en —— 与前端三语口径一致。</summary>
    internal static string NormalizeLanguage(string? language) =>
        language?.Trim().ToLowerInvariant() switch { "zh" => "zh", "fr" => "fr", _ => "en" };

    private static string LanguageName(string lang) => lang switch
    {
        "zh" => "Simplified Chinese (简体中文)",
        "fr" => "French (Français)",
        _ => "English"
    };

    private static string SystemPromptFor(string lang) =>
        "You are an interview coach for North American tech companies. Output exactly one JSON object, no other text.\n" +
        $"OUTPUT LANGUAGE: every natural-language value (titles, text, items, questions, standard answers) MUST be written in {LanguageName(lang)}. JSON keys and section ids stay exactly as specified below.\n" +
        "Structure: {\"sections\":[{\"id\":\"greeting|intro-listen|self-intro|deep-dive|tech-qa|behavioral|ask-back|closing\",\"title\":\"section title\",\"blocks\":[{\"type\":\"script|points|qa-ref|list\",\"title\":\"optional\",\"text\":\"body for script/points\",\"items\":[\"bullets for list\"]}],\"questions\":[{\"question\":\"...\",\"category\":\"...\",\"sourceTags\":[\"...\"],\"weaknessRelated\":true/false,\"stdSpoken\":\"spoken standard answer\",\"stdTechnical\":\"technical standard answer\"}]}]}\n" +
        "Output all 8 sections in real interview order: greeting, intro-listen, self-intro (60-second and 2-minute script versions in blocks), deep-dive, tech-qa (questions in questions), behavioral (STAR), ask-back, closing.\n" +
        "Technical questions may only be grounded in: JD keywords, the fetched interview experiences given in the prompt, the candidate's past questions, and identified weaknesses (weaknessRelated=true). Never invent interview experiences or attribute statements to a source that did not say them. Standard answers: first-person spoken version + bullet-style technical version, North American interview style.";

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
        var details = new JsonArray
        {
            Detail("questionBank",
                !ctx.QuestionBankEnabled ? "disabled" : pastCount > 0 ? "fetched" : "noData", pastCount),
            Detail("company",
                !ctx.CompanyBackgroundEnabled ? "disabled" : companyUsed ? "fetched" : "noData", null),
            Detail("web", ctx.Web?.Status ?? "disabled", ctx.Web?.Results.Count,
                ctx.Web?.Results.Select(r => r.Url)),
            Detail("glassdoor", ctx.Glassdoor?.Status ?? "disabled", ctx.Glassdoor?.Results.Count,
                ctx.Glassdoor?.Results.Select(r => r.Url)),
            Detail("manualExperiences",
                !string.IsNullOrWhiteSpace(req.Experiences) ? "fetched" : "noData", null)
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

    private static JsonObject Detail(string key, string status, int? count, IEnumerable<string>? links = null)
    {
        var o = new JsonObject { ["key"] = key, ["status"] = status };
        if (count is not null) o["count"] = count.Value;
        if (links is not null)
        {
            var arr = new JsonArray();
            foreach (var l in links) arr.Add(l);
            o["links"] = arr;
        }
        return o;
    }

    // ---------------- 提示词 ----------------

    private static string BuildAiPrompt(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand req, PrepSourceContext ctx, string lang)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Prep target: {e.CompanyName} — {e.Role} (Round {round.Order} / {round.Stage})");
        sb.AppendLine($"Output language: {LanguageName(lang)}");
        if (!string.IsNullOrWhiteSpace(round.Interviewers)) sb.AppendLine($"Interviewers: {round.Interviewers}");
        if (!string.IsNullOrWhiteSpace(round.Format)) sb.AppendLine($"Format: {round.Format}");
        var jd = !string.IsNullOrWhiteSpace(req.JdText) ? req.JdText
            : !string.IsNullOrWhiteSpace(e.JdText) ? e.JdText : e.JdSummary;
        if (!string.IsNullOrWhiteSpace(jd)) sb.AppendLine($"## JD\n{Trunc(jd!, 6000)}");
        if (ctx.CompanyBackgroundEnabled && !string.IsNullOrWhiteSpace(e.CompanyProfile))
            sb.AppendLine($"## Company background\n{Trunc(e.CompanyProfile, 3000)}");
        if (!string.IsNullOrWhiteSpace(req.ResumeText)) sb.AppendLine($"## Submitted resume\n{Trunc(req.ResumeText, 6000)}");
        if (!string.IsNullOrWhiteSpace(req.Experiences)) sb.AppendLine($"## Interview experiences pasted by the user\n{Trunc(req.Experiences, 5000)}");
        AppendFetched(sb, "## Fetched web interview experiences (search results; ground questions only in these)", ctx.Web);
        AppendFetched(sb, "## Fetched Glassdoor interview experiences (search results; ground questions only in these)", ctx.Glassdoor);
        if (ctx.QuestionBankEnabled)
        {
            var past = e.Questions.Take(30).ToList();
            if (past.Count > 0)
            {
                sb.AppendLine("## Past questions from similar roles (question bank)");
                foreach (var q in past) sb.AppendLine($"- {Trunc(q.QuestionText, 250)}");
            }
        }
        if (e.Weaknesses.Count > 0)
        {
            sb.AppendLine("## Identified technical weaknesses");
            foreach (var w in e.Weaknesses.Take(15)) sb.AppendLine($"- {w.Title}:{Trunc(w.Suggestion ?? w.Detail ?? "", 150)}");
        }
        return sb.ToString();
    }

    private static void AppendFetched(StringBuilder sb, string heading, PrepSourceSearchDto? search)
    {
        if (search is null || search.Status != "fetched" || search.Results.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var r in search.Results.Take(5))
            sb.AppendLine($"- {Trunc(r.Title, 200)} ({r.Url})\n  {Trunc(r.Snippet, 500)}");
    }

    // ---------------- 本地兜底(三语,跟随生成语言) ----------------

    /// <summary>本地聚合报告:结构与 AI 版一致;题库关 → 不带原题,公司背景关 → sources 如实标 disabled。</summary>
    internal static string BuildLocalReport(InterviewEntry e, InterviewRound round,
        GeneratePrepReportCommand req, PrepSourceContext ctx, string lang, JsonObject sources)
    {
        var t = LocalTexts.For(lang);
        var questions = new List<object>();
        if (ctx.QuestionBankEnabled)
        {
            var weaknessTitles = e.Weaknesses.Select(w => w.Title).ToList();
            foreach (var q in e.Questions.OrderByDescending(q => q.GotStuck).Take(20))
            {
                var weak = q.GotStuck || weaknessTitles.Any(x =>
                    q.QuestionText.Contains(x, StringComparison.OrdinalIgnoreCase));
                questions.Add(new
                {
                    question = q.QuestionText,
                    category = q.Category.ToString(),
                    sourceTags = new[] { t.PastTag },
                    weaknessRelated = weak,
                    stdSpoken = (string?)null,
                    stdTechnical = q.RecommendedAnswer,
                    knowledgeId = (Guid?)null
                });
            }
        }
        var fetchedItems = new List<string>();
        foreach (var s in new[] { ctx.Web, ctx.Glassdoor })
        {
            if (s is null || s.Status != "fetched") continue;
            foreach (var r in s.Results.Take(5)) fetchedItems.Add($"{r.Title} — {r.Url}");
        }
        var techBlocks = new List<object>();
        if (fetchedItems.Count > 0)
            techBlocks.Add(new { type = "list", title = t.FetchedTitle, items = fetchedItems.ToArray() });

        var sections = new object[]
        {
            new { id = "greeting", title = t.Titles[0], blocks = new object[] {
                new { type = "points", items = t.Greeting } }, questions = Array.Empty<object>() },
            new { id = "intro-listen", title = t.Titles[1], blocks = new object[] {
                new { type = "points", items = t.IntroListen } }, questions = Array.Empty<object>() },
            new { id = "self-intro", title = t.Titles[2], blocks = new object[] {
                new { type = "script", title = t.SelfIntroTitle, text = (string?)null } }, questions = Array.Empty<object>() },
            new { id = "deep-dive", title = t.Titles[3], blocks = new object[] {
                new { type = "points", items = t.DeepDive } }, questions = Array.Empty<object>() },
            new { id = "tech-qa", title = t.Titles[4], blocks = techBlocks.ToArray(), questions = questions.ToArray() },
            new { id = "behavioral", title = t.Titles[5], blocks = new object[] {
                new { type = "points", items = t.Behavioral } }, questions = Array.Empty<object>() },
            new { id = "ask-back", title = t.Titles[6], blocks = new object[] {
                new { type = "list", items = t.AskBack } }, questions = Array.Empty<object>() },
            new { id = "closing", title = t.Titles[7], blocks = new object[] {
                new { type = "points", items = t.Closing } }, questions = Array.Empty<object>() },
        };
        var report = new
        {
            generatedAt = DateTimeOffset.UtcNow.ToString("o"),
            generatedBy = "local-aggregation",
            sources,
            sections
        };
        return JsonFieldAdapter.SerializeRaw(report);
    }

    /// <summary>本地兜底文案三套(zh/en/fr)—— 与系统三语口径一致,禁止只写中文。</summary>
    private sealed record LocalTexts(
        string[] Titles, string[] Greeting, string[] IntroListen, string SelfIntroTitle,
        string[] DeepDive, string[] Behavioral, string[] AskBack, string[] Closing,
        string PastTag, string FetchedTitle)
    {
        public static LocalTexts For(string lang) => lang switch
        {
            "zh" => new LocalTexts(
                new[] { "1 · 开场寒暄", "2 · 听对方介绍", "3 · 自我介绍", "4 · 项目深挖", "5 · 技术问答", "6 · 行为题 STAR", "7 · 反问环节", "8 · 收尾与注意事项" },
                new[] { "提前 5 分钟进入会议", "开场一句:Hi, thanks for taking the time to speak with me today." },
                new[] { "记下团队规模、在招岗位的职责边界、当前项目关键词", "回应一句:That sounds great — the part about X is exactly what I've been doing." },
                "60 秒版(请按你的简历改写后朗读练习)",
                new[] { "准备 1 个核心项目的背景-难点-方案-结果四段式讲法", "数字先行:规模、性能提升、影响范围" },
                new[] { "每个故事按 Situation-Task-Action-Result 讲,Result 带数字", "准备:冲突、失败、赶工、学新技术 各一个" },
                new[] { "这个岗位前 90 天最重要的交付是什么?", "团队现在最大的技术挑战是什么?", "这个 role 的成功怎么衡量?" },
                new[] { "结束前重申兴趣:Based on what I've heard, this role is exactly what I'm looking for.", "当天:提前测试设备与网络,水杯与简历放手边" },
                "原题", "抓取到的面经来源"),
            "fr" => new LocalTexts(
                new[] { "1 · Accueil", "2 · Écouter leur présentation", "3 · Présentation", "4 · Projets en détail", "5 · Questions techniques", "6 · Comportement (STAR)", "7 · Vos questions", "8 · Conclusion et rappels" },
                new[] { "Rejoignez la réunion 5 minutes à l'avance", "Ouverture : Hi, thanks for taking the time to speak with me today." },
                new[] { "Notez la taille de l'équipe, les responsabilités du poste et les mots-clés du projet actuel", "Répondez : That sounds great — the part about X is exactly what I've been doing." },
                "Version 60 secondes (à adapter à votre CV et à pratiquer à voix haute)",
                new[] { "Préparez un projet clé en quatre temps : contexte → défi → approche → résultats", "Les chiffres d'abord : échelle, gains de performance, portée" },
                new[] { "Chaque histoire suit Situation-Tâche-Action-Résultat, avec des chiffres dans le résultat", "Préparez : un conflit, un échec, une urgence, un nouvel apprentissage" },
                new[] { "Quelles sont les livraisons les plus importantes des 90 premiers jours ?", "Quel est le plus grand défi technique de l'équipe en ce moment ?", "Comment mesure-t-on la réussite dans ce poste ?" },
                new[] { "Réaffirmez votre intérêt : Based on what I've heard, this role is exactly what I'm looking for.", "Le jour même : testez l'équipement et le réseau à l'avance, eau et CV à portée de main" },
                "Question passée", "Sources de retours récupérées"),
            _ => new LocalTexts(
                new[] { "1 · Warm-up", "2 · Hear their introduction", "3 · Self-introduction", "4 · Project deep-dive", "5 · Technical Q&A", "6 · Behavioral (STAR)", "7 · Questions to ask", "8 · Wrap-up & reminders" },
                new[] { "Join the meeting 5 minutes early", "Open with: Hi, thanks for taking the time to speak with me today." },
                new[] { "Note the team size, the role's responsibilities, and current project keywords", "Respond with: That sounds great — the part about X is exactly what I've been doing." },
                "60-second version (adapt it to your resume and practise aloud)",
                new[] { "Prepare one core project as background → challenge → approach → results", "Lead with numbers: scale, performance gains, impact" },
                new[] { "Tell every story as Situation-Task-Action-Result, with numbers in the Result", "Prepare one story each: conflict, failure, deadline crunch, learning something new" },
                new[] { "What are the most important deliverables in the first 90 days?", "What is the team's biggest technical challenge right now?", "How is success measured for this role?" },
                new[] { "Restate your interest: Based on what I've heard, this role is exactly what I'm looking for.", "On the day: test your equipment and network early; water and resume within reach" },
                "Past question", "Fetched experience sources")
        };
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}
