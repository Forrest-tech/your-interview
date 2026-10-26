using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Domain.JsonFields;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票19 ARC-06 EF 存储形测试(真实 DB,无 DB 环境跳过):
/// ① 新写入落库 = 版本信封,经聚合属性读回 = 原始载荷逐字;
/// ② 旧数据(原生 SQL 写入无信封原文)读回逐字不变(向后兼容);
/// ③ 库里出现未知版本信封时,属性读取抛 JsonFieldException(不静默吞)。
/// </summary>
public sealed class JsonFieldStorageTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private InterviewsDbContext _db = null!;
    private bool _available;

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options;
        _db = new InterviewsDbContext(opts, new TestCurrentUser());
        try { _available = await _db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private InterviewsDbContext NewCtx() => new(
        new DbContextOptionsBuilder<InterviewsDbContext>().UseNpgsql(Cs).Options,
        new TestCurrentUser());

    private async Task<string?> RawColumnAsync(string table, string column, Guid id)
    {
        await using var ctx = NewCtx();
        var conn = ctx.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT \"{column}\" FROM interviews.{table} WHERE \"Id\" = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : (string)v;
    }

    [Fact] // ① 新写入:列 = 信封(Unwrap 后逐字),新上下文读回逐字
    public async Task NewWrites_StoredEnveloped_ReadBackVerbatim()
    {
        if (!_available) return;
        const string debrief = "{\"text\":\"复盘 🎉 round-trip\"}";
        const string emails = "[{\"from\":\"a@b.c\",\"subject\":\"s\",\"date\":\"2026-10-10\",\"snippet\":\"x\"}]";

        var e = new InterviewEntry(Guid.NewGuid(),
            "T19S-" + Guid.NewGuid().ToString("N")[..8], "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        var r = e.AddRound("Technical");
        await _db.SaveChangesAsync();
        e.SetDebrief(debrief);
        e.SetRoundEmails(r.Id, emails);
        await _db.SaveChangesAsync();

        var rawDebrief = await RawColumnAsync("entries", "DebriefJson", e.Id);
        JsonFieldAdapter.IsEnveloped(rawDebrief).Should().BeTrue();
        JsonFieldAdapter.Unwrap(rawDebrief).Should().Be(debrief);

        var rawEmails = await RawColumnAsync("rounds", "EmailsJson", r.Id);
        JsonFieldAdapter.IsEnveloped(rawEmails).Should().BeTrue();
        JsonFieldAdapter.Unwrap(rawEmails).Should().Be(emails);

        await using var ctx = NewCtx();
        var loaded = await ctx.Entries.Include(x => x.Rounds).FirstAsync(x => x.Id == e.Id);
        loaded.DebriefJson.Should().Be(debrief);
        loaded.Rounds.Single().EmailsJson.Should().Be(emails);
        loaded.ToDto().DebriefJson.Should().Be(debrief);
    }

    [Fact] // ② 旧数据:无信封原文落库,读回逐字;再经领域写入后该字段转信封、其余旧字段仍可读
    public async Task LegacyRows_ReadVerbatim_ThenUpgradeOnWrite()
    {
        if (!_available) return;
        const string legacyDebrief = "{\"text\":\"存量复盘,无信封\"}";
        const string legacyQuestions = "[{\"text\":\"团队规模?\",\"checked\":false}]";

        var e = new InterviewEntry(Guid.NewGuid(),
            "T19S-" + Guid.NewGuid().ToString("N")[..8], "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE interviews.entries SET \"DebriefJson\" = {0}, \"QuestionsToAskJson\" = {1} WHERE \"Id\" = {2}",
            legacyDebrief, legacyQuestions, e.Id);
        _db.ChangeTracker.Clear();

        await using var ctx = NewCtx();
        var loaded = await ctx.Entries.FirstAsync(x => x.Id == e.Id);
        loaded.DebriefJson.Should().Be(legacyDebrief);
        loaded.QuestionsToAskJson.Should().Be(legacyQuestions);

        loaded.SetDebrief("{\"text\":\"新复盘\"}");
        await ctx.SaveChangesAsync();
        var raw = await RawColumnAsync("entries", "DebriefJson", e.Id);
        JsonFieldAdapter.IsEnveloped(raw).Should().BeTrue();
        JsonFieldAdapter.Unwrap(raw).Should().Be("{\"text\":\"新复盘\"}");
        // 未被改写的旧字段列值保持无信封原文(不强制回填)
        (await RawColumnAsync("entries", "QuestionsToAskJson", e.Id)).Should().Be(legacyQuestions);
    }

    [Fact] // ③ 未知版本信封:属性读取抛错,不静默吞数据
    public async Task UnknownVersionStored_PropertyReadThrows()
    {
        if (!_available) return;
        var e = new InterviewEntry(Guid.NewGuid(),
            "T19S-" + Guid.NewGuid().ToString("N")[..8], "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE interviews.entries SET \"DebriefJson\" = {0} WHERE \"Id\" = {1}",
            "{\"v\":99,\"data\":\"future\"}", e.Id);
        _db.ChangeTracker.Clear();

        await using var ctx = NewCtx();
        var loaded = await ctx.Entries.FirstAsync(x => x.Id == e.Id);
        var act = () => loaded.DebriefJson;
        act.Should().Throw<JsonFieldException>();
    }
}
