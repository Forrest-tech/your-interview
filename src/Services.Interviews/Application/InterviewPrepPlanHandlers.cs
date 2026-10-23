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

// ============================ 备考方案(2026-10-19) ============================
// AI 生成某轮备考方案,存为 Kind=PrepDoc / Phase=Pre 的轮次资产:
//   - 面试前:输入 JD + 公司情况 + 简历 + 全网机经 + 面试要求
//   - 下一轮:自动带入之前所有轮次的数据(转录/题目/反馈/分析报告)
// 全网机经当前为输入字段(粘贴);自动 web 搜索待接入搜索 API key
// (Tavily/Serper),见 GeneratePrepPlanCommand.Experiences 注释。

public sealed record PrepPlanDto(
    Guid AssetId, Guid EntryId, Guid RoundId, int RoundOrder, string Markdown,
    string? Model, DateTimeOffset GeneratedAt);

/// <summary>生成某轮备考方案(存为轮次资产)。Experiences=全网机经文本(手动粘贴;自动搜索待配 key)。</summary>
public sealed record GeneratePrepPlanCommand(
    Guid UserId, Guid EntryId, Guid RoundId,
    string? JdText = null,
    string? ResumeText = null,
    string? Experiences = null,
    string? Requirements = null) : IRequest<Result<PrepPlanDto>>;

/// <summary>取某轮最新的备考方案(在线阅读用)。</summary>
public sealed record GetPrepPlanQuery(Guid UserId, Guid EntryId, Guid RoundId)
    : IRequest<Result<PrepPlanDto>>;

public sealed class GeneratePrepPlanCommandValidator : AbstractValidator<GeneratePrepPlanCommand>
{
    public GeneratePrepPlanCommandValidator()
    {
        RuleFor(x => x.EntryId).NotEmpty();
        RuleFor(x => x.RoundId).NotEmpty();
    }
}

