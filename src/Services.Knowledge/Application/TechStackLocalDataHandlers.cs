using System.Globalization;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================ DTO ============================
// 形状与票14/15 两面保持一致口径:导入返回 { imported, total } 对账。

public sealed record CustomCategoryDto(string Name, DateTimeOffset CreatedAt);

/// <summary>复习活动一天。Date 为用户本地日历日期串(YYYY-MM-DD,逐字口径)。</summary>
public sealed record ReviewActivityDto(string Date, int Count);

/// <summary>一次性导入(localStorage → 后端)的对账结果:本次新导入条数 + 导入后总数。</summary>
public sealed record LocalDataImportResultDto(int Imported, int Total);

/// <summary>复习导入的一条:(本地日期串, 当天次数)。</summary>
public sealed record ReviewActivityImportItem(string? Date, int Count);

// ============================ 纯规则(单测直接覆盖) ============================

public static class TechStackLocalDataRules
{
    /// <summary>分类名上限(与 KnowledgeItem.Topic 的 HasMaxLength(100) 同口径)。</summary>
    public const int MaxCategoryNameLength = 100;
    /// <summary>一次导入的分类数上限(票14 收藏 5000 的保守同级)。</summary>
    public const int MaxImportCategories = 1000;
    /// <summary>一次导入的复习天数上限(12 周热力图仅 84 天,给足历史余量)。</summary>
    public const int MaxImportActivityDays = 5000;
    /// <summary>单日复习次数上限(防脏数据把热力图档位打爆;真实日复习远低于此)。</summary>
    public const int MaxDailyCount = 100000;

    /// <summary>
    /// 分类名清洗:Trim、丢空/纯空白、超长截断不做(超长由校验器拒)、
    /// 大小写不敏感去重保首现原文与顺序。null 元素按空处理。
    /// </summary>
    public static IReadOnlyList<string> NormalizeCategoryNames(IEnumerable<string?>? names)
    {
        if (names is null) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0) continue;
            if (seen.Add(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>
    /// 本地日期键解析:必须是严格 YYYY-MM-DD 且为真实日历日(2026-02-30 拒)。
    /// 只解析、不换算:键即用户本地日历日期,逐字保真(见实体注释)。
    /// </summary>
    public static bool TryParseLocalDate(string? raw, out DateOnly date)
        => DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);

    /// <summary>复习导入批内合并:同日多条取 max(与跨批合并口径一致,只升不降)。</summary>
    public static IReadOnlyDictionary<DateOnly, int> MergeImportBatch(
        IEnumerable<(DateOnly Date, int Count)> entries)
    {
        var merged = new Dictionary<DateOnly, int>();
        foreach (var (date, count) in entries)
        {
            if (!merged.TryGetValue(date, out var cur) || count > cur)
                merged[date] = count;
        }
        return merged;
    }
}

// ============================ 自定义分类 ============================

public sealed record ListCustomCategoriesQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<CustomCategoryDto>>>;

/// <summary>新增空分类(幂等:已存在含软删恢复,一律成功,不重复建行)。</summary>
public sealed record AddCustomCategoryCommand(Guid UserId, string Name) : IRequest<Result>;

/// <summary>删除空分类(幂等:不存在也成功)。</summary>
public sealed record RemoveCustomCategoryCommand(Guid UserId, string Name) : IRequest<Result>;

/// <summary>
/// 一次性导入本地分类(localStorage ts.customCategories → 后端)。
/// 幂等:大小写不敏感去重,只导入尚不存在的;重复调用 Imported=0。
/// </summary>
public sealed record ImportCustomCategoriesCommand(Guid UserId, IReadOnlyList<string> Names)
    : IRequest<Result<LocalDataImportResultDto>>;

// ============================ 复习活动 ============================

public sealed record GetReviewActivityQuery(Guid UserId)
    : IRequest<Result<IReadOnlyList<ReviewActivityDto>>>;

/// <summary>
/// 记录一次复习(前端 grade 成功后调用)。Date 由前端按本地日历口径算好后
/// 上报,后端只校验格式并按 (UserId, Date) 累加 —— 后端不用自身时钟推导
/// 「今天」,避免 UTC 口径悄悄替换本地口径。返回累加后的当天数据。
/// </summary>
public sealed record RecordReviewActivityCommand(Guid UserId, string Date)
    : IRequest<Result<ReviewActivityDto>>;

/// <summary>
/// 一次性导入本地复习记录(localStorage ts.reviewActivity → 后端)。
/// 合并口径 = 同日取 max(只升不降):首次导入后端为空,原值逐字保真;
/// 重复导入 Imported=0 且不虚增已有计数。
/// </summary>
public sealed record ImportReviewActivityCommand(Guid UserId, IReadOnlyList<ReviewActivityImportItem> Entries)
    : IRequest<Result<LocalDataImportResultDto>>;

// ============================ 校验 ============================

public sealed class AddCustomCategoryCommandValidator : AbstractValidator<AddCustomCategoryCommand>
{
    public AddCustomCategoryCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(TechStackLocalDataRules.MaxCategoryNameLength)
            .WithMessage("分类名必填且不超过 100 字符");
    }
}

