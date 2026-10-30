/**
 * 票 271：跟读一起听的人声增益级 + 人声过小判定（纯函数/可注入，Jest 可测）。
 *
 * 问题：跟读一起听时人声远小于参考（采集端进来就小；回放链此前无任何
 * 增益环节，元素音量上限 1.0 只能衰减不能放大）。修法（独立评分胜出
 * A + C 中的 A）：只在双轨一起听时，把人声元素经 Web Audio 图放大后
 * 输出——人声元素 → MediaElementSource → Gain → DynamicsCompressor
 * （限幅，防削波）→ destination；参考轨不动。增益只动听的路径：
 * 采集约束、存档 blob、take 数据一律不动；增益挂接失败（无
 * AudioContext、元素已被别的图占用等）时返回 null，调用方回落到
 * 不增益的原播放，绝不因增益失败而断播。
 *
 * 作用域（评分定案，勿扩大）：
 * - 只用于 dual together：voice 元素。人声独奏是单轨，无“与参考比”
 *   问题；mix 单轨行的人声已 baked 进混音、无法单独放大（整轨放大
 *   等比放大参考，无意义），故两者都不挂增益。
 * - 票 279 改动（评分胜出 C）：固定 ×3 + ratio 20 已废止——
 *   ① 自适应增益（computeTogetherGain）：按 take 的人声峰/参考峰
 *   定增益，钳在 [1.0, 3.0]，峰值缺失时回落固定 3.0；
 *   ② 限幅器改为纯限幅角色（只碰顶端约 1dB，中段语音不动），
 *   不再把大音量人声压扁出抽吸/生硬感；
 *   ③ 挂接前门控（ensureVoiceGainContextRunning）：图必须在
 *   上下文 running 后才挂，resume 失败/超时则不挂图、元素直出。
 */

/**
 * 一起听人声增益上限与峰值缺失时的回落值（线性倍数；271 走查实测
 * 调定，279 起仅作上限/回落，不再是固定增益）。
 */
export const SHADOW_VOICE_TOGETHER_GAIN = 3;

/** 自适应增益下限：只放大、永不衰减（大音量人声不被压小）。 */
export const TOGETHER_GAIN_FLOOR = 1;

/**
 * 票 279 限幅器参数（纯限幅角色）：阈值 -3dB + 宽膝 6dB + ratio 12。
 * 自适应增益保证入限幅峰值 ≈ 参考峰（≤ 1.0），故只有顶端约 3dB 会
 * 被碰到，中段语音基本不动——不再复现 271 ratio 20 + 阈值 -6dB
 * 把大音量语音全程压扁的抽吸感。阈值实测三轮：-1dB 时满幅正弦
 * 起振瞬态冲到 0.995、-2dB 时 0.981，均贴 271 的 0.98 安全线；
 * -3dB 时满幅 0.959、可比度仍 ≥ 95%，中段 0.5 输入仅抬到 0.541。
 * 注意：271 的“-3dB 不达标”是在固定 ×3 下入限幅峰值高达 3.0 的
 * 旧链上测的，与自适应增益（入限幅峰值 ≤ 1.0）不可比。参数只认
 * OfflineAudioContext 实测（见台账 279 实测表），不认公式推导。
 */
export const VOICE_LIMITER = {
  thresholdDb: -3,
  kneeDb: 6,
  ratio: 12,
  attackSec: 0.003,
  releaseSec: 0.1,
} as const;

export interface GainNodeLike {
  gain: { value: number };
  connect(node: unknown): void;
  disconnect(): void;
}

export interface DynamicsCompressorLike {
  threshold: { value: number };
  knee: { value: number };
  ratio: { value: number };
  attack: { value: number };
  release: { value: number };
  connect(node: unknown): void;
  disconnect(): void;
}

export interface MediaSourceLike {
  connect(node: unknown): void;
  disconnect(): void;
}

/** 挂增益所需的最小 AudioContext 面（真实 AudioContext / Jest 假对象）。 */
export interface VoiceGainContextLike {
  destination: unknown;
  /** 'running' | 'suspended' | 'closed'（真实 AudioContextState；假对象须给值）。 */
  state: string;
  createMediaElementSource(el: unknown): MediaSourceLike;
  createGain(): GainNodeLike;
  createDynamicsCompressor(): DynamicsCompressorLike;
  resume(): Promise<void> | void;
}

/** 峰值列的最大 v（0..100 口径）；空列 → null（调用方走回落）。 */
export function peakMaxValue(peaks: readonly { v: number }[]): number | null {
  let max: number | null = null;
  for (const p of peaks) {
    if (typeof p.v === 'number' && Number.isFinite(p.v) && (max === null || p.v > max)) max = p.v;
  }
  return max;
}

/**
 * 票 279 自适应一起听增益：把人声峰补到与参考峰可比。
 * 公式 gain = refPeak / voicePeak（两边同 0..100 口径，比值与量纲无关），
 * 钳在 [TOGETHER_GAIN_FLOOR, SHADOW_VOICE_TOGETHER_GAIN]：
 * 人声已 ≥ 参考 → 1.0（永不衰减）；人声极小 → 顶格 3.0（271 的可听上限）。
 * 任一峰值缺失/非正/非有限 → 回落固定 3.0（fallback: true）。
 */
export function computeTogetherGain(
  voicePeak: number | null | undefined,
  refPeak: number | null | undefined,
): { gain: number; fallback: boolean } {
  if (
    typeof voicePeak !== 'number' || typeof refPeak !== 'number' ||
    !Number.isFinite(voicePeak) || !Number.isFinite(refPeak) ||
    !(voicePeak > 0) || !(refPeak > 0)
  ) {
    return { gain: SHADOW_VOICE_TOGETHER_GAIN, fallback: true };
  }
  const raw = refPeak / voicePeak;
  const gain = Math.min(SHADOW_VOICE_TOGETHER_GAIN, Math.max(TOGETHER_GAIN_FLOOR, raw));
  return { gain, fallback: false };
}

