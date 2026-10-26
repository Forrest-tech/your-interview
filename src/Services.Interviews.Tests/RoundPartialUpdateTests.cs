using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using YourInterview.Services.Interviews.Infrastructure.Persistence;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// COD-04(S1):轮次保存「部分字段更新」不再静默清数据。
/// 协议:UpdateRoundCommand 的扩展字段 null/缺省 = 保持原值;空字符串 = 清空(存 null);
/// 非空 = 赋值。前端 round-card 保存与 comm-log 同步只传基础字段(扩展字段为 null),
/// 保存一次不得抹除已存的转写/备考题/邮件/录音/会议链接/时间/清单。
/// 走真实 UpdateRoundCommandHandler + 真实 DB(无 DB 环境跳过,同 PrepPlanFlowTests 先例)。
/// </summary>
public sealed class RoundPartialUpdateTests : IAsyncLifetime
{
    private const string Cs =
        "Host=localhost;Port=5432;Database=yourinterview;Username=postgres;Password=postgres";
    private InterviewsDbContext _db = null!;
    private bool _available;

    private const string MeetingLink = "https://meet.example.com/room-1";
    private const string ScheduledTime = "14:30";
    private const string PrepQuestionsJson = "[{\"text\":\"Tell me about yourself\",\"checked\":true}]";
    private const string EmailsJson =
        "[{\"from\":\"hr@example.com\",\"subject\":\"Invite\",\"date\":\"2026-10-10\",\"snippet\":\"hi\"}]";
    private const string TranscriptText = "Interviewer: walk me through it. Candidate: sure.";
    private const string RecordingUrl = "https://files.example.com/rec-1.mp3";
    private const string ChecklistJson = "[{\"text\":\"Bring ID\",\"done\":true}]";

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<InterviewsDbContext>()
            .UseNpgsql(Cs).Options;
        _db = new InterviewsDbContext(opts, new TestCurrentUser());
        try
        {
            _available = await _db.Database.CanConnectAsync();
        }
        catch { _available = false; }
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    /// <summary>建条目 + 轮次,再以全字段命令写入全部扩展字段(模拟 rounds-timeline 全字段写入方)。</summary>
    private async Task<(Guid EntryId, Guid RoundId)> SeedRoundWithAllDetailsAsync()
    {
        var e = new InterviewEntry(Guid.NewGuid(),
            "Cod04-" + Guid.NewGuid().ToString("N")[..8], "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        var r = e.AddRound("Technical");
        await _db.SaveChangesAsync();

        var full = new UpdateRoundCommand(Guid.Empty, e.Id, r.Id, "Technical",
            new DateOnly(2026, 10, 20), "Alice", "Video", "Toronto", "Pending",
            "note", "feedback",
            MeetingLink: MeetingLink, ScheduledTime: ScheduledTime,
            PrepQuestionsJson: PrepQuestionsJson, EmailsJson: EmailsJson,
            Transcript: TranscriptText, RecordingUrl: RecordingUrl,
            ChecklistJson: ChecklistJson);
        var res = await new UpdateRoundCommandHandler(_db).Handle(full, default);
        res.IsSuccess.Should().BeTrue();
        return (e.Id, r.Id);
    }

    private async Task<InterviewRound> ReloadRoundAsync(Guid roundId)
    {
        _db.ChangeTracker.Clear();
        return await _db.Rounds.AsNoTracking().FirstAsync(x => x.Id == roundId);
    }

    [Fact]
    public async Task PartialUpdate_BasicFieldsOnly_PreservesAllExtensionFields()
    {
        if (!_available) return; // 无沙盒 DB 的环境跳过
        var (entryId, roundId) = await SeedRoundWithAllDetailsAsync();

        // 仅基础字段的更新命令:扩展字段全部缺省(null)—— round-card 保存 / comm-log 同步的形态
        var partial = new UpdateRoundCommand(Guid.Empty, entryId, roundId, "Technical",
            new DateOnly(2026, 10, 21), "Alice, Bob", "Video", null, "Pending", null, null);
        var res = await new UpdateRoundCommandHandler(_db).Handle(partial, default);
        res.IsSuccess.Should().BeTrue();

        var stored = await ReloadRoundAsync(roundId);
        // 基础字段确实被更新(证明命令生效,不是整条被忽略)
        stored.ScheduledDate.Should().Be(new DateOnly(2026, 10, 21));
        stored.Interviewers.Should().Be("Alice, Bob");
        // 扩展字段必须原样保留
        stored.Transcript.Should().Be(TranscriptText);
        stored.PrepQuestionsJson.Should().Be(PrepQuestionsJson);
        stored.EmailsJson.Should().Be(EmailsJson);
        stored.RecordingUrl.Should().Be(RecordingUrl);
        stored.MeetingLink.Should().Be(MeetingLink);
        stored.ScheduledTime.Should().Be(ScheduledTime);
        stored.ChecklistJson.Should().Be(ChecklistJson);
    }

    /// <summary>票216:下拉框发 'Failed'（枚举无此值），写路径归一化为 Rejected 别名后入库。</summary>
    [Fact]
    public async Task FailedOutcome_AliasToRejected_StoredAsRejected()
    {
        if (!_available) return; // 无沙盒 DB 的环境跳过
        var e = new InterviewEntry(Guid.NewGuid(),
            "T216-" + Guid.NewGuid().ToString("N")[..8], "Senior .NET Developer");
        _db.Entries.Add(e);
        await _db.SaveChangesAsync();
        var r = e.AddRound("Technical");
        await _db.SaveChangesAsync();

        var cmd = new UpdateRoundCommand(Guid.Empty, e.Id, r.Id, "Technical",
            new DateOnly(2026, 10, 20), "Alice", "Video", "Toronto", "Failed", null, null);
        var res = await new UpdateRoundCommandHandler(_db).Handle(cmd, default);
        res.IsSuccess.Should().BeTrue();

        var stored = await ReloadRoundAsync(r.Id);
        stored.Outcome.Should().Be(InterviewRoundOutcome.Rejected);
    }

    [Fact]
    public async Task ClearSemantics_EmptyString_ClearsExtensionFields()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedRoundWithAllDetailsAsync();

        // 显式传空字符串 = 清空对应字段(存 null)
        var clear = new UpdateRoundCommand(Guid.Empty, entryId, roundId, "Technical",
            new DateOnly(2026, 10, 20), "Alice", "Video", "Toronto", "Pending", null, null,
            MeetingLink: "", ScheduledTime: "", PrepQuestionsJson: "", EmailsJson: "",
            Transcript: "", RecordingUrl: "", ChecklistJson: "");
        var res = await new UpdateRoundCommandHandler(_db).Handle(clear, default);
        res.IsSuccess.Should().BeTrue();

        var stored = await ReloadRoundAsync(roundId);
        stored.MeetingLink.Should().BeNull();
        stored.ScheduledTime.Should().BeNull();
        stored.PrepQuestionsJson.Should().BeNull();
        stored.EmailsJson.Should().BeNull();
        stored.Transcript.Should().BeNull();
        stored.RecordingUrl.Should().BeNull();
        stored.ChecklistJson.Should().BeNull();
    }

