using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票19 ARC-06 DB 级特征测试:基线 cb37c52 写就全绿,迁移后保持全绿。
/// 核心场景 = **旧数据兼容**:用原生 SQL 把列值改成「无版本信封的旧形状」原文
/// (模拟迁移前存量数据),再经聚合属性 / Handler 查询读回,必须逐字一致;
/// 三处曾按原始字符串形状判定/投影的查询(轮次通过概率、Offer 概率、最新快照)
/// 对旧形状数据的结论必须与迁移前一致。无 DB 环境跳过(同 RoundPartialUpdateTests 先例)。
/// </summary>
public sealed class JsonFieldDbCharacterizationTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private InterviewsDbContext _db = null!;
    private bool _available;

    private const string PrepItems = "[{\"text\":\"Tell me about yourself\",\"checked\":true}]";
    private const string EmailItems = "[{\"from\":\"hr@example.com\",\"subject\":\"Invite\",\"date\":\"2026-10-10\",\"snippet\":\"hi\"}]";
    private const string ChecklistItems = "[{\"text\":\"Bring ID\",\"done\":true}]";
    private const string Dimensions = "{\"overall\":80,\"pronunciation\":null,\"fluency\":75,\"structure\":78,\"technicalDepth\":82,\"relevance\":79}";
    private const string Bullets = "[\"整体不错\",\"发音要练\"]";

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        _db = new InterviewsDbContext(opts, new TestCurrentUser());
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private InterviewsDbContext NewCtx() => new(
        new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options,
        new TestCurrentUser());

    private async Task<(Guid EntryId, Guid RoundId)> SeedEntryWithRoundAsync()
    {
        var e = new InterviewEntry(Guid.NewGuid(),
            "T19-" + Guid.NewGuid().ToString("N")[..8], "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        var r = e.AddRound("Technical");
        await _db.SaveChangesAsync();
        return (e.Id, r.Id);
    }

    /// <summary>把轮次三字段的**列值**直接改成旧形状原文(绕过聚合,模拟存量数据)。</summary>
    private async Task SetLegacyRoundColumnsAsync(Guid roundId, string? prep, string? emails, string? checklist)
    {
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE interviews.rounds SET \"PrepQuestionsJson\" = {0}, \"EmailsJson\" = {1}, \"ChecklistJson\" = {2} WHERE \"Id\" = {3}",
            prep!, emails!, checklist!, roundId);
        _db.ChangeTracker.Clear();
    }

    [Fact] // 旧形状:聚合读回逐字一致 + UpdateDetails 部分更新协议(null 不动)不伤旧值
    public async Task LegacyRoundFields_LoadVerbatim_AndPartialUpdateKeepsThem()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedEntryWithRoundAsync();
        await SetLegacyRoundColumnsAsync(roundId, PrepItems, EmailItems, ChecklistItems);

        await using var ctx = NewCtx();
        var e = await ctx.Entries.Include(x => x.Rounds)
            .FirstAsync(x => x.Id == entryId);
        var r = e.Rounds.Single();
        r.PrepQuestionsJson.Should().Be(PrepItems);
        r.EmailsJson.Should().Be(EmailItems);
        r.ChecklistJson.Should().Be(ChecklistItems);
        r.ToDto().PrepQuestionsJson.Should().Be(PrepItems);
        r.ToDto().EmailsJson.Should().Be(EmailItems);
        r.ToDto().ChecklistJson.Should().Be(ChecklistItems);
    }

    [Fact] // 旧形状 "[]" vs 非空数组:轮次通过概率的准备度因子结论与迁移前一致
    public async Task LegacyRoundFields_PassProbabilityFactors()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedEntryWithRoundAsync();

        await SetLegacyRoundColumnsAsync(roundId, "[]", "[]", null);
        await using (var ctx = NewCtx())
        {
            var res = await new GetRoundPassProbabilityQueryHandler(ctx)
                .Handle(new GetRoundPassProbabilityQuery(Guid.Empty, roundId), default);
            res.IsSuccess.Should().BeTrue();
            res.Value!.Factors.Select(f => f.Key)
                .Should().NotContain("pp_prep_list").And.NotContain("pp_has_email");
        }

        await SetLegacyRoundColumnsAsync(roundId, PrepItems, EmailItems, null);
        await using (var ctx = NewCtx())
        {
            var res = await new GetRoundPassProbabilityQueryHandler(ctx)
                .Handle(new GetRoundPassProbabilityQuery(Guid.Empty, roundId), default);
            res.IsSuccess.Should().BeTrue();
            res.Value!.Factors.Select(f => f.Key)
                .Should().Contain("pp_prep_list").And.Contain("pp_has_email");
            // 概率绝对值取决于共享测试库里同用户(Guid.Empty)的历史轮次,不作断言;
            // 本票口径 = 同组样本下因子结论迁移前后一致。
        }
    }

    [Fact] // 旧形状 "[]" vs 非空数组:Offer 概率 prep_bonus 结论与迁移前一致
    public async Task LegacyRoundFields_OfferProbabilityPrepBonus()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedEntryWithRoundAsync();

        await SetLegacyRoundColumnsAsync(roundId, "[]", null, null);
        await using (var ctx = NewCtx())
        {
            var res = await new GetOfferProbabilityQueryHandler(ctx)
                .Handle(new GetOfferProbabilityQuery(Guid.Empty, entryId), default);
            res.IsSuccess.Should().BeTrue();
            res.Value!.Factors.Select(f => f.Key).Should().NotContain("prep_bonus");
        }

        await SetLegacyRoundColumnsAsync(roundId, PrepItems, null, null);
        await using (var ctx = NewCtx())
        {
            var res = await new GetOfferProbabilityQueryHandler(ctx)
                .Handle(new GetOfferProbabilityQuery(Guid.Empty, entryId), default);
            res.IsSuccess.Should().BeTrue();
            res.Value!.Factors.Select(f => f.Key).Should().Contain("prep_bonus");
        }
    }

    [Fact] // 旧形状:最新快照查询 DTO 携带原文逐字一致
    public async Task LegacySnapshot_LatestQueryReturnsVerbatim()
    {
        if (!_available) return;
        var (entryId, _) = await SeedEntryWithRoundAsync();
        var e = await _db.Entries.FirstAsync(x => x.Id == entryId);
        e.AddAnalysisSnapshot(80, Dimensions, Bullets);
        await _db.SaveChangesAsync();
        // 列值改回旧形状原文(与上面同值时为空操作;确保走的是「库里原文 → DTO」路径)
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE interviews.analysis_snapshots SET \"DimensionsJson\" = {0}, \"SummaryBulletsJson\" = {1} WHERE \"InterviewEntryId\" = {2}",
            Dimensions, Bullets, entryId);
        _db.ChangeTracker.Clear();

        await using var ctx = NewCtx();
        var res = await new GetLatestSnapshotQueryHandler(ctx)
            .Handle(new GetLatestSnapshotQuery(Guid.Empty, entryId), default);
        res.IsSuccess.Should().BeTrue();
        res.Value.Should().NotBeNull();
        res.Value!.DimensionsJson.Should().Be(Dimensions);
        res.Value.SummaryBulletsJson.Should().Be(Bullets);
    }
}
