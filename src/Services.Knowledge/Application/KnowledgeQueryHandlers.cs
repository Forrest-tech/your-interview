using System.Text.RegularExpressions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Knowledge.Domain;
using YourInterview.Services.Knowledge.Infrastructure.Persistence;

namespace YourInterview.Services.Knowledge.Application;

// ============================ Handler:查询 ============================

public sealed class ListKnowledgeQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<ListKnowledgeQuery, Result<PagedResult<KnowledgeListItemDto>>>
{
    public async Task<Result<PagedResult<KnowledgeListItemDto>>> Handle(
        ListKnowledgeQuery request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var page = request.Page < 1 ? 1 : request.Page;
        var size = request.PageSize is < 1 or > 200 ? 20 : request.PageSize;

        var q = db.Items.AsNoTracking().Where(x => x.UserId == request.UserId).AsQueryable();

        if (request.IncludeDeleted)
            q = q.IgnoreQueryFilters().Where(x => x.IsDeleted && x.UserId == request.UserId);

        if (!string.IsNullOrWhiteSpace(request.Topic))
            q = q.Where(x => x.Topic == request.Topic);

        if (!string.IsNullOrWhiteSpace(request.Mastery)
            && Enum.TryParse<MasteryLevel>(request.Mastery, true, out var m))
            q = q.Where(x => x.Mastery == m);

        if (!string.IsNullOrWhiteSpace(request.Source)
            && Enum.TryParse<KnowledgeSource>(request.Source, true, out var src))
            q = q.Where(x => x.Source == src);

        if (!string.IsNullOrWhiteSpace(request.Tag))
            q = q.Where(x => x.TagsJson != null && x.TagsJson.Contains(request.Tag));

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim().ToLowerInvariant();
            q = q.Where(x => x.Title.ToLower().Contains(s)
                          || x.Question.ToLower().Contains(s)
                          || x.Topic.ToLower().Contains(s));
        }

        // 今日待复习:没排过复习计划的(NextReviewAt 为空)也算待复习 —— 新知识点本来就该学
        if (request.DueOnly)
            q = q.Where(x => x.NextReviewAt == null || x.NextReviewAt <= now);

        // 按来源公司反查(需求 6.4.4):大小写不敏感 —— 公司名来自不同服务的录入,大小写不可控
        // 新多来源 SourcesJson 也要查:JSON 里找 "companyName":"xxx"
        if (!string.IsNullOrWhiteSpace(request.Company))
        {
            var c = request.Company.Trim().ToLower();
            var jsonPattern = "\"companyname\":\"" + c + "\"";
            q = q.Where(x => (x.SourceCompanyName != null
                          && x.SourceCompanyName.ToLower() == c)
                      || (x.SourcesJson != null
                          && x.SourcesJson.ToLower().Contains(jsonPattern)));
        }

        // 按某一场面试反查
        if (request.EntryId is not null && request.EntryId != Guid.Empty)
            q = q.Where(x => x.SourceInterviewEntryId == request.EntryId);

        var total = await q.CountAsync(ct);

        // 排序:待复习优先 → 重要度 → 难度 → 最近创建
        var items = await q
            .OrderByDescending(x => x.NextReviewAt == null || x.NextReviewAt <= now)
            .ThenByDescending(x => x.Importance)
            .ThenBy(x => x.Mastery)
            .ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .ToListAsync(ct);

        return Result.Success(new PagedResult<KnowledgeListItemDto>(
            items.Select(x => x.ToListItemDto(now)).ToList(), total, page, size));
    }
}

public sealed class GetKnowledgeItemQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetKnowledgeItemQuery, Result<KnowledgeItemDto>>
{
    public async Task<Result<KnowledgeItemDto>> Handle(GetKnowledgeItemQuery request, CancellationToken ct)
    {
        var k = await db.Items.AsNoTracking()
            .Include(x => x.ReviewLogs)
            .Include(x => x.Relations)
            .FirstOrDefaultAsync(x => x.Id == request.Id && x.UserId == request.UserId, ct);
        if (k is null)
            return Result.Failure<KnowledgeItemDto>(Error.NotFound("知识点"));

        // 票70:读时 xmin 一并投影取出,作为 DTO.Version 交客户端在保存时回传校验。
        // 实体走 AsNoTracking + Include,影子属性不便从实体状态读,故单独一条投影查询。
        var version = await db.Items.AsNoTracking()
            .Where(x => x.Id == k.Id)
            .Select(x => EF.Property<uint>(x, "xmin"))
            .FirstAsync(ct);

        return Result.Success(k.ToDto(clock.GetUtcNow(), version));
    }
}

