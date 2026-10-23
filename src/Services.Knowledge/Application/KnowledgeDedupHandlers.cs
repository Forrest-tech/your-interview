using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================================================================
//  需求 6.4.3 / 6.4.4:去重 · 来源反查 · 机经候选题幂等导入
// ============================================================================

/// <summary>标题归一化指纹 —— 判重用。去大小写、去标点空白,中英文都保留字。</summary>
internal static class KnowledgeFingerprint
{
    public static string Of(string topic, string title)
    {
        var t = Regex.Replace($"{topic}|{title}", @"[\s\p{P}\p{S}]", string.Empty);
        return t.ToLowerInvariant();
    }
}

public sealed class GetSourceCompaniesQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetSourceCompaniesQuery, Result<IReadOnlyList<SourceCompanyDto>>>
{
    public async Task<Result<IReadOnlyList<SourceCompanyDto>>> Handle(
        GetSourceCompaniesQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 只投影分组需要的三列,聚合放内存做 ——
        // 不同提供器对 DateOnly 的 Max() 支持不一致,为了一个"最后面试日期"
        // 去赌 SQL 翻译不值得;这里是个人规模的知识库(几百到几千条),内存聚合足够快。
        var rows = await db.Items.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .Where(x => (x.SourceCompanyName != null && x.SourceCompanyName != string.Empty)
                     || (x.SourcesJson != null && x.SourcesJson != string.Empty))
            .Select(x => new
            {
                OldCompany = x.SourceCompanyName,
                SourcesJson = x.SourcesJson,
                Mastered = x.Mastery == MasteryLevel.Mastered,
                Due = x.NextReviewAt == null || x.NextReviewAt <= now,
                Date = x.SourceDate
            })
            .ToListAsync(ct);

        // 聚合老单来源 + 新多来源(SourcesJson) 的公司
        var grouped = new Dictionary<string, List<(bool Mastered, bool Due, DateOnly? Date)>>(StringComparer.OrdinalIgnoreCase);
        void Add(string company, bool mastered, bool due, DateOnly? date)
        {
            if (string.IsNullOrWhiteSpace(company)) return;
            if (!grouped.TryGetValue(company.Trim(), out var list))
            {
                list = new List<(bool, bool, DateOnly?)>();
                grouped[company.Trim()] = list;
            }
            list.Add((mastered, due, date));
        }

        foreach (var r in rows)
        {
            if (!string.IsNullOrWhiteSpace(r.OldCompany))
                Add(r.OldCompany!, r.Mastered, r.Due, r.Date);
            if (!string.IsNullOrWhiteSpace(r.SourcesJson))
            {
                try
                {
                    var sources = System.Text.Json.JsonSerializer.Deserialize<List<SourceEntryJson>>(r.SourcesJson,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (sources != null)
                    {
                        foreach (var s in sources)
                        {
                            if (!string.IsNullOrWhiteSpace(s.CompanyName))
                            {
                                DateOnly? d = null;
                                if (!string.IsNullOrWhiteSpace(s.Date) && DateOnly.TryParse(s.Date, out var pd))
                                    d = pd;
                                Add(s.CompanyName!, r.Mastered, r.Due, d ?? r.Date);
                            }
                        }
                    }
                }
                catch { /* 坏 JSON 跳过,不影响聚合 */ }
            }
        }

        var list = grouped
            .Select(g => new SourceCompanyDto(
                g.Key,
                g.Value.Count,
                g.Value.Count(x => x.Mastered),
                g.Value.Count(x => x.Due),
                g.Value.Max(x => x.Date)))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Company, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Result.Success<IReadOnlyList<SourceCompanyDto>>(list);
    }
}

public sealed class GetSourceTrailQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetSourceTrailQuery, Result<SourceTrailDto>>
{
    public async Task<Result<SourceTrailDto>> Handle(GetSourceTrailQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (item is null) return Result.Failure<SourceTrailDto>(Error.NotFound("知识点"));

        // 同场面试一起进来的题:这是"反查"真正有价值的部分 ——
        // 复习时想回忆的不是孤零零一题,而是"那一场我被问住的这一串"。
        var siblings = new List<KnowledgeListItemDto>();
        if (item.SourceInterviewEntryId is not null && item.SourceInterviewEntryId != Guid.Empty)
        {
            var rows = await db.Items.AsNoTracking()
                .Where(x => x.UserId == request.UserId
                    && x.SourceInterviewEntryId == item.SourceInterviewEntryId && x.Id != item.Id)
                .OrderBy(x => x.SourceRoundNo ?? 0)
                .ThenByDescending(x => x.Importance)
                .Take(50)
                .ToListAsync(ct);
            siblings = rows.Select(x => x.ToListItemDto(now)).ToList();
        }

        return Result.Success(new SourceTrailDto(
            item.Id, item.SourceCompanyName, item.SourceDate, item.SourceRoundNo,
            item.SourceRoundStage, item.SourceInterviewEntryId, item.SourceJobApplicationId,
            null, siblings));
    }
}

public sealed class FindDuplicatesQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<FindDuplicatesQuery, Result<IReadOnlyList<DuplicateGroupDto>>>
{
    public async Task<Result<IReadOnlyList<DuplicateGroupDto>>> Handle(
        FindDuplicatesQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        // 归一化要去掉标点,数据库里做不到(不同 DB 的正则能力不一致),
        // 所以只把 (id, topic, title) 三列拉回内存做分组 —— 知识库是个人规模,这一列很轻。
        var rows = await db.Items.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .Select(x => new { x.Id, x.Topic, x.Title })
            .ToListAsync(ct);

        var groups = rows
            .GroupBy(x => KnowledgeFingerprint.Of(x.Topic, x.Title))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .Take(request.MaxGroups is < 1 or > 200 ? 50 : request.MaxGroups)
            .ToList();

        if (groups.Count == 0) return Result.Success<IReadOnlyList<DuplicateGroupDto>>([]);

        var ids = groups.SelectMany(g => g.Select(x => x.Id)).ToList();
        var full = await db.Items.AsNoTracking().Where(x => x.UserId == request.UserId && ids.Contains(x.Id)).ToListAsync(ct);
        var byId = full.ToDictionary(x => x.Id);

        var result = groups.Select(g => new DuplicateGroupDto(
            g.First().Topic,
            g.Key,
            g.Select(x => byId[x.Id].ToListItemDto(now)).ToList()))
            .ToList();

        return Result.Success<IReadOnlyList<DuplicateGroupDto>>(result);
    }
}

public sealed class MergeDuplicatesCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<MergeDuplicatesCommand, Result>
{
    public async Task<Result> Handle(MergeDuplicatesCommand request, CancellationToken ct)
    {
        if (request.KeepId == request.MergeId)
            return Result.Failure(Error.Validation("Merge.Self", "不能与自己合并"));

        var keep = await db.Items.FirstOrDefaultAsync(x => x.Id == request.KeepId && x.UserId == request.UserId, ct);
        if (keep is null) return Result.Failure(Error.NotFound("要保留的知识点"));
        var dup = await db.Items.FirstOrDefaultAsync(x => x.Id == request.MergeId && x.UserId == request.UserId, ct);
        if (dup is null) return Result.Failure(Error.NotFound("要合并的知识点"));

        // 合并策略:保留方已有的字段一律不动(用户的编辑优先),
        // 只把保留方为空、被合并方有内容的字段补进来 —— 合并是"补缺",不是"覆盖"。
        keep.UpdateDetails(
            keep.Title, keep.Topic, keep.SubTopic ?? dup.SubTopic, keep.Question,
            Math.Max(keep.Difficulty, dup.Difficulty), Math.Max(keep.Importance, dup.Importance),
            keep.TagsJson ?? dup.TagsJson,
            keep.ConceptExplanation ?? dup.ConceptExplanation,
            keep.MyAnswer ?? dup.MyAnswer,
            keep.BetterAnswer ?? dup.BetterAnswer,
            keep.KeyPointsJson ?? dup.KeyPointsJson,
            keep.CommonMistakesJson ?? dup.CommonMistakesJson,
            keep.FollowUpsJson ?? dup.FollowUpsJson);

        ReferenceApplier.ApplyReferences(keep, dup.ReferencesJson);
        keep.SetImportKey(keep.ImportKey ?? dup.ImportKey);

        // 复习次数刻意不叠加:那是真实的复习历史,合并两条记录不该凭空多出复习次数。
        if (keep.SourceInterviewEntryId is null && dup.SourceInterviewEntryId is not null)
            keep.LinkToInterview(dup.SourceInterviewEntryId.Value, dup.SourceCompanyName, dup.SourceDate,
                dup.SourceRoundNo, dup.SourceRoundStage, dup.SourceJobApplicationId);

        dup.MarkDeleted();
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed class ImportInterviewCandidatesCommandHandler(KnowledgeDbContext db)
    : IRequestHandler<ImportInterviewCandidatesCommand, Result<CandidateImportResultDto>>
{
    public async Task<Result<CandidateImportResultDto>> Handle(
        ImportInterviewCandidatesCommand request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count == 0)
            return Result.Failure<CandidateImportResultDto>(
                Error.Validation("Import.Empty", "没有待导入的题目"));
        if (request.Items.Count > 300)
            return Result.Failure<CandidateImportResultDto>(
                Error.Validation("Import.TooMany", "单次最多导入 300 条"));

        // ---- 先建"已有指纹"集合,保证幂等 ----
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (request.EntryId is not null && request.EntryId != Guid.Empty)
        {
            var rows = await db.Items.AsNoTracking()
                .Where(x => x.UserId == request.UserId && x.SourceInterviewEntryId == request.EntryId)
                .Select(x => new { x.Title, x.Topic, x.ImportKey })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                seen.Add(KnowledgeFingerprint.Of(r.Topic, r.Title));
                if (!string.IsNullOrWhiteSpace(r.ImportKey)) seen.Add("key:" + r.ImportKey);
            }
        }

        var keys = request.Items
            .Select(x => x.ClientKey?.Trim())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!)
            .Distinct()
            .ToList();

