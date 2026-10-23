using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
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
}