/**
 * 票 279 挂接前门控：图必须在上下文 running 后才挂。
 * 已 running → 立即 true；suspended → 调 resume 并等到 running 或超时
 * （默认 1000ms）；closed / 无上下文 / resume 抛错且状态仍非 running → false。
 * 调用方在 false 时必须不挂图（元素直出）——元素一旦被
 * MediaElementSource 捕获，输出只走图，断开图并不能恢复直出，
 * 所以“先挂后拆”不是合法回落。
 */
export async function ensureVoiceGainContextRunning(
  ctx: VoiceGainContextLike | null | undefined,
  timeoutMs = 1000,
): Promise<boolean> {
  if (!ctx) return false;
  if (ctx.state === 'running') return true;
  try {
    const r = ctx.resume();
    if (r && typeof (r as Promise<void>).then === 'function') {
      await Promise.race([
        (r as Promise<void>).catch(() => undefined),
        new Promise<void>((resolve) => setTimeout(resolve, timeoutMs)),
      ]);
    }
  } catch {
    /* resume 抛错 → 下方状态判定兜底 */
  }
  return ctx.state === 'running';
}

export interface VoiceGainStage {
  /** 实际生效的增益倍数（即传入值；调用方用它做可比性断言）。 */
  gainValue: number;
  disconnect(): void;
}

/**
 * 把人声元素接入增益图。失败返回 null（调用方回落原播放）。
 * 注意：同一元素只能建一次 MediaElementSource；调用方保证每元素只
 * 挂一次（session 在 buildController 每次建新元素时挂，旧元素随
 * stopShadowPlayback 丢弃）。
 */
export function attachVoiceGainStage(
  ctx: VoiceGainContextLike | null | undefined,
  el: unknown,
  gainValue: number = SHADOW_VOICE_TOGETHER_GAIN,
): VoiceGainStage | null {
  if (!ctx || !el || !(gainValue > 0)) return null;
  try {
    const source = ctx.createMediaElementSource(el);
    const gain = ctx.createGain();
    gain.gain.value = gainValue;
    const limiter = ctx.createDynamicsCompressor();
    limiter.threshold.value = VOICE_LIMITER.thresholdDb;
    limiter.knee.value = VOICE_LIMITER.kneeDb;
    limiter.ratio.value = VOICE_LIMITER.ratio;
    limiter.attack.value = VOICE_LIMITER.attackSec;
    limiter.release.value = VOICE_LIMITER.releaseSec;
    source.connect(gain);
    gain.connect(limiter);
    limiter.connect(ctx.destination);
    try {
      const r = ctx.resume();
      if (r && typeof (r as Promise<void>).catch === 'function') {
        (r as Promise<void>).catch(() => undefined);
      }
    } catch {
      /* 票 279 更正 271 旧注：被 MediaElementSource 捕获的元素在
         suspended 上下文里是静音的（输出只走图），不存在“元素照常
         出声、只是增益待恢复”——所以调用方必须先门控
         （ensureVoiceGainContextRunning）再挂图，挂接内的 resume
         只是尽力而为，不能当作前置静音的解法。 */
    }
    return {
      gainValue,
      disconnect(): void {
        try {
          source.disconnect();
        } catch {
          /* 已断开时忽略 */
        }
        try {
          gain.disconnect();
        } catch {
          /* 已断开时忽略 */
        }
        try {
          limiter.disconnect();
        } catch {
          /* 已断开时忽略 */
        }
      },
    };
  } catch {
    return null;
  }
}

/**
 * 票 271 C：人声过小提示判定（纯函数；提示本身不改任何音频）。
 * 触发条件：参考峰值存在，且人声峰值不低于近静音线（< 5 归票 261
 * noVoice 警告管，不叠加），但不到参考峰值的 35%。
 * 峰值口径：两边都是 0~100（framePeak / bucketPeaks 同口径）。
 */
export function shouldShowQuietHint(voiceMax: number, refMax: number): boolean {
  if (!(refMax > 0)) return false;
  if (!(voiceMax >= 5)) return false;
  return voiceMax < 0.35 * refMax;
}

/** 录音中麦克风音量表读数：最近 12 帧峰值的最大值（0~100），空为 0。 */
export function micLevelFromPeaks(peaks: readonly { v: number }[]): number {
  if (!peaks.length) return 0;
  let max = 0;
  const start = Math.max(0, peaks.length - 12);
  for (let i = start; i < peaks.length; i++) {
    if (peaks[i].v > max) max = peaks[i].v;
  }
  return Math.min(100, Math.max(0, Math.round(max)));
}

/**
 * 票 284：录音期间参考声压低系数（相对原音量）。
 * 适度压低、绝非静音：参考声仍需可跟读，只是让进入麦克风的回声
 * 绝对值变小 —— AEC 少取消、AGC 少向下增益，录制端增益放大的
 * 回声污染也随之变小。仅限 take 期间（session 在停参考时恢复原音量），
 * 只在扬声器模式生效（耳机模式无声学回声，不压）。
 * 票 291 A：0.6 → 0.3（再轻约 6dB），进麦的串音同量下降；仍非静音。
 */
export const SHADOW_REF_DUCK_FACTOR = 0.3;

/** 压低后音量 = 原音量 × 系数；非法输入原样返回（调用方不因此断播）。 */
export function duckedVolume(baseVolume: number): number {
  if (typeof baseVolume !== 'number' || !Number.isFinite(baseVolume)) return baseVolume;
  return baseVolume * SHADOW_REF_DUCK_FACTOR;
}
