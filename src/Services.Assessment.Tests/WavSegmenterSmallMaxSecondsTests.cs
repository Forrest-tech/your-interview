using System.Text;
using FluentAssertions;
using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票103:WavSegmenter.Split 在 maxSeconds&lt;3 时死循环(WavSeg-01)的回归 + Boundary 测试。
/// 未修机理(evidence-103/STATUS.md 读码定位):FindQuietCut 搜索窗下界
/// lo=max(target−3s, 0) 未钳到当前段起点 start;maxSeconds&lt;3 时窗内段首之前的
/// 低能量窗使 cut≤start,Split 主循环 start=cut 不前进 → 无限产出空段、永不返回。
/// 修法(选票面 (b) 并论证于 STATUS):FindQuietCut 接收段起点,lo 钳到段起点,
/// 返回值再兜底钳到 &gt;段起点;28s 生产路径 lo/best 逐值不变(票100 的 50 例守恒)。
///
/// 超时护栏口径:每例调用经 SplitWithTimeout 以 Task 竞速 10 秒断言——
/// 未修代码上以「超时未返回」失败(RED),不会拖死常驻套件(不用 xunit Timeout
/// 属性:xunit 2.x 对其支持口径不一致;竞速断言失败信息明确可控)。
/// 期望段谱推导(静音/等能量时严格 &lt; 使首窗胜出,切点=窗下界+step/2):
/// 16kHz 时 step=320、首窗中点偏移=160 帧;切点钳到段首后每轮至少前进 160 帧,
/// 直至剩余帧 ≤ maxFrames+2s 尾段并入。
/// </summary>
public sealed class WavSegmenterSmallMaxSecondsTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    // ---------- 构造辅助(与 WavSegmenterBoundaryTests 同形态,自带一份不跨类耦合) ----------

    private static byte[] MakeWav(int sampleRate, int channels, short[] interleavedSamples)
    {
        var data = new byte[interleavedSamples.Length * 2];
        for (var i = 0; i < interleavedSamples.Length; i++)
            BitConverter.GetBytes(interleavedSamples[i]).CopyTo(data, i * 2);

        var fmt = new byte[16];
        BitConverter.GetBytes((short)1).CopyTo(fmt, 0);
        BitConverter.GetBytes((short)channels).CopyTo(fmt, 2);
        BitConverter.GetBytes(sampleRate).CopyTo(fmt, 4);
        BitConverter.GetBytes(sampleRate * channels * 2).CopyTo(fmt, 8);
        BitConverter.GetBytes((short)(channels * 2)).CopyTo(fmt, 12);
        BitConverter.GetBytes((short)16).CopyTo(fmt, 14);

        var wav = new byte[44 + data.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + data.Length).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        fmt.CopyTo(wav, 20);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(data.Length).CopyTo(wav, 40);
        data.CopyTo(wav, 44);
        return wav;
    }

    private static short[] Silence(int frames) => new short[frames];

    private static short[] Constant(int frames, short value)
    {
        var samples = new short[frames];
        Array.Fill(samples, value);
        return samples;
    }

    private static WavSegmenter.WavInfo Parse(byte[] wav)
    {
        WavSegmenter.TryParse(wav, out var info).Should().BeTrue();
        return info;
    }

    private static int SegmentFrames(byte[] segment)
    {
        var info = Parse(segment);
        return info.DataLength / (info.Channels * (info.BitsPerSample / 8));
    }

    private static short SampleAt(byte[] segment, int frame)
        => BitConverter.ToInt16(segment, 44 + frame * 2);

    /// <summary>超时护栏:Split 必须在 Guard 内返回,否则断言失败(RED 形态),不挂死套件。</summary>
    private static List<byte[]> SplitWithTimeout(byte[] wav, double maxSeconds, WavSegmenter.WavInfo? info = null)
    {
        var task = Task.Run(() => WavSegmenter.Split(wav, maxSeconds, info));
        var winner = Task.WhenAny(task, Task.Delay(Guard)).GetAwaiter().GetResult();
        winner.Should().BeSameAs(task,
            $"Split(maxSeconds={maxSeconds}) 应在 {Guard.TotalSeconds} 秒内返回(票103 死循环回归护栏)");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>守恒断言:段非空、无空段、总帧数与原音频逐帧守恒。</summary>
    private static void AssertConserved(List<byte[]> segments, int totalFrames)
    {
        segments.Should().NotBeEmpty();
        segments.Should().OnlyContain(s => SegmentFrames(s) > 0, "死循环形态为无限产出 0 帧空段,修复后每段必须前进");
        segments.Sum(SegmentFrames).Should().Be(totalFrames);
    }

    // ---------- 复现例(票100 登记形态) ----------

    [Fact] // Boundary: 参数下限域 — 票100 复现:maxSeconds=1.0 + 10s 静音 @16k(未修:60s 超时未返回)
    public void Split_MaxSeconds1_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, 1.0);
        AssertConserved(segments, 160000);
        // 推导段谱(依据 evidence-103/STATUS.md 协调者裁定二 2026-10-23):静音首窗胜出 best=lo+step/2,
        // 兜底钳制 Max(best, segmentStart+1) 只保前进不覆盖合法 best,每轮前进恰 step/2=160 帧;
        // 尾段阈值 = maxFrames+2s = 16000+32000 = 48000,start=112000(=160×700)时剩余 48000 并入尾段 → 700+1=701 段
        segments.Should().HaveCount(701);
        segments.Take(700).Should().OnlyContain(s => SegmentFrames(s) == 160);
        SegmentFrames(segments[700]).Should().Be(48000);
    }

    [Fact] // Boundary: 参数临界 — maxSeconds 恰 3(窗下界恰等于段首的域边界)
    public void Split_MaxSecondsExactly3_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, 3.0, Parse(wav));
        AssertConserved(segments, 160000);
        // 推导段谱(依据 evidence-103/STATUS.md 协调者裁定二 2026-10-23):同 MaxSeconds1 静音例,
        // 每轮前进恰 step/2=160 帧;尾段阈值 = maxFrames+2s = 48000+32000 = 80000,
        // start=80000(=160×500)时剩余 80000 并入尾段 → 500+1=501 段
        segments.Should().HaveCount(501);
        segments.Take(500).Should().OnlyContain(s => SegmentFrames(s) == 160);
        SegmentFrames(segments[500]).Should().Be(80000);
    }

    [Fact] // Boundary: 参数临界 — maxSeconds = 3−ε(2.999,窗下界越段首 16 帧)
    public void Split_MaxSecondsJustBelow3_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, 2.999, Parse(wav));
        AssertConserved(segments, 160000);
    }

    [Fact] // Boundary: 参数临界 — maxSeconds = 3+ε(3.001,窗下界恰在段首之后)
    public void Split_MaxSecondsJustAbove3_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, 3.001, Parse(wav));
        AssertConserved(segments, 160000);
    }

    [Fact] // Boundary: 极小正值 — maxSeconds=0.001(maxFrames=16 帧)
    public void Split_MaxSecondsTinyPositive_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, 0.001, Parse(wav));
        AssertConserved(segments, 160000);
    }

    [Fact] // Boundary: 0 值 — maxSeconds=0
    public void Split_MaxSecondsZero_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, 0.0, Parse(wav));
        AssertConserved(segments, 160000);
    }

    [Fact] // Boundary: 负值 — maxSeconds=−1(搜索窗仍部分落在段首之后)
    public void Split_MaxSecondsNegativeOne_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, -1.0, Parse(wav));
        AssertConserved(segments, 160000);
    }

    [Fact] // Boundary: 负值退化域 — maxSeconds=−5(hi≤lo 搜索循环不执行,靠返回值兜底钳制前进)
    public void Split_MaxSecondsNegativeFive_Silence_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Silence(160000));
        var segments = SplitWithTimeout(wav, -5.0, Parse(wav));
        AssertConserved(segments, 160000);
        // 推导段谱(依据协调者裁定二):hi≤lo 搜索不执行、best=target<段首,兜底切点 = 段首+1,
        // 尾段阈值 maxFrames+2s<0 并入条件恒不满足 → 恰 160000 段、每段 1 帧
        segments.Should().HaveCount(160000);
        segments.Should().OnlyContain(s => SegmentFrames(s) == 1);
    }

    [Fact] // Boundary: 非静音素材 — 恒定响度 + maxSeconds=1.0(等能量首窗胜出,段谱同静音)
    public void Split_MaxSeconds1_ConstantTone_ReturnsWithinTimeout()
    {
        var wav = MakeWav(16000, 1, Constant(160000, 1000));
        var segments = SplitWithTimeout(wav, 1.0, Parse(wav));
        AssertConserved(segments, 160000);
        // 段谱(依据 evidence-103/STATUS.md 协调者裁定二 2026-10-23):恒音等能量首窗胜出,同静音例
        // 每轮前进恰 step/2=160 帧;尾段阈值 48000,start=112000 时剩余并入尾段 → 701 段
        segments.Should().HaveCount(701);
        SampleAt(segments[0], 0).Should().Be(1000);
        SampleAt(segments[0], 159).Should().Be(1000);
        SampleAt(segments[700], 0).Should().Be(1000); // 尾段首帧 = 原帧 112000,内容按帧对位
    }

    [Fact] // Boundary: 非静音素材 — 能量递减 + maxSeconds=0(窗内末窗胜出,切点仍须前进)
    public void Split_MaxSecondsZero_DecreasingTone_ReturnsWithinTimeout()
    {
        var samples = new short[160000];
        for (var f = 0; f < samples.Length; f++) samples[f] = (short)(16000 - f / 10);
        var wav = MakeWav(16000, 1, samples);
        var segments = SplitWithTimeout(wav, 0.0, Parse(wav));
        AssertConserved(segments, 160000);
    }
}
