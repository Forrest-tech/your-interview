using FluentAssertions;
using YourInterview.Services.Interviews.Domain;
using Xunit;

namespace YourInterview.Services.Interviews.Tests;

/// <summary>
/// COD-07:开始转写的资产定位。前端轮次页在某一轮的录音上点「开始转写」,
/// 后端必须精确转写那一件录音 —— 条目级旧行为选全条目第一件录音,
/// 多轮条目会转错轮次。无 assetId 时保留条目级旧语义 (Worker 重跑路径在用)。
/// </summary>
public sealed class TranscriptionStartTests
{
    private static InterviewEntry EntryWithTwoRoundAudios(
        out InterviewAsset audio1, out InterviewAsset audio2)
    {
        var e = new InterviewEntry(Guid.NewGuid(), "TranscribeCo", "Senior .NET Developer");
        var r1 = e.AddRound("Technical");
        var r2 = e.AddRound("SystemDesign");
        audio1 = e.AttachAsset(AssetKind.Audio, "r1.wav", "audio/wav", 100, "audio/r1.wav", null,
            roundId: r1.Id, phase: AssetPhase.Post);
        audio2 = e.AttachAsset(AssetKind.Audio, "r2.wav", "audio/wav", 100, "audio/r2.wav", null,
            roundId: r2.Id, phase: AssetPhase.Post);
        return e;
    }

    [Fact]
    public void StartTranscription_WithAssetId_RaisesEventForThatAsset()
    {
        var e = EntryWithTwoRoundAudios(out _, out var audio2);

        e.StartTranscription(audio2.Id);

        e.Status.Should().Be(InterviewStatus.Transcribing);
        var evt = e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Single();
        evt.AssetId.Should().Be(audio2.Id);
        evt.StoragePath.Should().Be("audio/r2.wav");
    }

    [Fact]
    public void StartTranscription_WithoutAssetId_PicksFirstAudio()
    {
        var e = EntryWithTwoRoundAudios(out var audio1, out _);

        e.StartTranscription();

        var evt = e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Single();
        evt.AssetId.Should().Be(audio1.Id);
        evt.StoragePath.Should().Be("audio/r1.wav");
    }

    [Fact]
    public void StartTranscription_UnknownAsset_ThrowsAndKeepsStatus()
    {
        var e = EntryWithTwoRoundAudios(out _, out _);

        var act = () => e.StartTranscription(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>().WithMessage("*材料不存在*");
        e.Status.Should().Be(InterviewStatus.AssetsUploaded);
        e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public void StartTranscription_NonAudioAsset_Throws()
    {
        var e = EntryWithTwoRoundAudios(out _, out _);
        var text = e.AttachAsset(AssetKind.Text, "notes.txt", "text/plain", 10, null, null,
            transcriptText: "hello");
        // 文本材料会把条目推进到 Transcribed;从 Transcribed 发起转写是合法重跑,守卫不挡,
        // 但指定对象是文本材料本身 —— 不是录音,必须拒绝而不是悄悄转第一件录音。
        var act = () => e.StartTranscription(text.Id);

        act.Should().Throw<InvalidOperationException>().WithMessage("*不是可转写的录音*");
        e.DomainEvents.OfType<InterviewTranscriptionStartedDomainEvent>().Should().BeEmpty();
    }

    [Fact]
    public void StartTranscription_AudioWithoutStoragePath_Throws()
    {
        var e = new InterviewEntry(Guid.NewGuid(), "TranscribeCo", "Senior .NET Developer");
        var r1 = e.AddRound("Technical");
        var audio = e.AttachAsset(AssetKind.Audio, "r1.wav", "audio/wav", 100, null, null,
            roundId: r1.Id, phase: AssetPhase.Post);

        var act = () => e.StartTranscription(audio.Id);

        act.Should().Throw<InvalidOperationException>().WithMessage("*不是可转写的录音*");
    }
}
