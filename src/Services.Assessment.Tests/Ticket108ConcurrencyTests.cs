using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票108 §7(承票101/70):Assessment 子行乐观并发 ——
/// A10 recordings score(评分行补 xmin 钉版)、A09 题目 answer/score/skip
/// (题目行补 xmin 钉版)、A13 storage settings(设置行补 xmin 钉版)。
/// 两「页签」= 两次独立读同版本 + 各自新 DbContext Handler 串行双写,
/// 第二写持旧 Version 应抛 DbUpdateConcurrencyException(全局映射 409),
/// 且第一写数据完好;Version=null(旧调用方)不钉版兼容。
/// DB 门控同仓内先例:连不上库跳过;每例独立随机 UserId,Dispose 清数据。
/// </summary>
public sealed class Ticket108ConcurrencyTests : IAsyncLifetime
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
        var recs = await db.Recordings.IgnoreQueryFilters().Where(r => _users.Contains(r.UserId)).ToListAsync();
        db.Recordings.RemoveRange(recs);
        var sessions = await db.Sessions.IgnoreQueryFilters().Where(s => _users.Contains(s.UserId)).ToListAsync();
        db.Sessions.RemoveRange(sessions);
        var settings = await db.StorageSettings.Where(s => _users.Contains(s.UserId)).ToListAsync();
        db.StorageSettings.RemoveRange(settings);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private Guid NewUser() { var u = Guid.NewGuid(); _users.Add(u); return u; }

    // ---------------- A10 recordings score ----------------

    private async Task<Guid> SeedRecordingAsync(Guid user)
    {
        await using var db = NewDb();
        var rec = new PracticeRecording(Guid.NewGuid(), user, Guid.NewGuid(),
            "t108/test.wav", "audio/wav", 100, 1.5);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private static SaveRecordingScoreCommand ScoreCmd(Guid user, Guid recId, double pron, uint? version = null) =>
        new(user, recId, pron, 80, null, null, null,
            "hello", null, "eastus", "hello",
            BilledSeconds: null, BilledBytes: null, Version: version);

    private async Task<RecordingScoreDto> SaveScoreAsync(Guid user, Guid recId, double pron, uint? version = null)
    {
        await using var db = NewDb();
        var r = await new SaveRecordingScoreCommandHandler(db).Handle(ScoreCmd(user, recId, pron, version), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<RecordingScoreDto> GetScoreAsync(Guid user, Guid recId)
    {
        await using var db = NewDb();
        var r = await new GetRecordingScoreQueryHandler(db).Handle(new GetRecordingScoreQuery(user, recId), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact] // A10:首次评分回传 Version;B 持旧版本重评分 → 并发异常,A 的分数完好
    public async Task RecordingScore_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var recId = await SeedRecordingAsync(user);

        var first = await SaveScoreAsync(user, recId, 50);
        first.Version.Should().NotBeNull();
        var v = first.Version!.Value;

        // 页签 A:持 v 重评分成功(90)
        var a = await SaveScoreAsync(user, recId, 90, v);
        a.PronScore.Should().Be(90);
        a.Version.Should().NotBe(v);

        // 页签 B:仍持旧 v 重评分(10)→ 应冲突
        await using var dbB = NewDb();
        var act = () => new SaveRecordingScoreCommandHandler(dbB).Handle(ScoreCmd(user, recId, 10, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await GetScoreAsync(user, recId)).PronScore.Should().Be(90);
    }

    [Fact] // A10 兼容:不回传 Version 的旧调用方重评分仍成功(LWW 兼容面)
    public async Task RecordingScore_NoVersion_StillSaves()
    {
        if (!_available) return;
        var user = NewUser();
        var recId = await SeedRecordingAsync(user);

        await SaveScoreAsync(user, recId, 50);
        var again = await SaveScoreAsync(user, recId, 70, version: null);
        again.PronScore.Should().Be(70);
        (await GetScoreAsync(user, recId)).PronScore.Should().Be(70);
    }

    [Fact] // A10 读面:GET score 与 recordings 列表回传同一 Version
    public async Task RecordingScore_Version_ExposedOnReads()
    {
        if (!_available) return;
        var user = NewUser();
        var recId = await SeedRecordingAsync(user);
        var saved = await SaveScoreAsync(user, recId, 66);

        var got = await GetScoreAsync(user, recId);
        got.Version.Should().Be(saved.Version);

        await using var db = NewDb();
        var rows = await new ListAllRecordingsQueryHandler(db).Handle(new ListAllRecordingsQuery(user), default);
        rows.Single(x => x.Id == recId).Score!.Version.Should().Be(saved.Version);
    }

    // ---------------- A09 题目 answer/score/skip ----------------

    private async Task<(Guid SessionId, Guid QuestionId, uint Version)> SeedSessionAsync(Guid user)
    {
        await using var db = NewDb();
        var created = await new CreateSessionCommandHandler(db).Handle(
            new CreateSessionCommand(user, "T108 session", SessionMode.TopicDrill, "EF Core",
                null, 5, "en"), default);
        created.IsSuccess.Should().BeTrue(created.Error.Description);
        var added = await new AddQuestionsCommandHandler(db).Handle(
            new AddQuestionsCommand(created.Value, user,
                [new QuestionDraftIn("What is EF Core tracking?", "Technical", 3, null)]), default);
        added.IsSuccess.Should().BeTrue(added.Error.Description);
        var detail = await GetDetailAsync(user, created.Value);
        var q = detail.Questions.Single();
        q.Version.Should().NotBeNull();
        return (created.Value, q.Id, q.Version!.Value);
    }

    private async Task<SessionDetailDto> GetDetailAsync(Guid user, Guid sessionId)
    {
        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(sessionId, user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private static AnswerQuestionCommand AnswerCmd(Guid s, Guid q, Guid user, string text, uint? version = null) =>
        new(s, q, user, text, null, null, null, version);

    private static ScoreQuestionCommand ScoreCmd(Guid s, Guid q, Guid user, uint? version = null) =>
        new(s, q, user, 80, 80, 80, 80, 80, 80, "c", null, null, null, [], version);

    [Fact] // A09 answer:B 持旧题目版本作答 → 并发异常,A 的答案完好
    public async Task Answer_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (sid, qid, v) = await SeedSessionAsync(user);

        await using (var dbA = NewDb())
            (await new AnswerQuestionCommandHandler(dbA).Handle(
                AnswerCmd(sid, qid, user, "A answer", v), default)).IsSuccess.Should().BeTrue();

        await using var dbB = NewDb();
        var act = () => new AnswerQuestionCommandHandler(dbB).Handle(
            AnswerCmd(sid, qid, user, "B answer", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await GetDetailAsync(user, sid)).Questions.Single().AnswerText.Should().Be("A answer");
    }

    [Fact] // A09 score:B 持旧题目版本评分 → 并发异常
    public async Task Score_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (sid, qid, v) = await SeedSessionAsync(user);

        await using (var dbA = NewDb())
            (await new AnswerQuestionCommandHandler(dbA).Handle(
                AnswerCmd(sid, qid, user, "ans", v), default)).IsSuccess.Should().BeTrue();
        var v2 = (await GetDetailAsync(user, sid)).Questions.Single().Version;

        await using (var dbA2 = NewDb())
            (await new ScoreQuestionCommandHandler(dbA2).Handle(
                ScoreCmd(sid, qid, user, v2), default)).IsSuccess.Should().BeTrue();

        await using var dbB = NewDb();
        var act = () => new ScoreQuestionCommandHandler(dbB).Handle(
            ScoreCmd(sid, qid, user, v2), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact] // A09 skip:A 先作答(版本推进)后 B 持旧版本 skip → 并发异常,题目仍 Answered
    public async Task Skip_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();
        var (sid, qid, v) = await SeedSessionAsync(user);

        await using (var dbA = NewDb())
            (await new AnswerQuestionCommandHandler(dbA).Handle(
                AnswerCmd(sid, qid, user, "ans", v), default)).IsSuccess.Should().BeTrue();

        await using var dbB = NewDb();
        var act = () => new SkipQuestionCommandHandler(dbB).Handle(
            new SkipQuestionCommand(sid, qid, user, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await GetDetailAsync(user, sid)).Questions.Single().State.Should().Be("Answered");
    }

    [Fact] // A09 兼容:三命令不带 Version 仍可写(旧调用方/AI 编排口径不变)
    public async Task QuestionCommands_NoVersion_StillSave()
    {
        if (!_available) return;
        var user = NewUser();
        var (sid, qid, _) = await SeedSessionAsync(user);

        await using (var db = NewDb())
            (await new AnswerQuestionCommandHandler(db).Handle(
                AnswerCmd(sid, qid, user, "ans"), default)).IsSuccess.Should().BeTrue();
        await using (var db = NewDb())
            (await new ScoreQuestionCommandHandler(db).Handle(
                ScoreCmd(sid, qid, user), default)).IsSuccess.Should().BeTrue();

        var (sid2, qid2, _) = await SeedSessionAsync(user);
        await using (var db = NewDb())
            (await new SkipQuestionCommandHandler(db).Handle(
                new SkipQuestionCommand(sid2, qid2, user), default)).IsSuccess.Should().BeTrue();
        (await GetDetailAsync(user, sid2)).Questions.Single().State.Should().Be("Skipped");
    }

    // ---------------- A13 storage settings ----------------

    /// <summary>最小 IAudioStore:根目录解析只认设置行本身(与 LocalAudioStore 的 DesiredRoot 口径一致)。</summary>
    private sealed class FakeStore : IAudioStore
    {
        public bool IsConfigured => true;
        public string DefaultRoot => "/tmp/t108-default";
        public Task<string> SaveAsync(Guid u, Guid r, string e, Stream c, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<string> SaveVoiceTrackAsync(Guid u, Guid r, string e, Stream c, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(Guid u, string p, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid u, string p, CancellationToken ct)
            => throw new NotSupportedException();
        public async Task<StorageRoots> ResolveRootsAsync(Guid userId, CancellationToken ct)
        {
            await using var db = NewDb();
            var row = await db.StorageSettings.AsNoTracking()
                .FirstOrDefaultAsync(x => x.UserId == userId, ct);
            return new StorageRoots(DefaultRoot, row?.RootPath, DefaultRoot, false, null);
        }
    }

    /// <summary>SaveStorageSettingCommandHandler 尾部回读 GetStorageSettingQuery 的分发桩(同 MockAi 先例)。</summary>
    private sealed class SettingDispatchSender : ISender
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        {
            await using var db = NewDb();
            object? result = request switch
            {
                GetStorageSettingQuery q => await new GetStorageSettingQueryHandler(
                    new FakeStore(), new ConfigurationBuilder().Build(), db).Handle(q, ct),
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

    private async Task<StorageSettingDto> SaveSettingAsync(Guid user, string? path, uint? version = null)
    {
        await using var db = NewDb();
        var r = await new SaveStorageSettingCommandHandler(db, new SettingDispatchSender())
            .Handle(new SaveStorageSettingCommand(user, path, version), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<StorageSettingDto> GetSettingAsync(Guid user)
    {
        await using var db = NewDb();
        return await new GetStorageSettingQueryHandler(
            new FakeStore(), new ConfigurationBuilder().Build(), db)
            .Handle(new GetStorageSettingQuery(user), default);
    }

    [Fact] // A13:新建回传 Version;A 持当前版本改路径成功,B 持旧版本 → 并发异常,A 路径完好
    public async Task StorageSetting_StaleVersion_Throws()
    {
        if (!_available) return;
        var user = NewUser();

        var created = await SaveSettingAsync(user, "/tmp/t108-a/root");
        created.Version.Should().NotBeNull();
        var v = created.Version!.Value;

        var a = await SaveSettingAsync(user, "/tmp/t108-a2/root", v);
        a.DesiredPath.Should().Be("/tmp/t108-a2/root");
        a.Version.Should().NotBe(v);

        await using var dbB = NewDb();
        var act = () => new SaveStorageSettingCommandHandler(dbB, new SettingDispatchSender())
            .Handle(new SaveStorageSettingCommand(user, "/tmp/t108-b/root", v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await GetSettingAsync(user)).DesiredPath.Should().Be("/tmp/t108-a2/root");
    }

    [Fact] // A13:旧页签持旧版本"恢复默认"(删行)同样被钉版拦下
    public async Task StorageSetting_StaleVersionDelete_Throws()
    {
        if (!_available) return;
        var user = NewUser();

        var created = await SaveSettingAsync(user, "/tmp/t108-c/root");
        var v = created.Version!.Value;
        await SaveSettingAsync(user, "/tmp/t108-c2/root", v);

        await using var dbB = NewDb();
        var act = () => new SaveStorageSettingCommandHandler(dbB, new SettingDispatchSender())
            .Handle(new SaveStorageSettingCommand(user, null, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();

        (await GetSettingAsync(user)).DesiredPath.Should().Be("/tmp/t108-c2/root");
    }

    [Fact] // A13 兼容:不带 Version 保存/恢复默认仍成功
    public async Task StorageSetting_NoVersion_StillSaves()
    {
        if (!_available) return;
        var user = NewUser();

        await SaveSettingAsync(user, "/tmp/t108-d/root");
        var updated = await SaveSettingAsync(user, "/tmp/t108-d2/root", version: null);
        updated.DesiredPath.Should().Be("/tmp/t108-d2/root");

        var cleared = await SaveSettingAsync(user, null, version: null);
        cleared.DesiredPath.Should().BeNull();
        cleared.Version.Should().BeNull();
    }
}
