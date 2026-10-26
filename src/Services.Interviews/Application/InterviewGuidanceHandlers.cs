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

// ============================ 指导材料(缺口2) ============================

/// <summary>手动触发:为该条目生成一份新的面试指导材料(版本号自动 +1)。</summary>
public sealed record GenerateGuidanceCommand(
    Guid UserId, Guid EntryId,
    string? JdText = null,
    string? ResumeText = null,
    string? InterviewExperiences = null,
    string? CustomRequirements = null) : IRequest<Result<GuidanceMaterialDto>>;

public sealed record GetGuidanceQuery(Guid UserId, Guid EntryId, int? Version = null)
    : IRequest<Result<GuidanceMaterialDto>>;

public sealed record ListGuidanceVersionsQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<IReadOnlyList<GuidanceVersionDto>>>;

public sealed class GenerateGuidanceCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GenerateGuidanceCommand, Result<GuidanceMaterialDto>>
{
    public async Task<Result<GuidanceMaterialDto>> Handle(GenerateGuidanceCommand request, CancellationToken ct)
    {
        var e = await db.Entries
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            .Include(x => x.Rounds)
            .Include(x => x.Materials)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<GuidanceMaterialDto>(Error.NotFound("面试条目"));

        var prompt = BuildPrompt(e, request);

        AiCompletionResult completion;
        try
        {
            completion = await ai.CompleteAsync("interview-guidance", prompt,
                "你是资深面试教练,只输出 Markdown,不要多余寒暄。", 0.7, 4000, ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            return Result.Failure<GuidanceMaterialDto>(
                Error.Validation("Guidance.AiNotConfigured", "AI 未配置:请先在设置里配置模型凭据。"));
        }
        catch (AiGatewayException ex)
        {
            return Result.Failure<GuidanceMaterialDto>(Error.Validation("Guidance.AiFailed", ex.Message));
        }

        var m = e.AddMaterial(completion.Text, completion.Model,
            completion.PromptTokens, completion.CompletionTokens);
        await db.SaveChangesAsync(ct);
        return Result.Success(m.ToDto());
    }

    private static string BuildPrompt(InterviewEntry e, GenerateGuidanceCommand req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 面试对象:{e.CompanyName} —— {e.Role}");
        if (!string.IsNullOrWhiteSpace(e.CompanyProfile))
            sb.AppendLine($"## 公司情况\n{e.CompanyProfile}");
        // JD:优先用本次传入的,fallback 到 entry 存的
        var jd = req.JdText;
        if (!string.IsNullOrWhiteSpace(jd))
            sb.AppendLine($"## JD(本次提供)\n{jd[..Math.Min(jd.Length, 8000)]}");
        else if (!string.IsNullOrWhiteSpace(e.JdSummary))
            sb.AppendLine($"## JD 要点\n{e.JdSummary}");
        else if (!string.IsNullOrWhiteSpace(e.JdText))
            sb.AppendLine($"## JD 原文(截断)\n{e.JdText[..Math.Min(e.JdText.Length, 6000)]}");
        // 简历
        if (!string.IsNullOrWhiteSpace(req.ResumeText))
            sb.AppendLine($"## 候选人简历\n{req.ResumeText[..Math.Min(req.ResumeText.Length, 8000)]}");
        // 全网面经
        if (!string.IsNullOrWhiteSpace(req.InterviewExperiences))
            sb.AppendLine($"## 相关面经(全网检索)\n{req.InterviewExperiences[..Math.Min(req.InterviewExperiences.Length, 8000)]}");
        // 用户自定义要求
        if (!string.IsNullOrWhiteSpace(req.CustomRequirements))
            sb.AppendLine($"## 用户特别要求\n{req.CustomRequirements}");

        var rounds = e.Rounds.OrderBy(r => r.Order).ToList();
        if (rounds.Count > 0)
        {
            sb.AppendLine("## 已知轮次");
            foreach (var r in rounds)
                sb.AppendLine($"- 第{r.Order}轮 {r.Stage}:面试官 {r.Interviewers ?? "未知"},形式 {r.Format ?? "未知"},结果 {r.Outcome}");
        }

        if (!string.IsNullOrWhiteSpace(e.AnalysisSummary))
            sb.AppendLine($"## AI 总评\n{e.AnalysisSummary}");
        sb.AppendLine($"## 六维分数:发音 {e.PronunciationScore},流利 {e.FluencyScore}," +
                      $"结构 {e.StructureScore},技术深度 {e.TechnicalDepthScore},相关度 {e.RelevanceScore}");

        var qs = e.Questions.OrderBy(q => q.Sequence).ToList();
        if (qs.Count > 0)
        {
            sb.AppendLine("## 历史问答(重点看答得不好的)");
            foreach (var q in qs.Take(30))
            {
                sb.AppendLine($"- [{q.Category}] {q.QuestionText}");
                if (!string.IsNullOrWhiteSpace(q.MyAnswerText))
                    sb.AppendLine($"  我的回答:{q.MyAnswerText[..Math.Min(q.MyAnswerText.Length, 500)]}");
                if (!string.IsNullOrWhiteSpace(q.Assessment))
                    sb.AppendLine($"  诊断:{q.Assessment}");
                if (q.GotStuck) sb.AppendLine($"  ⚠️ 当场卡壳:{q.StuckReason}");
                if (!string.IsNullOrWhiteSpace(q.RecommendedAnswer))
                    sb.AppendLine($"  推荐答案:{q.RecommendedAnswer[..Math.Min(q.RecommendedAnswer.Length, 500)]}");
            }
        }

        var ws = e.Weaknesses.OrderByDescending(w => w.Severity).ToList();
        if (ws.Count > 0)
        {
            sb.AppendLine("## 短板清单");
            foreach (var w in ws.Take(15))
                sb.AppendLine($"- [{w.Category} 严重度{w.Severity}] {w.Title}: {w.Suggestion}");
        }

        sb.AppendLine();
        sb.AppendLine("请输出一份面试指导材料(Markdown),结构:");
        sb.AppendLine("## 1. 公司与岗位速览(3-5 条要点)");
        sb.AppendLine("## 2. 必准备的高频问题(5-8 个,带答题要点,不是完整背稿)");
        sb.AppendLine("## 3. 我的短板针对性补救(结合上面的短板和卡壳点)");
        sb.AppendLine("## 4. 每轮的注意事项(结合已知轮次)");
        sb.AppendLine("## 5. 临场 checklist(开场 30 秒 / 结构骨架 / trade-off 话术)");
        sb.AppendLine("要求:具体、可执行、不说空话;英文术语保留原文。");
        return sb.ToString();
    }
}

public sealed class GetGuidanceQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetGuidanceQuery, Result<GuidanceMaterialDto>>
{
    public async Task<Result<GuidanceMaterialDto>> Handle(GetGuidanceQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<GuidanceMaterialDto>(Error.NotFound("面试条目"));

        var q = db.GuidanceMaterials.Where(m => m.InterviewEntryId == request.EntryId);
        var m = request.Version.HasValue
            ? await q.FirstOrDefaultAsync(x => x.Version == request.Version.Value, ct)
            : await q.OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
        if (m is null) return Result.Failure<GuidanceMaterialDto>(Error.NotFound("指导材料"));
        return Result.Success(m.ToDto());
    }
}

public sealed class ListGuidanceVersionsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListGuidanceVersionsQuery, Result<IReadOnlyList<GuidanceVersionDto>>>
{
    public async Task<Result<IReadOnlyList<GuidanceVersionDto>>> Handle(
        ListGuidanceVersionsQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<IReadOnlyList<GuidanceVersionDto>>(Error.NotFound("面试条目"));

        var list = await db.GuidanceMaterials
            .Where(m => m.InterviewEntryId == request.EntryId)
            .OrderByDescending(m => m.Version)
            .Select(m => new GuidanceVersionDto(m.Id, m.Version, m.GeneratedAt, m.Model))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<GuidanceVersionDto>>(list);
    }
}

// ============================ 问答候选(缺口3) ============================

/// <summary>把该条目的问答转成候选列表,供前端勾选导入 TechStack。默认全选由前端做。</summary>
public sealed record ListQuestionCandidatesQuery(Guid UserId, Guid EntryId)
    : IRequest<Result<IReadOnlyList<QuestionCandidateDto>>>;

public sealed class ListQuestionCandidatesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListQuestionCandidatesQuery, Result<IReadOnlyList<QuestionCandidateDto>>>
{
    public async Task<Result<IReadOnlyList<QuestionCandidateDto>>> Handle(
        ListQuestionCandidatesQuery request, CancellationToken ct)
    {
        var exists = await db.Entries.AnyAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (!exists) return Result.Failure<IReadOnlyList<QuestionCandidateDto>>(Error.NotFound("面试条目"));

        var list = await db.Questions
            .Where(q => q.InterviewEntryId == request.EntryId)
            .OrderBy(q => q.Sequence)
            .Select(q => q.ToCandidateDto())
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<QuestionCandidateDto>>(list);
    }
}

