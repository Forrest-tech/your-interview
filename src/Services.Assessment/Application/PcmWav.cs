using System.Buffers.Binary;

namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// 音频转码:浏览器录音 → Azure 能吃的 PCM WAV。
///
/// 为什么必须做这一步:
///   浏览器 MediaRecorder 默认输出 webm/opus(或 mp4/aac),
///   而 Azure Speech 的 REST 端点只接受 PCM WAV(16kHz / 16bit / 单声道)。
///   直接上传 webm 会得到 400,且错误信息不直观。
///
/// 实现选择:只认"已经是 WAV 但采样率/位深/声道不对"的情况做头部处理,
/// 真正的 webm→PCM 解码交给前端的 Web Audio API 完成
/// (AudioContext.decodeAudioData 在浏览器里是原生能力,比服务端拉 ffmpeg 依赖干净得多)。
/// 这样服务端不需要 ffmpeg,部署面更小。
/// </summary>
public static class PcmWav
{
    private const int TargetSampleRate = 16000;
    private const short TargetChannels = 1;
    private const short TargetBits = 16;

    /// <summary>
    /// 把 float PCM 样本(前端 Web Audio 解出的原始数据)打包成标准 WAV。
    /// 前端拿到 AudioBuffer 后,把 getChannelData(0) 的 Float32Array 作为 base64 发过来。
    /// </summary>
    public static byte[] FromFloat32(float[] samples, int sampleRate)
    {
        // 先重采样到 16k(线性插值 —— 对语音评测足够,且无外部依赖)
        var resampled = Resample(samples, sampleRate, TargetSampleRate);

        var dataBytes = resampled.Length * 2;
        var buffer = new byte[44 + dataBytes];

        // ---- RIFF 头 ----
        "RIFF"u8.CopyTo(buffer.AsSpan(0));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(buffer.AsSpan(8));

        // ---- fmt 块 ----
        "fmt "u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16);       // 块大小
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1);        // PCM
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), TargetChannels);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), TargetSampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28),
            TargetSampleRate * TargetChannels * TargetBits / 8);              // 字节率
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32),
            (short)(TargetChannels * TargetBits / 8));                        // 块对齐
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), TargetBits);

        // ---- data 块 ----
        "data"u8.CopyTo(buffer.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataBytes);

        for (var i = 0; i < resampled.Length; i++)
        {
            // 钳位后转 16bit 有符号
            var v = Math.Clamp(resampled[i], -1f, 1f);
            var s = (short)Math.Round(v * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(44 + i * 2), s);
        }

        return buffer;
    }

    /// <summary>线性插值重采样。语音评测不需要高级滤波器,简单可靠优先。</summary>
    private static float[] Resample(float[] input, int fromRate, int toRate)
    {
        if (fromRate == toRate || input.Length == 0) return input;

        var ratio = (double)fromRate / toRate;
        var outLen = (int)Math.Floor(input.Length / ratio);
        if (outLen <= 0) return [];

        var output = new float[outLen];
        for (var i = 0; i < outLen; i++)
        {
            var src = i * ratio;
            var i0 = (int)Math.Floor(src);
            var i1 = Math.Min(i0 + 1, input.Length - 1);
            var frac = (float)(src - i0);
            output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }
        return output;
    }

    /// <summary>
    /// 判断一段字节是不是 WAV(以 RIFF/WAVE 开头)。
    /// 前端若能直接给 WAV 就省掉解码,所以先探一下。
    /// </summary>
    public static bool IsWav(byte[] data) =>
        data.Length > 12
        && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
        && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';

    /// <summary>
    /// 从 WAV 头算出音频秒数。
    ///
    /// ★ 第三十四轮(Forrest:"给我准确的消耗了多少"):
    ///   Azure 发音评估的**真实计费单位是音频时长**(按小时计价),
    ///   不是 token。这个值可以从我们自己构造的 WAV 头 100% 精确算出 ——
    ///   字节率(offset 28)与 data 块长度(offset 40)相除即可,无需调 Azure。
    ///   所以它是**精确值**,不是估算。
    ///
    /// 解析失败(非标准头)返回 null —— 拿不到就如实为空,不编一个数。
    /// </summary>
    public static double? DurationSeconds(byte[] wav)
    {
        // 44 字节标准头:字节率 @28,data 长度 @40
        if (wav.Length < 44 || !IsWav(wav)) return null;
        var byteRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28));
        if (byteRate <= 0) return null;
        var dataBytes = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        if (dataBytes <= 0) return null;
        return Math.Round((double)dataBytes / byteRate, 3);
    }
}
