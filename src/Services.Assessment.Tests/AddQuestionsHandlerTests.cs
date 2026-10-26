using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票69:AddQuestions 对全新会话恒回 409 的回归 + 真并发口径 + boundary。
///
/// 根源(实测状态转储钉死):全新会话经 Include 加载后 AddQuestion,新 MockQuestion
/// 因 Guid 主键在构造函数预赋值被 EF 误判为 Modified → UPDATE 命中 0 行 →
/// DbUpdateConcurrencyException → 全局映射 409。AssessmentDbContext 的
/// MarkNewChildrenAsAdded 原只翻 Detached,漏掉 Modified 误判;修复后按
/// 「查库不存在才标 Added」处理 MockQuestion / DimensionIssue 两类子实体。
///
/// 走真实 Handler + 真实 DB(无 DB 环境跳过,同 SoftDeleteFilterTests 先例)。
/// 每例独立随机 UserId 隔离。boundary 类别:空集合、超上限(上限值 ±1)、
/// 空/超长正文、难度越界、真并发冲突、跨用户/不存在会话。
/// </summary>
public sealed class AddQuestionsHandlerTests : IAsyncLifetime
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

    /// <summary>经 Create Handler 建全新会话(独立上下文,与 API 首加题链路同形)。</summary>
    private static async Task<Guid> CreateFreshSessionAsync(Guid userId, int target = 5)
    {
        await using var db = NewDb();
        var r = await new CreateSessionCommandHandler(db).Handle(
            new CreateSessionCommand(userId, "t69-" + Guid.NewGuid().ToString("N")[..8],
                SessionMode.FullLoop, null, null, target, "en"), default);
        r.IsSuccess.Should().BeTrue();
        return r.Value;
    }

    private static QuestionDraftIn Q(string text, int difficulty = 3)
        => new(text, "Technical", difficulty, null);

    // ---------- 核心回归 ----------

    [Fact]
    public async Task FreshSession_FirstAddQuestions_SucceedsAndPersists()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(user);

        await using var db = NewDb();
        var r = await new AddQuestionsCommandHandler(db).Handle(
            new AddQuestionsCommand(sid, user, new[] { Q("t69 q1"), Q("t69 q2") }), default);
        r.IsSuccess.Should().BeTrue("全新会话首加题不得再抛并发冲突(修复前 DbUpdateConcurrencyException→409)");
        r.Value.Should().HaveCount(2);

        await using var verify = NewDb();
        var detail = await new GetSessionQueryHandler(verify).Handle(new GetSessionQuery(sid, user), default);
        detail.IsSuccess.Should().BeTrue();
        detail.Value.Questions.Should().HaveCount(2);
        detail.Value.Questions.Select(q => q.Sequence).Should().Equal(1, 2);
        detail.Value.Status.Should().Be(nameof(SessionStatus.InProgress));
    }

    [Fact]
    public async Task AddQuestions_SecondBatch_AppendsWithContinuingSequence()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(user);

        await using (var db = NewDb())
        {
            var r1 = await new AddQuestionsCommandHandler(db).Handle(
                new AddQuestionsCommand(sid, user, new[] { Q("batch1 q1") }), default);
            r1.IsSuccess.Should().BeTrue();
        }
        await using (var db = NewDb())
        {
            var r2 = await new AddQuestionsCommandHandler(db).Handle(
                new AddQuestionsCommand(sid, user, new[] { Q("batch2 q1"), Q("batch2 q2") }), default);
            r2.IsSuccess.Should().BeTrue("对已持久化会话的后续加题同样不得 409");
        }

        await using var verify = NewDb();
        var detail = await new GetSessionQueryHandler(verify).Handle(new GetSessionQuery(sid, user), default);
        detail.Value.Questions.Should().HaveCount(3);
        detail.Value.Questions.Select(q => q.Sequence).Should().Equal(1, 2, 3);
    }

    [Fact] // 邻居回归:加题→答题→评分(带新 DimensionIssue)全链,同根 Modified 误判一并钉住
    public async Task AddAnswerScore_FullChain_SucceedsAfterFix()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(user);
        Guid qid;
        await using (var db = NewDb())
        {
            var add = await new AddQuestionsCommandHandler(db).Handle(
                new AddQuestionsCommand(sid, user, new[] { Q("chain q") }), default);
            add.IsSuccess.Should().BeTrue();
            qid = add.Value[0];
        }
        await using (var db = NewDb())
        {
            var ans = await new AnswerQuestionCommandHandler(db).Handle(
                new AnswerQuestionCommand(sid, qid, user, "chain answer", null, null, null), default);
            ans.IsSuccess.Should().BeTrue();
        }
        await using (var db = NewDb())
        {
            var score = await new ScoreQuestionCommandHandler(db).Handle(
                new ScoreQuestionCommand(sid, qid, user, 80, 80, 80, 80, 80, 80,
                    "ok", null, null, null,
                    new[] { new IssueDraftIn("结构", "chain issue", null, null, null, 3) }), default);
            score.IsSuccess.Should().BeTrue("评分新增 DimensionIssue 同属子实体误判面,不得 409");
        }

        await using var verify = NewDb();
        var detail = await new GetSessionQueryHandler(verify).Handle(new GetSessionQuery(sid, user), default);
        var q = detail.Value.Questions.Should().ContainSingle().Subject;
        q.State.Should().Be(nameof(QuestionState.Scored));
        q.AnswerText.Should().Be("chain answer");
        q.Scores.Should().NotBeNull();
        q.Issues.Should().ContainSingle(i => i.Title == "chain issue");
    }

    // ---------- 真并发冲突仍 409(票07 口径不回退) ----------

    [Fact]
    public async Task TrueConcurrency_StaleSessionWrite_StillThrowsDbUpdateConcurrency()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(user);

        // 两个上下文同时加载同一会话(各自捕获当时的 xmin)
        await using var dbA = NewDb();
        await using var dbB = NewDb();
        var a = await dbA.Sessions.Include(x => x.Questions).FirstAsync(x => x.Id == sid);
        var b = await dbB.Sessions.Include(x => x.Questions).FirstAsync(x => x.Id == sid);

        b.Complete("B summary", "B action");
        await dbB.SaveChangesAsync(); // 先保存者成功,会话行 xmin 推进

        a.Complete("A summary", "A action");
        // 后保存者持旧 xmin → UPDATE 0 行 → DbUpdateConcurrencyException(全局层映射 409)
        var act = async () => await dbA.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>(
            "真并发冲突必须仍以并发异常收场,不得被修复逻辑吞掉或静默覆盖");
    }

    // ---------- boundary ----------

    [Fact] // boundary:空题目列表(校验层) —— Handler 上游的 Validator 必须拒
    public void AddQuestionsValidator_EmptyList_Rejected()
    {
        var v = new AddQuestionsCommandValidator();
        var r = v.Validate(new AddQuestionsCommand(Guid.NewGuid(), Guid.NewGuid(),
            Array.Empty<QuestionDraftIn>()));
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName == "Questions");
    }

    [Fact] // boundary:超 TargetQuestionCount —— 恰达上限放行,再加一题被领域层拒(上限 ±1)
    public async Task AddQuestions_ExactlyTarget_Succeeds_OneMore_Rejected()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(user, target: 2);

        await using var db = NewDb();
        var handler = new AddQuestionsCommandHandler(db);
        var fill = await handler.Handle(
            new AddQuestionsCommand(sid, user, new[] { Q("fill 1"), Q("fill 2") }), default);
        fill.IsSuccess.Should().BeTrue("恰好达 TargetQuestionCount 应放行");

        var over = await handler.Handle(
            new AddQuestionsCommand(sid, user, new[] { Q("one too many") }), default);
        over.IsFailure.Should().BeTrue("超出 TargetQuestionCount 必须失败(领域校验),不得静默写入");
    }

    [Fact] // boundary:一批总数超 TargetQuestionCount(中途溢出整批回滚,不落半批)
    public async Task AddQuestions_BatchExceedingTarget_FailsAtomically()
    {
        if (!_available) return;
        var user = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(user, target: 2);

        await using var db = NewDb();
        var r = await new AddQuestionsCommandHandler(db).Handle(
            new AddQuestionsCommand(sid, user, new[] { Q("x1"), Q("x2"), Q("x3") }), default);
        r.IsFailure.Should().BeTrue();

        await using var verify = NewDb();
        var detail = await new GetSessionQueryHandler(verify).Handle(new GetSessionQuery(sid, user), default);
        detail.Value.Questions.Should().BeEmpty("溢出批次不得落半批数据");
    }

    [Theory] // boundary:题目正文 空/空白(拒)、上限 4000(放行)、4001(拒)
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(4000, true)]
    [InlineData(4001, false)]
    public void AddQuestionsValidator_QuestionTextLength_Boundaries(object textOrLen, bool expectValid)
    {
        var text = textOrLen is int n ? new string('题', n) : (string)textOrLen;
        var v = new AddQuestionsCommandValidator();
        var r = v.Validate(new AddQuestionsCommand(Guid.NewGuid(), Guid.NewGuid(),
            new[] { Q(text) }));
        r.IsValid.Should().Be(expectValid);
    }

    [Theory] // boundary:难度越界 0 / 6 拒,边界 1 / 5 放行
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void AddQuestionsValidator_Difficulty_Boundaries(int difficulty, bool expectValid)
    {
        var v = new AddQuestionsCommandValidator();
        var r = v.Validate(new AddQuestionsCommand(Guid.NewGuid(), Guid.NewGuid(),
            new[] { Q("ok text", difficulty) }));
        r.IsValid.Should().Be(expectValid);
    }

    [Fact] // boundary:不存在的会话 / 跨用户 —— NotFound 口径不变
    public async Task AddQuestions_UnknownOrForeignSession_ReturnsFailure()
    {
        if (!_available) return;
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var sid = await CreateFreshSessionAsync(owner);

        await using var db = NewDb();
        var handler = new AddQuestionsCommandHandler(db);
        var unknown = await handler.Handle(
            new AddQuestionsCommand(Guid.NewGuid(), owner, new[] { Q("nope") }), default);
        unknown.IsFailure.Should().BeTrue();
        var foreign = await handler.Handle(
            new AddQuestionsCommand(sid, stranger, new[] { Q("nope") }), default);
        foreign.IsFailure.Should().BeTrue("跨用户加题必须失败(归属隔离口径不变)");
    }
}