        if (keys.Count > 0)
        {
            var hit = await db.Items.AsNoTracking()
                .Where(x => x.UserId == request.UserId && x.ImportKey != null && keys.Contains(x.ImportKey))
                .Select(x => x.ImportKey!)
                .ToListAsync(ct);
            foreach (var h in hit) seen.Add("key:" + h);
        }

        var created = 0;
        var skipped = 0;
        var ids = new List<Guid>();

        foreach (var row in request.Items)
        {
            if (string.IsNullOrWhiteSpace(row.Title) || string.IsNullOrWhiteSpace(row.Topic))
            {
                skipped++;   // 无效行跳过而不是整体失败 —— 批量导入要容错
                continue;
            }

            var fp = KnowledgeFingerprint.Of(row.Topic, row.Title);
            var kf = string.IsNullOrWhiteSpace(row.ClientKey) ? null : "key:" + row.ClientKey!.Trim();

            // 幂等命中:安静跳过。用户重复点"导入"不该看到报错。
            if (seen.Contains(fp) || (kf is not null && seen.Contains(kf))) { skipped++; continue; }

            var item = new KnowledgeItem(row.Title, row.Topic, row.Question,
                KnowledgeSource.FromInterview, subTopic: row.SubTopic,
                difficulty: row.Difficulty, importance: row.Importance,
                userId: request.UserId);

            item.UpdateDetails(row.Title, row.Topic, row.SubTopic, row.Question,
                row.Difficulty, row.Importance, row.TagsJson,
                row.ConceptExplanation, null, row.BetterAnswer,
                row.KeyPointsJson, null, null);
            item.SetImportKey(row.ClientKey);

            if (request.EntryId is not null && request.EntryId != Guid.Empty)
                item.LinkToInterview(request.EntryId.Value, request.Company, request.Date,
                    request.RoundNo, request.RoundStage, request.ApplicationId);
            else if (!string.IsNullOrWhiteSpace(request.Company))
                item.LinkToInterview(Guid.Empty, request.Company, request.Date,
                    request.RoundNo, request.RoundStage, request.ApplicationId);

            db.Items.Add(item);
            seen.Add(fp);
            if (kf is not null) seen.Add(kf);
            ids.Add(item.Id);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success(new CandidateImportResultDto(created, skipped, request.Items.Count, ids));
    }
}
