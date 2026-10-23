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

// ============================ Handler:命令 ============================

public sealed class CreateEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<CreateEntryCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateEntryCommand request, CancellationToken ct)
    {
        // FUN-03(票10):JobApplicationId 幂等 —— Tracker 点 Prep 重复点击/重试时,
        // 当前用户名下已有同 JobApplicationId 的条目就直接返回既有条目 id、不新建,
        // 否则同一投递散落两条备考记录,轮次/材料各记一半。口径与票03 用户隔离
        // 一致:只查当前用户自己的条目(全局过滤器之外再显式带 UserId 谓词);
        // 软删条目被全局过滤器排除,删除后重新 Prep 仍可正常新建。
        // 无 JobApplicationId(或空 Guid)的建单不参与去重,行为不变。
        // 存量若已有多条重复,返回最早建的那条(原始条目)。
        if (request.JobApplicationId is { } jobApplicationId && jobApplicationId != Guid.Empty)
        {
            var existingId = await db.Entries.AsNoTracking()
                .Where(x => x.UserId == request.UserId && x.JobApplicationId == jobApplicationId)
                .OrderBy(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id)
                .FirstOrDefaultAsync(ct);
            if (existingId is not null)
                return Result.Success(existingId.Value);
        }

        // CompanyId 可空:Tracker 一键创建时只知道公司名,按名复用已有公司的 Id,
        // 找不到则生成新的 —— 避免 Guid.Empty 导致公司树聚合错乱。
        var companyId = request.CompanyId;
        if (companyId is null || companyId == Guid.Empty)
        {
            var existing = await db.Entries.AsNoTracking()
                .Where(x => x.CompanyName == request.CompanyName && x.UserId == request.UserId)
                .Select(x => x.CompanyId)
                .FirstOrDefaultAsync(ct);
            companyId = existing == Guid.Empty ? Guid.NewGuid() : existing;
        }

        var entry = new InterviewEntry(companyId.Value, request.CompanyName, request.Role,
            request.JobApplicationId, userId: request.UserId);

        if (request.CompanyProfile is not null || request.JdText is not null
            || request.JdSummary is not null || request.Interviewers is not null
            || request.InterviewDate is not null || request.Location is not null
            || request.Notes is not null)
        {
            entry.UpdateBasicInfo(request.CompanyName, request.Role, request.CompanyProfile,
                request.JdText, request.JdSummary, 1, request.InterviewDate,
                request.InterviewFormat, request.Interviewers, request.Location, null,
                request.Notes);
        }

        // 从 Tracker 建条目必须至少有一个 Round 实体,否则 Rounds tab 与 Basic info 不一致
        var round = entry.AddRound();
        if (request.InterviewDate is not null || request.InterviewFormat is not null
            || request.Interviewers is not null || request.Location is not null)
        {
            entry.UpdateRound(round.Id, round.Stage, request.InterviewDate,
                request.Interviewers, request.InterviewFormat, request.Location,
                round.Outcome, null, null);
        }

        db.Entries.Add(entry);
        await db.SaveChangesAsync(ct);
        return Result.Success(entry.Id);
    }
}