public sealed class GeneratePrepPlanCommandHandler(InterviewsDbContext db, IAiGatewayClient ai)
    : IRequestHandler<GeneratePrepPlanCommand, Result<PrepPlanDto>>
{
    public async Task<Result<PrepPlanDto>> Handle(GeneratePrepPlanCommand request, CancellationToken ct)
    {
        var e = await db.Entries
            .Include(x => x.Rounds)
            .Include(x => x.Assets)
            .Include(x => x.Questions)
            .Include(x => x.Weaknesses)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<PrepPlanDto>(Error.NotFound("面试条目"));
        var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
        if (round is null) return Result.Failure<PrepPlanDto>(Error.NotFound("轮次"));

        var prompt = BuildPrompt(e, round, request);

        AiCompletionResult completion;
        try
        {
            completion = await ai.CompleteAsync("interview-prep-plan", prompt,
                "你是资深面试教练。只输出中文 Markdown 备考方案,结构清晰、可直接执行,不要多余寒暄。",
                0.7, 6000, ct);
        }
        catch (AiGatewayException ex) when (ex.IsConfigurationError)
        {
            return Result.Failure<PrepPlanDto>(
                Error.Validation("PrepPlan.AiNotConfigured", "AI 未配置:请先在设置里配置模型凭据。"));
        }
        catch (AiGatewayException ex)
        {
            return Result.Failure<PrepPlanDto>(
                Error.Validation("PrepPlan.AiFailed", ex.Message));
        }

        try
        {
            var asset = e.AttachAsset(AssetKind.PrepDoc,
                $"备考方案-Round{round.Order}.md",
                "text/markdown",
                completion.Text.Length, null, null,
                transcriptText: completion.Text,
                roundId: round.Id, phase: AssetPhase.Pre);
            await db.SaveChangesAsync(ct);
            return Result.Success(new PrepPlanDto(asset.Id, e.Id, round.Id, round.Order,
                completion.Text, completion.Model, asset.UploadedAt));
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<PrepPlanDto>(Error.Conflict("PrepPlan.InvalidState", ex.Message));
        }
    }

    public static string BuildPrompt(InterviewEntry e, InterviewRound round, GeneratePrepPlanCommand req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 备考目标:{e.CompanyName} —— {e.Role}(第 {round.Order} 轮 / {round.Stage})");
        if (!string.IsNullOrWhiteSpace(round.Interviewers))
            sb.AppendLine($"面试官:{round.Interviewers}");
        if (!string.IsNullOrWhiteSpace(round.Format))
            sb.AppendLine($"形式:{round.Format}");

        // 本轮输入
        var jd = req.JdText;
        if (!string.IsNullOrWhiteSpace(jd))
            sb.AppendLine($"## JD\n{Trunc(jd, 8000)}");
        else if (!string.IsNullOrWhiteSpace(e.JdSummary))
            sb.AppendLine($"## JD 要点\n{e.JdSummary}");
        else if (!string.IsNullOrWhiteSpace(e.JdText))
            sb.AppendLine($"## JD 原文(截断)\n{Trunc(e.JdText, 6000)}");
        if (!string.IsNullOrWhiteSpace(e.CompanyProfile))
            sb.AppendLine($"## 公司情况\n{Trunc(e.CompanyProfile, 4000)}");
        if (!string.IsNullOrWhiteSpace(req.ResumeText))
            sb.AppendLine($"## 候选人简历\n{Trunc(req.ResumeText, 8000)}");
        if (!string.IsNullOrWhiteSpace(req.Experiences))
            sb.AppendLine($"## 全网机经(候选人提供)\n{Trunc(req.Experiences, 6000)}");
        if (!string.IsNullOrWhiteSpace(req.Requirements))
            sb.AppendLine($"## 本轮面试要求\n{Trunc(req.Requirements, 4000)}");

        // 之前所有轮次的数据(下一轮备考的关键输入)
        var prev = e.Rounds.Where(r => r.Order < round.Order).OrderBy(r => r.Order).ToList();
        if (prev.Count > 0)
        {
            sb.AppendLine("## 之前轮次复盘(重点:吸取教训,本轮针对性改进)");
            foreach (var r in prev)
            {
                sb.AppendLine($"### 第 {r.Order} 轮({r.Stage},结果:{r.Outcome})");
                if (!string.IsNullOrWhiteSpace(r.Feedback))
                    sb.AppendLine($"反馈:{Trunc(r.Feedback, 2000)}");
                if (!string.IsNullOrWhiteSpace(r.Transcript))
                    sb.AppendLine($"转录:{Trunc(r.Transcript, 3000)}");
                var rAssets = e.Assets.Where(a => a.RoundId == r.Id && !string.IsNullOrWhiteSpace(a.TranscriptText)).ToList();
                foreach (var a in rAssets)
                    sb.AppendLine($"材料({a.Kind},{a.FileName}):{Trunc(a.TranscriptText!, 3000)}");
            }
        }

        // 题目库(问答导入):暴露过的弱点重点补
        var stuck = e.Questions.Where(q => q.GotStuck).Take(20).ToList();
        if (stuck.Count > 0)
        {
            sb.AppendLine("## 曾卡壳的题目(重点补强)");
            foreach (var q in stuck)
                sb.AppendLine($"- {Trunc(q.QuestionText, 300)}");
        }

        // 短板 + 分析报告
        if (e.Weaknesses.Count > 0)
        {
            sb.AppendLine("## 已识别短板");
            foreach (var w in e.Weaknesses.Take(15))
                sb.AppendLine($"- {w.Title}: {Trunc(w.Suggestion ?? w.Detail ?? "", 200)}");
        }
        if (!string.IsNullOrWhiteSpace(e.AnalysisSummary))
            sb.AppendLine($"## 分析报告摘要\n{Trunc(e.AnalysisSummary, 3000)}");

        sb.AppendLine();
        sb.AppendLine("请输出备考方案,包含:1)本轮考察重点预测 2)必准备的题目清单(带答题要点) " +
            "3)简历针对性包装建议 4)上一轮教训的改进动作(如有) 5)面试当天 Checklist。");
        return sb.ToString();
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed class GetPrepPlanQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetPrepPlanQuery, Result<PrepPlanDto>>
{
    public async Task<Result<PrepPlanDto>> Handle(GetPrepPlanQuery request, CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<PrepPlanDto>(Error.NotFound("面试条目"));

        var a = await db.Assets.AsNoTracking()
            .Where(x => x.InterviewEntryId == request.EntryId
                && x.RoundId == request.RoundId
                && x.Kind == AssetKind.PrepDoc)
            .OrderByDescending(x => x.UploadedAt)
            .FirstOrDefaultAsync(ct);
        if (a is null) return Result.Failure<PrepPlanDto>(Error.NotFound("备考方案"));
        var order = await db.Entries.AsNoTracking()
            .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
            .SelectMany(e => e.Rounds)
            .Where(r => r.Id == request.RoundId)
            .Select(r => r.Order)
            .FirstOrDefaultAsync(ct);
        return Result.Success(new PrepPlanDto(a.Id, request.EntryId, request.RoundId, order,
            a.TranscriptText ?? "", null, a.UploadedAt));
    }
}

