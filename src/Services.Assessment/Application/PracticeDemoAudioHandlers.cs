using Microsoft.EntityFrameworkCore;
using YourInterview.BuildingBlocks.Results;
using YourInterview.BuildingBlocks.Security;
using YourInterview.Services.Assessment.Domain;
using YourInterview.Services.Assessment.Infrastructure.Persistence;
using YourInterview.Services.Assessment.Infrastructure.Storage;

namespace YourInterview.Services.Assessment.Application;

// ============================================================
// 示范朗读音频缓存(2026-09-16 第三十一轮)
//
// 需求(Forrest 第 3 条):点「示范朗读」→ 若 Azure 已生成过同一段文本的音频,
//   直接回放本地缓存,**不再花 Azure TTS 额度**;
//   文本(或音色/倍速)变了 → 重新合成一份并存下来。
//
// 设计要点:
//   · 缓存键 = SHA-256(归一化文本 + 音色 + 倍速 + 语言),见 PracticeTtsCache.ComputeKey;
//   · 音频落文件系统(与录音同一 IAudioStore),库里只存相对路径;
//   · **回放路径绝不调 Azure** —— GetOrCreateTtsCache 先查库、命中即返回字节流。
// ============================================================

/// <summary>
/// 取(或生成)一段示范朗读音频。
/// </summary>
/// <param name="Force">
/// true = 忽略缓存,强制重新合成并覆盖缓存。
/// 正常朗读传 false(默认),这样第二遍起就是纯本地回放、零额度。
/// </param>
public sealed record GetOrCreateTtsCommand(Guid UserId, string Text, string? Voice,
    double? Speed, string Language, bool Force = false)
    : MediatR.IRequest<Result<TtsAudioResult>>;

/// <summary>
/// 示范朗读音频结果。
///
/// <paramref name="FromCache"/> 是诚实的来源标记 —— 前端据此提示
/// "已有本地缓存,未消耗额度",而不是含糊其辞。
///
/// ★ 第三十四轮(Forrest 要求"给我准确的消耗了多少"):
///   新增 <paramref name="BilledChars"/> 与 <paramref name="Voice"/>。
///
///   ⚠️ **为什么不是"token 数":Azure 语音 TTS 根本不以 token 计费** ——
///      它的真实计费单位是**合成字符数**(按每 1M 字符计价,
///      神经语音与标准语音单价不同)。响应里也没有任何 token 字段。
///      所以这里如实给出 ** billedChars = 本次送给 Azure 的字符数**,
///      这是能 100% 精确算出、且与账单口径一致的数字;
///      编一个假的"token 数"比不显示更坏。
///   字符数按 PowerShell/.NET 的 UTF-16 代码单元计(与 Azure 口径一致),
///   中文每个字算 1,emoji 算 2 —— 不做"看起来更少"的美化。
/// </summary>
public sealed record TtsAudioResult(byte[] Audio, string ContentType, bool FromCache,
    int BilledChars, string Voice = "", int AudioBytes = 0)
{
    /// <summary>兼容旧构造调用(第三十一轮的 3 参形式)。</summary>
    public TtsAudioResult(byte[] audio, string contentType, bool fromCache)
        : this(audio, contentType, fromCache, 0, string.Empty, audio.Length) { }
}

