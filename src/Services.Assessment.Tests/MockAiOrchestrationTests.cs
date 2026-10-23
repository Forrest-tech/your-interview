using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.SharedContracts.Ai;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票95:编排 handler 级测试(Fake IAiGatewayClient + 真 DB 门控,无 DB 跳过——同票69先例)。
/// 沙盒 other_tcp 策略拦截服务间出站 HTTP(dotnet→本地 stub 亦然,票34/91 先例),
/// 故端到端接线在此层验:真 handler + 真落库命令 + 确定性 Fake 网关响应;
/// HTTP 层失败语义(网关不可达→502、会话留 Draft)已由 API 活体实证(live-api.txt)。
/// 连接串:环境变量 T95_TEST_CS 优先,否则本地票95 活体库 yi_t95 口径。
/// </summary>
public sealed class MockAiOrchestrationTests : IAsyncLifetime
{
    private static readonly string Cs =
        Environment.GetEnvironmentVariable("T95_TEST_CS")
        ?? "Host=localhost;Port=5432;Database=yi_t95;Username=postgres;Password=postgres";
    private bool _available;

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static AssessmentDbContext NewDb()
        => new(new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options);

    private sealed class FakeAi : IAiGatewayClient
    {
        public string Text = "";
        public AiGatewayException? Throw;
        public int Calls;
        public Task<AiCompletionResult> CompleteAsync(string purpose, string prompt,
            string? systemPrompt, double? temperature, int? maxTokens, CancellationToken ct)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new AiCompletionResult(Text, "fake", 1, 1, 1));
        }
        public Task<PrepSourceSettingsDto> GetPrepSourceSettingsAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<PrepSourceSearchDto> SearchPrepSourcesAsync(string s, string c,
            string? r, string? st, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>把编排 handler 对 AddQuestions/Score 的落库调用分发到真 handler(独立上下文,同 scoped 口径)。</summary>
    private sealed class DispatchSender : ISender
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request,
            CancellationToken ct = default)
        {
            await using var db = NewDb();
            object? result = request switch
            {
                AddQuestionsCommand c => await new AddQuestionsCommandHandler(db).Handle(c, ct),
                ScoreQuestionCommand c => await new ScoreQuestionCommandHandler(db).Handle(c, ct),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (TResponse)result!;
        }
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default)
            where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
            where TNotification : INotification => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request,
            CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static async Task<Guid> CreateSessionAsync(Guid user, int target = 3)
    {
        await using var db = NewDb();
        var r = await new CreateSessionCommandHandler(db).Handle(
            new CreateSessionCommand(user, "t95-" + Guid.NewGuid().ToString("N")[..8],
                SessionMode.TopicDrill, "EF Core", null, target, "en"), default);
        r.IsSuccess.Should().BeTrue();
        return r.Value;
    }

    private static async Task<SessionDetailDto> DetailAsync(Guid sid, Guid user)
    {
        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(sid, user), default);
        r.IsSuccess.Should().BeTrue();
        return r.Value;
    }

    private const string ThreeQuestions =
        "[{\"questionText\":\"Q1?\",\"type\":\"Technical\",\"difficulty\":3}," +
        "{\"questionText\":\"Q2?\",\"type\":\"SystemDesign\",\"difficulty\":4}," +
        "{\"questionText\":\"Q3?\",\"type\":\"Behavioral\",\"difficulty\":2}]";

    private const string ScoreJson =
        "{\"pronunciation\":82,\"fluency\":74,\"sentenceIntegrity\":68,\"structure\":55," +
        "\"technicalDepth\":88,\"relevance\":91,\"comment\":\"ok\",\"recommendedAnswer\":\"rec\"," +
        "\"issues\":[{\"dimension\":\"structure\",\"title\":\"No skeleton\",\"severity\":4}]}";

    [Fact]
    public async Task Generate_HappyPath_PersistsAllQuestions()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var sid = await CreateSessionAsync(user);
        var ai = new FakeAi { Text = ThreeQuestions };
        await using var db = NewDb();
        var r = await new GenerateQuestionsCommandHandler(db, ai, new DispatchSender())
            .Handle(new GenerateQuestionsCommand(sid, user), default);
        r.IsSuccess.Should().BeTrue();
        r.Value.Should().Be(3);
        var d = await DetailAsync(sid, user);
        d.Questions.Should().HaveCount(3);
        d.Status.Should().Be(nameof(SessionStatus.InProgress));
    }

    [Fact]
    public async Task Generate_WhenFull_DoesNotCallAiAgain()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var sid = await CreateSessionAsync(user);
        var ai = new FakeAi { Text = ThreeQuestions };
        await using var db = NewDb();
        var h = new GenerateQuestionsCommandHandler(db, ai, new DispatchSender());
        (await h.Handle(new GenerateQuestionsCommand(sid, user), default)).IsSuccess.Should().BeTrue();
        (await h.Handle(new GenerateQuestionsCommand(sid, user), default)).IsSuccess.Should().BeTrue();
        ai.Calls.Should().Be(1, "已出满时幂等返回,不重复调 AI/出题");
        (await DetailAsync(sid, user)).Questions.Should().HaveCount(3);
    }

    [Fact]
    public async Task Generate_BadAiOutput_FailsAndLeavesDraftEmpty()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var sid = await CreateSessionAsync(user);
        var ai = new FakeAi { Text = "no json here" };
        await using var db = NewDb();
        var r = await new GenerateQuestionsCommandHandler(db, ai, new DispatchSender())
            .Handle(new GenerateQuestionsCommand(sid, user), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Mock.AiInvalidResponse");
        var d = await DetailAsync(sid, user);
        d.Status.Should().Be(nameof(SessionStatus.Draft));
        d.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_NotConfigured_PropagatesGatewayException()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var sid = await CreateSessionAsync(user);
        var ai = new FakeAi { Throw = new AiGatewayException("Ai.NotConfigured", true) };
        await using var db = NewDb();
        var act = async () => await new GenerateQuestionsCommandHandler(db, ai, new DispatchSender())
            .Handle(new GenerateQuestionsCommand(sid, user), default);
        var ex = await act.Should().ThrowAsync<AiGatewayException>();
        ex.Which.IsConfigurationError.Should().BeTrue("控制器据此映射 503 Mock.AiNotConfigured");
    }

    [Fact]
    public async Task Generate_UnknownSession_NotFound()
    {
        if (!_available) return;
        var ai = new FakeAi { Text = ThreeQuestions };
        await using var db = NewDb();
        var r = await new GenerateQuestionsCommandHandler(db, ai, new DispatchSender())
            .Handle(new GenerateQuestionsCommand(Guid.NewGuid(), Guid.NewGuid()), default);
        r.IsFailure.Should().BeTrue();
        ai.Calls.Should().Be(0);
    }

    private async Task<(Guid Sid, Guid Qid)> AnsweredQuestionAsync(Guid user)
    {
        var sid = await CreateSessionAsync(user, target: 1);
        Guid qid;
        await using (var db = NewDb())
        {
            var add = await new AddQuestionsCommandHandler(db).Handle(
                new AddQuestionsCommand(sid, user,
                    [new QuestionDraftIn("Q?", "Technical", 3, null)]), default);
            qid = add.Value[0];
        }
        await using (var db = NewDb())
        {
            var ans = await new AnswerQuestionCommandHandler(db).Handle(
                new AnswerQuestionCommand(sid, qid, user, "my answer", null, null, null), default);
            ans.IsSuccess.Should().BeTrue();
        }
        return (sid, qid);
    }

    [Fact]
    public async Task Score_HappyPath_PersistsScoresAndIssues()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var (sid, qid) = await AnsweredQuestionAsync(user);
        var ai = new FakeAi { Text = ScoreJson };
        await using var db = NewDb();
        var r = await new ScoreQuestionWithAiCommandHandler(db, ai, new DispatchSender())
            .Handle(new ScoreQuestionWithAiCommand(sid, qid, user), default);
        r.IsSuccess.Should().BeTrue();
        var q = (await DetailAsync(sid, user)).Questions.Single();
        q.State.Should().Be(nameof(QuestionState.Scored));
        q.Scores!.TechnicalDepth.Should().Be(88);
        q.Issues.Should().ContainSingle(i => i.Title == "No skeleton");
        // 幂等:再调不重复调 AI
        var r2 = await new ScoreQuestionWithAiCommandHandler(db, ai, new DispatchSender())
            .Handle(new ScoreQuestionWithAiCommand(sid, qid, user), default);
        r2.IsSuccess.Should().BeTrue();
        ai.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Score_BadAiOutput_LeavesQuestionAnsweredWithAnswerKept()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var (sid, qid) = await AnsweredQuestionAsync(user);
        var ai = new FakeAi { Text = "cannot score" };
        await using var db = NewDb();
        var r = await new ScoreQuestionWithAiCommandHandler(db, ai, new DispatchSender())
            .Handle(new ScoreQuestionWithAiCommand(sid, qid, user), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Mock.AiInvalidResponse");
        var q = (await DetailAsync(sid, user)).Questions.Single();
        q.State.Should().Be(nameof(QuestionState.Answered));
        q.AnswerText.Should().Be("my answer");
        q.Scores.Should().BeNull();
    }

    [Fact]
    public async Task Score_NotAnswered_FailsWithoutCallingAi()
    {
        if (!_available) return;
        var user = Guid.NewGuid(); var sid = await CreateSessionAsync(user, target: 1);
        Guid qid;
        await using (var db = NewDb())
        {
            var add = await new AddQuestionsCommandHandler(db).Handle(
                new AddQuestionsCommand(sid, user,
                    [new QuestionDraftIn("Q?", "Technical", 3, null)]), default);
            qid = add.Value[0];
        }
        var ai = new FakeAi { Text = ScoreJson };
        await using var db2 = NewDb();
        var r = await new ScoreQuestionWithAiCommandHandler(db2, ai, new DispatchSender())
            .Handle(new ScoreQuestionWithAiCommand(sid, qid, user), default);
        r.IsFailure.Should().BeTrue();
        r.Error.Code.Should().Be("Mock.NotAnswered");
        ai.Calls.Should().Be(0);
    }
}