public sealed class UpdateEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateEntryCommand, Result>
{
    public async Task<Result> Handle(UpdateEntryCommand request, CancellationToken ct)
    {
        // 票04:UpdateBasicInfo 要按 RoundNo 找对应轮次的定论枚举填结构化 outcome,
        // 必须带 Rounds 加载,否则 _rounds 为空、事件永远无枚举。
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        e.UpdateBasicInfo(request.CompanyName, request.Role, request.CompanyProfile,
            request.JdText, request.JdSummary, request.RoundNo, request.InterviewDate,
            request.InterviewFormat, request.Interviewers, request.Location, request.Result,
            request.Notes, request.QuestionsToAskJson);
        if (request.DebriefJson is not null)
            e.SetDebrief(request.DebriefJson);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class LinkJobApplicationCommandHandler(InterviewsDbContext db)
    : IRequestHandler<LinkJobApplicationCommand, Result>
{
    public async Task<Result> Handle(LinkJobApplicationCommand request, CancellationToken ct)
    {
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        if (e.LinkJobApplication(request.JobApplicationId))
            await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteEntryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteEntryCommand, Result>
{
    public async Task<Result> Handle(DeleteEntryCommand request, CancellationToken ct)
    {
        var e = await db.Entries.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        e.MarkDeleted();   // 软删除:全局查询过滤器会自动隐藏
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AttachAssetCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AttachAssetCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AttachAssetCommand request, CancellationToken ct)
    {
        // 带子集合加载 —— 聚合方法要往集合里加东西(Rounds 用于校验 RoundId)
        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<AssetKind>(request.Kind, true, out var kind))
            return Result.Failure<Guid>(Error.Validation("Asset.InvalidKind", "材料类型无效"));

        // 2026-10-19:阶段枚举(Pre=面试前 / Post=面试后),大小写不敏感
        AssetPhase? phase = null;
        if (!string.IsNullOrWhiteSpace(request.Phase))
        {
            if (!Enum.TryParse<AssetPhase>(request.Phase, true, out var p))
                return Result.Failure<Guid>(Error.Validation("Asset.InvalidPhase", "阶段只能是 Pre / Post"));
            phase = p;
        }

        try
        {
            var asset = e.AttachAsset(kind, request.FileName, request.ContentType, request.SizeBytes,
                request.StoragePath, request.BlobUrl, request.TranscriptText,
                request.TranscriptSegmentsJson, request.DurationSeconds, request.SourceLanguage,
                request.Sha256, request.RoundId, phase, request.MetaJson);
            await db.SaveChangesAsync(ct);
            return Result.Success(asset.Id);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Conflict("Asset.InvalidState", ex.Message));
        }
    }
}

public sealed class UploadAssetFileCommandHandler(
    InterviewsDbContext db,
    IInterviewAudioStore store,
    ILogger<UploadAssetFileCommandHandler> logger)
    : IRequestHandler<UploadAssetFileCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UploadAssetFileCommand request, CancellationToken ct)
    {
        if (request.Content is null || request.Content.CanRead == false)
            return Result.Failure<Guid>(Error.Validation("Asset.EmptyFile", "没有收到音频内容"));

        if (request.Content.Length > LocalInterviewAudioStore.MaxFileBytes)
            return Result.Failure<Guid>(Error.Validation("Asset.TooLarge",
                $"录音超过上限 {LocalInterviewAudioStore.MaxFileBytes / 1024 / 1024}MB"));

        var e = await db.Entries.Include(x => x.Assets).Include(x => x.Questions)
            .Include(x => x.Weaknesses).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        // 1) 先落盘(文件名由存储层生成,天然唯一;tmp+原子改名,崩溃不留半截文件)
        StoredAudioFile stored;
        try
        {
            stored = await store.SaveAsync(request.EntryId, request.FileName, request.Content, ct);
        }
        catch (InvalidOperationException ex)   // 扩展名白名单拒绝
        {
            return Result.Failure<Guid>(Error.Validation("Asset.BadFormat", ex.Message));
        }

        // 2) 再登记元数据。落盘成功但入库失败时,尽力清掉孤儿文件 ——
        //    留着它,巡检永远报"数据库里没有这条资产"。
        AssetPhase? phase = null;
        if (!string.IsNullOrWhiteSpace(request.Phase))
        {
            if (!Enum.TryParse<AssetPhase>(request.Phase, true, out var p2))
                return Result.Failure<Guid>(Error.Validation("Asset.InvalidPhase", "阶段只能是 Pre / Post"));
            phase = p2;
        }
        try
        {
            var asset = e.AttachAsset(AssetKind.Audio, request.FileName, request.ContentType,
                stored.SizeBytes, stored.RelativePath, null, null, null,
                request.DurationSeconds, request.SourceLanguage ?? "en", stored.Sha256,
                request.RoundId, phase);
            await db.SaveChangesAsync(ct);
            return Result.Success(asset.Id);
        }
        catch (InvalidOperationException ex)
        {
            await TryDeleteQuietly(stored.RelativePath, ct);
            return Result.Failure<Guid>(Error.Conflict("Asset.InvalidState", ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "登记录音元数据失败,清理已落盘文件 {Path}", stored.RelativePath);
            await TryDeleteQuietly(stored.RelativePath, ct);
            throw;
        }
    }

    private async Task TryDeleteQuietly(string relativePath, CancellationToken ct)
    {
        try { await store.DeleteAsync(relativePath, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "清理孤儿录音文件失败 {Path}", relativePath); }
    }
}

public sealed class GetAssetAudioQueryHandler(
    InterviewsDbContext db,
    IInterviewAudioStore store)
    : IRequestHandler<GetAssetAudioQuery, Result<(Stream, string, string)>>
{
    public async Task<Result<(Stream, string, string)>> Handle(GetAssetAudioQuery request,
        CancellationToken ct)
    {
        if (!await OwnedEntry.ExistsAsync(db, request.EntryId, request.UserId, ct))
            return Result.Failure<(Stream, string, string)>(Error.NotFound("材料"));

        var a = await db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == request.AssetId
                && x.InterviewEntryId == request.EntryId, ct);
        if (a is null) return Result.Failure<(Stream, string, string)>(Error.NotFound("材料"));

        if (string.IsNullOrWhiteSpace(a.StoragePath))
            return Result.Failure<(Stream, string, string)>(new Error("AssetAudio.NotFound",
                "该材料没有本地文件(可能是外部链接或纯文本)", ErrorType.NotFound));

        var stream = await store.OpenReadAsync(a.StoragePath, ct);
        if (stream is null)
            return Result.Failure<(Stream, string, string)>(new Error("AssetAudio.NotFound",
                "录音文件已丢失,可重新上传;系统巡检会标记此类资产", ErrorType.NotFound));

        return Result.Success((stream,
            a.ContentType ?? "application/octet-stream", a.FileName));
    }
}

