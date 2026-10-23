using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 2026-10-19:备考方案全链集成测试(真实 Postres + Fake AI):
/// 生成 → 存为 Kind=PrepDoc/Phase=Pre 的轮次资产 → 读回。
/// 沙盒 DB 仅测试数据;无 DB 环境时跳过(见 AssertDbAvailable)。
/// </summary>
public sealed class PrepPlanFlowTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private InterviewsDbContext _db = null!;
    private bool _available;

    private sealed class FakeAi : IAiGatewayClient
    {
        public Task<AiCompletionResult> CompleteAsync(string purpose, string prompt,
            string? systemPrompt, double? temperature, int? maxTokens, CancellationToken ct)
        {
            purpose.Should().Be("interview-prep-plan");
            prompt.Should().Contain("之前轮次复盘");
            return Task.FromResult(new AiCompletionResult(
                "# 备考方案\n- 重点:URL shortener", "fake-model", 10, 20, 5));
        }

        public Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct)
            => Task.FromResult(new PrepSourceSettingsDto(true, true, false, false, null, false, null, null));

        public Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string source, string company,
            string? role, string? stage, CancellationToken ct)
            => Task.FromResult(new PrepSourceSearchDto(source, "disabled", null, [], null));
    }

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql(Cs).Options;
        _db = new InterviewsDbContext(opts, new TestCurrentUser());
        try
        {
            _available = await _db.Database.CanConnectAsync();
        }
        catch { _available = false; }
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Generate_SavesPrepDocAsset_AndGetReturnsIt()
    {
        if (!_available) return; // 无沙盒 DB 的环境(用户 Mac CI)跳过

        var e = new InterviewEntry(Guid.NewGuid(), "PrepPlanCo-" + Guid.NewGuid().ToString("N")[..6],
            "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        var r1 = e.AddRound("Technical");
        var r2 = e.AddRound("SystemDesign");
        await _db.SaveChangesAsync();
        e.UpdateRound(r1.Id, "Technical", null, "David", "Video", null,
            InterviewRoundOutcome.Passed, null, "DI 反馈:不够深");
        await _db.SaveChangesAsync();
        // 必须重载条目让聚合根带上 rounds/assets 集合
        var loaded = await _db.Entries
            .Include(x => x.Rounds).Include(x => x.Assets)
            .Include(x => x.Questions).Include(x => x.Weaknesses)
            .FirstAsync(x => x.Id == e.Id);
        var handler = new GeneratePrepPlanCommandHandler(_db, new FakeAi());
        var cmd = new GeneratePrepPlanCommand(Guid.Empty, loaded.Id, r2.Id,
            JdText: "JD", ResumeText: "resume", Experiences: "机经", Requirements: "req");

        var result = await handler.Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Markdown.Should().Contain("备考方案");
        result.Value.RoundOrder.Should().Be(2);

        var asset = await _db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == result.Value.AssetId);
        asset.Should().NotBeNull();
        asset!.Kind.Should().Be(AssetKind.PrepDoc);
        asset.Phase.Should().Be(AssetPhase.Pre);
        asset.RoundId.Should().Be(r2.Id);

        var get = await new GetPrepPlanQueryHandler(_db)
            .Handle(new GetPrepPlanQuery(Guid.Empty, loaded.Id, r2.Id), default);
        get.IsSuccess.Should().BeTrue();
        get.Value.Markdown.Should().Contain("URL shortener");
    }
}
