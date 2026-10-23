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
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt,
    // ★ 票111 段3(I05):故事行读时 xmin —— DELETE 时 If-Match 回传钉版用。
    uint Version = 0);

public sealed record StarStoryInput(
    string ClientId, string Title, string? Situation, string? Task,
    string? Action, string? Result, string? Tags);

/// <summary>
/// 归档列表 DTO:EntryId 字段名不变(线上契约),票77 起类型 Guid → string、
/// 值 = 归档键 EntryKey —— 匹配条目的行 = 原 Guid 规范串逐字(与旧口径
/// 读回一致),孤儿行 = 本地键后缀原文逐字。
/// </summary>
public sealed record LegacyDebriefDto(string EntryId, string PayloadJson);

public sealed record LegacyPastedDto(string EntryId, string Content);

/// <summary>导入条目:EntryId = 本地键后缀原文(票77,不再限 Guid 形态)。</summary>
public sealed record LegacyDebriefImportItem(string EntryId, string PayloadJson);

public sealed record LegacyPastedImportItem(string EntryId, string Content);

/// <summary>
/// 票77:归档键归一(纯函数)。后缀能解析为 Guid 时存其规范形
/// (Guid.ToString(),大小写/书写格式差异收敛为同一键 —— 与票15 旧口径
/// 按 Guid 值去重/读回逐字一致);否则后缀原文逐字(孤儿键保全)。
/// </summary>
public static class LegacyEntryKeyRules
{
    public static string Normalize(string raw)
        => Guid.TryParse(raw, out var g) ? g.ToString() : raw;
}

/// <summary>一次性导入(localStorage → 后端)的对账结果:本次新导入条数 + 导入后总数。</summary>
public sealed record PlaybookImportResultDto(int Imported, int Total);

// ============================ STAR 故事库 ============================

public sealed record ListStarStoriesQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<StarStoryDto>>>;

/// <summary>保存一个故事(按 ClientId upsert:没有则新建、有则整卡更新,含软删恢复)。</summary>
public sealed record UpsertStarStoryCommand(Guid UserId, StarStoryInput Story)
    : IRequest<Result<Guid>>;

/// <summary>删除一个故事(软删;不存在也成功 —— 幂等)。</summary>
public sealed record DeleteStarStoryCommand(Guid UserId, string ClientId, uint? Version = null)
    : IRequest<Result>; // 票111段3:If-Match 钉版(故事行 xmin),null=不钉兼容;PUT upsert(I04)未动

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
/// 一次性导入本地历史复盘(pb.debrief.&lt;后缀&gt; → 后端归档)。
/// 幂等:按 (UserId, EntryKey) 去重,已有归档不覆盖(首导为准 —— 本地原值
/// 逐字归档,重复导入没有新信息)。票77:不再要求后缀能匹配现存条目 ——
/// 孤儿键(条目已删等)以后缀原文为归档键落库(见 LegacyEntryKeyRules);
/// 归档行恒属导入者,他人的条目 id 后缀只归档导入者本地的那份数据。
/// </summary>
public sealed record ImportLegacyDebriefsCommand(Guid UserId, IReadOnlyList<LegacyDebriefImportItem> Items)
    : IRequest<Result<PlaybookImportResultDto>>;

// ============================ 历史粘贴材料(孤儿键归档) ============================

public sealed record ListLegacyPastedQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<LegacyPastedDto>>>;

/// <summary>一次性导入本地历史粘贴材料(pb-pasted-&lt;后缀&gt;),口径同复盘导入。</summary>
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
    /// <summary>票77:归档键(键后缀原文)上限 —— 本地键后缀是短串,500 足量且防滥用。</summary>
    public const int EntryKeyMax = 500;
}

