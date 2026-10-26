using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// FUN-03(票10):CreateEntry 对 JobApplicationId 幂等。
/// Tracker 点 Prep(PrepPlanDialog 路径与一键创建路径)重复触发时,当前用户名下
/// 已有同 JobApplicationId 的条目就返回既有条目 id、不新建第二条;
/// 无关联的建单行为不变;他人同 JobApplicationId 的条目不得被复用(票03 隔离口径)。
/// 走真实 CreateEntryCommandHandler + 真实 DB(无 DB 环境跳过,同 RoundPartialUpdateTests 先例)。
/// 测试当前用户恒为 Guid.Empty(TestCurrentUser),命令也以 Guid.Empty 发起。
/// </summary>
public sealed class CreateEntryDedupeTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";
    private InterviewsDbContext _db = null!;
    private bool _available;

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

    private static string UniqueCompany() => "Fun03-" + Guid.NewGuid().ToString("N")[..8];

    private CreateEntryCommandHandler Handler => new(_db);

    private async Task<int> CountByJobApplicationIdAsync(Guid jobApplicationId, bool ignoreFilters = false)
    {
        _db.ChangeTracker.Clear();
        var q = _db.Entries.AsNoTracking();
        if (ignoreFilters) q = q.IgnoreQueryFilters();
        return await q.CountAsync(x => x.JobApplicationId == jobApplicationId);
    }

    private async Task<int> CountByCompanyAsync(string company)
    {
        _db.ChangeTracker.Clear();
        return await _db.Entries.AsNoTracking().CountAsync(x => x.CompanyName == company);
    }

    [Fact]
    public async Task SameJobApplicationId_Twice_ReturnsExistingId_AndDoesNotCreateSecondEntry()
    {
        if (!_available) return; // 无沙盒 DB 的环境跳过
        var jobAppId = Guid.NewGuid();
        var company = UniqueCompany();
        var cmd = new CreateEntryCommand(Guid.Empty, null, company, "Senior .NET Developer", jobAppId);

        var first = await Handler.Handle(cmd, default);
        var second = await Handler.Handle(cmd, default);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value); // 第二次 Prep 跳既有条目
        (await CountByJobApplicationIdAsync(jobAppId)).Should().Be(1);
    }

    [Fact]
    public async Task NoJobApplicationId_Twice_CreatesTwoEntries()
    {
        if (!_available) return;
        var company = UniqueCompany();
        var cmd = new CreateEntryCommand(Guid.Empty, null, company, "Senior .NET Developer");

        var first = await Handler.Handle(cmd, default);
        var second = await Handler.Handle(cmd, default);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().NotBe(first.Value); // 无关联建单不去重,行为不变
        (await CountByCompanyAsync(company)).Should().Be(2);
    }

    [Fact]
    public async Task EmptyJobApplicationId_Twice_CreatesTwoEntries()
    {
        if (!_available) return;
        var company = UniqueCompany();
        // Guid.Empty 不是有效关联(领域层 LinkJobApplication 同口径),不得当去重键
        var cmd = new CreateEntryCommand(Guid.Empty, null, company, "Senior .NET Developer", Guid.Empty);

        var first = await Handler.Handle(cmd, default);
        var second = await Handler.Handle(cmd, default);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().NotBe(first.Value);
        (await CountByCompanyAsync(company)).Should().Be(2);
    }

    [Fact]
    public async Task DifferentJobApplicationIds_CreateSeparateEntries()
    {
        if (!_available) return;
        var company = UniqueCompany();

        var a = await Handler.Handle(
            new CreateEntryCommand(Guid.Empty, null, company, "Senior .NET Developer", Guid.NewGuid()), default);
        var b = await Handler.Handle(
            new CreateEntryCommand(Guid.Empty, null, company, "Senior .NET Developer", Guid.NewGuid()), default);

        a.IsSuccess.Should().BeTrue();
        b.IsSuccess.Should().BeTrue();
        b.Value.Should().NotBe(a.Value);
        (await CountByCompanyAsync(company)).Should().Be(2);
    }

    [Fact]
    public async Task OtherUsersEntry_WithSameJobApplicationId_IsNotReused()
    {
        if (!_available) return;
        var jobAppId = Guid.NewGuid();
        var company = UniqueCompany();
        var otherUser = Guid.NewGuid();

        // 直接种子:另一个用户已有一条同 JobApplicationId 的条目
        var seeded = new InterviewEntry(Guid.NewGuid(), company, "Senior .NET Developer",
            jobAppId, userId: otherUser);
        _db.Entries.Add(seeded);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // 当前用户(Empty)同 JobApplicationId 建单:不得命中他人的条目,必须自建
        var cmd = new CreateEntryCommand(Guid.Empty, null, company, "Senior .NET Developer", jobAppId);
        var mine = await Handler.Handle(cmd, default);
        mine.IsSuccess.Should().BeTrue();
        mine.Value.Should().NotBe(seeded.Id);

        // 再建一次:命中自己那条(幂等在本人范围内生效)
        var again = await Handler.Handle(cmd, default);
        again.IsSuccess.Should().BeTrue();
        again.Value.Should().Be(mine.Value);

        // 总数:他的 1 条 + 我的 1 条,互不吞并
        (await CountByJobApplicationIdAsync(jobAppId, ignoreFilters: true)).Should().Be(2);
    }
}
