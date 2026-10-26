using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================ Handler:命令 ============================

public sealed class CreateKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<CreateKnowledgeCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateKnowledgeCommand request, CancellationToken ct)
    {
        if (!Enum.TryParse<KnowledgeSource>(request.Source, true, out var source))
            source = KnowledgeSource.Personal;

        var item = new KnowledgeItem(request.Title, request.Topic, request.Question, source,
            subTopic: request.SubTopic, difficulty: request.Difficulty, importance: request.Importance,
            userId: request.UserId);

        item.UpdateDetails(request.Title, request.Topic, request.SubTopic, request.Question,
            request.Difficulty, request.Importance, request.TagsJson,
            request.ConceptExplanation, request.MyAnswer, request.BetterAnswer,
            request.KeyPointsJson, request.CommonMistakesJson, request.FollowUpsJson);
        ReferenceApplier.ApplyReferences(item, request.ReferencesJson);

        if (request.SourceInterviewEntryId is not null)
            item.LinkToInterview(request.SourceInterviewEntryId.Value, request.SourceCompanyName,
                request.SourceDate, request.SourceRoundNo, request.SourceRoundStage,
                request.SourceJobApplicationId);
        else if (!string.IsNullOrWhiteSpace(request.SourceCompanyName))
            // 只知道公司、没有机经条目时也要标来源 —— "我知道这题是面 A 公司时遇到的"
            // 本身就是有价值的信息,不该因为缺 EntryId 就退化成 Personal。
            item.LinkToInterview(Guid.Empty, request.SourceCompanyName, request.SourceDate,
                request.SourceRoundNo, request.SourceRoundStage, request.SourceJobApplicationId);

        db.Items.Add(item);
        await db.SaveChangesAsync(ct);
        return Result.Success(item.Id);
    }
}

