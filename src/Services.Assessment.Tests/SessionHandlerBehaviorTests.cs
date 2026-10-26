using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票54(ARC-08b):Assessment 会话核心写路径 Handler 行为测试 —— 建会话/
/// 加题/作答/评分/跳过/完成/放弃/软删/推荐,经真实持久化 seam 测公开行为
/// (归属校验、题目上限与原子性、题态守卫、总分与短板聚合),不测实现细节。
/// DB 门控同仓内先例(AddQuestionsHandlerTests):连不上库时跳过;
/// 每例独立随机 UserId 隔离,Dispose 清本例用户数据。
/// boundary 类别逐例标注;文件类不适用(本组 Handler 无文件/流输入,
/// 录音文件路径仅作字符串引用,音频用例以路径形态覆盖)。
/// </summary>
public sealed class SessionHandlerBehaviorTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly List<Guid> _users = [];

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available || _users.Count == 0) return;
        await using var db = NewDb();
        var sessions = await db.Sessions.IgnoreQueryFilters().Where(s => _users.Contains(s.UserId)).ToListAsync();
        db.Sessions.RemoveRange(sessions);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    private async Task<Guid> CreateSessionAsync(Guid user, int target = 5, string title = "行为测试会话")
    {
        await using var db = NewDb();
        var r = await new CreateSessionCommandHandler(db).Handle(
            new CreateSessionCommand(user, title, SessionMode.FullLoop, null, null, target, "en"), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<IReadOnlyList<Guid>> AddQuestionsAsync(Guid user, Guid sessionId, params string[] texts)
    {
        await using var db = NewDb();
        var r = await new AddQuestionsCommandHandler(db).Handle(
            new AddQuestionsCommand(sessionId, user, texts.Select(t => new QuestionDraftIn(t, "Technical", 3, null)).ToList()), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<SessionDetailDto> GetAsync(Guid user, Guid sessionId)
    {
        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(sessionId, user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task ScoreAsync(Guid user, Guid sessionId, Guid questionId, int uniform = 80,
        params IssueDraftIn[] issues)
    {
        await using var db = NewDb();
        var r = await new ScoreQuestionCommandHandler(db).Handle(
            new ScoreQuestionCommand(sessionId, questionId, user, uniform, uniform, uniform, uniform,
                uniform, uniform, "评语", null, null, null, issues), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
    }

    // ---------- CreateSession ----------

    [Theory] // boundary: 0 与负数 / 超长上限 ±1 —— 目标题数被领域钳制到 1..20(Handler 不报错)
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    public async Task CreateSession_TargetCount_ClampedToRange(int given, int expected)
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user, target: given);
        var detail = await GetAsync(user, sid);
        detail.TargetQuestionCount.Should().Be(expected);
        detail.Status.Should().Be("Draft");
    }

    [Fact] // boundary: 空/null —— 纯空白标题被领域构造拒绝(现状锁定:Handler 不吞异常,上游靠校验器拦)
    public async Task CreateSession_WhitespaceTitle_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb();
        var act = () => new CreateSessionCommandHandler(db).Handle(
            new CreateSessionCommand(user, "   ", SessionMode.FullLoop, null, null, 5, "en"), default);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact] // boundary: 特殊字符/Unicode —— 标题去首尾空白后逐字落库
    public async Task CreateSession_TitleTrimmedAndUnicodePreserved()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user, title: "  系统设计 🎯 «deep dive»  ");
        (await GetAsync(user, sid)).Title.Should().Be("系统设计 🎯 «deep dive»");
    }

    // ---------- AddQuestions ----------

    [Fact] // boundary: 枚举回落 —— 未知题型回落 Technical,已知题型(大小写不敏感)正确解析
    public async Task AddQuestions_TypeParsing_FallbackAndKnown()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        await using (var db = NewDb())
        {
            var r = await new AddQuestionsCommandHandler(db).Handle(new AddQuestionsCommand(sid, user,
                [new QuestionDraftIn("q1", "Banana", 3, null), new QuestionDraftIn("q2", "systemdesign", 3, null)]), default);
            r.IsSuccess.Should().BeTrue();
        }
        var detail = await GetAsync(user, sid);
        detail.Questions.Select(q => q.Type).Should().Equal("Technical", "SystemDesign");
        detail.Status.Should().Be("InProgress"); // 首加题 Draft→InProgress
    }

    [Fact] // boundary: 超长上限 —— 批量超过目标题数整批失败且零落库(原子性)
    public async Task AddQuestions_ExceedingTarget_FailsAtomically()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user, target: 2);
        await using (var db = NewDb())
        {
            var r = await new AddQuestionsCommandHandler(db).Handle(new AddQuestionsCommand(sid, user,
                [new QuestionDraftIn("q1", "Technical", 3, null),
                 new QuestionDraftIn("q2", "Technical", 3, null),
                 new QuestionDraftIn("q3", "Technical", 3, null)]), default);
            r.IsFailure.Should().BeTrue();
            r.Error.Type.Should().Be(ErrorType.Validation);
        }
        (await GetAsync(user, sid)).Questions.Should().BeEmpty();
    }

    [Fact] // boundary: 状态流转 —— 已完成会话再加题 → Validation
    public async Task AddQuestions_AfterComplete_Fails()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        await using (var db = NewDb())
            (await new CompleteSessionCommandHandler(db).Handle(
                new CompleteSessionCommand(sid, user, null, null), default)).IsSuccess.Should().BeTrue();
        await using (var db = NewDb())
        {
            var r = await new AddQuestionsCommandHandler(db).Handle(new AddQuestionsCommand(sid, user,
                [new QuestionDraftIn("late", "Technical", 3, null)]), default);
            r.IsFailure.Should().BeTrue();
            r.Error.Type.Should().Be(ErrorType.Validation);
        }
    }

    [Fact] // boundary: 归属校验 + 空 Guid —— 跨用户/不存在会话均 NotFound
    public async Task AddQuestions_CrossUserOrMissing_NotFound()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var sid = await CreateSessionAsync(user);
        await using (var db = NewDb())
        {
            var cross = await new AddQuestionsCommandHandler(db).Handle(new AddQuestionsCommand(sid, other,
                [new QuestionDraftIn("q", "Technical", 3, null)]), default);
            cross.IsFailure.Should().BeTrue(); cross.Error.Type.Should().Be(ErrorType.NotFound);
            var ghost = await new AddQuestionsCommandHandler(db).Handle(new AddQuestionsCommand(Guid.Empty, user,
                [new QuestionDraftIn("q", "Technical", 3, null)]), default);
            ghost.IsFailure.Should().BeTrue(); ghost.Error.Type.Should().Be(ErrorType.NotFound);
        }
        (await GetAsync(user, sid)).Questions.Should().BeEmpty();
    }

    // ---------- Answer / Score / Skip 题态流转 ----------

    [Fact] // boundary: 状态流转 —— 作答落 Answered + 文本逐字;对不存在的题作答 → Validation
    public async Task AnswerQuestion_Flow_AndUnknownQuestion()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        var qid = (await AddQuestionsAsync(user, sid, "解释幂等?"))[0];
        await using (var db = NewDb())
        {
            var ghost = await new AnswerQuestionCommandHandler(db).Handle(
                new AnswerQuestionCommand(sid, Guid.NewGuid(), user, "x", null, null, null), default);
            ghost.IsFailure.Should().BeTrue(); ghost.Error.Type.Should().Be(ErrorType.Validation);
            var ok = await new AnswerQuestionCommandHandler(db).Handle(
                new AnswerQuestionCommand(sid, qid, user, "答:用唯一键 «idempotency-key» 🚀", null, 42.5, null), default);
            ok.IsSuccess.Should().BeTrue(ok.Error.Description); // boundary: 特殊字符/Unicode 回答逐字
        }
        var q = (await GetAsync(user, sid)).Questions.Single();
        q.State.Should().Be("Answered");
        q.AnswerText.Should().Be("答:用唯一键 «idempotency-key» 🚀");
        q.AnswerDurationSeconds.Should().Be(42.5);
    }

    [Fact] // boundary: 状态流转 —— 已评分题不可再答 → Validation;纯音频作答合法(校验器口径在纯测)
    public async Task AnswerQuestion_AfterScored_Fails()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        var qid = (await AddQuestionsAsync(user, sid, "q"))[0];
        await ScoreAsync(user, sid, qid);
        await using var db = NewDb();
        var r = await new AnswerQuestionCommandHandler(db).Handle(
            new AnswerQuestionCommand(sid, qid, user, "再答一次", null, null, null), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact] // boundary: 空集合/替换语义 —— 评分带 issues 落库;重评替换旧 issues 不累积
    public async Task ScoreQuestion_IssuesPersist_RescoreReplaces()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        var qid = (await AddQuestionsAsync(user, sid, "q"))[0];
        await ScoreAsync(user, sid, qid, 80,
            new IssueDraftIn("Structure", "缺骨架", null, null, null, 4),
            new IssueDraftIn("Fluency", "填充词多", null, null, null, 3));
        var first = (await GetAsync(user, sid)).Questions.Single();
        first.State.Should().Be("Scored");
        first.Scores!.Overall.Should().Be(80); // 六维等值时总分即该值
        first.Issues.Should().HaveCount(2);

        await ScoreAsync(user, sid, qid, 60, new IssueDraftIn("Structure", "仍缺骨架", null, null, null, 5));
        var second = (await GetAsync(user, sid)).Questions.Single();
        second.Scores!.Overall.Should().Be(60);
        second.Issues.Should().ContainSingle().Which.Title.Should().Be("仍缺骨架");
    }

    [Fact] // boundary: 归属校验 —— 跨用户评分 NotFound;不存在的题评分 Validation
    public async Task ScoreQuestion_CrossUserAndUnknownQuestion()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var sid = await CreateSessionAsync(user);
        var qid = (await AddQuestionsAsync(user, sid, "q"))[0];
        await using (var db = NewDb())
        {
            var cross = await new ScoreQuestionCommandHandler(db).Handle(
                new ScoreQuestionCommand(sid, qid, other, 1, 1, 1, 1, 1, 1, null, null, null, null, []), default);
            cross.IsFailure.Should().BeTrue(); cross.Error.Type.Should().Be(ErrorType.NotFound);
            var ghost = await new ScoreQuestionCommandHandler(db).Handle(
                new ScoreQuestionCommand(sid, Guid.NewGuid(), user, 1, 1, 1, 1, 1, 1, null, null, null, null, []), default);
            ghost.IsFailure.Should().BeTrue(); ghost.Error.Type.Should().Be(ErrorType.Validation);
        }
        (await GetAsync(user, sid)).Questions.Single().State.Should().Be("Asked"); // 未被污染
    }

    [Fact] // boundary: 状态流转 —— 跳过未评题 → Skipped;跳过已评题为 no-op 保持 Scored
    public async Task SkipQuestion_StateRules()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        var ids = await AddQuestionsAsync(user, sid, "q1", "q2");
        await ScoreAsync(user, sid, ids[1]);
        await using (var db = NewDb())
        {
            (await new SkipQuestionCommandHandler(db).Handle(new SkipQuestionCommand(sid, ids[0], user), default))
                .IsSuccess.Should().BeTrue();
            (await new SkipQuestionCommandHandler(db).Handle(new SkipQuestionCommand(sid, ids[1], user), default))
                .IsSuccess.Should().BeTrue();
        }
        var detail = await GetAsync(user, sid);
        detail.Questions.Single(q => q.Id == ids[0]).State.Should().Be("Skipped");
        detail.Questions.Single(q => q.Id == ids[1]).State.Should().Be("Scored");
    }

    // ---------- Complete / Abandon / Delete ----------

    [Fact] // boundary: 聚合 —— 完成时总分 = 已评题 Overall 均值(未评不拉低),短板按维度聚合;二次完成幂等
    public async Task CompleteSession_Aggregates_AndIdempotent()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user, target: 3);
        var ids = await AddQuestionsAsync(user, sid, "q1", "q2", "q3");
        await ScoreAsync(user, sid, ids[0], 80, new IssueDraftIn("Structure", "a", null, null, null, 4));
        await ScoreAsync(user, sid, ids[1], 60, new IssueDraftIn("Structure", "b", null, null, null, 2));
        // ids[2] 不评分
        SessionSummaryResult summary;
        await using (var db = NewDb())
        {
            var r = await new CompleteSessionCommandHandler(db).Handle(
                new CompleteSessionCommand(sid, user, "总评", "先练结构"), default);
            r.IsSuccess.Should().BeTrue(); summary = r.Value;
            var again = await new CompleteSessionCommandHandler(db).Handle(
                new CompleteSessionCommand(sid, user, "改总评", null), default);
            again.IsSuccess.Should().BeTrue();
            again.Value.OverallScore.Should().Be(70); // 幂等:重算口径一致,不漂移
        }
        summary.OverallScore.Should().Be(70); // (80+60)/2
        summary.ScoredQuestions.Should().Be(2);
        summary.Weaknesses.Should().ContainSingle(w => w.Dimension == "Structure" && w.Count == 2);
        summary.WeakestDimension.Should().Be("Structure");
        (await GetAsync(user, sid)).Status.Should().Be("Completed");
    }

    [Fact] // boundary: 空集合 —— 零题会话完成:总分 null、已评 0、短板空集
    public async Task CompleteSession_NoQuestions_NullScore()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        await using var db = NewDb();
        var r = await new CompleteSessionCommandHandler(db).Handle(
            new CompleteSessionCommand(sid, user, null, null), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.OverallScore.Should().BeNull();
        r.Value.ScoredQuestions.Should().Be(0);
        r.Value.Weaknesses.Should().BeEmpty();
        r.Value.WeakestDimension.Should().BeNull();
    }

    [Fact] // boundary: 状态流转 —— 放弃后 Abandoned;已完成会话放弃为 no-op 保持 Completed
    public async Task AbandonSession_StateRules()
    {
        if (!_available) return;
        var user = NewUser();
        var open = await CreateSessionAsync(user);
        var done = await CreateSessionAsync(user);
        await using (var db = NewDb())
            (await new CompleteSessionCommandHandler(db).Handle(
                new CompleteSessionCommand(done, user, null, null), default)).IsSuccess.Should().BeTrue();
        await using (var db = NewDb())
        {
            (await new AbandonSessionCommandHandler(db).Handle(new AbandonSessionCommand(open, user), default))
                .IsSuccess.Should().BeTrue();
            (await new AbandonSessionCommandHandler(db).Handle(new AbandonSessionCommand(done, user), default))
                .IsSuccess.Should().BeTrue();
        }
        (await GetAsync(user, open)).Status.Should().Be("Abandoned");
        (await GetAsync(user, done)).Status.Should().Be("Completed");
    }

    [Fact] // boundary: 状态流转/归属 —— 软删后详情与列表均不可见;跨用户删 NotFound 且数据完好
    public async Task DeleteSession_SoftDeleteVisibility_AndCrossUserGuard()
    {
        if (!_available) return;
        var user = NewUser(); var other = NewUser();
        var sid = await CreateSessionAsync(user);
        await using (var db = NewDb())
        {
            var cross = await new DeleteSessionCommandHandler(db).Handle(new DeleteSessionCommand(sid, other), default);
            cross.IsFailure.Should().BeTrue(); cross.Error.Type.Should().Be(ErrorType.NotFound);
        }
        (await GetAsync(user, sid)).Status.Should().Be("Draft"); // 跨用户删未生效
        await using (var db = NewDb())
            (await new DeleteSessionCommandHandler(db).Handle(new DeleteSessionCommand(sid, user), default))
                .IsSuccess.Should().BeTrue();
        await using (var db = NewDb())
        {
            var g = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(sid, user), default);
            g.IsFailure.Should().BeTrue(); g.Error.Type.Should().Be(ErrorType.NotFound);
            var list = await new ListSessionsQueryHandler(db).Handle(new ListSessionsQuery(user), default);
            list.Value.Items.Should().NotContain(i => i.Id == sid);
        }
    }

    // ---------- SuggestSession ----------

    [Theory] // boundary: 0 与负数 / 超长上限 ±1 —— 无历史时推荐题数钳制 1..20,默认综合主题
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(20, 20)]
    [InlineData(100, 20)]
    public async Task SuggestSession_NoHistory_DefaultsAndClamp(int given, int expected)
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb();
        var r = await new SuggestSessionCommandHandler(db).Handle(new SuggestSessionCommand(user, given), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.TargetQuestionCount.Should().Be(expected);
        r.Value.Topic.Should().Be("综合");
        r.Value.Mode.Should().Be(SessionMode.WeaknessFocus);
    }

    [Fact] // boundary: 聚合 —— 历史短板按 issues 计数取最高维度为推荐主题
    public async Task SuggestSession_TopWeaknessDimensionWins()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        var ids = await AddQuestionsAsync(user, sid, "q1", "q2");
        await ScoreAsync(user, sid, ids[0], 50,
            new IssueDraftIn("Structure", "a", null, null, null, 3),
            new IssueDraftIn("Fluency", "b", null, null, null, 3));
        await ScoreAsync(user, sid, ids[1], 50, new IssueDraftIn("Structure", "c", null, null, null, 3));
        await using var db = NewDb();
        var r = await new SuggestSessionCommandHandler(db).Handle(new SuggestSessionCommand(user, 5), default);
        r.Value.Topic.Should().Be("Structure");
        r.Value.Rationale.Should().Contain("Structure");
    }

    // ★ ML-FIX-UI(W-UI-9):推荐文案按 lang 渲染 —— 英文 UI 下推荐卡不再出中文。

    [Theory]
    [InlineData("en", "General", "Weakness focus", "No practice history")]
    [InlineData("fr", "Général", "Renforcement des faiblesses", "Aucun historique")]
    [InlineData("xx", "综合", "薄弱点强化", "还没有历史练习记录")]
    public async Task SuggestSession_NoHistory_LocalizedByLang(string lang, string topic, string titlePart, string rationalePart)
    {
        if (!_available) return;
        var user = NewUser();
        await using var db = NewDb();
        var r = await new SuggestSessionCommandHandler(db).Handle(new SuggestSessionCommand(user, 5, lang), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Topic.Should().Be(topic);
        r.Value.Title.Should().Contain(titlePart);
        r.Value.Rationale.Should().Contain(rationalePart);
    }

    [Fact]
    public async Task SuggestSession_TopWeakness_LocalizedByLang()
    {
        if (!_available) return;
        var user = NewUser();
        var sid = await CreateSessionAsync(user);
        var ids = await AddQuestionsAsync(user, sid, "q1", "q2");
        await ScoreAsync(user, sid, ids[0], 50,
            new IssueDraftIn("Structure", "a", null, null, null, 3),
            new IssueDraftIn("Fluency", "b", null, null, null, 3));
        await ScoreAsync(user, sid, ids[1], 50, new IssueDraftIn("Structure", "c", null, null, null, 3));
        await using var db = NewDb();
        var r = await new SuggestSessionCommandHandler(db).Handle(new SuggestSessionCommand(user, 5, "en"), default);
        r.Value.Title.Should().Contain("Weakness focus");
        r.Value.Rationale.Should().Contain("Structure");
        r.Value.Rationale.Should().NotContain("历史评分");
    }
}