public sealed class UpsertStarStoryCommandValidator : AbstractValidator<UpsertStarStoryCommand>
{
    public UpsertStarStoryCommandValidator()
    {
        RuleFor(x => x.Story).NotNull();
        // 票107:必填/长度文案分挂两条规则(WithMessage 只挂紧邻的前一条规则)。
        RuleFor(x => x.Story.ClientId).NotEmpty().WithMessage("故事 id 必填")
            .MaximumLength(StarStoryLimits.ClientIdMax).WithMessage("故事 id 长度不能超过 {MaxLength} 个字符");
        RuleFor(x => x.Story.Title).NotEmpty().WithMessage("故事标题必填")
            .MaximumLength(StarStoryLimits.TitleMax).WithMessage("故事标题长度不能超过 {MaxLength} 个字符");
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
            // 票77:归档键 = 键后缀原文,不再限 Guid 形态;空/超长仍拒。
            i.RuleFor(x => x.EntryId).NotEmpty().WithMessage("复盘必须带归档键")
                .MaximumLength(LegacyImportLimits.EntryKeyMax).WithMessage("归档键长度不能超过 {MaxLength} 个字符");
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
            // 票77:口径同复盘导入(归档键 = 后缀原文)。
            i.RuleFor(x => x.EntryId).NotEmpty().WithMessage("粘贴材料必须带归档键")
                .MaximumLength(LegacyImportLimits.EntryKeyMax).WithMessage("归档键长度不能超过 {MaxLength} 个字符");
            i.RuleFor(x => x.Content).NotEmpty().MaximumLength(LegacyImportLimits.ContentMax)
                .WithMessage("粘贴材料过长");
        });
    }
}

// ============================ STAR upsert 并发裁决(票78) ============================

public static class StarStoryUpsertRules
{
    /// <summary>按 (UserId, ClientId) 回查到的故事行状态(upsert 裁决的输入)。</summary>
    public enum ExistingStoryState { None, Active, SoftDeleted }

    /// <summary>保存一个故事的处置裁决。</summary>
    public enum UpsertDecision { Create, Update, Conflict }

    /// <summary>
    /// 票78:upsert 处置裁决(纯逻辑,单测覆盖全组合,口径同票92 DecideAddCategory)。
    /// raceLost=false 是首次「先查」的结果;true 是 SaveChanges 撞 DbUpdateException
    /// (唯一约束 23505 / xmin 并发)后的回查结果 —— 赢家行已在(Active/SoftDeleted)
    /// 则按 upsert 语义把本请求的值整卡写到该行(last-write-wins,本请求即后写);
    /// 回查仍无行,说明撞的是别的约束 → Conflict(409),不冒充成功。
    /// </summary>
    public static UpsertDecision DecideUpsert(ExistingStoryState state, bool raceLost)
        => (state, raceLost) switch
        {
            (ExistingStoryState.Active, _) => UpsertDecision.Update,
            (ExistingStoryState.SoftDeleted, _) => UpsertDecision.Update,
            (ExistingStoryState.None, false) => UpsertDecision.Create,
            _ => UpsertDecision.Conflict,
        };

    /// <summary>已存行内容与本次输入逐字段相等(整卡口径,含软删标记不算内容)。</summary>
    public static bool HasSameContent(StarStory row, StarStoryInput input)
        => row.Title == input.Title && row.Situation == input.Situation
            && row.Task == input.Task && row.Action == input.Action
            && row.Result == input.Result && row.Tags == input.Tags;
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
                s.Action, s.Result, s.Tags, s.SortOrder, s.CreatedAt, s.UpdatedAt,
                EF.Property<uint>(s, "xmin")))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<StarStoryDto>>(items);
    }
}