/// <summary>读取转写稿(供 Analysis.Worker 文本模式取粘贴文本)。</summary>
public sealed record GetTranscriptQuery(Guid UserId, Guid EntryId, Guid AssetId, Guid? OwnerUserId = null)
    : IRequest<Result<TranscriptPayload>>;

public sealed record TranscriptPayload(string? FullText, string? SegmentsJson);

public sealed class GetTranscriptQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetTranscriptQuery, Result<TranscriptPayload>>
{
    public async Task<Result<TranscriptPayload>> Handle(GetTranscriptQuery request,
        CancellationToken ct)
    {
        var e = await OwnedEntry.LoadNoTrackingAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets), ct);
        if (e is null) return Result.Failure<TranscriptPayload>(Error.NotFound("面试条目"));
        var a = e.Assets.FirstOrDefault(x => x.Id == request.AssetId);
        if (a is null) return Result.Failure<TranscriptPayload>(Error.NotFound("材料"));
        return Result.Success(new TranscriptPayload(a.TranscriptText, a.TranscriptSegmentsJson));
    }
}

public sealed class SaveTranscriptCommandHandler(InterviewsDbContext db)
    : IRequestHandler<SaveTranscriptCommand, Result>
{
    public async Task<Result> Handle(SaveTranscriptCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions).Include(x => x.Weaknesses), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.CompleteTranscription(request.AssetId, request.FullText, request.SegmentsJson);
            // 转写稿落库 = 管线走完了前半程:同事务关单(Succeeded)
            await db.StageCloseOpenJobsAsync(request.EntryId, AnalysisJobStatus.Succeeded, null, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Transcript.InvalidState", ex.Message));
        }
    }
}

