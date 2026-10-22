using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;

namespace YourInterview.Services.Interviews.Application;

// ============================ DTO ============================
// 口径与票14(Jobs 面 Tracker 收藏/自备问题)一致:导入一律返回
// { imported, total } 对账;列表 DTO 带回前端寻址用的 clientId。

public sealed record StarStoryDto(
    Guid Id, string ClientId, string Title, string? Situation, string? Task,
    string? Action, string? Result, string? Tags, int SortOrder,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record StarStoryInput(
    string ClientId, string Title, string? Situation, string? Task,
    string? Action, string? Result, string? Tags);

public sealed record LegacyDebriefDto(Guid EntryId, string PayloadJson);

public sealed record LegacyPastedDto(Guid EntryId, string Content);

public sealed record LegacyDebriefImportItem(Guid EntryId, string PayloadJson);

public sealed record LegacyPastedImportItem(Guid EntryId, string Content);

/// <summary>一次性导入(localStorage → 后端)的对账结果:本次新导入条数 + 导入后总数。</summary>
public sealed record PlaybookImportResultDto(int Imported, int Total);

// ============================ STAR 故事库 ============================

public sealed record ListStarStoriesQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<StarStoryDto>>>;

/// <summary>保存一个故事(按 ClientId upsert:没有则新建、有则整卡更新,含软删恢复)。</summary>
public sealed record UpsertStarStoryCommand(Guid UserId, StarStoryInput Story)
    : IRequest<Result<Guid>>;

/// <summary>删除一个故事(软删;不存在也成功 —— 幂等)。</summary>
public sealed record DeleteStarStoryCommand(Guid UserId, string ClientId)
    : IRequest<Result>;

/// <summary>
/// 一次性导入本地故事库(localStorage pb.starStories → 后端)。
/// 幂等:按 ClientId 去重 —— 已有的(含软删)更新内容/恢复,不建第二行;
/// 重复调用 Imported=0。顺序按输入序续接当前最大 SortOrder。
/// </summary>
public sealed record ImportStarStoriesCommand(Guid UserId, IReadOnlyList<StarStoryInput> Stories)
    : IRequest<Result<PlaybookImportResultDto>>;

// ============================ 历史复盘(孤儿键归档) ============================

public sealed record ListLegacyDebriefsQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<LegacyDebriefDto>>>;

/// <summary>
/// 一次性导入本地历史复盘(pb.debrief.&lt;entryId&gt; → 后端归档)。
/// 幂等:按 (UserId, EntryId) 去重,已有归档不覆盖(首导为准 —— 本地原值
/// 逐字归档,重复导入没有新信息)。条目不属于当前用户的静默跳过(票14 脏 id
/// 口径:本地可能残留已删/他人数据的键,导入不该整批失败;对账靠 imported/total)。
/// </summary>
public sealed record ImportLegacyDebriefsCommand(Guid UserId, IReadOnlyList<LegacyDebriefImportItem> Items)
    : IRequest<Result<PlaybookImportResultDto>>;

// ============================ 历史粘贴材料(孤儿键归档) ============================

public sealed record ListLegacyPastedQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<LegacyPastedDto>>>;

/// <summary>一次性导入本地历史粘贴材料(pb-pasted-&lt;entryId&gt;),口径同复盘导入。</summary>
public sealed record ImportLegacyPastedCommand(Guid UserId, IReadOnlyList<LegacyPastedImportItem> Items)
    : IRequest<Result<PlaybookImportResultDto>>;

// ============================ 校验 ============================
// 上限口径:故事各字段按本地实际形态给足空间(标题/标签是短文本,S/T/A/R 是
// 段落文本按 20000 与条目 Notes 同档);超限 400 而非落库 500(校验层把关)。
// Must 谓词一律 null-safe(票14 教训:同链后续规则在 NotNull 失败后仍执行,
// 直接 .Count 会在 null 列表上抛 NRE 把 400 变成 500)。

public static class StarStoryLimits
{
    public const int ClientIdMax = 100;
    public const int TitleMax = 500;
    public const int FieldMax = 20000;
    public const int TagsMax = 1000;
    public const int ImportBatchMax = 1000;
}

public static class LegacyImportLimits
{
    public const int BatchMax = 5000;
    public const int PayloadMax = 100000;
    public const int ContentMax = 500000;
}

public sealed class UpsertStarStoryCommandValidator : AbstractValidator<UpsertStarStoryCommand>
{
    public UpsertStarStoryCommandValidator()
    {
        RuleFor(x => x.Story).NotNull();
        RuleFor(x => x.Story.ClientId).NotEmpty().MaximumLength(StarStoryLimits.ClientIdMax)
            .WithMessage("故事 id 必填");
        RuleFor(x => x.Story.Title).NotEmpty().MaximumLength(StarStoryLimits.TitleMax)
            .WithMessage("故事标题必填");
        RuleFor(x => x.Story.Situation).MaximumLength(StarStoryLimits.FieldMax);
        RuleFor(x => x.Story.Task).MaximumLength(StarStoryLimits.FieldMax);
        RuleFor(x => x.Story.Action).MaximumLength(StarStoryLimits.FieldMax);
        RuleFor(x => x.Story.Result).MaximumLength(StarStoryLimits.FieldMax);
        RuleFor(x => x.Story.Tags).MaximumLength(StarStoryLimits.TagsMax);
    }
}

public sealed class DeleteStarStoryCommandValidator : AbstractValidator<DeleteStarStoryCommand>
{
    public DeleteStarStoryCommandValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(StarStoryLimits.ClientIdMax);
    }
}