public sealed class GetTopicsQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetTopicsQuery, Result<IReadOnlyList<TopicSummaryDto>>>
{
    public async Task<Result<IReadOnlyList<TopicSummaryDto>>> Handle(GetTopicsQuery request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        var rows = await db.Items.AsNoTracking()
            .Where(x => x.UserId == request.UserId)
            .GroupBy(x => x.Topic)
            .Select(g => new TopicSummaryDto(
                g.Key,
                g.Count(),
                g.Count(x => x.Mastery == MasteryLevel.Mastered),
                g.Count(x => x.NextReviewAt == null || x.NextReviewAt <= now),
                g.Average(x => (double?)x.ReviewCount)))
            .ToListAsync(ct);

        return Result.Success<IReadOnlyList<TopicSummaryDto>>(
            rows.OrderByDescending(r => r.Total).ToList());
    }
}

public sealed class GetKnowledgeStatsQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetKnowledgeStatsQuery, Result<KnowledgeStatsDto>>
{
    public async Task<Result<KnowledgeStatsDto>> Handle(GetKnowledgeStatsQuery request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var weekEnd = now.AddDays(7);
        var weekStart = now.AddDays(-7);

        var items = await db.Items.AsNoTracking()
            .Where(x => x.UserId == request.UserId).ToListAsync(ct);
        // 复习日志是子表(无 UserId):按当前用户的条目 Id 集合收口
        var ownedIds = db.Items.Where(x => x.UserId == request.UserId).Select(x => x.Id);
        var logs = await db.ReviewLogs.AsNoTracking()
            .Where(l => ownedIds.Contains(l.ItemId)).ToListAsync(ct);

        var byMastery = items.GroupBy(x => x.Mastery.ToString())
            .Select(g => new MasteryBucketDto(g.Key, g.Count()))
            .OrderBy(x => x.Mastery)
            .ToList();

        var byTopic = items.GroupBy(x => x.Topic)
            .Select(g => new TopicSummaryDto(g.Key, g.Count(),
                g.Count(x => x.Mastery == MasteryLevel.Mastered),
                g.Count(x => x.NextReviewAt == null || x.NextReviewAt <= now),
                Math.Round(g.Average(x => (double)x.ReviewCount), 1)))
            .OrderByDescending(x => x.Total)
            .ToList();

        var bySource = items.GroupBy(x => x.Source.ToString())
            .Select(g => new CategoryCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        // "最薄弱的技术主题" = 掌握度最低 + 复习次数多的 topic —— 给前端"该补哪里"用
        var weakTopics = items.GroupBy(x => x.Topic)
            .Select(g => new
            {
                Topic = g.Key,
                Score = g.Count(x => x.Mastery is MasteryLevel.New or MasteryLevel.NeedsReview)
                        * 2 + g.Count(x => x.Mastery == MasteryLevel.Learning)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(8)
            .Select(x => new CategoryCountDto(x.Topic, x.Score))
            .ToList();

        var mastered = items.Count(x => x.Mastery == MasteryLevel.Mastered);

        return Result.Success(new KnowledgeStatsDto(
            items.Count,
            items.Count(x => x.NextReviewAt == null || x.NextReviewAt <= now),
            items.Count(x => x.NextReviewAt > now && x.NextReviewAt <= weekEnd),
            logs.Count(l => l.ReviewedAt >= weekStart),
            logs.Count,
            items.Count == 0 ? null : Math.Round(items.Average(x => (double)x.ReviewCount), 1),
            items.Count == 0 ? null : Math.Round(mastered * 100.0 / items.Count, 1),
            byMastery, byTopic, bySource, weakTopics));
    }
}

public sealed class GetDuePlanQueryHandler(KnowledgeDbContext db, TimeProvider clock)
    : IRequestHandler<GetDuePlanQuery, Result<IReadOnlyList<DuePlanDto>>>
{
    public async Task<Result<IReadOnlyList<DuePlanDto>>> Handle(GetDuePlanQuery request,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var days = request.Days is < 1 or > 90 ? 7 : request.Days;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var horizon = now.AddDays(days);

        var items = await db.Items.AsNoTracking()
            .Include(x => x.ReviewLogs)
            .Include(x => x.Relations)
            .Where(x => x.UserId == request.UserId
                && (x.NextReviewAt == null || x.NextReviewAt <= horizon))
            .ToListAsync(ct);

        // 逾期的统一归到"今天",让复习计划不出现过去的日期
        var plan = items
            .GroupBy(x =>
            {
                if (x.NextReviewAt is null || x.NextReviewAt <= now) return today;
                var d = DateOnly.FromDateTime(x.NextReviewAt.Value.UtcDateTime);
                return d < today ? today : d;
            })
            .OrderBy(g => g.Key)
            .Select(g => new DuePlanDto(g.Key,
                g.OrderByDescending(x => x.Importance).Select(x => x.ToListItemDto(now)).ToList()))
            .ToList();

        return Result.Success<IReadOnlyList<DuePlanDto>>(plan);
    }
}

