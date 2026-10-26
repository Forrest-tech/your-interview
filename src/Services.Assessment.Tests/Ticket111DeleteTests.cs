using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票111 段3 §7(Assessment 删除 If-Match 甲案 + 前置补口):
/// A06 类别 / A08 会话 / A11 录音 —— 三 DTO 补 Version 回传(实体本有 xmin,零迁移);
/// 删除带读时 Version:陈旧 → 409(DbUpdateConcurrencyException 或 handler 前置冲突)且数据完好
/// (A11 含音频文件不被删);新鲜 → 成功;不带 → 兼容成功。
/// DB 门控同 Ticket108 先例。
/// </summary>
public sealed class Ticket111DeleteTests : IAsyncLifetime
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
        var cats = await db.Categories.Where(c => _users.Contains(c.UserId)).ToListAsync();
        db.Categories.RemoveRange(cats);
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

    // ---------- A06 类别 ----------

    private async Task<PracticeCategoryDto> CreateCategoryAsync(Guid user, string name)
    {
        await using var db = NewDb();
        var r = await new CreateCategoryCommandHandler(db).Handle(new CreateCategoryCommand(user, name), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    private async Task<List<PracticeCategoryDto>> ListCategoriesAsync(Guid user)
    {
        await using var db = NewDb();
        return (await new ListCategoriesQueryHandler(db).Handle(new ListCategoriesQuery(user), default)).ToList();
    }

    [Fact]
    public async Task DeleteCategory_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var created = await CreateCategoryAsync(user, "T111Cat");
        created.Version.Should().NotBeNull("A06 补口:新建类别 DTO 须回传 Version");
        var v = created.Version!.Value;

        // A 先改名(版本推进)
        await using (var dbA = NewDb())
            (await new RenameCategoryCommandHandler(dbA).Handle(
                new RenameCategoryCommand(user, created.Id, "T111Cat-A"), default)).IsSuccess.Should().BeTrue();
        var v2 = (await ListCategoriesAsync(user)).Single(c => c.Id == created.Id).Version;
        v2.Should().NotBeNull().And.NotBe(v);

        // B 持旧版本删除 → 冲突,类别完好
        await using var dbB = NewDb();
        var act = () => new DeleteCategoryCommandHandler(dbB).Handle(new DeleteCategoryCommand(user, created.Id, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await ListCategoriesAsync(user)).Should().Contain(c => c.Id == created.Id);

        await using (var dbF = NewDb())
            (await new DeleteCategoryCommandHandler(dbF).Handle(new DeleteCategoryCommand(user, created.Id, v2), default))
                .IsSuccess.Should().BeTrue();
        (await ListCategoriesAsync(user)).Should().NotContain(c => c.Id == created.Id);

        var c2 = await CreateCategoryAsync(user, "T111Cat2");
        await using (var dbN = NewDb())
            (await new DeleteCategoryCommandHandler(dbN).Handle(new DeleteCategoryCommand(user, c2.Id), default))
                .IsSuccess.Should().BeTrue();
    }

    // ---------- A08 会话 ----------

    private async Task<SessionDetailDto> GetDetailAsync(Guid user, Guid sessionId)
    {
        await using var db = NewDb();
        var r = await new GetSessionQueryHandler(db).Handle(new GetSessionQuery(sessionId, user), default);
        r.IsSuccess.Should().BeTrue(r.Error.Description);
        return r.Value;
    }

    [Fact]
    public async Task DeleteSession_StaleVersion_Throws_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        Guid sid;
        await using (var db = NewDb())
        {
            var created = await new CreateSessionCommandHandler(db).Handle(
                new CreateSessionCommand(user, "T111 session", SessionMode.TopicDrill, "EF Core", null, 5, "en"), default);
            created.IsSuccess.Should().BeTrue(created.Error.Description);
            sid = created.Value;
        }
        var detail = await GetDetailAsync(user, sid);
        detail.Version.Should().NotBeNull("A08 补口:会话详情 DTO 须回传会话行 Version");
        var v = detail.Version!.Value;

        // A 先完成会话(会话行版本推进)
        await using (var dbA = NewDb())
            (await new CompleteSessionCommandHandler(dbA).Handle(
                new CompleteSessionCommand(sid, user, "done", null), default)).IsSuccess.Should().BeTrue();
        (await GetDetailAsync(user, sid)).Version.Should().NotBe(v);

        // B 持旧版本删除 → 冲突,会话完好
        await using var dbB = NewDb();
        var act = () => new DeleteSessionCommandHandler(dbB).Handle(new DeleteSessionCommand(sid, user, v), default);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await GetDetailAsync(user, sid)).Status.Should().Be("Completed");

        var v2 = (await GetDetailAsync(user, sid)).Version;
        await using (var dbF = NewDb())
            (await new DeleteSessionCommandHandler(dbF).Handle(new DeleteSessionCommand(sid, user, v2), default))
                .IsSuccess.Should().BeTrue();

        Guid sid2;
        await using (var db = NewDb())
        {
            var created = await new CreateSessionCommandHandler(db).Handle(
                new CreateSessionCommand(user, "T111 session 2", SessionMode.TopicDrill, "EF Core", null, 5, "en"), default);
            created.IsSuccess.Should().BeTrue(created.Error.Description);
            sid2 = created.Value;
        }
        await using (var dbN = NewDb())
            (await new DeleteSessionCommandHandler(dbN).Handle(new DeleteSessionCommand(sid2, user), default))
                .IsSuccess.Should().BeTrue();
    }

    // ---------- A11 录音 ----------

    private sealed class StubStore : IAudioStore
    {
        public bool DeleteCalled;
        public bool IsConfigured => true;
        public string DefaultRoot => "/tmp/t111";
        public Task<string> SaveAsync(Guid u, Guid r, string e, Stream c, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(Guid u, string p, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid u, string p, CancellationToken ct)
        {
            DeleteCalled = true;
            return Task.FromResult(true);
        }
        public Task<StorageRoots> ResolveRootsAsync(Guid userId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private async Task<Guid> SeedRecordingAsync(Guid user)
    {
        await using var db = NewDb();
        var rec = new PracticeRecording(Guid.NewGuid(), user, Guid.NewGuid(),
            "t111/test.wav", "audio/wav", 100, 1.5);
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    private async Task<RecordingDto> GetRecordingAsync(Guid user, Guid recId)
    {
        await using var db = NewDb();
        var rows = await new ListAllRecordingsQueryHandler(db).Handle(new ListAllRecordingsQuery(user), default);
        return rows.Single(x => x.Id == recId);
    }

    [Fact]
    public async Task DeleteRecording_StaleVersion_Conflict_FileAndRowIntact_FreshAndNoVersion_Succeed()
    {
        if (!_available) return;
        var user = NewUser();
        var recId = await SeedRecordingAsync(user);
        var dto = await GetRecordingAsync(user, recId);
        dto.Version.Should().NotBeNull("A11 补口:录音列表 DTO 须回传录音行 Version");
        var v = dto.Version!.Value;

        // A 改动录音行(存储层直写推进 xmin —— 录音行无其他用户级写路径,
        //   以同值 UPDATE 模拟另一页签对该行的写入,PG 仍会推进 xmin)
        await using (var dbA = NewDb())
            await dbA.Database.ExecuteSqlRawAsync(
                "UPDATE assessment.practice_recordings SET \"ContentType\" = \"ContentType\" WHERE \"Id\" = {0}", recId);
        (await GetRecordingAsync(user, recId)).Version.Should().NotBe(v);

        // B 持旧版本删除 → 前置冲突(409 口径),且文件删除绝不发生、行完好
        var store = new StubStore();
        await using (var dbB = NewDb())
        {
            var r = await new DeleteRecordingCommandHandler(dbB, store,
                NullLogger<DeleteRecordingCommandHandler>.Instance)
                .Handle(new DeleteRecordingCommand(user, recId, v), default);
            r.IsSuccess.Should().BeFalse();
            r.Error.Code.Should().Be("concurrency_conflict");
        }
        store.DeleteCalled.Should().BeFalse("陈旧删除被拒时音频文件必须完好");
        (await GetRecordingAsync(user, recId)).Id.Should().Be(recId);

        // 新鲜版本删除 → 成功(文件删除被调用)
        var v2 = (await GetRecordingAsync(user, recId)).Version;
        var store2 = new StubStore();
        await using (var dbF = NewDb())
        {
            var r = await new DeleteRecordingCommandHandler(dbF, store2,
                NullLogger<DeleteRecordingCommandHandler>.Instance)
                .Handle(new DeleteRecordingCommand(user, recId, v2), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
        store2.DeleteCalled.Should().BeTrue();

        // 不带版本 → 兼容成功
        var recId2 = await SeedRecordingAsync(user);
        var store3 = new StubStore();
        await using (var dbN = NewDb())
        {
            var r = await new DeleteRecordingCommandHandler(dbN, store3,
                NullLogger<DeleteRecordingCommandHandler>.Instance)
                .Handle(new DeleteRecordingCommand(user, recId2), default);
            r.IsSuccess.Should().BeTrue(r.Error.Description);
        }
    }
}