public sealed class UpsertStarStoryCommandHandler(InterviewsDbContext db)
    : IRequestHandler<UpsertStarStoryCommand, Result<Guid>>
{
    // 票78 并发收敛:连点/多页签同发同一 ClientId 时「先查后写」有竞态窗 ——
    // 双插撞 (UserId, ClientId) 唯一约束 23505、并发更新同一行撞 xmin,此前都
    // 穿透成 500(票15 D2 实测)。本端点契约是 upsert(last-write-wins):撞
    // DbUpdateException 后清跟踪回查、按 DecideUpsert(raceLost: true) 重新
    // 裁决 —— 赢家行已在就把本请求的值整卡写入该行(本请求即后写,胜出);
    // 回查仍无行 = 撞了别的约束 → 409 安全文案,绝不回原始异常。
    // 尝试上限 5:每轮至少一个竞争者落库成功,余者回查后转为更新;内容与
    // 已存行逐字段相等且行未删时免写直接成功(同帧连点的同值双发在此收敛,
    // 不再互相撞 xmin)。全局 DbUpdate 映射仍归票90,本票只做本端点单点收敛
    // (口径同票92 分类新增)。
    private const int MaxAttempts = 5;

    public async Task<Result<Guid>> Handle(UpsertStarStoryCommand request, CancellationToken ct)
    {
        var input = request.Story;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // 含软删行查:删过的故事用同一 ClientId 重存必须恢复原行 ——
            // (UserId, ClientId) 唯一索引不允许第二行(票14 收藏同口径)。
            var existing = await db.StarStories.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.UserId == request.UserId && s.ClientId == input.ClientId, ct);
            var state = existing is null
                ? StarStoryUpsertRules.ExistingStoryState.None
                : existing.IsDeleted
                    ? StarStoryUpsertRules.ExistingStoryState.SoftDeleted
                    : StarStoryUpsertRules.ExistingStoryState.Active;
            switch (StarStoryUpsertRules.DecideUpsert(state, raceLost: attempt > 0))
            {
                case StarStoryUpsertRules.UpsertDecision.Conflict:
                    return Conflict();
                case StarStoryUpsertRules.UpsertDecision.Update:
                    if (!existing!.IsDeleted
                        && StarStoryUpsertRules.HasSameContent(existing, input))
                        return Result.Success(existing.Id); // 同值免写:已是后写结果
                    existing.Update(input.Title, input.Situation, input.Task, input.Action,
                        input.Result, input.Tags);
                    if (existing.IsDeleted) existing.Restore();
                    try { await db.SaveChangesAsync(ct); return Result.Success(existing.Id); }
                    catch (DbUpdateException) { db.ChangeTracker.Clear(); }
                    break;
                case StarStoryUpsertRules.UpsertDecision.Create:
                    var nextOrder = await db.StarStories.IgnoreQueryFilters()
                        .Where(s => s.UserId == request.UserId)
                        .MaxAsync(s => (int?)s.SortOrder, ct) ?? 0;
                    var story = new StarStory(request.UserId, input.ClientId, input.Title,
                        input.Situation, input.Task, input.Action, input.Result, input.Tags, nextOrder + 1);
                    db.StarStories.Add(story);
                    try { await db.SaveChangesAsync(ct); return Result.Success(story.Id); }
                    catch (DbUpdateException) { db.ChangeTracker.Clear(); }
                    break;
            }
        }
        return Conflict();

        static Result<Guid> Conflict() => Result.Failure<Guid>(Error.Conflict(
            "StarStory.SaveConflict", "故事保存与并发请求冲突,请刷新后重试"));
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

        ConcurrencyPin.Pin(db, story, request.Version);
        story.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportStarStoriesCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ImportStarStoriesCommand, Result<PlaybookImportResultDto>>
{
    // 票78 顺手核查(明示扩围,理由见 evidence-78/CLOSE-REPORT):本导入与
    // upsert 同族竞态 —— 两会话并发同发同批时双插撞 23505 → 500(改前活体
    // 实测)。导入契约本就是幂等(按 ClientId 去重),故同法收敛:撞
    // DbUpdateException 清跟踪后整批重跑 —— 重跑时赢家行已在,按既有行
    // 更新/计数口径自然收敛(imported 只数重跑时新建行,对账不虚报)。
    private const int MaxAttempts = 5;

    public async Task<Result<PlaybookImportResultDto>> Handle(
        ImportStarStoriesCommand request, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try { return await HandleCore(request, ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
        return Result.Failure<PlaybookImportResultDto>(Error.Conflict(
            "StarStory.ImportConflict", "故事导入与并发请求冲突,请重试"));
    }

    private async Task<Result<PlaybookImportResultDto>> HandleCore(
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
                // 票78:内容与已存行逐字段相等且行未删时免写 —— 并发同发同批
                // 时收敛方的重跑若再整卡写,只会互撞 xmin 把重试轮次耗尽出 409
                // (活体实测);同值免写后重跑是纯读,自然收敛(同 upsert 口径)。
                if (row.IsDeleted)
                {
                    row.Update(input.Title, input.Situation, input.Task, input.Action,
                        input.Result, input.Tags);
                    row.Restore();
                    changed = true;
                }
                else if (!StarStoryUpsertRules.HasSameContent(row, input))
                {
                    row.Update(input.Title, input.Situation, input.Task, input.Action,
                        input.Result, input.Tags);
                    changed = true;
                }
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
            .Select(d => new LegacyDebriefDto(d.EntryKey, d.PayloadJson))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<LegacyDebriefDto>>(items);
    }
}

public sealed class ImportLegacyDebriefsCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ImportLegacyDebriefsCommand, Result<PlaybookImportResultDto>>
{
    // 票78 同族收敛(同 ImportStarStories 口径):并发同发同批双插撞
    // (UserId, EntryId) 唯一约束 → 500(改前活体实测);整批重跑时赢家行
    // 已在既有键集中,按「首导为准」跳过,imported/total 自然收敛。
    // (票77 后唯一键为 (UserId, EntryKey),收敛环结构不变。)
    private const int MaxAttempts = 3;

    public async Task<Result<PlaybookImportResultDto>> Handle(
        ImportLegacyDebriefsCommand request, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try { return await HandleCore(request, ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
        return Result.Failure<PlaybookImportResultDto>(Error.Conflict(
            "LegacyDebrief.ImportConflict", "复盘导入与并发请求冲突,请重试"));
    }

    private async Task<Result<PlaybookImportResultDto>> HandleCore(
        ImportLegacyDebriefsCommand request, CancellationToken ct)
    {
        // 票77:按归档键收口 —— 后缀原文归一为 EntryKey(Guid 后缀存规范形、
        // 孤儿后缀逐字),批内同键后出现的覆盖先出现的(与既有批内去重口径
        // 一致);不再查 Entries 归属:孤儿键的条目本就可能已删,归档行恒属
        // 导入者(UserId 收口),这正是本票要保全的数据。
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in request.Items)
        {
            if (item is null || string.IsNullOrEmpty(item.EntryId) || item.PayloadJson is null) continue;
            byKey[LegacyEntryKeyRules.Normalize(item.EntryId)] = item.PayloadJson;
        }

        var existingKeys = (await db.LegacyDebriefs.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.UserId == request.UserId)
            .Select(d => d.EntryKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        foreach (var (entryKey, payload) in byKey)
        {
            if (existingKeys.Contains(entryKey)) continue; // 已归档(含软删)不覆盖:首导为准
            db.LegacyDebriefs.Add(new LegacyDebrief(request.UserId, entryKey, payload));
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
            .Select(p => new LegacyPastedDto(p.EntryKey, p.Content))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<LegacyPastedDto>>(items);
    }
}

public sealed class ImportLegacyPastedCommandHandler(InterviewsDbContext db)
    : IRequestHandler<ImportLegacyPastedCommand, Result<PlaybookImportResultDto>>
{
    // 票78 同族收敛(同 ImportLegacyDebriefs 口径)。
    private const int MaxAttempts = 3;

    public async Task<Result<PlaybookImportResultDto>> Handle(
        ImportLegacyPastedCommand request, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try { return await HandleCore(request, ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
        return Result.Failure<PlaybookImportResultDto>(Error.Conflict(
            "LegacyPasted.ImportConflict", "粘贴材料导入与并发请求冲突,请重试"));
    }

    private async Task<Result<PlaybookImportResultDto>> HandleCore(
        ImportLegacyPastedCommand request, CancellationToken ct)
    {
        // 票77:口径同复盘导入 —— 按 (UserId, EntryKey) 收口,不查条目归属。
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in request.Items)
        {
            if (item is null || string.IsNullOrEmpty(item.EntryId) || item.Content is null) continue;
            byKey[LegacyEntryKeyRules.Normalize(item.EntryId)] = item.Content;
        }

        var existingKeys = (await db.LegacyPastedMaterials.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.UserId == request.UserId)
            .Select(p => p.EntryKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        foreach (var (entryKey, content) in byKey)
        {
            if (existingKeys.Contains(entryKey)) continue; // 首导为准,不覆盖
            db.LegacyPastedMaterials.Add(new LegacyPastedMaterial(request.UserId, entryKey, content));
            imported++;
        }
        if (imported > 0) await db.SaveChangesAsync(ct);

        var total = await db.LegacyPastedMaterials.AsNoTracking()
            .CountAsync(p => p.UserId == request.UserId, ct);
        return Result.Success(new PlaybookImportResultDto(imported, total));
    }
}
