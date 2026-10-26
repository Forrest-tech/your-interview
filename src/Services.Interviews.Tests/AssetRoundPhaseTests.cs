using FluentAssertions;
using YourInterview.Services.Interviews.Application;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>2026-10-19:资产按 Round 管理(Phase/Kind)+ 备考方案提示词组装。</summary>
public sealed class AssetRoundPhaseTests
{
    private static InterviewEntry NewEntryWithRounds(out InterviewRound r1, out InterviewRound r2)
    {
        var e = new InterviewEntry(Guid.NewGuid(), "Arvato", "Senior .NET Developer");
        r1 = e.AddRound("Technical");
        r2 = e.AddRound("SystemDesign");
        return e;
    }

    [Fact]
    public void AttachAsset_WithRoundAndPhase_SetsFields()
    {
        var e = NewEntryWithRounds(out var r1, out _);

        var a = e.AttachAsset(AssetKind.Email, "sched.eml", "text/plain", 10, null, null,
            transcriptText: "interview scheduled at Mon 10am",
            roundId: r1.Id, phase: AssetPhase.Pre);

        a.RoundId.Should().Be(r1.Id);
        a.Phase.Should().Be(AssetPhase.Pre);
        a.Kind.Should().Be(AssetKind.Email);
    }

    [Fact]
    public void AttachAsset_InvalidRound_Throws()
    {
        var e = NewEntryWithRounds(out _, out _);

        var act = () => e.AttachAsset(AssetKind.Text, "x.txt", null, 1, null, null,
            transcriptText: "hello", roundId: Guid.NewGuid(), phase: AssetPhase.Post);

        act.Should().Throw<InvalidOperationException>().WithMessage("*轮次不存在*");
    }

    [Fact]
    public void AttachAsset_PrepDoc_DoesNotTransitionToTranscribed()
    {
        var e = NewEntryWithRounds(out var r1, out _);

        e.AttachAsset(AssetKind.PrepDoc, "备考方案-Round1.md", "text/markdown", 100, null, null,
            transcriptText: "# 备考方案\n...", roundId: r1.Id, phase: AssetPhase.Pre);

        // 备考文档不是候选人语音:不能把条目推进为"已转写"
        e.Status.Should().Be(InterviewStatus.AssetsUploaded);
    }

    [Fact]
    public void AttachAssetValidator_EntryLevelWithoutRoundId_Valid()
    {
        // RoundId 可空是有意设计:条目级材料(截图、整场转写等)靠 EntryId 挂载,
        // 不属于某一轮;校验器对 RoundId 无规则,缺 RoundId 时应校验通过。
        var cmd = new AttachAssetCommand(Guid.Empty, Guid.NewGuid(), "Text", "x.txt", null, 1, null, null,
            "hello", null, null);
        new AttachAssetCommandValidator().Validate(cmd).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AttachAssetValidator_MissingEntryId_Invalid()
    {
        var cmd = new AttachAssetCommand(Guid.Empty, Guid.Empty, "Text", "x.txt", null, 1, null, null,
            "hello", null, null);
        new AttachAssetCommandValidator().Validate(cmd).IsValid.Should().BeFalse();
    }

    [Fact]
    public void AttachAssetValidator_NewKinds_Valid()
    {
        foreach (var kind in new[] { "PrepDoc", "Email", "Feedback", "Other" })
        {
            var cmd = new AttachAssetCommand(Guid.Empty, Guid.NewGuid(), kind, "x.txt", null, 1, null, null,
                "content", null, null, "en", null, Guid.NewGuid(), "Pre");
            new AttachAssetCommandValidator().Validate(cmd).IsValid
                .Should().BeTrue($"{kind} 应是合法类型");
        }
    }

    [Fact]
    public void PrepPlanPrompt_NextRound_IncludesPreviousRoundsData()
    {
        var e = NewEntryWithRounds(out var r1, out var r2);
        // R1:回写反馈 + 本轮转录资产
        e.UpdateRound(r1.Id, "Technical", null, "David", "Video", null,
            InterviewRoundOutcome.Passed, null, "DI 讲得不够深");
        e.AttachAsset(AssetKind.Transcript, "r1.txt", null, 10, null, null,
            transcriptText: "R1TRANSCRIPT: asked about EF Core migrations",
            roundId: r1.Id, phase: AssetPhase.Post);

        var cmd = new GeneratePrepPlanCommand(Guid.Empty, e.Id, r2.Id,
            JdText: "JD: system design focus", ResumeText: "Forrest resume",
            Experiences: "Glassdoor: URL shortener", Requirements: "SystemDesign round");
        var prompt = GeneratePrepPlanCommandHandler.BuildPrompt(e, r2, cmd);

        prompt.Should().Contain("JD: system design focus");
        prompt.Should().Contain("Forrest resume");
        prompt.Should().Contain("Glassdoor: URL shortener");
        prompt.Should().Contain("SystemDesign round");
        // 之前轮次的复盘数据必须带入(下一轮备考)
        prompt.Should().Contain("之前轮次复盘");
        prompt.Should().Contain("DI 讲得不够深");
        prompt.Should().Contain("R1TRANSCRIPT");
    }

    [Fact]
    public void PrepPlanPrompt_FirstRound_NoPreviousSection()
    {
        var e = NewEntryWithRounds(out var r1, out _);
        var cmd = new GeneratePrepPlanCommand(Guid.Empty, e.Id, r1.Id, JdText: "JD only");
        var prompt = GeneratePrepPlanCommandHandler.BuildPrompt(e, r1, cmd);
        prompt.Should().NotContain("之前轮次复盘");
        prompt.Should().Contain("备考方案");
    }
}
