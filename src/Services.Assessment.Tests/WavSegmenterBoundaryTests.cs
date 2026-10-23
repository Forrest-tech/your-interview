using System.Text;
using FluentAssertions;
using YourInterview.Services.Assessment.Application;
using Xunit;

namespace YourInterview.Services.Assessment.Tests;

/// <summary>
/// 票100:WavSegmenter 边界测试(改前 0%:internal 且 Services.Assessment 无 IVT 不可达;
/// 本票新增 Properties/AssemblyInfo.cs IVT 后直测)。
/// 期望口径全部按 evidence-100/STATUS.md 读码定明锁定:
/// TryParse 魔数/44 字节门槛/chunk 遍历(size&lt;0 中断、fmt body+16 边界、data 取 min、末胜出、奇数对齐填充)/
/// 末检(sampleRate&gt;0 &amp;&amp; channels&gt;0 &amp;&amp; bits==16);DurationSeconds 整数分母;
/// Split 解析失败与 totalFrames==0 同引用回退、尾段 (max, max+2s] 并入、切点窗 [target−3s, target+2s]
/// 最低能量最早窗中点、44 字节头重装、不足一帧尾字节丢弃。
/// 每例以行内注释标注其 Boundary 类别。
/// </summary>
public sealed class WavSegmenterBoundaryTests
{
    // ---------- 构造辅助(标准 44 字节头布局,与 BuildSegment 重装形态逐字一致) ----------

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts) { part.CopyTo(result, offset); offset += part.Length; }
        return result;
    }

    private static byte[] Chunk(string id, int declaredSize, byte[] body, bool padAfter = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes(id));
        bytes.AddRange(BitConverter.GetBytes(declaredSize));
        bytes.AddRange(body);
        if (padAfter) bytes.Add(0);
        return bytes.ToArray();
    }

    private static byte[] FmtBody(int channels, int sampleRate, int bits, int extraBytes = 0)
    {
        var body = new byte[16 + extraBytes];
        BitConverter.GetBytes((short)1).CopyTo(body, 0);                              // PCM
        BitConverter.GetBytes((short)channels).CopyTo(body, 2);                       // 声道 @+2
        BitConverter.GetBytes(sampleRate).CopyTo(body, 4);                            // 采样率 @+4
        BitConverter.GetBytes(sampleRate * channels * (bits / 8)).CopyTo(body, 8);    // byteRate
        BitConverter.GetBytes((short)(channels * (bits / 8))).CopyTo(body, 12);       // blockAlign
        BitConverter.GetBytes((short)bits).CopyTo(body, 14);                          // 位深 @+14
        return body;
    }

    private static byte[] FmtChunk(int channels, int sampleRate, int bits, int extraBytes = 0)
        => Chunk("fmt ", 16 + extraBytes, FmtBody(channels, sampleRate, bits, extraBytes));

    private static byte[] RiffWrap(params byte[][] chunks)
    {
        var rest = Concat(chunks);
        return Concat(Encoding.ASCII.GetBytes("RIFF"),
                      BitConverter.GetBytes(4 + rest.Length),
                      Encoding.ASCII.GetBytes("WAVE"),
                      rest);
    }

    private static byte[] MakeWavRaw(int sampleRate, int channels, int bits, byte[] data)
        => RiffWrap(FmtChunk(channels, sampleRate, bits),
                    Chunk("data", data.Length, data, padAfter: (data.Length & 1) == 1));

    private static byte[] MakeWav(int sampleRate, int channels, short[] interleavedSamples, int bits = 16)
    {
        var data = new byte[interleavedSamples.Length * 2];
        for (var i = 0; i < interleavedSamples.Length; i++)
            BitConverter.GetBytes(interleavedSamples[i]).CopyTo(data, i * 2);
        return MakeWavRaw(sampleRate, channels, bits, data);
    }

    private static short[] Silence(int frames, int channels = 1) => new short[frames * channels];

    private static short[] Constant(int frames, short value, int channels = 1)
    {
        var samples = new short[frames * channels];
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

    private static short SampleAt(byte[] segment, int frame, int channels = 1)
        => BitConverter.ToInt16(segment, 44 + frame * channels * 2);

    // ---------- TryParse ----------

    [Fact] // Boundary: 空/0 字节输入
    public void TryParse_Empty_ReturnsFalse()
    {
        var wav = Array.Empty<byte>();
        WavSegmenter.TryParse(wav, out var info).Should().BeFalse();
        info.Buffer.Should().BeSameAs(wav); // 失败时 out 为占位 WavInfo,Buffer 仍指入参
        info.SampleRate.Should().Be(0);
    }

    [Fact] // Boundary: 文件类 — 长度临界(44 字节门槛 −1)
    public void TryParse_43Bytes_ReturnsFalse()
    {
        var wav = MakeWav(16000, 1, Silence(10)).Take(43).ToArray();
        wav.Length.Should().Be(43);
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 损坏格式 — RIFF 魔数错
    public void TryParse_BadRiffMagic_ReturnsFalse()
    {
        var wav = MakeWav(16000, 1, Silence(10));
        wav[0] = (byte)'X';
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 损坏格式 — WAVE 魔数错
    public void TryParse_BadWaveMagic_ReturnsFalse()
    {
        var wav = MakeWav(16000, 1, Silence(10));
        wav[8] = (byte)'X';
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 长度临界(恰 44 字节,头完整 data 为空)
    public void TryParse_HeaderOnly44_ReturnsTrueWithZeroData()
    {
        var wav = MakeWav(16000, 2, Silence(0));
        wav.Length.Should().Be(44);
        var info = Parse(wav);
        info.SampleRate.Should().Be(16000);
        info.Channels.Should().Be(2);
        info.BitsPerSample.Should().Be(16);
        info.DataOffset.Should().Be(44);
        info.DataLength.Should().Be(0);
    }

    [Fact] // Boundary: 损坏格式 — 无 data chunk
    public void TryParse_FmtOnlyNoData_ReturnsFalse()
    {
        var wav = RiffWrap(FmtChunk(1, 16000, 16));
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 损坏格式 — chunk size<0 中断全解析(其后 data 不可见)
    public void TryParse_NegativeChunkSize_BreaksBeforeData_ReturnsFalse()
    {
        var wav = RiffWrap(
            FmtChunk(1, 16000, 16),
            Chunk("JUNK", -1, Array.Empty<byte>()),
            Chunk("data", 4, new byte[4]));
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 损坏格式 — size<0 只中断其后 chunk,此前已捕获的 fmt/data 保留
    public void TryParse_NegativeChunkSizeAfterData_KeepsEarlierChunks()
    {
        var wav = RiffWrap(
            FmtChunk(1, 8000, 16),
            Chunk("data", 4, new byte[4]),
            Chunk("JUNK", -5, Array.Empty<byte>()),
            FmtChunk(1, 44100, 16)); // 中断后不可见,采样率仍为首个 fmt 的 8000
        var info = Parse(wav);
        info.SampleRate.Should().Be(8000);
        info.DataLength.Should().Be(4);
    }

    [Fact] // Boundary: fmt 字段偏移与 body+16 边界 — body+16 越界则 fmt 被跳过
    public void TryParse_FmtBodyPlus16BeyondLength_SkipsFmt_ReturnsFalse()
    {
        // data 先行(4 字节),fmt 头在 24,body 起 32,文件恰 44 字节:32+16=48 > 44
        var wav = RiffWrap(
            Chunk("data", 4, new byte[4]),
            Concat(Encoding.ASCII.GetBytes("fmt "), BitConverter.GetBytes(16), FmtBody(1, 16000, 16).Take(12).ToArray()));
        wav.Length.Should().Be(44);
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: fmt body+16 恰等于文件长(边界值本身)则正常读取
    public void TryParse_FmtBodyPlus16ExactlyLength_ReturnsTrue()
    {
        // data 空头在前(12..19),fmt 头 20..27,body 28..43:28+16=44 == 文件长
        var wav = RiffWrap(
            Chunk("data", 0, Array.Empty<byte>()),
            FmtChunk(1, 16000, 16));
        wav.Length.Should().Be(44);
        var info = Parse(wav);
        info.SampleRate.Should().Be(16000);
        info.DataLength.Should().Be(0);
    }

    [Fact] // Boundary: data 声明长于实际 — 取 min(声明, 实际)
    public void TryParse_DataDeclaredLongerThanActual_TakesActual()
    {
        var wav = RiffWrap(
            FmtChunk(1, 16000, 16),
            Chunk("data", 100000, new byte[100]));
        var info = Parse(wav);
        info.DataLength.Should().Be(100);
    }

    [Fact] // Boundary: data 声明短于实际 — 取 min(声明, 实际)
    public void TryParse_DataDeclaredShorterThanActual_TakesDeclared()
    {
        var wav = RiffWrap(
            FmtChunk(1, 16000, 16),
            Chunk("data", 50, new byte[100]));
        var info = Parse(wav);
        info.DataLength.Should().Be(50);
    }

    [Fact] // Boundary: 奇数 size chunk 有 1 字节对齐填充 — 后继 chunk 对齐读取
    public void TryParse_OddSizeChunkWithPad_ParsesFollowingChunks()
    {
        var wav = RiffWrap(
            Chunk("JUNK", 3, new byte[] { 1, 2, 3 }, padAfter: true),
            FmtChunk(1, 16000, 16),
            Chunk("data", 4, new byte[4]));
        var info = Parse(wav);
        info.SampleRate.Should().Be(16000);
        info.DataLength.Should().Be(4);
    }

    [Fact] // Boundary: 奇数 size chunk 缺对齐填充 — 后继 chunk 错位不可读
    public void TryParse_OddSizeChunkWithoutPad_Misaligns_ReturnsFalse()
    {
        var wav = RiffWrap(
            Chunk("JUNK", 3, new byte[] { 1, 2, 3 }),
            FmtChunk(1, 16000, 16),
            Chunk("data", 4, new byte[4]));
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: chunk 顺序无关 — data 在 fmt 之前
    public void TryParse_DataBeforeFmt_ReturnsTrue()
    {
        var wav = RiffWrap(
            Chunk("data", 6, new byte[6]),
            FmtChunk(2, 22050, 16));
        var info = Parse(wav);
        info.SampleRate.Should().Be(22050);
        info.Channels.Should().Be(2);
        info.DataOffset.Should().Be(20);
        info.DataLength.Should().Be(6);
    }

    [Fact] // Boundary: chunk 末胜出 — 两个 fmt 取最后一个
    public void TryParse_TwoFmtChunks_LastWins()
    {
        var wav = RiffWrap(
            FmtChunk(1, 8000, 16),
            FmtChunk(2, 16000, 16),
            Chunk("data", 4, new byte[4]));
        var info = Parse(wav);
        info.SampleRate.Should().Be(16000);
        info.Channels.Should().Be(2);
    }

    [Fact] // Boundary: chunk 末胜出 — 两个 data 取最后一个的偏移与长度
    public void TryParse_TwoDataChunks_LastWins()
    {
        var wav = RiffWrap(
            Chunk("data", 4, new byte[4]),
            FmtChunk(1, 16000, 16),
            Chunk("data", 8, new byte[8]));
        var info = Parse(wav);
        info.DataOffset.Should().Be(56); // 12 + data块20 + fmt块24 + data头8
        info.DataLength.Should().Be(8);
    }

    [Fact] // Boundary: fmt 声明长于 16(WAVEFORMATEX 形态)— 只读前 16 字节字段
    public void TryParse_FmtSize18_ReadsFirst16Bytes()
    {
        var wav = RiffWrap(
            FmtChunk(2, 44100, 16, extraBytes: 2),
            Chunk("data", 4, new byte[4]));
        var info = Parse(wav);
        info.SampleRate.Should().Be(44100);
        info.Channels.Should().Be(2);
        info.BitsPerSample.Should().Be(16);
    }

    [Fact] // Boundary: RIFF 总长字段不校验 — 置 0 仍可解析
    public void TryParse_RiffSizeFieldIgnored()
    {
        var wav = MakeWav(16000, 1, Silence(4));
        BitConverter.GetBytes(0).CopyTo(wav, 4);
        Parse(wav).SampleRate.Should().Be(16000);
    }

    [Fact] // Boundary: 采样率异常 — sampleRate=0 拒绝
    public void TryParse_SampleRateZero_ReturnsFalse()
    {
        var wav = MakeWav(0, 1, Silence(4));
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 采样率异常 — sampleRate 为负拒绝
    public void TryParse_SampleRateNegative_ReturnsFalse()
    {
        var wav = MakeWav(-16000, 1, Silence(4));
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Fact] // Boundary: 声道异常 — channels=0 拒绝
    public void TryParse_ChannelsZero_ReturnsFalse()
    {
        var wav = MakeWavRaw(16000, 0, 16, new byte[8]);
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    [Theory] // Boundary: 位深异常 — bits≠16 一律拒绝(仅支持 16bit PCM)
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(32)]
    public void TryParse_BitsNot16_ReturnsFalse(int bits)
    {
        var wav = MakeWavRaw(16000, 1, bits, new byte[16]);
        WavSegmenter.TryParse(wav, out _).Should().BeFalse();
    }

    // ---------- DurationSeconds ----------

    [Fact] // Boundary: 时长精确值 — 单声道 16k×1s
    public void DurationSeconds_MonoOneSecond()
    {
        var info = Parse(MakeWav(16000, 1, Silence(16000)));
        WavSegmenter.DurationSeconds(info).Should().Be(1.0);
    }

    [Fact] // Boundary: 时长精确值 — 双声道 48k×1s(分母含声道数)
    public void DurationSeconds_StereoOneSecond()
    {
        var info = Parse(MakeWav(48000, 2, Silence(48000, channels: 2)));
        WavSegmenter.DurationSeconds(info).Should().Be(1.0);
    }

    [Fact] // Boundary: 时长分数值 — 1000 字节 @16k 单声道 = 1/32 秒(精确二进制分数)
    public void DurationSeconds_Fractional()
    {
        var info = new WavSegmenter.WavInfo(16000, 1, 16, Array.Empty<byte>(), 0, 1000);
        WavSegmenter.DurationSeconds(info).Should().Be(0.03125);
    }

    [Fact] // Boundary: 0 值 — DataLength=0 时长为 0
    public void DurationSeconds_ZeroDataLength()
    {
        var info = Parse(MakeWav(16000, 1, Silence(0)));
        WavSegmenter.DurationSeconds(info).Should().Be(0.0);
    }

    [Fact] // Boundary: 整数分母口径 — DataLength 不整除帧长时按原始字节数算,不取整到帧
    public void DurationSeconds_OddDataLengthNotFlooredToFrame()
    {
        var info = new WavSegmenter.WavInfo(16000, 1, 16, Array.Empty<byte>(), 0, 7);
        WavSegmenter.DurationSeconds(info).Should().Be(7.0 / 32000);
    }

    // ---------- Split ----------

    [Fact] // Boundary: 损坏格式回退 — 解析失败(垃圾字节)同引用返回原数组
    public void Split_Unparseable_ReturnsSameReference()
    {
        var wav = new byte[10];
        var segments = WavSegmenter.Split(wav, 28.0);
        segments.Should().HaveCount(1);
        segments[0].Should().BeSameAs(wav);
    }

    [Fact] // Boundary: 损坏格式回退 — bits=8 解析失败同引用返回
    public void Split_Bits8_ReturnsSameReference()
    {
        var wav = MakeWavRaw(16000, 1, 8, new byte[32000]);
        var segments = WavSegmenter.Split(wav, 28.0);
        segments.Should().HaveCount(1);
        segments[0].Should().BeSameAs(wav);
    }

    [Fact] // Boundary: 0 值回退 — data 为空(totalFrames=0)同引用返回
    public void Split_HeaderOnly_ReturnsSameReference()
    {
        var wav = MakeWav(16000, 1, Silence(0));
        var segments = WavSegmenter.Split(wav, 28.0);
        segments.Should().HaveCount(1);
        segments[0].Should().BeSameAs(wav);
    }

    [Fact] // Boundary: 0 值回退 — 显式传入 totalFrames=0 的 parsed 同样同引用
    public void Split_ExplicitZeroFrameInfo_ReturnsSameReference()
    {
        var wav = MakeWav(16000, 1, Silence(100));
        var info = new WavSegmenter.WavInfo(16000, 1, 16, wav, 44, 0);
        var segments = WavSegmenter.Split(wav, 28.0, info);
        segments.Should().HaveCount(1);
        segments[0].Should().BeSameAs(wav);
    }

    [Fact] // Boundary: 单段 — 短音频整段重装,内容与标准头原文件逐字一致
    public void Split_ShortAudio_SingleSegmentEqualsOriginal()
    {
        var samples = new short[16000];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(i % 100 - 50);
        var wav = MakeWav(16000, 1, samples);
        var segments = WavSegmenter.Split(wav, 28.0);
        segments.Should().HaveCount(1);
        segments[0].Should().Equal(wav);
    }

    [Fact] // Boundary: 单段临界 — 时长恰等于 maxSeconds 不切分
    public void Split_ExactlyMaxSeconds_SingleSegment()
    {
        var wav = MakeWav(1000, 1, Silence(28000));
        var segments = WavSegmenter.Split(wav, 28.0, Parse(wav));
        segments.Should().HaveCount(1);
        segments[0].Should().Equal(wav);
    }

    [Theory] // Boundary: 尾段并入临界 — 时长在 (max, max+2s] 整段并入,越界 +1 帧才切
    [InlineData(2000, 1)] // == max
    [InlineData(2001, 1)] // max + 1 帧(∈ (max, max+2s])
    [InlineData(4000, 1)] // == max + 2s(并入上限)
    [InlineData(4001, 2)] // max + 2s + 1 帧 → 切分
    public void Split_TailMergeBoundary(int totalFrames, int expectedSegments)
    {
        var wav = MakeWav(1000, 1, Silence(totalFrames));
        var segments = WavSegmenter.Split(wav, 2.0, Parse(wav));
        segments.Should().HaveCount(expectedSegments);
        segments.Sum(SegmentFrames).Should().Be(totalFrames); // 帧无丢失无重复
    }

    [Fact] // Boundary: 尾段并入临界帧数 — max+2s+1 帧时首切落在最早静音窗中点,余下整段为尾段
    public void Split_TailMergeBoundary_FrameCounts()
    {
        // sr=1000,max=2s:全静音 → 切点窗 lo=0 起,首窗中点 = step/2 = 10 帧
        var wav = MakeWav(1000, 1, Silence(4001));
        var segments = WavSegmenter.Split(wav, 2.0, Parse(wav));
        segments.Should().HaveCount(2);
        SegmentFrames(segments[0]).Should().Be(10);
        SegmentFrames(segments[1]).Should().Be(3991);
    }

    [Fact] // Boundary: 切点窗口 — [target−3s, target+2s] 内最低能量窗,段在此切开且数据按帧对位
    public void Split_QuietCut_PicksLowestEnergyWindow()
    {
        // sr=100,max=10s(target=1000 帧,窗 [700,1200]);全程响度 1000,仅 [800,900) 静音
        var samples = Constant(2000, 1000);
        for (var f = 800; f < 900; f++) samples[f] = 0;
        var wav = MakeWav(100, 1, samples);
        var segments = WavSegmenter.Split(wav, 10.0, Parse(wav));
        segments.Should().HaveCount(2);
        SegmentFrames(segments[0]).Should().Be(801); // 首个静音窗 [800,802) 中点 800+step/2
        SegmentFrames(segments[1]).Should().Be(1199);
        SampleAt(segments[0], 799).Should().Be(1000);
        SampleAt(segments[0], 800).Should().Be(0);
        SampleAt(segments[1], 98).Should().Be(0);    // 原帧 899 仍静音
        SampleAt(segments[1], 99).Should().Be(1000); // 原帧 900 恢复响度
    }

    [Fact] // Boundary: 切点窗口 — 同能量(并列静音区)取最早窗
    public void Split_QuietCut_TiePicksEarliestWindow()
    {
        var samples = Constant(2000, 1000);
        for (var f = 800; f < 820; f++) samples[f] = 0;
        for (var f = 900; f < 920; f++) samples[f] = 0;
        var wav = MakeWav(100, 1, samples);
        var segments = WavSegmenter.Split(wav, 10.0, Parse(wav));
        SegmentFrames(segments[0]).Should().Be(801); // 最早静音窗中点,非 901
    }

    [Fact] // Boundary: 切点窗口 — 全静音时严格 < 口径使首窗胜出(最早窗中点)
    public void Split_QuietCut_AllSilence_PicksEarliestWindowMidpoint()
    {
        var wav = MakeWav(100, 1, Silence(2000));
        var segments = WavSegmenter.Split(wav, 10.0, Parse(wav));
        segments.Should().HaveCount(3);
        SegmentFrames(segments[0]).Should().Be(701); // 窗左界 700 + step/2(1)
        SegmentFrames(segments[1]).Should().Be(701); // 第二轮窗左界 1401 + 1,切点 1402
        SegmentFrames(segments[2]).Should().Be(598); // 尾段并入
    }

    [Fact] // Boundary: 切点窗口 — 能量单调递减时取窗内最后一窗中点(hi 边界 f+step≤hi)
    public void Split_QuietCut_DecreasingEnergy_PicksLastWindowInRange()
    {
        var samples = new short[2000];
        for (var f = 0; f < samples.Length; f++) samples[f] = (short)(2000 - f);
        var wav = MakeWav(100, 1, samples);
        var segments = WavSegmenter.Split(wav, 10.0, Parse(wav));
        SegmentFrames(segments[0]).Should().Be(1199); // 末窗 [1198,1200) 中点
        SegmentFrames(segments[1]).Should().Be(801);
    }

    [Fact] // Boundary: 多段谱 — 20s 静音 @max=5s 反复切分,段数/每段帧数/总帧守恒
    public void Split_MultiSegment_Silence()
    {
        // 每轮切点 = 窗左界(start+max−3s)+step/2 → 每段 2010 帧,尾段 5930 帧(≤ max+2s)
        var wav = MakeWav(1000, 1, Silence(20000));
        var segments = WavSegmenter.Split(wav, 5.0, Parse(wav));
        segments.Should().HaveCount(8);
        segments.Take(7).Should().OnlyContain(s => SegmentFrames(s) == 2010);
        SegmentFrames(segments[7]).Should().Be(5930);
        segments.Sum(SegmentFrames).Should().Be(20000);
    }

    [Fact] // Boundary: 超长 — 120s @16k、max=28s(生产口径)切 5 段,每段 ≤30s 且总帧守恒
    public void Split_LongAudio_120Seconds()
    {
        var wav = MakeWav(16000, 1, Silence(120 * 16000));
        var segments = WavSegmenter.Split(wav, 28.0, Parse(wav));
        segments.Should().HaveCount(5);
        segments.Take(4).Should().OnlyContain(s => SegmentFrames(s) == 400160);
        SegmentFrames(segments[4]).Should().Be(319360);
        segments.Sum(SegmentFrames).Should().Be(120 * 16000);
        foreach (var segment in segments)
            WavSegmenter.DurationSeconds(Parse(segment)).Should().BeLessThanOrEqualTo(30.0);
    }

    [Fact] // Boundary: 不足一帧尾字节丢弃 — data 7 字节(3 帧半)重装后只剩 3 帧 6 字节
    public void Split_TrailingPartialFrameByte_Dropped()
    {
        var data = new byte[] { 1, 0, 2, 0, 3, 0, 0xFF };
        var wav = MakeWavRaw(16000, 1, 16, data);
        var segments = WavSegmenter.Split(wav, 28.0, Parse(wav));
        segments.Should().HaveCount(1);
        segments[0].Length.Should().Be(44 + 6);
        Parse(segments[0]).DataLength.Should().Be(6);
        SampleAt(segments[0], 2).Should().Be(3);
    }

    [Fact] // Boundary: 双声道帧长口径 — frameBytes=4,切分/重装按帧不按字节
    public void Split_Stereo_SingleSegmentEqualsOriginal()
    {
        var samples = new short[16000 * 2];
        for (var f = 0; f < 16000; f++) { samples[f * 2] = 100; samples[f * 2 + 1] = -100; }
        var wav = MakeWav(8000, 2, samples);
        var segments = WavSegmenter.Split(wav, 28.0, Parse(wav));
        segments.Should().HaveCount(1);
        segments[0].Should().Equal(wav);
        SegmentFrames(segments[0]).Should().Be(16000);
    }

    [Fact] // Boundary: 44 字节头重装 — 段头 RIFF 长度/字节率/data 长度字段与段数据自洽
    public void Split_SegmentHeaderFields()
    {
        var wav = MakeWav(100, 1, Constant(2000, 1000));
        var segments = WavSegmenter.Split(wav, 10.0, Parse(wav));
        var segment = segments[0];
        var info = Parse(segment);
        Encoding.ASCII.GetString(segment, 0, 4).Should().Be("RIFF");
        Encoding.ASCII.GetString(segment, 36, 4).Should().Be("data");
        BitConverter.ToInt32(segment, 4).Should().Be(36 + info.DataLength);   // RIFF 长度
        BitConverter.ToInt32(segment, 28).Should().Be(100 * 2);               // byteRate = sr×frameBytes
        BitConverter.ToInt16(segment, 32).Should().Be(2);                     // blockAlign
        BitConverter.ToInt32(segment, 40).Should().Be(info.DataLength);       // data 块长
        segment.Length.Should().Be(44 + info.DataLength);
    }
}