public sealed class RequestAnalysisCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RequestAnalysisCommand, Result>
{
    public async Task<Result> Handle(RequestAnalysisCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions).Include(x => x.Weaknesses), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.StartAnalysis();
            await db.SaveChangesAsync(ct);   // 领域事件在保存后由拦截器发布 → 触发 Worker
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Analysis.InvalidState", ex.Message));
        }
    }
}

public sealed class ApplyAnalysisCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ApplyAnalysisCommand, Result>
{
    public async Task<Result> Handle(ApplyAnalysisCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId,
            q => q.Include(x => x.Assets).Include(x => x.Questions)
                .Include(x => x.Weaknesses).Include(x => x.Snapshots), ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        try
        {
            e.ApplyAnalysis(request.Overall, request.Pronunciation, request.Fluency,
                request.Structure, request.TechnicalDepth, request.Relevance, request.Summary,
                request.Questions, request.Weaknesses, request.SpeechMetricsJson,
                request.CandidateTranscript);
            // A3:每次分析成功回写记一条快照 —— 趋势曲线与"较上次"对比的数据源。
            // DimensionsJson 存 6 维可空分数;Summary 按句切成 3~5 条一句话结论。
            e.AddAnalysisSnapshot(request.Overall,
                PlaybookText.BuildDimensionsJson(request.Overall, request.Pronunciation,
                    request.Fluency, request.Structure, request.TechnicalDepth,
                    request.Relevance),
                PlaybookText.SummaryToBulletsJson(request.Summary));
            // 分析结果回写 = 流水线终点:同事务关单
            await db.StageCloseOpenJobsAsync(request.EntryId, AnalysisJobStatus.Succeeded, null, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Analysis.InvalidState", ex.Message));
        }
    }
}

public sealed class MarkEntryFailedCommandHandler(InterviewsDbContext db)
    : IRequestHandler<MarkEntryFailedCommand, Result>
{
    public async Task<Result> Handle(MarkEntryFailedCommand request, CancellationToken ct)
    {
        var e = await OwnedEntry.LoadAsync(db, request.EntryId, request.UserId, request.OwnerUserId, null, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try
        {
            e.MarkFailed(request.Reason);
            // 失败上报:同事务把任务关成 Failed,原因留档可查
            await db.StageCloseOpenJobsAsync(request.EntryId, AnalysisJobStatus.Failed,
                request.Reason, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.Conflict("Entry.InvalidState", ex.Message));
        }
    }
}

public sealed class AddQuestionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AddQuestionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddQuestionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions).Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        if (!Enum.TryParse<QuestionCategory>(request.Category, true, out var cat))
            return Result.Failure<Guid>(Error.Validation("Question.InvalidCategory", "问题类型无效"));

        InterviewQuestion q;
        try
        {
            q = e.AddQuestion(request.QuestionText, request.MyAnswerText, cat, request.Difficulty,
                request.RoundId, request.AskedBy);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure<Guid>(Error.Conflict("Question.InvalidState", ex.Message));
        }
        if (request.Assessment is not null)
            q.UpdateFromAnalysis(request.QuestionText, request.MyAnswerText, request.Assessment,
                cat, request.Difficulty, false, null, null, null);

        await db.SaveChangesAsync(ct);
        return Result.Success(q.Id);
    }
}