public sealed class GetOrCreateTtsCommandHandler(AssessmentDbContext db, SpeechSynthesizer synth,
    IAudioStore store, PronunciationAssessor assessor)
    : MediatR.IRequestHandler<GetOrCreateTtsCommand, Result<TtsAudioResult>>
{
    private const int MaxTextLength = 4000;

    public async Task<Result<TtsAudioResult>> Handle(GetOrCreateTtsCommand r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Text))
            return Result.Failure<TtsAudioResult>(Error.Validation("Tts.TextEmpty", "请先选择要朗读的素材内容。"));
        if (r.Text.Length > MaxTextLength)
            return Result.Failure<TtsAudioResult>(Error.Validation("Tts.TextTooLong",
                $"文本 {r.Text.Length} 字符超过 {MaxTextLength} 上限。"));

        var language = string.IsNullOrWhiteSpace(r.Language) ? "en-US" : r.Language.Trim();
        var speed = Math.Clamp(r.Speed is > 0 ? r.Speed.Value : 1.0, 0.5, 2.0);
        var cacheKey = PracticeTtsCache.ComputeKey(r.Text, r.Voice, speed, language);

        // ---------- 1) 查缓存(命中则零 Azure 调用) ----------
        if (!r.Force)
        {
            var hit = await db.TtsCache
                .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.CacheKey == cacheKey, ct);
            if (hit is not null)
            {
                var cached = await store.OpenReadAsync(r.UserId, hit.StoragePath, ct);
                if (cached is not null)
                {
                    await using (cached)
                    {
                        using var ms = new MemoryStream();
                        await cached.CopyToAsync(ms, ct);
                        hit.MarkUsed();
                        // 命中计数是统计信息,写失败不该让朗读报错 —— 尽力而为
                        try { await db.SaveChangesAsync(ct); } catch { /* 统计失败可忽略 */ }
                        // ★ 缓存命中:真正应付费字符数 = 0(没调 Azure)。
                        //   但也如实带上"这段文本本该花多少",让界面能说清楚
                        //   "本次 0 字符 / 本可花费 N 字符 —— 因为命中了本地缓存"。
                        var bytesHit = ms.ToArray();
                        return Result.Success(new TtsAudioResult(bytesHit, hit.ContentType, true,
                            0, hit.Voice, bytesHit.Length));
                    }
                }
                // 库里有记录但盘上文件没了(被清理/换机器)→ 清掉脏行,走重新合成
                db.TtsCache.Remove(hit);
                await db.SaveChangesAsync(ct);
            }
        }

        // ---------- 2) 未命中 → 真调 Azure 合成 ----------
        if (!await assessor.IsAvailableAsync(r.UserId, ct))
            return Result.Failure<TtsAudioResult>(new Error("Tts.NotConfigured",
                "服务端缺少可用的 AzureSpeech key,示范朗读不可用。请先在 AI 语音设置里保存密钥。",
                ErrorType.Failure));

        byte[] mp3;
        string voiceUsed;
        try
        {
            // ★ 2026-09-19:把语言透传给合成器 —— 之前这里就没传,
            //   导致 SSML 的 xml:lang 与音色永远只能是英语。
            mp3 = await synth.SynthesizeAsync(r.Text, r.Voice, speed, r.UserId, ct, language);
            // ★ 2026-09-25:voiceUsed 的推断复用 SpeechSynthesizer.DefaultVoiceFor ——
            //   此前这里手抄了一份 fr/else 判断,漏了中文,与合成器实际选音色分叉。
            voiceUsed = string.IsNullOrWhiteSpace(r.Voice)
                ? SpeechSynthesizer.DefaultVoiceFor(language)
                : r.Voice.Trim();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("未配置"))
        {
            return Result.Failure<TtsAudioResult>(new Error("Tts.NotConfigured", ex.Message, ErrorType.Failure));
        }
        // ⚠️ AzureAuthException / 其它异常直接向上抛,由控制器映射为 502 ——
        //    不在这里吞咽,免得把"上游凭据错"伪装成"成功但空音频"。

        // ---------- 3) 落盘 + 入库(= 下次的缓存) ----------
        // 落盘失败不阻断本次播放:能听到声音比存下缓存重要。
        try
        {
            using var ms = new MemoryStream(mp3);
            // ⚠️ IAudioStore.SaveAsync 的第二个参数是 Guid,它同时用于**命名磁盘文件**。
            //    而我们的缓存键是 64 位十六进制字符串 —— 两者形状不同。
            //    这里用缓存键派生一个**确定性 Guid**(同一个缓存键永远得到同一个 Guid),
            //    好处:重复合成时文件名稳定不变(不会每次落一堆孤儿 MP3),
            //    也让"键 → 文件"这条链路可复现。
            var fileId = DeriveGuidFromCacheKey(cacheKey);
            var rel = await store.SaveAsync(r.UserId, fileId, "mp3", ms, ct);

            var row = await db.TtsCache
                .FirstOrDefaultAsync(x => x.UserId == r.UserId && x.CacheKey == cacheKey, ct);
            if (row is null)
            {
                row = new PracticeTtsCache(r.UserId, cacheKey, rel, "audio/mpeg", mp3.Length,
                    PracticeTtsCache.ComputeTextHash(r.Text), voiceUsed, speed, language);
                db.TtsCache.Add(row);
            }
            else
            {
                // 强制重合成:覆盖既有行的路径与大小(主键不变)
                db.Entry(row).CurrentValues.SetValues(new PracticeTtsCache(
                    r.UserId, cacheKey, rel, "audio/mpeg", mp3.Length,
                    PracticeTtsCache.ComputeTextHash(r.Text), voiceUsed, speed, language));
            }
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // 缓存失败不能影响"这一次能听到" —— 静默降级,下次再合成一遍
            db.ChangeTracker.Clear();
        }

        return Result.Success(new TtsAudioResult(mp3, "audio/mpeg", false,
            BilledChars(r.Text), voiceUsed, mp3.Length));
    }

    /// <summary>
    /// 本次送给 Azure 的**计费字符数**(= 文本的 UTF-16 代码单元数)。
    ///
    /// ⚠️ 为什么不是 r.Text.Length?
    ///   其实 .NET 的 string.Length **就是** UTF-16 代码单元数,
    ///   与 Azure 计费口径一致(BMP 内每字符 1,代理对如 emoji 算 2)。
    ///   单独抽成方法是为了:① 把"这就是计费单位"这件事写在名字上;
    ///   ② 日后若 Azure 改口径(如改按码点计)只改这一处。
    /// 不做任何"看起来更少"的美化 —— 是多少就报多少。
    /// </summary>
    public static int BilledChars(string text) => text.Length;

    /// <summary>
    /// 从缓存键派生一个确定性 Guid(取 SHA-256 前 16 字节)。
    /// 只用于**命名磁盘文件**,不参与缓存查找 —— 查找永远走 (UserId, CacheKey)。
    /// </summary>
    private static Guid DeriveGuidFromCacheKey(string cacheKey)
    {
        var raw = System.Text.Encoding.UTF8.GetBytes(cacheKey);
        var hash = System.Security.Cryptography.SHA256.HashData(raw);
        return new Guid(hash.AsSpan(0, 16));
    }
}

