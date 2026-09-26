using System.Text;

namespace YourInterview.Services.Assessment.Application;

/// <summary>
/// PCM WAV 解析 + 静音感知分段。
///
/// ★ 2026-09-26(Forrest 报"评分只有一半")为长音频发音评估而生:
///   Azure STT 短音频 REST 接口请求最多 60 秒音频,且官方文档明确
///   "发音评估音频不应超过 30 秒";超长音频 Azure **不报错、静默只处理
///   前 ~60 秒** → 后半篇全部记 Omission,Completeness/Recognition 腰斩
///   (实测 1:59 录音只评出前 59.5s)。所以后端在这里把长录音切成
///   ≤30s 的段,逐段送评再合并。
///
/// 切点策略:在目标切点 ±1 秒内找**能量最低的 20ms 窗**(句间停顿),
/// 尽量不把一个词劈成两半;找不到明显静音就按目标点硬切(兜底)。
///
/// 只支持 16bit PCM(前端 encodeWavBase64 / 服务端 PcmWav.FromFloat32
/// 产出的就是这个格式);其他格式解析失败时调用方回退单次评估 ——
/// 行为与旧版一致,不会更糟。
/// </summary>
internal static class WavSegmenter
{
    public sealed record WavInfo(
        int SampleRate, int Channels, int BitsPerSample,
        byte[] Buffer, int DataOffset, int DataLength);

    /// <summary>解析 RIFF/WAVE 头。仅支持 16bit PCM。</summary>
    public static bool TryParse(byte[] wav, out WavInfo info)
    {
        info = new WavInfo(0, 0, 0, wav, 0, 0);
        if (wav.Length < 44) return false;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return false;
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return false;

        var pos = 12;
        int sampleRate = 0, channels = 0, bits = 0, dataOff = -1, dataLen = 0;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (size < 0) break; // 头损坏,别越界乱走
            var body = pos + 8;
            if (id == "fmt " && body + 16 <= wav.Length)
            {
                channels = BitConverter.ToUInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                bits = BitConverter.ToUInt16(wav, body + 14);
            }
            else if (id == "data")
            {
                dataOff = body;
                dataLen = Math.Min(size, wav.Length - body);
            }
            pos = body + size + (size & 1); // RIFF 规定 chunk 按字(2 字节)对齐
        }

        if (dataOff < 0 || sampleRate <= 0 || channels <= 0 || bits != 16) return false;
        info = new WavInfo(sampleRate, channels, bits, wav, dataOff, dataLen);
        return true;
    }

    public static double DurationSeconds(WavInfo w) =>
        (double)w.DataLength / (w.SampleRate * w.Channels * (w.BitsPerSample / 8));

    /// <summary>
    /// 切段:每段 ≤ maxSeconds;尾段不足 maxSeconds+2s 时并入当前段
    /// (最多 +2s,仍在 30s 官方上限内)。切点在目标附近找静音处,尽量不劈词。
    /// </summary>
    public static List<byte[]> Split(byte[] wav, double maxSeconds, WavInfo? parsed = null)
    {
        if (parsed is null && !TryParse(wav, out parsed))
            return [wav]; // 非 16bit PCM:交回原样,由调用方走单次评估兜底
        var info = parsed!;

        var result = new List<byte[]>();
        int frameBytes = info.Channels * (info.BitsPerSample / 8);
        int totalFrames = info.DataLength / frameBytes;
        if (totalFrames == 0) { result.Add(wav); return result; }

        int maxFrames = (int)(maxSeconds * info.SampleRate);
        int start = 0;
        while (start < totalFrames)
        {
            var remaining = totalFrames - start;
            if (remaining <= maxFrames + 2 * info.SampleRate)
            {
                // 尾段:并入当前段,不再单独成段
                result.Add(BuildSegment(info, start, remaining));
                break;
            }
            var cut = FindQuietCut(info, start + maxFrames, totalFrames);
            result.Add(BuildSegment(info, start, cut - start));
            start = cut;
        }
        return result;
    }

    /// <summary>
    /// 在目标切点附近找 20ms 能量最低窗(句间停顿),返回切点帧号。
    /// 搜索窗:目标点**前 3 秒 ~ 后 2 秒** —— 往前多找是因为真实朗读的句间停顿
    /// 不一定恰好落在切点上,宁可切早一点(段更短)也要落在停顿处;
    /// 往后最多 2s 保证段长 ≤30s 官方上限。窗内没有真静音时取能量最低点兜底。
    /// </summary>
    private static int FindQuietCut(WavInfo w, int targetFrame, int totalFrames)
    {
        int back = 3 * w.SampleRate;                // 前移 3 秒
        int fwd = 2 * w.SampleRate;                 // 后移最多 2 秒(段长 ≤30s)
        int lo = Math.Max(targetFrame - back, 0);
        int hi = Math.Min(targetFrame + fwd, totalFrames);
        int frameBytes = w.Channels * 2;
        int step = Math.Max(w.SampleRate / 50, 1);  // 20ms 能量窗
        int best = Math.Min(targetFrame, totalFrames);
        long bestEnergy = long.MaxValue;
        for (int f = lo; f + step <= hi; f += step)
        {
            var e = FrameEnergy(w, f, step, frameBytes);
            if (e < bestEnergy) { bestEnergy = e; best = Math.Min(f + step / 2, totalFrames); }
        }
        return best;
    }

    private static long FrameEnergy(WavInfo w, int startFrame, int frames, int frameBytes)
    {
        long sum = 0;
        int off = w.DataOffset + startFrame * frameBytes;
        int end = Math.Min(off + frames * frameBytes, w.DataOffset + w.DataLength);
        for (int p = off; p + 1 < end; p += 2)
        {
            short v = BitConverter.ToInt16(w.Buffer, p);
            sum += (long)v * v;
        }
        return sum;
    }

    /// <summary>按原采样率/声道重装一个标准 44 字节头的 PCM WAV 段。</summary>
    private static byte[] BuildSegment(WavInfo w, int startFrame, int frameCount)
    {
        int frameBytes = w.Channels * 2;
        int dataLen = frameCount * frameBytes;
        var buf = new byte[44 + dataLen];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        WriteInt32(buf, 4, 36 + dataLen);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(buf, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(buf, 12);
        WriteInt32(buf, 16, 16);                            // fmt 块长
        WriteInt16(buf, 20, 1);                             // PCM
        WriteInt16(buf, 22, (short)w.Channels);
        WriteInt32(buf, 24, w.SampleRate);
        WriteInt32(buf, 28, w.SampleRate * frameBytes);     // byteRate
        WriteInt16(buf, 32, (short)frameBytes);             // blockAlign
        WriteInt16(buf, 34, 16);                            // bitsPerSample
        Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        WriteInt32(buf, 40, dataLen);
        Array.Copy(w.Buffer, w.DataOffset + startFrame * frameBytes,
                   buf, 44, dataLen);
        return buf;
    }

    private static void WriteInt16(byte[] b, int pos, short v)
    {
        b[pos] = (byte)v; b[pos + 1] = (byte)(v >> 8);
    }

    private static void WriteInt32(byte[] b, int pos, int v)
    {
        b[pos] = (byte)v; b[pos + 1] = (byte)(v >> 8);
        b[pos + 2] = (byte)(v >> 16); b[pos + 3] = (byte)(v >> 24);
    }
}