public sealed class ImportStarStoriesCommandValidator : AbstractValidator<ImportStarStoriesCommand>
{
    public ImportStarStoriesCommandValidator()
    {
        RuleFor(x => x.Stories).NotNull()
            .Must(s => s is null || s.Count <= StarStoryLimits.ImportBatchMax)
            .WithMessage("一次导入的故事数过多");
        RuleForEach(x => x.Stories).ChildRules(s =>
        {
            s.RuleFor(x => x.ClientId).NotEmpty().MaximumLength(StarStoryLimits.ClientIdMax);
            s.RuleFor(x => x.Title).NotEmpty().MaximumLength(StarStoryLimits.TitleMax);
            s.RuleFor(x => x.Situation).MaximumLength(StarStoryLimits.FieldMax);
            s.RuleFor(x => x.Task).MaximumLength(StarStoryLimits.FieldMax);
            s.RuleFor(x => x.Action).MaximumLength(StarStoryLimits.FieldMax);
            s.RuleFor(x => x.Result).MaximumLength(StarStoryLimits.FieldMax);
            s.RuleFor(x => x.Tags).MaximumLength(StarStoryLimits.TagsMax);
        });
    }
}

public sealed class ImportLegacyDebriefsCommandValidator : AbstractValidator<ImportLegacyDebriefsCommand>
{
    public ImportLegacyDebriefsCommandValidator()
    {
        RuleFor(x => x.Items).NotNull()
            .Must(i => i is null || i.Count <= LegacyImportLimits.BatchMax)
            .WithMessage("一次导入的复盘数过多");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.EntryId).NotEmpty().WithMessage("复盘必须带条目 id");
            i.RuleFor(x => x.PayloadJson).NotEmpty().MaximumLength(LegacyImportLimits.PayloadMax)
                .WithMessage("复盘内容过长");
        });
    }
}

public sealed class ImportLegacyPastedCommandValidator : AbstractValidator<ImportLegacyPastedCommand>
{
    public ImportLegacyPastedCommandValidator()
    {
        RuleFor(x => x.Items).NotNull()
            .Must(i => i is null || i.Count <= LegacyImportLimits.BatchMax)
            .WithMessage("一次导入的粘贴材料数过多");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.EntryId).NotEmpty().WithMessage("粘贴材料必须带条目 id");
            i.RuleFor(x => x.Content).NotEmpty().MaximumLength(LegacyImportLimits.ContentMax)
                .WithMessage("粘贴材料过长");
        });
    }
}

// ============================ Handler:STAR 故事库 ============================

