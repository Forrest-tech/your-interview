using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Assessment.Application;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票65(ARC-08d):DeserializeWords 词级明细反序列化补测。
/// 该函数 internal 且 Assessment 无 InternalsVisibleTo —— 经公开查询面
/// ListRecordingsQueryHandler 真库验证(仓内标准 seam,无 DB 跳过):
/// 正常 JSON 逐词还原、坏 JSON/空/null 降级空明细(不让列表接口 500)、无评分录音 Score=null。
/// </summary>
public sealed class RecordingWordsDeserializationTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=<redacted>";
    private bool _available;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _material = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        try { _available = await db.Database.CanConnectAsync(); }
        catch { _available = false; }
    }

    public async Task DisposeAsync()
    {
        if (!_available) return;
        await using var db = NewDb();
        var recs = await db.Recordings.IgnoreQueryFilters()
            .Where(r => r.UserId == _user).ToListAsync();
        db.Recordings.RemoveRange(recs);
        await db.SaveChangesAsync();
    }

    private static AssessmentDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<AssessmentDbContext>().UseNpgsql(Cs).Options;
        return new AssessmentDbContext(opts);
    }

    private async Task SeedAsync(string? wordsJson, bool withScore = true)
    {
        await using var db = NewDb();
        var rec = new PracticeRecording(Guid.NewGuid(), _user, _material,
            "t65/test.wav", "audio/wav", 100, 1.5);
        if (withScore)
            rec.AttachScore(new PracticeRecordingScore(rec.Id, 90, 88, null, null, null,
                "hello world", wordsJson, "eastus", "hello world"));
        db.Recordings.Add(rec);
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<RecordingDto>> ListAsync()
    {
        await using var db = NewDb();
        return await new ListRecordingsQueryHandler(db)
            .Handle(new ListRecordingsQuery(_user, _material), default);
    }

    [Fact] // 正常形态:逐词明细逐字还原(字段名大小写不敏感)
    public async Task ValidWordsJson_Deserializes()
    {
        if (!_available) return;
        await SeedAsync("[{\"word\":\"hello\",\"accuracy\":92.5,\"errorType\":\"None\"}," +
                        "{\"word\":\"world\",\"accuracy\":40,\"errorType\":\"Mispronunciation\"}]");
        var rows = await ListAsync();
        var words = rows.Single().Score!.Words;
        words.Should().HaveCount(2);
        words[0].Word.Should().Be("hello");
        words[0].Accuracy.Should().Be(92.5);
        words[1].ErrorType.Should().Be("Mispronunciation");
    }

    [Theory] // 降级形态 → 空明细,Score 本身仍在。注:WordsJson 是 jsonb 列,
             // 语法坏的 JSON 存不进来;真实脏数据形态 = 合法 JSON 但形状不对
             // (对象/标量数组),反序列化抛错走 catch 降级。
    [InlineData("{\"word\":\"x\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("[]")]
    [InlineData(null)]
    public async Task BadOrEmptyWordsJson_DegradesToEmpty(string? wordsJson)
    {
        if (!_available) return;
        await SeedAsync(wordsJson);
        var rows = await ListAsync();
        rows.Single().Score.Should().NotBeNull();
        rows.Single().Score!.Words.Should().BeEmpty();
    }

    [Fact] // 未评分录音:Score=null,不触达反序列化
    public async Task UnscoredRecording_ScoreNull()
    {
        if (!_available) return;
        await SeedAsync(null, withScore: false);
        var rows = await ListAsync();
        rows.Single().Score.Should().BeNull();
    }
}
