using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票23 / COD-12:ListSessions 对「已评分但 Issues 为空」的会话不再 500。
///
/// 根源:ListSessionsQueryHandler 的 WeakestDimension 投影只守 scored.Count,
/// 未守 Issues 空集 —— scored 非空但各题 Issues 全空时 SelectMany 得空序列,
/// GroupBy 无组,.First() 抛 InvalidOperationException("Sequence contains no elements")
/// 冒成 500,单条合法数据即可打挂整个会话列表接口。
/// 空集口径(不自创,对齐相邻先例):取 null —— 同字段 scored.Count==0 支、
/// Complete 的 FirstOrDefault()?.Dimension、Domain TopIssue 的 Count==0→null、
/// Stats 无数据时 null/空表,四处一致。
///
/// Issues 空集合 vs null 的现状区分:API 层 ScoreBody.Issues 可空,Controller 以
/// `body.Issues ?? []` 归一化,null 只存在于请求边界;Handler/领域/EF 层 Issues
/// 导航恒为(可能为空的)集合、永不为 null —— 持久层不存在 null 形态,故本组用例
/// 只构造空集合形态,null 形态以 GetSession 回读 Issues 非 null 空集钉住(见首例)。
///
/// 走真实 Handler + 真实 DB(无 DB 环境跳过,同 SoftDeleteFilterTests 先例)。
/// 每例独立随机 UserId 隔离。boundary 类别:空集合(核心)、未评分会话、无题会话、
/// 空列表用户、混合会话(已评分空 Issues + 已评分有 Issues + 未评分同列)。
/// </summary>
public sealed class ListSessionsEmptyIssuesTests : IAsyncLifetime
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

    private static DimensionScores Scores(int v = 80) => new(v, v, v, v, v, v);

    /// <summary>
    /// 种子:经聚合直接建会话并落库。questionSpecs 每项 = 一道题的评分形态:
    /// null = 未评分;非 null = 已评分且 Issues 为该数组(可为空数组 = 本票目标形态)。
    /// </summary>
    private static async Task<Guid> SeedAsync(Guid userId,
        params IEnumerable<DimensionIssue>?[] questionSpecs)
    {
        await using var db = NewDb();
        var s = new MockSession(userId, "t23-" + Guid.NewGuid().ToString("N")[..8],
            SessionMode.FullLoop, null, null, Math.Max(questionSpecs.Length, 1), "en");
        foreach (var spec in questionSpecs)
        {
            var q = s.AddQuestion("t23 question", QuestionType.Technical);
            if (spec is not null)
                s.ScoreQuestion(q.Id, Scores(), spec, "t23 comment", null, null);
        }
        db.Sessions.Add(s);
        await db.SaveChangesAsync();
        return s.Id;
    }

    private static DimensionIssue Issue(string dimension, int severity)
        => new(dimension, $"t23 issue {dimension}", null, null, null, severity);

    // ---------- 核心回归(COD-12) ----------

    [Fact] // boundary:空集合 —— 已评分 + Issues 为空集
    public async Task ScoredWithEmptyIssues_List_Succeeds_SessionAppears_WeakestNull()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await SeedAsync(user, Array.Empty<DimensionIssue>());

        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue(
            "已评分+空 Issues 会话不得再让列表抛 InvalidOperationException(修复前此例红:First() 空序列)");
        r.Value.Total.Should().Be(1);
        var item = r.Value.Items.Should().ContainSingle(x => x.Id == sid).Subject;
        item.ScoredCount.Should().Be(1);
        item.QuestionCount.Should().Be(1);
        item.AnsweredCount.Should().Be(1, "已评分题 State=Scored ≠ Asked,计入已答");
        item.WeakestDimension.Should().BeNull(
            "空 Issues 的空集口径 = null,与同字段未评分口径及 Complete/TopIssue 先例一致");

        // null vs 空集现状钉子:同数据回读详情,Issues 是非 null 的空集合、TopIssue 为 null
        await using var db2 = NewDb();
        var detail = await new GetSessionQueryHandler(db2).Handle(new GetSessionQuery(sid, user), default);
        detail.IsSuccess.Should().BeTrue();
        var q = detail.Value.Questions.Should().ContainSingle().Subject;
        q.Issues.Should().NotBeNull().And.BeEmpty("持久层 Issues 恒为集合,null 形态不存在");
        q.TopIssueTitle.Should().BeNull();
        q.Scores.Should().NotBeNull();
    }

    [Fact] // 核心回归的真评分链形态:经 ScoreQuestionCommandHandler 以空 Issues 落分(评分端点允许空集)
    public async Task ScoredViaHandlerWithEmptyIssues_List_Succeeds()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        Guid sid, qid;
        await using (var db = NewDb())
        {
            var create = await new CreateSessionCommandHandler(db).Handle(
                new CreateSessionCommand(user, "t23-handler-chain", SessionMode.FullLoop,
                    null, null, 5, "en"), default);
            create.IsSuccess.Should().BeTrue();
            sid = create.Value;
            var add = await new AddQuestionsCommandHandler(db).Handle(
                new AddQuestionsCommand(sid, user,
                    new[] { new QuestionDraftIn("t23 chain q", "Technical", 3, null) }), default);
            add.IsSuccess.Should().BeTrue();
            qid = add.Value[0];
        }
        await using (var db = NewDb())
        {
            var ans = await new AnswerQuestionCommandHandler(db).Handle(
                new AnswerQuestionCommand(sid, qid, user, "t23 answer", null, null, null), default);
            ans.IsSuccess.Should().BeTrue();
            var score = await new ScoreQuestionCommandHandler(db).Handle(
                new ScoreQuestionCommand(sid, qid, user, 80, 80, 80, 80, 80, 80,
                    "ok", null, null, null, Array.Empty<IssueDraftIn>()), default);
            score.IsSuccess.Should().BeTrue("评分端点允许 Issues 为空集(本票前提)");
        }

        await using var verify = NewDb();
        var list = await new ListSessionsQueryHandler(verify).Handle(new ListSessionsQuery(user), default);
        list.IsSuccess.Should().BeTrue();
        var item = list.Value.Items.Should().ContainSingle(x => x.Id == sid).Subject;
        item.ScoredCount.Should().Be(1);
        item.WeakestDimension.Should().BeNull();
    }

    // ---------- 不变项:非空 Issues 的口径逐字不变 ----------

    [Fact] // 不变项:有 Issues 时 WeakestDimension 仍 = 平均 Severity 最高维度的分组键
    public async Task ScoredWithIssues_List_WeakestIsHighestAvgSeverityDimension_Unchanged()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        // 结构 avg=2;技术深度 avg=(5+3)/2=4 → 期望 技术深度(修复前后同口径)
        var sid = await SeedAsync(user,
            new[] { Issue("结构", 2), Issue("技术深度", 5), Issue("技术深度", 3) });

        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        var item = r.Value.Items.Should().ContainSingle(x => x.Id == sid).Subject;
        item.WeakestDimension.Should().Be("技术深度");
        item.ScoredCount.Should().Be(1);
    }

    // ---------- boundary / 邻居 ----------

    [Fact] // boundary:未评分会话(既有 scored.Count==0 支)—— WeakestDimension 仍 null,计数为 0
    public async Task UnscoredSession_List_WeakestNull_ScoredCountZero()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await SeedAsync(user, (IEnumerable<DimensionIssue>?)null);

        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        var item = r.Value.Items.Should().ContainSingle(x => x.Id == sid).Subject;
        item.ScoredCount.Should().Be(0);
        item.WeakestDimension.Should().BeNull();
    }

    [Fact] // boundary:无题会话(空 Questions 集合)—— 正常出现,各计数 0
    public async Task SessionWithNoQuestions_List_AppearsWithZeroCounts()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await SeedAsync(user);

        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        var item = r.Value.Items.Should().ContainSingle(x => x.Id == sid).Subject;
        item.QuestionCount.Should().Be(0);
        item.ScoredCount.Should().Be(0);
        item.WeakestDimension.Should().BeNull();
    }

    [Fact] // boundary:空列表用户(零会话)—— Total=0、空 Items,不抛
    public async Task EmptyUser_List_ReturnsZeroTotal()
    {
        if (!_available) return;
        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db)
            .Handle(new ListSessionsQuery(Guid.NewGuid()), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Total.Should().Be(0);
        r.Value.Items.Should().BeEmpty();
    }

    [Fact] // boundary:混合列表 —— 空 Issues 会话不得打挂同页其他会话,各自口径独立
    public async Task MixedList_EmptyIssuesSession_DoesNotBreakOthers()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var emptySid = await SeedAsync(user,
            Array.Empty<DimensionIssue>(),                       // 已评分题 Issues 空
            (IEnumerable<DimensionIssue>?)null);             // + 未评分题(同会话混合)
        var withSid = await SeedAsync(user, new[] { Issue("流利度", 4) });
        var unscoredSid = await SeedAsync(user, (IEnumerable<DimensionIssue>?)null);

        await using var db = NewDb();
        var r = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Total.Should().Be(3);
        var byId = r.Value.Items.ToDictionary(x => x.Id);
        byId[emptySid].ScoredCount.Should().Be(1);
        byId[emptySid].QuestionCount.Should().Be(2);
        byId[emptySid].WeakestDimension.Should().BeNull();
        byId[withSid].WeakestDimension.Should().Be("流利度");
        byId[unscoredSid].WeakestDimension.Should().BeNull();
    }

    [Fact] // 邻居核查:GetAssessmentStats 对同数据原本空集安全 —— TotalIssues=0、buckets 空、均分照常(不动 stats)
    public async Task StatsNeighbor_ScoredEmptyIssues_TotalIssuesZero_BucketsEmpty_AvgPresent()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        await SeedAsync(user, Array.Empty<DimensionIssue>());

        await using var db = NewDb();
        var r = await new GetAssessmentStatsQueryHandler(db)
            .Handle(new GetAssessmentStatsQuery(user), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.TotalSessions.Should().Be(1);
        r.Value.ScoredQuestions.Should().Be(1);
        r.Value.TotalIssues.Should().Be(0);
        r.Value.IssuesByDimension.Should().BeEmpty();
        r.Value.AverageScores.Should().NotBeNull("已评分题存在时均分口径不变(与 Issues 是否为空无关)");
        r.Value.AverageOverall.Should().NotBeNull();
    }
}
