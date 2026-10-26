using System.Buffers.Binary;
using FluentAssertions;
using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票65(ARC-08d):PcmWav 纯字节逻辑补测(改前 0%)。
/// 覆盖:FromFloat32 头字段/样本钳位与量化、重采样升/降采样率与空/单样本 boundary、
/// IsWav 魔数与长度门槛、DurationSeconds 精确值与三种失败回落(非 WAV/字节率≤0/data≤0)。
/// </summary>
public sealed class PcmWavTests
{
    private static short SampleAt(byte[] wav, int i)
        => BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2));

    private static int DataBytes(byte[] wav)
        => BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));

    [Fact] // 标准头:RIFF/WAVE/fmt/data + 16k/单声道/16bit/字节率 32000
    public void FromFloat32_HeaderFields()
    {
        var wav = PcmWav.FromFloat32([0f, 0.5f], 16000);
        System.Text.Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
        System.Text.Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
        System.Text.Encoding.ASCII.GetString(wav, 12, 4).Should().Be("fmt ");
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)).Should().Be(1); // PCM
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)).Should().Be(1); // 单声道
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)).Should().Be(16000);
        BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)).Should().Be(32000);
        BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)).Should().Be(16);
        DataBytes(wav).Should().Be(4);
        wav.Length.Should().Be(48);
    }

    [Theory] // 样本量化:0/满幅/半幅 + 超幅钳位(boundary ±1 之外)
    [InlineData(0f, 0)]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32767)]
    [InlineData(0.5f, 16384)]
    [InlineData(-0.5f, -16384)]
    [InlineData(2f, 32767)]   // >1 钳位
    [InlineData(-2f, -32767)] // <-1 钳位
    public void FromFloat32_SampleQuantization(float input, int expected)
        => SampleAt(PcmWav.FromFloat32([input], 16000), 0).Should().Be((short)expected);

    [Fact] // 空样本:只有 44 字节头,data 长度 0
    public void FromFloat32_EmptySamples_HeaderOnly()
    {
        var wav = PcmWav.FromFloat32([], 16000);
        wav.Length.Should().Be(44);
        DataBytes(wav).Should().Be(0);
    }

    [Fact] // 升采样 8k→16k:样本数翻倍,线性插值中点
    public void FromFloat32_Upsample8k_DoublesSamples()
    {
        var wav = PcmWav.FromFloat32([0f, 1f, 0f, -1f], 8000);
        DataBytes(wav).Should().Be(16); // 8 个样本
        SampleAt(wav, 0).Should().Be(0);
        SampleAt(wav, 1).Should().Be(16384); // 0 与 1 的中点
        SampleAt(wav, 2).Should().Be(32767);
    }

    [Fact] // 降采样 32k→16k:样本数减半,取偶数位
    public void FromFloat32_Downsample32k_HalvesSamples()
    {
        var wav = PcmWav.FromFloat32([0f, 0.2f, 0.4f, 0.6f], 32000);
        DataBytes(wav).Should().Be(4); // 2 个样本
        SampleAt(wav, 0).Should().Be(0);
        SampleAt(wav, 1).Should().Be((short)13107); // 0.4 * 32767 ≈ 13106.8 → 13107
    }

    [Fact] // boundary:单样本升采样(outLen=2,尾索引钳到末位不越界)
    public void FromFloat32_SingleSampleUpsample_NoOverflow()
    {
        var wav = PcmWav.FromFloat32([0.5f], 8000);
        DataBytes(wav).Should().Be(4);
        SampleAt(wav, 0).Should().Be(16384);
        SampleAt(wav, 1).Should().Be(16384);
    }

    [Fact] // boundary:极短输入降采样 outLen=0 → 空 data(不抛)
    public void FromFloat32_TinyInputDownsample_EmptyData()
    {
        var wav = PcmWav.FromFloat32([0.5f], 48000);
        DataBytes(wav).Should().Be(0);
    }

    [Theory] // IsWav:魔数与长度门槛(>12)
    [InlineData(true)]
    [InlineData(false)]
    public void IsWav_Variants(bool _)
    {
        var wav = PcmWav.FromFloat32([0f], 16000);
        PcmWav.IsWav(wav).Should().BeTrue();
        PcmWav.IsWav(wav[..12]).Should().BeFalse();      // 恰 12 字节不 >12
        PcmWav.IsWav(wav[..13]).Should().BeTrue();       // 13 字节魔数齐
        PcmWav.IsWav([]).Should().BeFalse();
        var bad = (byte[])wav.Clone(); bad[0] = (byte)'X';
        PcmWav.IsWav(bad).Should().BeFalse();
        var bad2 = (byte[])wav.Clone(); bad2[8] = (byte)'X';
        PcmWav.IsWav(bad2).Should().BeFalse();
    }

    [Fact] // 时长精确值:16000 样本 @16k = 恰 1.000 秒(计费口径,见类注释)
    public void DurationSeconds_ExactFromHeader()
    {
        var wav = PcmWav.FromFloat32(new float[16000], 16000);
        PcmWav.DurationSeconds(wav).Should().Be(1.0);
        var half = PcmWav.FromFloat32(new float[8000], 16000);
        PcmWav.DurationSeconds(half).Should().Be(0.5);
    }

    [Fact] // 失败回落:非 WAV / 过短 → null(不编数)
    public void DurationSeconds_NotWav_ReturnsNull()
    {
        PcmWav.DurationSeconds(new byte[100]).Should().BeNull();
        PcmWav.DurationSeconds(PcmWav.FromFloat32([0f], 16000)[..20]).Should().BeNull();
    }

    [Fact] // 失败回落:字节率 ≤0 / data 长度 ≤0 → null
    public void DurationSeconds_BadHeaderNumbers_ReturnsNull()
    {
        var wav = PcmWav.FromFloat32([0f, 0f], 16000);
        var zeroRate = (byte[])wav.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(zeroRate.AsSpan(28), 0);
        PcmWav.DurationSeconds(zeroRate).Should().BeNull();

        var empty = PcmWav.FromFloat32([], 16000); // data=0
        PcmWav.DurationSeconds(empty).Should().BeNull();
    }
}