public sealed class ListStarStoriesQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListStarStoriesQuery, Result<IReadOnlyList<StarStoryDto>>>
{
    public async Task<Result<IReadOnlyList<StarStoryDto>>> Handle(
        ListStarStoriesQuery request, CancellationToken ct)
    {
        var items = await db.StarStories.AsNoTracking()
            .Where(s => s.UserId == request.UserId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt)
            .Select(s => new StarStoryDto(s.Id, s.ClientId, s.Title, s.Situation, s.Task,
                s.Action, s.Result, s.Tags, s.SortOrder, s.CreatedAt, s.UpdatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<StarStoryDto>>(items);
    }
}

public sealed class UpsertStarStoryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpsertStarStoryCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(UpsertStarStoryCommand request, CancellationToken ct)
    {
        var input = request.Story;
        // 含软删行查:删过的故事用同一 ClientId 重存必须恢复原行 ——
        // (UserId, ClientId) 唯一索引不允许第二行(票14 收藏同口径)。
        var existing = await db.StarStories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.UserId == request.UserId && s.ClientId == input.ClientId, ct);
        if (existing is not null)
        {
            existing.Update(input.Title, input.Situation, input.Task, input.Action,
                input.Result, input.Tags);
            if (existing.IsDeleted) existing.Restore();
            await db.SaveChangesAsync(ct);
            return Result.Success(existing.Id);
        }

        var nextOrder = await db.StarStories.IgnoreQueryFilters()
            .Where(s => s.UserId == request.UserId)
            .MaxAsync(s => (int?)s.SortOrder, ct) ?? 0;
        var story = new StarStory(request.UserId, input.ClientId, input.Title,
            input.Situation, input.Task, input.Action, input.Result, input.Tags, nextOrder + 1);
        db.StarStories.Add(story);
        await db.SaveChangesAsync(ct);
        return Result.Success(story.Id);
    }
}

public sealed class DeleteStarStoryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<DeleteStarStoryCommand, Result>
{
    public async Task<Result> Handle(DeleteStarStoryCommand request, CancellationToken ct)
    {
        var story = await db.StarStories.FirstOrDefaultAsync(
            s => s.UserId == request.UserId && s.ClientId == request.ClientId, ct);
        if (story is null) return Result.Success(); // 幂等:没这个故事也算删除成功

        story.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportStarStoriesCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ImportStarStoriesCommand, Result<PlaybookImportResultDto>>
{
    public async Task<Result<PlaybookImportResultDto>> Handle(
        ImportStarStoriesCommand request, CancellationToken ct)
    {
        // 输入内先按 ClientId 去重(后出现的覆盖先出现的 —— 与本地数组
        // findIndex 首个命中不同,这里取最后一版,与用户最后编辑的意图一致)。
        var byClientId = new Dictionary<string, StarStoryInput>(StringComparer.Ordinal);
        foreach (var s in request.Stories)
        {
            if (s is null || string.IsNullOrEmpty(s.ClientId)) continue;
            byClientId[s.ClientId] = s;
        }

        var existing = await db.StarStories.IgnoreQueryFilters()
            .Where(s => s.UserId == request.UserId)
            .ToListAsync(ct);
        var existingByClientId = existing.ToDictionary(s => s.ClientId, StringComparer.Ordinal);
        var nextOrder = existing.Count == 0 ? 0 : existing.Max(s => s.SortOrder);

        var imported = 0;
        var changed = false;
        foreach (var (clientId, input) in byClientId)
        {
            if (existingByClientId.TryGetValue(clientId, out var row))
            {
                // 已导入过的:内容以本地为准同步一次(导入语义 = 把本地现状搬上后端),
                // 但不计 imported —— 对账口径 imported 只数新建行。
                row.Update(input.Title, input.Situation, input.Task, input.Action,
                    input.Result, input.Tags);
                if (row.IsDeleted) row.Restore();
                changed = true;
                continue;
            }
            db.StarStories.Add(new StarStory(request.UserId, clientId, input.Title,
                input.Situation, input.Task, input.Action, input.Result, input.Tags, ++nextOrder));
            imported++;
            changed = true;
        }
        if (changed) await db.SaveChangesAsync(ct);

        var total = await db.StarStories.AsNoTracking()
            .CountAsync(s => s.UserId == request.UserId, ct);
        return Result.Success(new PlaybookImportResultDto(imported, total));
    }
}

// ============================ Handler:历史复盘归档 ============================

public sealed class ListLegacyDebriefsQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListLegacyDebriefsQuery, Result<IReadOnlyList<LegacyDebriefDto>>>
{
    public async Task<Result<IReadOnlyList<LegacyDebriefDto>>> Handle(
        ListLegacyDebriefsQuery request, CancellationToken ct)
    {
        var items = await db.LegacyDebriefs.AsNoTracking()
            .Where(d => d.UserId == request.UserId)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new LegacyDebriefDto(d.EntryId, d.PayloadJson))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<LegacyDebriefDto>>(items);
    }
}

public sealed class ImportLegacyDebriefsCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ImportLegacyDebriefsCommand, Result<PlaybookImportResultDto>>
{
    public async Task<Result<PlaybookImportResultDto>> Handle(
        ImportLegacyDebriefsCommand request, CancellationToken ct)
    {
        var byEntry = new Dictionary<Guid, string>();
        foreach (var item in request.Items)
        {
            if (item is null || item.EntryId == Guid.Empty || item.PayloadJson is null) continue;
            byEntry[item.EntryId] = item.PayloadJson;
        }

        // 归属校验:条目属于当前用户才归档(含软删条目 —— 用户删了条目,
        // 历史复盘仍是他的数据,保全优先);他人/不存在的条目静默跳过。
        var wantedIds = byEntry.Keys.ToList();
        var ownedIds = await db.Entries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.UserId == request.UserId && wantedIds.Contains(e.Id))
            .Select(e => e.Id).ToListAsync(ct);
        var owned = ownedIds.ToHashSet();