public sealed class UpdateQuestionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateQuestionCommand, Result>
{
    public async Task<Result> Handle(UpdateQuestionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        if (!Enum.TryParse<QuestionCategory>(request.Category, true, out var cat))
            return Result.Failure(Error.Validation("Question.InvalidCategory", "问题类型无效"));

        try
        {
            e.UpdateQuestion(request.QuestionId, request.QuestionText, request.MyAnswerText, cat,
                request.Difficulty, request.Assessment, request.GotStuck, request.StuckReason,
                request.RecommendedAnswer);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class RemoveQuestionCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RemoveQuestionCommand, Result>
{
    public async Task<Result> Handle(RemoveQuestionCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Questions).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try
        {
            e.RemoveQuestion(request.QuestionId);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class AddRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<AddRoundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AddRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure<Guid>(Error.NotFound("面试条目"));

        var r = e.AddRound(request.Stage);
        // ★ 一次性设置日期/面试官/形式/地点,避免建完再改两步走
        if (request.ScheduledDate.HasValue || !string.IsNullOrWhiteSpace(request.Interviewers) ||
            !string.IsNullOrWhiteSpace(request.Format) || !string.IsNullOrWhiteSpace(request.Location))
        {
            e.UpdateRound(r.Id, request.Stage, request.ScheduledDate, request.Interviewers,
                request.Format, request.Location, InterviewRoundOutcome.Pending, null, null);
        }
        // 2026-10-20 v2 修复:开始时间此前被丢弃 —— 统一入口/沟通建轮次都带时间,必须落库。
        if (!string.IsNullOrWhiteSpace(request.ScheduledTime))
        {
            r.UpdateDetails(null, request.ScheduledTime, null, null, null, null);
        }
        await db.SaveChangesAsync(ct);
        return Result.Success(r.Id);
    }
}

/// <summary>全局 upcoming:未来 N 天内所有条目的轮次,按日期排序,供日历视图用。</summary>
public sealed class GetUpcomingRoundsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetUpcomingRoundsQuery, Result<List<UpcomingRoundDto>>>
{
    public async Task<Result<List<UpcomingRoundDto>>> Handle(
        GetUpcomingRoundsQuery request, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var limit = today.AddDays(request.Days);

        var rows = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { e, r })
            .Where(x => x.r.ScheduledDate != null
                && x.r.ScheduledDate >= today
                && x.r.ScheduledDate <= limit)
            .OrderBy(x => x.r.ScheduledDate).ThenBy(x => x.r.ScheduledTime)
            .Select(x => new UpcomingRoundDto(
                x.r.Id, x.e.Id, x.e.CompanyName, x.e.Role,
                x.r.Order, x.r.Stage, x.r.ScheduledDate, x.r.ScheduledTime,
                x.r.Interviewers, x.r.Format, x.r.Location,
                x.r.MeetingLink, x.r.Outcome.ToString()))
            .ToListAsync(ct);

        return Result.Success(rows);
    }
}

/// <summary>轮次通过概率:基于用户历史同 stage 轮次的通过率 + 本轮准备度信号。</summary>
public sealed record PassProbabilityDto(
    int Probability,           // 0-100
    int SampleSize,            // 历史样本数
    int PassedCount,
    string Stage,
    List<OfferFactorDto> Factors);     // 影响因素说明(Key 由前端按 locale 格式化)

public sealed record GetRoundPassProbabilityQuery(Guid UserId, Guid RoundId) : IRequest<Result<PassProbabilityDto>>;

public sealed class GetRoundPassProbabilityQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetRoundPassProbabilityQuery, Result<PassProbabilityDto>>
{
    public async Task<Result<PassProbabilityDto>> Handle(
        GetRoundPassProbabilityQuery request, CancellationToken ct)
    {
        // 找到本轮,取其 stage
        var round = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds, (e, r) => new { e, r })
            .Where(x => x.r.Id == request.RoundId)
            .Select(x => new { x.r.Stage, RoundId = x.r.Id })
            .FirstOrDefaultAsync(ct);
        if (round is null) return Result.Failure<PassProbabilityDto>(Error.NotFound("Round"));

        // 历史同 stage 轮次:只算有定论的 (Passed / Rejected)
        var history = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds)
            .Where(r => r.Stage == round.Stage
                && r.Id != round.RoundId
                && (r.Outcome == InterviewRoundOutcome.Passed
                    || r.Outcome == InterviewRoundOutcome.Rejected))
            .ToListAsync(ct);

        var factors = new List<OfferFactorDto>();
        int baseProb;
        int historyCount = history.Count;
        if (historyCount == 0)
        {
            // 无历史数据:中性 50%,提示多攒数据
            baseProb = 50;
            factors.Add(new OfferFactorDto("pp_no_history"));
        }
        else
        {
            var passed = history.Count(r => r.Outcome == InterviewRoundOutcome.Passed);
            baseProb = (int)Math.Round(100.0 * passed / historyCount);
            factors.Add(new OfferFactorDto("pp_history_rate", passed, historyCount));
        }

        // 准备度信号:本轮的准备越充分,微调概率
        // 票19:同上 —— 加载轮次实体,Json 字段经属性 getter(原始载荷)判定
        var target = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId)
            .SelectMany(e => e.Rounds)
            .Where(r => r.Id == request.RoundId)
            .FirstOrDefaultAsync(ct);