public sealed class UpdateKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<UpdateKnowledgeCommand, Result>
{
    public async Task<Result> Handle(UpdateKnowledgeCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));

        // 票70:回传了读时版本就把 xmin 的 OriginalValue 钉回读时值——本请求现查现改
        // 默认拿到的是最新 xmin,串行双页签永远命中;钉回旧值后,若期间已被他人保存
        // (xmin 已推进),SaveChanges 影响 0 行 → DbUpdateConcurrencyException → 409。
        if (request.Version is { } version)
            db.Entry(k).Property<uint>("xmin").OriginalValue = version;

        k.UpdateDetails(request.Title, request.Topic, request.SubTopic, request.Question,
            request.Difficulty, request.Importance, request.TagsJson,
            request.ConceptExplanation, request.MyAnswer, request.BetterAnswer,
            request.KeyPointsJson, request.CommonMistakesJson, request.FollowUpsJson,
            request.SourcesJson);
        ReferenceApplier.ApplyReferences(k, request.ReferencesJson);

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class DeleteKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<DeleteKnowledgeCommand, Result>
{
    public async Task<Result> Handle(DeleteKnowledgeCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        ConcurrencyPin.Pin(db, k, request.Version);
        k.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class RestoreKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<RestoreKnowledgeCommand, Result>
{
    public async Task<Result> Handle(RestoreKnowledgeCommand request, CancellationToken ct)
    {
        var k = await db.Items.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        k.Restore();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class UpdateMyAnswerCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<UpdateMyAnswerCommand, Result>
{
    public async Task<Result> Handle(UpdateMyAnswerCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        ConcurrencyPin.Pin(db, k, request.Version); // 票101:旧版本保存 → 409
        k.SetMyAnswer(request.MyAnswer);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class UpdateBetterAnswerCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<UpdateBetterAnswerCommand, Result>
{
    public async Task<Result> Handle(UpdateBetterAnswerCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        ConcurrencyPin.Pin(db, k, request.Version); // 票101
        k.SetBetterAnswer(request.BetterAnswer);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class UpdateQuestionCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<UpdateQuestionCommand, Result>
{
    public async Task<Result> Handle(UpdateQuestionCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        ConcurrencyPin.Pin(db, k, request.Version); // 票101
        k.SetQuestion(request.Question);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class UpdateFromTechCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<UpdateFromTechCommand, Result>
{
    public async Task<Result> Handle(UpdateFromTechCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));
        ConcurrencyPin.Pin(db, k, request.Version); // 票101
        k.SetTitle(request.Question);
        k.SetBetterAnswer(request.BetterAnswer);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class RenameTopicCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<RenameTopicCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RenameTopicCommand request, CancellationToken ct)
    {
        var oldName = request.OldName?.Trim();
        var newName = request.NewName?.Trim();
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return Result.Failure<int>(Error.Validation("Topic.Empty", "分类名不能为空"));
        // 票83:handler 守卫与 RenameTopicCommandValidator 同口径(双保险)——
        // 超长新名回明确 Validation 400,不许落到 Topic 列宽处爆 DB 异常。
        if (newName.Length > TechStackLocalDataRules.MaxCategoryNameLength)
            return Result.Failure<int>(Error.Validation("Topic.TooLong", "分类名不超过 100 字符"));
        if (oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            return Result.Success(0);

        var items = await db.Items
            .Where(x => x.UserId == request.UserId && x.Topic != null && x.Topic.ToLower() == oldName.ToLower())
            .ToListAsync(ct);
        foreach (var k in items) k.SetTopic(newName);
        await db.SaveChangesAsync(ct);
        return Result.Success(items.Count);
    }
}

public sealed class DeleteTopicCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<DeleteTopicCommand, Result<int>>
{
    public async Task<Result<int>> Handle(DeleteTopicCommand request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<int>(Error.Validation("Topic.Empty", "分类名不能为空"));

        var items = await db.Items
            .Where(x => x.UserId == request.UserId && x.Topic != null && x.Topic.ToLower() == name.ToLower())
            .ToListAsync(ct);
        foreach (var k in items) k.SetTopic(null);
        await db.SaveChangesAsync(ct);
        return Result.Success(items.Count);
    }
}

public sealed class RecordReviewCommandHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<RecordReviewCommand, Result<ReviewOutcomeDto>>
{
    public async Task<Result<ReviewOutcomeDto>> Handle(RecordReviewCommand request,
        CancellationToken ct)
    {
        var k = await db.Items.Include(x => x.ReviewLogs).FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure<ReviewOutcomeDto>(Error.NotFound("知识点"));

        if (!Enum.TryParse<ReviewResult>(request.Result, true, out var result))
            return Result.Failure<ReviewOutcomeDto>(
                Error.Validation("Review.InvalidResult", "复习结果无效"));

        k.RecordReview(result, request.ConfidenceBefore, request.ConfidenceAfter,
            request.Note, request.DurationSeconds);

        await db.SaveChangesAsync(ct);   // 领域事件 → 集成事件(票91:复习内真变级经此发出)

        var interval = k.NextReviewAt is null
            ? 0
            : Math.Max(0, (int)Math.Round((k.NextReviewAt.Value - clock.GetUtcNow()).TotalDays));

        return Result.Success(new ReviewOutcomeDto(k.NextReviewAt, interval,
            Math.Round(k.EasinessFactor, 2), k.Mastery.ToString(), k.RepetitionStreak));
    }
}

public sealed class ChangeMasteryCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ChangeMasteryCommand, Result>
{
    public async Task<Result> Handle(ChangeMasteryCommand request, CancellationToken ct)
    {
        var k = await db.Items.FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure(Error.NotFound("知识点"));

        if (!Enum.TryParse<MasteryLevel>(request.Mastery, true, out var level))
            return Result.Failure(Error.Validation("Mastery.Invalid", "掌握度无效"));

        ConcurrencyPin.Pin(db, k, request.Version); // 票101
        k.PromoteMastery(level);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class AttachRelationCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<AttachRelationCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(AttachRelationCommand request, CancellationToken ct)
    {
        var k = await db.Items.Include(x => x.Relations).FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null) return Result.Failure<Guid>(Error.NotFound("知识点"));

        if (request.Id == request.RelatedItemId)
            return Result.Failure<Guid>(Error.Validation("Relation.SelfReference", "不能关联自己"));

        if (!await db.Items.AnyAsync(x => x.Id == request.RelatedItemId && x.UserId == request.UserId, ct))
            return Result.Failure<Guid>(Error.Validation("Relation.TargetNotFound", "关联的知识点不存在"));

        if (!Enum.TryParse<KnowledgeRelationType>(request.RelationType, true, out var type))
            return Result.Failure<Guid>(Error.Validation("Relation.InvalidType", "关联类型无效"));

        var r = k.AttachRelation(request.RelatedItemId, type, request.Note);
        await db.SaveChangesAsync(ct);
        return Result.Success(r.Id);
    }
}

public sealed class ImportKnowledgeCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ImportKnowledgeCommand, Result<int>>
{
    public async Task<Result<int>> Handle(ImportKnowledgeCommand request, CancellationToken ct)
    {
        if (request.Items.Count == 0)
            return Result.Failure<int>(Error.Validation("Import.Empty", "导入内容为空"));

        if (request.Items.Count > 500)
            return Result.Failure<int>(Error.Validation("Import.TooMany", "单次最多导入 500 条"));

        var created = 0;
        foreach (var row in request.Items)
        {
            if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.Topic))
                continue;   // 跳过无效行而不是整体失败 —— 批量导入要容错

            // 幂等:同 topic + 同 title 视为已存在,不重复插
            if (await db.Items.AnyAsync(x => x.UserId == request.UserId && x.Topic == row.Topic && x.Title == row.Title, ct))
                continue;

            if (!Enum.TryParse<KnowledgeSource>(row.Source, true, out var source))
                source = KnowledgeSource.Personal;

            var item = new KnowledgeItem(row.Title, row.Topic, row.Question, source,
                subTopic: row.SubTopic, difficulty: row.Difficulty, importance: row.Importance,
                userId: request.UserId);

            item.UpdateDetails(row.Title, row.Topic, row.SubTopic, row.Question,
                row.Difficulty, row.Importance, null,
                row.ConceptExplanation, null, null,
                row.KeyPointsJson, row.CommonMistakesJson, null);
            ReferenceApplier.ApplyReferences(item, row.ReferencesJson);

            if (!string.IsNullOrWhiteSpace(row.Mastery)
                && Enum.TryParse<MasteryLevel>(row.Mastery, true, out var ml))
                item.PromoteMastery(ml);

            if (!string.IsNullOrWhiteSpace(row.SourceCompanyName))
                item.LinkToInterview(Guid.Empty, row.SourceCompanyName, null,
                    row.SourceRoundNo, row.SourceRoundStage);

            db.Items.Add(item);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(created);
    }
}

