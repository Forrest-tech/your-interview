using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票66:MockSession 软删除全局过滤器(AssessmentDbContext.HasQueryFilter)。
/// 此前 MockSession 漏配过滤器:MarkDeleted 后 GET 仍 200、列表/统计仍计入、
/// 删后写路径(答题/完成/重复删)照常成功。配过滤器后:一切读路径查不到、
/// 写路径先查会话→查不到即失败;未删会话与他人口径不受影响。
/// 走真实 Handler + 真实 DB(无 DB 环境跳过,同 CreateEntryDedupeTests 先例)。
/// 每例用独立随机 UserId 隔离统计/列表计数。
/// boundary 类别:已删实体、重复删除、不存在 id、空用户(零会话)、跨用户隔离。
/// </summary>
public sealed class SoftDeleteFilterTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";
    private bool _available;

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    /// <summary>种子:建一个会话(可选带一题),返回 (sessionId, questionId?)。</summary>
    private static async Task<(Guid SessionId, Guid? QuestionId)> SeedAsync(
        Guid userId, bool withQuestion)
    {
        await using var db = NewDb();
        var s = new MockSession(userId, "t66-" + Guid.NewGuid().ToString("N")[..8],
            SessionMode.FullLoop, null, null, 5, "en");
        Guid? qid = null;
        if (withQuestion)
            qid = s.AddQuestion("t66 question", QuestionType.Technical).Id;
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return (s.Id, qid);
    }

    private static async Task SoftDeleteAsync(Guid sessionId)
    {
        await using var db = NewDb();
        var s = await db.Sessions.FirstAsync(x => x.Id == sessionId);
        s.MarkDeleted();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task DeletedSession_Get_ReturnsFailure()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var (id, _) = await SeedAsync(user, withQuestion: false);
        await SoftDeleteAsync(id);

        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(id, user), default);
        r.IsFailure.Should().BeTrue("已软删会话不得再被读到(修复前此例返回 Success/200)");
    }

    [Fact]
    public async Task DeletedSession_List_ExcludesIt_KeepsAliveControl()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var (dead, _) = await SeedAsync(user, withQuestion: false);
        var (alive, _) = await SeedAsync(user, withQuestion: false);
        await SoftDeleteAsync(dead);

        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Items.Select(x => x.Id).Should().Contain(alive).And.NotContain(dead);
        r.Value.Total.Should().Be(1);
    }

    [Fact]
    public async Task DeletedSession_Stats_ExcludeSessionAndItsQuestions()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var (dead, _) = await SeedAsync(user, withQuestion: true);
        var (_, _) = await SeedAsync(user, withQuestion: true); // 未删对照:1 会话 1 题
        await SoftDeleteAsync(dead);

        await using var db = NewDb();
        var r = await new GetAssessmentStatsQueryHandler(db)
            .Handle(new GetAssessmentStatsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.TotalSessions.Should().Be(1, "统计不得计入已删会话");
        r.Value.TotalQuestions.Should().Be(1, "已删会话的题目不得计入(经 Sessions 关联过滤)");
    }

    [Fact]
    public async Task DeletedSession_DeleteAgain_ReturnsFailure()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var (id, _) = await SeedAsync(user, withQuestion: false);
        await SoftDeleteAsync(id);

        await using var db = NewDb();
        var r = await new DeleteSessionCommandHandler(db)
            .Handle(new DeleteSessionCommand(id, user), default);
        r.IsFailure.Should().BeTrue("重复删除已删会话应失败(修复前返回 Success/204)");
    }

    [Fact]
    public async Task DeletedSession_Answer_ReturnsFailure()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var (id, qid) = await SeedAsync(user, withQuestion: true);
        await SoftDeleteAsync(id);

        await using var db = NewDb();
        var r = await new AnswerQuestionCommandHandler(db).Handle(
            new AnswerQuestionCommand(id, qid!.Value, user, "late answer", null, null, null), default);
        r.IsFailure.Should().BeTrue("删后写路径必须失败(修复前可继续答题)");
    }

    [Fact]
    public async Task AliveSession_GetListStats_Unaffected_AndDeleteLifecycleWorks()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var (id, _) = await SeedAsync(user, withQuestion: true);

        await using var db = NewDb();
        var get = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(id, user), default);
        get.IsSuccess.Should().BeTrue("不变项:未删会话照常可读");
        var stats = await new GetAssessmentStatsQueryHandler(db)
            .Handle(new GetAssessmentStatsQuery(user), default);
        stats.Value.TotalSessions.Should().Be(1);

        var del = await new DeleteSessionCommandHandler(db)
            .Handle(new DeleteSessionCommand(id, user), default);
        del.IsSuccess.Should().BeTrue("不变项:正常删除流程仍成功");
        // 行仍在库里(软删不丢数据),只是被过滤器隐藏
        var raw = await db.Sessions.IgnoreQueryFilters().CountAsync(x => x.Id == id && x.IsDeleted);
        raw.Should().Be(1);
    }

    [Fact] // boundary:不存在的 id
    public async Task UnknownSession_Get_ReturnsFailure()
    {
        if (!_available) return;
        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db)
            .Handle(new GetSessionQuery(Guid.NewGuid(), Guid.NewGuid()), default);
        r.IsFailure.Should().BeTrue();
    }

    [Fact] // boundary:空用户(零会话) + 跨用户隔离不变项
    public async Task EmptyUser_ListAndStats_AreZero_AndOtherUsersSessionInvisible()
    {
        if (!_available) return;
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var (id, _) = await SeedAsync(owner, withQuestion: false);

        await using var db = NewDb();
        var list = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(stranger), default);
        list.IsSuccess.Should().BeTrue();
        list.Value.Total.Should().Be(0);
        var stats = await new GetAssessmentStatsQueryHandler(db)
            .Handle(new GetAssessmentStatsQuery(stranger), default);
        stats.Value.TotalSessions.Should().Be(0);
        var get = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(id, stranger), default);
        get.IsFailure.Should().BeTrue("不变项:他人会话不可见(隔离口径)");
    }
}