        int adjustment = 0;
        if (target is not null)
        {
            if (!JsonFieldAdapter.IsEmptyArray(target.PrepQuestionsJson))
            { adjustment += 5; factors.Add(new OfferFactorDto("pp_prep_list")); }

            if (!string.IsNullOrWhiteSpace(target.Notes))
            { adjustment += 3; factors.Add(new OfferFactorDto("pp_has_notes")); }

            if (!JsonFieldAdapter.IsEmptyArray(target.EmailsJson))
            { adjustment += 2; factors.Add(new OfferFactorDto("pp_has_email")); }
        }

        // 查本轮所属条目的 Q&A 积累情况
        var entry = await db.Entries.AsNoTracking()
            .Where(e => e.UserId == request.UserId && e.Rounds.Any(r => r.Id == request.RoundId))
            .Select(e => new { e.Id, QaCount = e.Questions.Count })
            .FirstOrDefaultAsync(ct);
        if (entry is not null)
        {
            if (entry.QaCount > 0)
            { adjustment += Math.Min(5, entry.QaCount); factors.Add(new OfferFactorDto("pp_qa_accum", entry.QaCount, Math.Min(5, entry.QaCount))); }
        }

        var prob = Math.Clamp(baseProb + adjustment, 5, 95);
        var passedTotal = history.Count(r => r.Outcome == InterviewRoundOutcome.Passed);
        return Result.Success(new PassProbabilityDto(
            prob, historyCount, passedTotal,
            round.Stage, factors));
    }
}

/// <summary>整单 offer 概率:漏斗模型 —— 走到当前轮次的人,历史上有多少拿到 offer。</summary>
public sealed record OfferProbabilityDto(
    int Probability,           // 0-100
    int SampleSize,            // 走到当前轮次的历史样本数
    int OfferCount,            // 其中拿到 offer 的
    int CurrentRound,          // 当前第几轮
    List<OfferFactorDto> Factors);

/// <summary>概率因子:Key 由前端按 locale 格式化,避免后端硬编码中文。</summary>
public sealed record OfferFactorDto(string Key, int Arg1 = 0, int Arg2 = 0, int Arg3 = 0);

public sealed record GetOfferProbabilityQuery(Guid UserId, Guid EntryId) : IRequest<Result<OfferProbabilityDto>>;

