using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Services.Jobs.Application;
using YourInterview.Services.Jobs.Domain;
using YourInterview.Services.Jobs.Infrastructure.Persistence;
using YourInterview.SharedContracts.Events;
using Xunit;

namespace YourInterview.Services.Jobs.Tests;

/// <summary>
/// 票65(ARC-08d):LegacyOutcomeTextAdapter + 消费者结果映射 boundary 补强。
/// adapter 为 internal 且无 InternalsVisibleTo —— 全部经消费者公开面
/// HandleAsync 走真库验证(同 OutcomeWritebackConsumerTests 先例,无 DB 跳过)。
/// 覆盖:受控词全表(票58 规则 0)、大小写/空白形态、越界数字串(开关默认 → 不回写,
/// 与 Interviews 侧 DeriveOutcome 的 D1 行为对照)、中文关键词回落四路与优先级
/// (未通过先于通过)、不可识别 → 不回写;FromContract 契约枚举全表 + 越界抛错。
/// </summary>
public sealed class OutcomeMappingBoundaryTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";

    private static readonly Guid Owner = Guid.NewGuid();
    private readonly List<Guid> _appIds = [];
    private JobsDbContext _seedDb = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        _seedDb = NewDb(Owner);
        try { _available = await _seedDb.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (_available)
        {
            var apps = await _seedDb.Applications.IgnoreQueryFilters()
                .Where(a => _appIds.Contains(a.Id)).ToListAsync();
            _seedDb.Applications.RemoveRange(apps);
            var companies = await _seedDb.Companies.IgnoreQueryFilters()
                .Where(c => c.UserId == Owner).ToListAsync();
            _seedDb.Companies.RemoveRange(companies);
            await _seedDb.SaveChangesAsync();
        }
        await _seedDb.DisposeAsync();
    }

    private static JobsDbContext NewDb(Guid currentUser)
    {
        var opts = new DbContextOptionsBuilder<JobsDbContext>().UseNpgsql(Cs).Options;
        return new JobsDbContext(opts, new TestCurrentUser(currentUser));
    }

    private async Task<JobApplication> SeedAppAsync()
    {
        var company = new Company("T65-" + Guid.NewGuid().ToString("N")[..8], userId: Owner);
        _seedDb.Companies.Add(company);
        await _seedDb.SaveChangesAsync();
        var app = new JobApplication(company.Id, "Senior .NET Developer", userId: Owner);
        app.MarkApplied();
        app.AddRound("Technical", null, null, null, null);
        _seedDb.Applications.Add(app);
        await _seedDb.SaveChangesAsync();
        _seedDb.ChangeTracker.Clear();
        _appIds.Add(app.Id);
        return app;
    }

    private async Task ConsumeAsync(InterviewOutcomeRecorded e)
    {
        await using var db = NewDb(Guid.Empty);
        var consumer = new InterviewOutcomeRecordedConsumer(db,
            NullLogger<InterviewOutcomeRecordedConsumer>.Instance);
        await consumer.HandleAsync(e, default);
    }

    private async Task<JobApplication> ReloadAsync(Guid appId)
    {
        await using var db = NewDb(Owner);
        return await db.Applications.IgnoreQueryFilters().Include(a => a.Rounds)
            .FirstAsync(a => a.Id == appId);
    }

    private static InterviewOutcomeRecorded Evt(Guid appId, string result, InterviewOutcome? outcome)
        => new(appId, Guid.NewGuid(), Owner, 1, result, outcome);

    /// <summary>旧事件(无枚举)+ 给定 Result → 断言第 1 轮投影结果。</summary>
    private async Task AssertLegacyAsync(string result, RoundOutcome expected)
    {
        var app = await SeedAppAsync();
        await ConsumeAsync(Evt(app.Id, result, null));
        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single(r => r.Order == 1).Outcome.Should().Be(expected);
    }

    // ---------- 规则 0:受控词整串精确匹配全表(既有测试仅覆盖 Rejected) ----------

    [Theory]
    [InlineData("Pending", RoundOutcome.Pending)]
    [InlineData("Passed", RoundOutcome.Passed)]
    [InlineData("Ghosted", RoundOutcome.Ghosted)]
    [InlineData("Cancelled", RoundOutcome.Cancelled)]
    [InlineData("NoShow", RoundOutcome.NoShow)]
    [InlineData(" ghosted ", RoundOutcome.Ghosted)] // Trim 后精确匹配
    [InlineData("PASSED", RoundOutcome.Passed)]     // 忽略大小写
    [InlineData("noshow", RoundOutcome.NoShow)]
    public async Task LegacyControlledVocabulary(string result, RoundOutcome expected)
    {
        if (!_available) return;
        await AssertLegacyAsync(result, expected);
    }

    // ---------- 中文关键词回落四路 + 优先级 ----------

    [Theory]
    [InlineData("通过", RoundOutcome.Passed)]
    [InlineData("恭喜,通过了终面", RoundOutcome.Passed)]
    [InlineData("不通过", RoundOutcome.Failed)]   // 「不通过」先于「通过」
    [InlineData("未通过", RoundOutcome.Failed)]
    [InlineData("被拒了", RoundOutcome.Failed)]
    [InlineData("挂了", RoundOutcome.Failed)]
    public async Task LegacyKeywordFallback(string result, RoundOutcome expected)
    {
        if (!_available) return;
        await AssertLegacyAsync(result, expected);
    }

    [Theory]
    [InlineData("待定")]        // 明确不回写(等人确认)
    [InlineData("等回复")]
    [InlineData("随便写点什么")] // 不可识别
    [InlineData("")]            // 空串 boundary
    [InlineData("-1")]          // 越界数字:Jobs 侧开关默认 → null,不回写(与 D1 对照:此处安全)
    [InlineData("99")]
    [InlineData("1")]           // 区间内数字:名字对不上 → 不当受控词;无中文关键词 → 不回写
    public async Task LegacyUnrecognized_NoWriteback(string result)
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await ConsumeAsync(Evt(app.Id, result, null));
        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single(r => r.Order == 1).Outcome.Should().Be(RoundOutcome.Pending);
        stored.Status.Should().Be(ApplicationStatus.Interview);
    }

    // ---------- FromContract:契约枚举全表(经 Outcome 字段直达) ----------

    [Theory]
    [InlineData(InterviewOutcome.Pending, RoundOutcome.Pending)]
    [InlineData(InterviewOutcome.Cancelled, RoundOutcome.Cancelled)]
    [InlineData(InterviewOutcome.NoShow, RoundOutcome.NoShow)]
    public async Task ContractEnum_RemainingArms(InterviewOutcome outcome, RoundOutcome expected)
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        await ConsumeAsync(Evt(app.Id, "任意展示文本", outcome));
        var stored = await ReloadAsync(app.Id);
        stored.Rounds.Single(r => r.Order == 1).Outcome.Should().Be(expected);
    }

    [Fact] // boundary:契约枚举越界值 → FromContract 抛 ArgumentOutOfRange(不静默回写)
    public async Task ContractEnum_OutOfRange_Throws()
    {
        if (!_available) return;
        var app = await SeedAppAsync();
        var act = () => ConsumeAsync(Evt(app.Id, "x", (InterviewOutcome)99));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