/// <summary>
/// 票54:Assessment 校验器 boundary(纯规则,无 DB)—— 长度上限 ±1、
/// 分数区间 0/100 ±1、模式条件必填、作答二选一。
/// </summary>
public sealed class AssessmentValidatorBoundaryTests
{
    private static bool HasError<T>(FluentValidation.AbstractValidator<T> v, T cmd, string property)
        => v.Validate(cmd).Errors.Any(e => e.PropertyName == property);

    private static CreateSessionCommand Session(string title = "t", int target = 5,
        SessionMode mode = SessionMode.FullLoop, string? topic = null, string? style = null, string lang = "en")
        => new(Guid.NewGuid(), title, mode, topic, style, target, lang);

    [Fact] // boundary: 超长上限 ±1 —— Title 200 放行 / 201 拒绝;空标题拒绝
    public void CreateSession_TitleBoundary()
    {
        var v = new CreateSessionCommandValidator();
        HasError(v, Session(title: new string('t', 200)), "Title").Should().BeFalse();
        HasError(v, Session(title: new string('t', 201)), "Title").Should().BeTrue();
        HasError(v, Session(title: ""), "Title").Should().BeTrue();
    }

    [Theory] // boundary: 0 与负数 / 上限 ±1 —— TargetQuestionCount 1..20
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(20, true)] [InlineData(21, false)]
    public void CreateSession_TargetBoundary(int target, bool valid)
        => HasError(new CreateSessionCommandValidator(), Session(target: target), "TargetQuestionCount")
            .Should().Be(!valid);

    [Fact] // boundary: 条件必填 —— TopicDrill 须 Topic、CompanyStyle 须 CompanyStyle,FullLoop 不须
    public void CreateSession_ModeConditionalRequirements()
    {
        var v = new CreateSessionCommandValidator();
        HasError(v, Session(mode: SessionMode.TopicDrill, topic: null), "Topic").Should().BeTrue();
        HasError(v, Session(mode: SessionMode.TopicDrill, topic: "EF Core"), "Topic").Should().BeFalse();
        HasError(v, Session(mode: SessionMode.CompanyStyle, style: null), "CompanyStyle").Should().BeTrue();
        HasError(v, Session(mode: SessionMode.CompanyStyle, style: "Geotab"), "CompanyStyle").Should().BeFalse();
        v.Validate(Session(mode: SessionMode.FullLoop)).IsValid.Should().BeTrue();
    }

    [Fact] // boundary: 空集合 —— 题目列表为空拒绝;单题文本 4000/4001、难度 0/6、空题型
    public void AddQuestions_Boundaries()
    {
        var v = new AddQuestionsCommandValidator();
        var empty = new AddQuestionsCommand(Guid.NewGuid(), Guid.NewGuid(), []);
        HasError(v, empty, "Questions").Should().BeTrue();
        AddQuestionsCommand One(string text, int diff = 3, string type = "Technical")
            => new(Guid.NewGuid(), Guid.NewGuid(), [new QuestionDraftIn(text, type, diff, null)]);
        v.Validate(One(new string('q', 4000))).IsValid.Should().BeTrue();
        v.Validate(One(new string('q', 4001))).IsValid.Should().BeFalse();
        v.Validate(One("q", diff: 0)).IsValid.Should().BeFalse();
        v.Validate(One("q", diff: 6)).IsValid.Should().BeFalse();
        v.Validate(One("q", type: "")).IsValid.Should().BeFalse();
        v.Validate(One("")).IsValid.Should().BeFalse(); // boundary: 空/null 题面
    }

    [Theory] // boundary: 分数区间 —— 六维 0 与 100 放行,-1 与 101 拒绝(以 Pronunciation/TechnicalDepth 为代表)
    [InlineData(-1, false)] [InlineData(0, true)] [InlineData(100, true)] [InlineData(101, false)]
    public void ScoreQuestion_ScoreRange(int score, bool valid)
    {
        var cmd = new ScoreQuestionCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            score, 50, 50, 50, score, 50, null, null, null, null, []);
        var errs = new ScoreQuestionCommandValidator().Validate(cmd).Errors;
        errs.Any(e => e.PropertyName is "Pronunciation" or "TechnicalDepth").Should().Be(!valid);
    }

    [Theory] // boundary: 0 与上限 ±1 —— issue Severity 1..5
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(5, true)] [InlineData(6, false)]
    public void ScoreQuestion_IssueSeverityRange(int severity, bool valid)
    {
        var cmd = new ScoreQuestionCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            50, 50, 50, 50, 50, 50, null, null, null, null,
            [new IssueDraftIn("Structure", "t", null, null, null, severity)]);
        new ScoreQuestionCommandValidator().Validate(cmd).IsValid.Should().Be(valid);
    }

    [Fact] // boundary: 空/null 组合 —— 作答文本与音频至少一项;两者皆空拒绝,纯音频放行
    public void AnswerQuestion_RequiresTextOrAudio()
    {
        var v = new AnswerQuestionCommandValidator();
        AnswerQuestionCommand Cmd(string? text, string? audio)
            => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), text, audio, null, null);
        v.Validate(Cmd(null, null)).IsValid.Should().BeFalse();
        v.Validate(Cmd("  ", "")).IsValid.Should().BeFalse();
        v.Validate(Cmd(null, "/tmp/a.wav")).IsValid.Should().BeTrue();
        v.Validate(Cmd("text", null)).IsValid.Should().BeTrue();
    }
}