public sealed class GetOfferProbabilityQueryHandler(InterviewsDbContext db)
    : IRequestHandler<GetOfferProbabilityQuery, Result<OfferProbabilityDto>>
{
    public async Task<Result<OfferProbabilityDto>> Handle(
        GetOfferProbabilityQuery request, CancellationToken ct)
    {
        // 当前条目:第几轮(按 rounds 数量)
        var current = await db.Entries.AsNoTracking()
            .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
            .Select(e => new { RoundCount = e.Rounds.Count, e.Result })
            .FirstOrDefaultAsync(ct);
        if (current is null) return Result.Failure<OfferProbabilityDto>(Error.NotFound("Entry"));

        int currentRound = Math.Max(1, current.RoundCount);
        var factors = new List<OfferFactorDto>();

        // 历史漏斗:走到 >= 当前轮次 且已有定论的条目
        var history = await db.Entries.AsNoTracking()
            .Where(e => e.Id != request.EntryId && e.UserId == request.UserId
                && e.Rounds.Count >= currentRound
                && (e.Result == "Passed" || e.Result == "Rejected"))
            .Select(e => e.Result)
            .ToListAsync(ct);

        int baseProb;
        if (history.Count == 0)
        {
            baseProb = 50;
            factors.Add(new OfferFactorDto("no_history", currentRound));
        }
        else
        {
            var offers = history.Count(r => r == "Passed");
            baseProb = (int)Math.Round(100.0 * offers / history.Count);
            factors.Add(new OfferFactorDto("history_rate", currentRound, offers, history.Count));
        }

        // 准备度:整单的 Q&A、轮次准备完整度微调
        var prep = await db.Entries.AsNoTracking()
            .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
            .Select(e => new { QaCount = e.Questions.Count })
            .FirstOrDefaultAsync(ct);
        // 票19:PrepQuestionsJson 存版本信封后 SQL 侧字符串判定失效 —— 拉本条目轮次(个人量级)
        // 经属性 getter + adapter 类型化判定计数,与迁移前口径逐项一致(文件内 GetQuestionHistory 同先例)
        var roundPrepDone = 0;
        if (prep is not null)
        {
            var rounds = await db.Entries.AsNoTracking()
                .Where(e => e.Id == request.EntryId && e.UserId == request.UserId)
                .SelectMany(e => e.Rounds)
                .ToListAsync(ct);
            roundPrepDone = rounds.Count(r => !JsonFieldAdapter.IsEmptyArray(r.PrepQuestionsJson));
        }

        int adjustment = 0;
        if (prep is not null)
        {
            if (prep.QaCount >= 5)
            { adjustment += 5; factors.Add(new OfferFactorDto("qa_bonus", prep.QaCount)); }
            if (roundPrepDone > 0)
            {
                int prepBonus = 3 * Math.Min(3, roundPrepDone);
                adjustment += prepBonus;
                factors.Add(new OfferFactorDto("prep_bonus", roundPrepDone, prepBonus));
            }
        }

        // 越往后的轮次,基础概率自然越高(已经筛掉了前面的人)
        int funnelBonus = Math.Min(15, (currentRound - 1) * 5);
        if (funnelBonus > 0)
        { adjustment += funnelBonus; factors.Add(new OfferFactorDto("funnel_bonus", currentRound, funnelBonus)); }

        var prob = Math.Clamp(baseProb + adjustment, 5, 95);
        return Result.Success(new OfferProbabilityDto(
            prob, history.Count,
            history.Count(r => r == "Passed"),
            currentRound, factors));
    }
}

public sealed class UpdateRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpdateRoundCommand, Result>
{
    public async Task<Result> Handle(UpdateRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));

        if (!Enum.TryParse<InterviewRoundOutcome>(request.Outcome, true, out var outcome))
            return Result.Failure(Error.Validation("Round.InvalidOutcome", "轮次结果无效"));

        try
        {
            e.UpdateRound(request.RoundId, request.Stage, request.ScheduledDate, request.Interviewers,
                request.Format, request.Location, outcome, request.Notes, request.Feedback);
            var round = e.Rounds.FirstOrDefault(r => r.Id == request.RoundId);
            round?.UpdateDetails(request.MeetingLink, request.ScheduledTime, request.PrepQuestionsJson,
                request.EmailsJson, request.Transcript, request.RecordingUrl, request.ChecklistJson);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

public sealed class RemoveRoundCommandHandler(InterviewsDbContext db)
    : IRequestHandler<RemoveRoundCommand, Result>
{
    public async Task<Result> Handle(RemoveRoundCommand request, CancellationToken ct)
    {
        var e = await db.Entries.Include(x => x.Rounds).FirstOrDefaultAsync(x => x.Id == request.EntryId && x.UserId == request.UserId, ct);
        if (e is null) return Result.Failure(Error.NotFound("面试条目"));
        try
        {
            e.RemoveRound(request.RoundId);
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (InvalidOperationException ex)
        {
            return Result.Failure(Error.NotFound(ex.Message));
        }
    }
}