        var existingIds = (await db.LegacyDebriefs.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.UserId == request.UserId)
            .Select(d => d.EntryId).ToListAsync(ct)).ToHashSet();

        var imported = 0;
        foreach (var (entryId, payload) in byEntry)
        {
            if (!owned.Contains(entryId)) continue;
            if (existingIds.Contains(entryId)) continue; // 已归档(含软删)不覆盖:首导为准
            db.LegacyDebriefs.Add(new LegacyDebrief(request.UserId, entryId, payload));
            imported++;
        }
        if (imported > 0) await db.SaveChangesAsync(ct);

        var total = await db.LegacyDebriefs.AsNoTracking()
            .CountAsync(d => d.UserId == request.UserId, ct);
        return Result.Success(new PlaybookImportResultDto(imported, total));
    }
}

// ============================ Handler:历史粘贴材料归档 ============================

public sealed class ListLegacyPastedQueryHandler(InterviewsDbContext db)
    : IRequestHandler<ListLegacyPastedQuery, Result<IReadOnlyList<LegacyPastedDto>>>
{
    public async Task<Result<IReadOnlyList<LegacyPastedDto>>> Handle(
        ListLegacyPastedQuery request, CancellationToken ct)
    {
        var items = await db.LegacyPastedMaterials.AsNoTracking()
            .Where(p => p.UserId == request.UserId)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new LegacyPastedDto(p.EntryId, p.Content))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<LegacyPastedDto>>(items);
    }
}

public sealed class ImportLegacyPastedCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ImportLegacyPastedCommand, Result<PlaybookImportResultDto>>
{
    public async Task<Result<PlaybookImportResultDto>> Handle(
        ImportLegacyPastedCommand request, CancellationToken ct)
    {
        var byEntry = new Dictionary<Guid, string>();
        foreach (var item in request.Items)
        {
            if (item is null || item.EntryId == Guid.Empty || item.Content is null) continue;
            byEntry[item.EntryId] = item.Content;
        }

        var wantedIds = byEntry.Keys.ToList();
        var ownedIds = await db.Entries.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.UserId == request.UserId && wantedIds.Contains(e.Id))
            .Select(e => e.Id).ToListAsync(ct);
        var owned = ownedIds.ToHashSet();

        var existingIds = (await db.LegacyPastedMaterials.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.UserId == request.UserId)
            .Select(p => p.EntryId).ToListAsync(ct)).ToHashSet();

        var imported = 0;
        foreach (var (entryId, content) in byEntry)
        {
            if (!owned.Contains(entryId)) continue;
            if (existingIds.Contains(entryId)) continue; // 首导为准,不覆盖
            db.LegacyPastedMaterials.Add(new LegacyPastedMaterial(request.UserId, entryId, content));
            imported++;
        }
        if (imported > 0) await db.SaveChangesAsync(ct);

        var total = await db.LegacyPastedMaterials.AsNoTracking()
            .CountAsync(p => p.UserId == request.UserId, ct);
        return Result.Success(new PlaybookImportResultDto(imported, total));
    }
}
