using FluentAssertions;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// 票65(ARC-08d):StartTranscription 守卫与回落 boundary 补强(特征测试锁现状)。
/// 与 TranscriptionStartTests(精确选材五例)互补:本文件覆盖
/// 无材料守卫、三态幂等守卫(Transcribing/Analyzing/Analyzed)、
/// Transcribed 合法重跑、无 assetId 且无可用录音时的回落事件形态、
/// 空白 StoragePath 的指定材料拒绝。纯领域测试,不依赖 DB。
/// </summary>
public sealed class TranscriptionGuardBoundaryTests
{
    private static InterviewEntry EntryWithAudio(out InterviewAsset audio)
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");
        audio = e.AttachAsset(AssetKind.Audio, "r1.wav", "audio/wav", 100, "audio/r1.wav", null);
        return e;
    }

    [Fact] // boundary:空集合 —— 一件材料都没有
    public void StartTranscription_NoAssets_Throws()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");

        var act = () => e.StartTranscription();

        act.Should().Throw<InvalidOperationException>().WithMessage("*还没有上传任何材料*");
        e.Status.Should().Be(InterviewStatus.Draft);
    }

    [Fact] // 守卫:Transcribing 中重复调用 = 静默返回,不重复发事件
    public void StartTranscription_WhileTranscribing_IsIdempotentNoOp()
    {
        var e = EntryWithAudio(out _);
        e.StartTranscription();
        e.ClearDomainEvents();

        e.StartTranscription();

        e.Status.Should().Be(InterviewStatus.Transcribing);
        e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Should().BeEmpty();
    }

    [Fact] // 守卫:Analyzing 中调用 = 静默返回
    public void StartTranscription_WhileAnalyzing_IsNoOp()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");
        e.AttachAsset(AssetKind.Text, "notes.txt", "text/plain", 10, null, null,
            transcriptText: "hello");
        e.StartAnalysis();
        e.ClearDomainEvents();

        e.StartTranscription();

        e.Status.Should().Be(InterviewStatus.Analyzing);
        e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Should().BeEmpty();
    }

    [Fact] // 守卫:Analyzed 终态调用 = 静默返回
    public void StartTranscription_WhenAnalyzed_IsNoOp()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");
        e.AttachAsset(AssetKind.Text, "notes.txt", "text/plain", 10, null, null,
            transcriptText: "hello");
        e.StartAnalysis();
        e.ApplyAnalysis(80, null, 70, 70, 70, 70, null);
        e.Status.Should().Be(InterviewStatus.Analyzed);
        e.ClearDomainEvents();

        e.StartTranscription();

        e.Status.Should().Be(InterviewStatus.Analyzed);
        e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Should().BeEmpty();
    }

    [Fact] // Transcribed 不在守卫内:已转写后重新发起 = 合法重跑
    public void StartTranscription_FromTranscribed_RerunAllowed()
    {
        var e = EntryWithAudio(out var audio);
        e.AttachAsset(AssetKind.Text, "notes.txt", "text/plain", 10, null, null,
            transcriptText: "hello");
        e.Status.Should().Be(InterviewStatus.Transcribed);
        e.ClearDomainEvents();

        e.StartTranscription(audio.Id);

        e.Status.Should().Be(InterviewStatus.Transcribing);
        e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>()
            .Single().AssetId.Should().Be(audio.Id);
    }

    [Fact] // boundary:无 assetId + 有材料但无可用录音 → 回落事件携带 Guid.Empty/null 路径(现状锁定)
    public void StartTranscription_NoAudioAvailable_RaisesEmptyAssetEvent()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");
        e.AttachAsset(AssetKind.Text, "notes.txt", "text/plain", 10, null, null,
            transcriptText: "hello");
        e.ClearDomainEvents();

        e.StartTranscription();

        e.Status.Should().Be(InterviewStatus.Transcribing);
        var evt = e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Single();
        evt.AssetId.Should().Be(Guid.Empty);
        evt.StoragePath.Should().BeNull();
    }

    [Fact] // boundary:无 assetId + 录音无落盘路径 → 同样回落空事件(不抛错,现状锁定)
    public void StartTranscription_AudioWithoutPath_Untargeted_RaisesEmptyAssetEvent()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");
        e.AttachAsset(AssetKind.Audio, "r1.wav", "audio/wav", 100, null, null);
        e.ClearDomainEvents();

        e.StartTranscription();

        var evt = e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Single();
        evt.AssetId.Should().Be(Guid.Empty);
        evt.StoragePath.Should().BeNull();
    }

    [Fact] // boundary:指定录音的 StoragePath 为纯空白 → 按不可转写拒绝
    public void StartTranscription_WhitespaceStoragePath_Throws()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "GuardCo", "Senior .NET Developer");
        var audio = e.AttachAsset(AssetKind.Audio, "r1.wav", "audio/wav", 100, "   ", null);

        var act = () => e.StartTranscription(audio.Id);

        act.Should().Throw<InvalidOperationException>().WithMessage("*不是可转写的录音*");
    }
}