    [Fact]
    public async Task MixedUpdate_NullKeeps_EmptyClears_ValueAssigns()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedRoundWithAllDetailsAsync();

        var mixed = new UpdateRoundCommand(Guid.Empty, entryId, roundId, "Technical",
            new DateOnly(2026, 10, 20), "Alice", "Video", "Toronto", "Pending", null, null,
            MeetingLink: "https://meet.example.com/room-2", // 非空 = 赋值
            Transcript: "",                                 // 空 = 清空
            RecordingUrl: null);                            // null = 保持
        var res = await new UpdateRoundCommandHandler(_db).Handle(mixed, default);
        res.IsSuccess.Should().BeTrue();

        var stored = await ReloadRoundAsync(roundId);
        stored.MeetingLink.Should().Be("https://meet.example.com/room-2");
        stored.Transcript.Should().BeNull();
        stored.RecordingUrl.Should().Be(RecordingUrl);
        stored.PrepQuestionsJson.Should().Be(PrepQuestionsJson);
        stored.EmailsJson.Should().Be(EmailsJson);
        stored.ScheduledTime.Should().Be(ScheduledTime);
        stored.ChecklistJson.Should().Be(ChecklistJson);
    }

    [Fact]
    public async Task FullUpdate_NonEmptyValues_OverwriteExtensionFields()
    {
        if (!_available) return;
        var (entryId, roundId) = await SeedRoundWithAllDetailsAsync();

        // 全字段写入方(rounds-timeline 路径)行为不变:非空值整体覆盖
        var full = new UpdateRoundCommand(Guid.Empty, entryId, roundId, "SystemDesign",
            new DateOnly(2026, 10, 22), "Carol", "Onsite", "Office", "Passed", "n2", "f2",
            MeetingLink: "https://meet.example.com/room-9", ScheduledTime: "09:00",
            PrepQuestionsJson: "[]", EmailsJson: "[]",
            Transcript: "new transcript", RecordingUrl: "https://files.example.com/rec-2.mp3",
            ChecklistJson: "[]");
        var res = await new UpdateRoundCommandHandler(_db).Handle(full, default);
        res.IsSuccess.Should().BeTrue();

        var stored = await ReloadRoundAsync(roundId);
        stored.MeetingLink.Should().Be("https://meet.example.com/room-9");
        stored.ScheduledTime.Should().Be("09:00");
        stored.PrepQuestionsJson.Should().Be("[]");
        stored.EmailsJson.Should().Be("[]");
        stored.Transcript.Should().Be("new transcript");
        stored.RecordingUrl.Should().Be("https://files.example.com/rec-2.mp3");
        stored.ChecklistJson.Should().Be("[]");
    }
}