public sealed class ImportCustomCategoriesCommandValidator : AbstractValidator<ImportCustomCategoriesCommand>
{
    public ImportCustomCategoriesCommandValidator()
    {
        // Must 谓词须 null-safe(票14 boundary 教训:同链后续规则仍会执行,null 上 .Count 抛 NRE)。
        RuleFor(x => x.Names).NotNull()
            .Must(ns => ns is null || ns.Count <= TechStackLocalDataRules.MaxImportCategories)
            .WithMessage("一次导入的分类数过多");
        RuleForEach(x => x.Names).MaximumLength(TechStackLocalDataRules.MaxCategoryNameLength)
            .WithMessage("分类名过长");
    }
}

public sealed class RecordReviewActivityCommandValidator : AbstractValidator<RecordReviewActivityCommand>
{
    public RecordReviewActivityCommandValidator()
    {
        RuleFor(x => x.Date)
            .Must(d => TechStackLocalDataRules.TryParseLocalDate(d, out _))
            .WithMessage("日期必须是 YYYY-MM-DD 格式的真实日历日");
    }
}

public sealed class ImportReviewActivityCommandValidator : AbstractValidator<ImportReviewActivityCommand>
{
    public ImportReviewActivityCommandValidator()
    {
        RuleFor(x => x.Entries).NotNull()
            .Must(es => es is null || es.Count <= TechStackLocalDataRules.MaxImportActivityDays)
            .WithMessage("一次导入的复习天数过多");
        RuleForEach(x => x.Entries).ChildRules(item =>
        {
            item.RuleFor(e => e.Date)
                .Must(d => TechStackLocalDataRules.TryParseLocalDate(d, out _))
                .WithMessage("日期必须是 YYYY-MM-DD 格式的真实日历日");
            item.RuleFor(e => e.Count)
                .InclusiveBetween(1, TechStackLocalDataRules.MaxDailyCount)
                .WithMessage("单日复习次数须在 1..100000");
        });
    }
}

// ============================ Handler:自定义分类 ============================

public sealed class ListCustomCategoriesQueryHandler(KnowledgeDbContext db)
    : IRequestHandler<ListCustomCategoriesQuery, Result<IReadOnlyList<CustomCategoryDto>>>
{
    public async Task<Result<IReadOnlyList<CustomCategoryDto>>> Handle(
        ListCustomCategoriesQuery request, CancellationToken ct)
    {
        var items = await db.CustomCategories.AsNoTracking()
            .Where(c => c.UserId == request.UserId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Name)
            .Select(c => new CustomCategoryDto(c.Name, c.CreatedAt))
            .ToListAsync(ct);
        return Result.Success<IReadOnlyList<CustomCategoryDto>>(items);
    }
}

public sealed class AddCustomCategoryCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<AddCustomCategoryCommand, Result>
{
    public async Task<Result> Handle(AddCustomCategoryCommand request, CancellationToken ct)
    {
        var name = TechStackLocalDataRules.NormalizeCategoryNames([request.Name]).FirstOrDefault();
        if (name is null) return Result.Failure(Error.Validation("Category.Empty", "分类名不能为空"));

        // 含软删行查:删除是软删,重加必须恢复原行 —— (UserId, Name) 唯一索引
        // 不允许第二行。比较口径与前端查重一致:大小写不敏感(同 RenameTopic 先例的 ToLower 比对)。
        var existing = await db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == request.UserId)
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), ct);
        if (existing is not null)
        {
            if (existing.IsDeleted) { existing.Restore(); await db.SaveChangesAsync(ct); }
            return Result.Success();
        }

        db.CustomCategories.Add(new TechStackCategory(request.UserId, name));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class RemoveCustomCategoryCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<RemoveCustomCategoryCommand, Result>
{
    public async Task<Result> Handle(RemoveCustomCategoryCommand request, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0) return Result.Success(); // 幂等:空名无可删,按已删处理

        var cat = await db.CustomCategories
            .Where(c => c.UserId == request.UserId)
            .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower(), ct);
        if (cat is null) return Result.Success(); // 幂等:不存在也算删除成功

        cat.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportCustomCategoriesCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ImportCustomCategoriesCommand, Result<LocalDataImportResultDto>>
{
    public async Task<Result<LocalDataImportResultDto>> Handle(
        ImportCustomCategoriesCommand request, CancellationToken ct)
    {
        var wanted = TechStackLocalDataRules.NormalizeCategoryNames(request.Names);

        var existing = await db.CustomCategories.IgnoreQueryFilters()
            .Where(c => c.UserId == request.UserId)
            .ToListAsync(ct);
        var existingKeys = existing.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var restored = 0;
        foreach (var cat in existing.Where(c => c.IsDeleted &&
                     wanted.Any(w => string.Equals(w, c.Name, StringComparison.OrdinalIgnoreCase))))
        { cat.Restore(); restored++; }
        foreach (var name in wanted.Where(w => !existingKeys.Contains(w)))
        {
            db.CustomCategories.Add(new TechStackCategory(request.UserId, name));
            imported++;
        }
        if (imported > 0 || restored > 0)
            await db.SaveChangesAsync(ct);

        var total = await db.CustomCategories.AsNoTracking()
            .CountAsync(c => c.UserId == request.UserId, ct);
        return Result.Success(new LocalDataImportResultDto(imported, total));
    }
}

// ============================ Handler:复习活动 ============================

public sealed class GetReviewActivityQueryHandler(KnowledgeDbContext db)
    : IRequestHandler<GetReviewActivityQuery, Result<IReadOnlyList<ReviewActivityDto>>>
{
    public async Task<Result<IReadOnlyList<ReviewActivityDto>>> Handle(
        GetReviewActivityQuery request, CancellationToken ct)
    {
        var rows = await db.ReviewActivities.AsNoTracking()
            .Where(a => a.UserId == request.UserId)
            .OrderBy(a => a.Date)
            .ToListAsync(ct);
        var items = rows
            .Select(a => new ReviewActivityDto(
                a.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), a.Count))
            .ToList();
        return Result.Success<IReadOnlyList<ReviewActivityDto>>(items);
    }
}

public sealed class RecordReviewActivityCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<RecordReviewActivityCommand, Result<ReviewActivityDto>>
{
    public async Task<Result<ReviewActivityDto>> Handle(
        RecordReviewActivityCommand request, CancellationToken ct)
    {
        if (!TechStackLocalDataRules.TryParseLocalDate(request.Date, out var date))
            return Result.Failure<ReviewActivityDto>(
                Error.Validation("ReviewActivity.InvalidDate", "日期必须是 YYYY-MM-DD 格式的真实日历日"));

        var row = await db.ReviewActivities
            .FirstOrDefaultAsync(a => a.UserId == request.UserId && a.Date == date, ct);
        if (row is null)
        {
            row = new ReviewActivityDay(request.UserId, date, 1);
            db.ReviewActivities.Add(row);
        }
        else
        {
            row.Increment();
        }
        await db.SaveChangesAsync(ct);

        return Result.Success(new ReviewActivityDto(
            date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.Count));
    }
}

public sealed class ImportReviewActivityCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ImportReviewActivityCommand, Result<LocalDataImportResultDto>>
{
    public async Task<Result<LocalDataImportResultDto>> Handle(
        ImportReviewActivityCommand request, CancellationToken ct)
    {
        // 校验器已保证日期合法/计数在界内;此处再解析一遍,解析不出的防御性跳过。
        var parsed = new List<(DateOnly Date, int Count)>();
        foreach (var e in request.Entries)
        {
            if (e is not null && TechStackLocalDataRules.TryParseLocalDate(e.Date, out var d))
                parsed.Add((d, e.Count));
        }
        var batch = TechStackLocalDataRules.MergeImportBatch(parsed);
        if (batch.Count == 0)
        {
            var current = await db.ReviewActivities.AsNoTracking()
                .CountAsync(a => a.UserId == request.UserId, ct);
            return Result.Success(new LocalDataImportResultDto(0, current));
        }

        var dates = batch.Keys.ToList();
        var existing = await db.ReviewActivities
            .Where(a => a.UserId == request.UserId && dates.Contains(a.Date))
            .ToListAsync(ct);
        var byDate = existing.ToDictionary(a => a.Date);

        var imported = 0;
        var changed = false;
        foreach (var (date, count) in batch)
        {
            if (byDate.TryGetValue(date, out var row))
            {
                if (row.RaiseTo(count)) changed = true; // 已有日期:只升不降,不计 imported
            }
            else
            {
                db.ReviewActivities.Add(new ReviewActivityDay(request.UserId, date, count));
                imported++;
                changed = true;
            }
        }
        if (changed) await db.SaveChangesAsync(ct);

        var total = await db.ReviewActivities.AsNoTracking()
            .CountAsync(a => a.UserId == request.UserId, ct);
        return Result.Success(new LocalDataImportResultDto(imported, total));
    }
}
