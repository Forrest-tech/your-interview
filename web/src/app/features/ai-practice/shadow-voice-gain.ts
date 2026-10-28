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
 * - 固定增益值（SHADOW_VOICE_TOGETHER_GAIN）：走查实测调定（见台账
 *   271 条目），限幅器保证大音量人声不削波。
 */

/** 一起听人声增益（线性倍数；走查实测调定，见台账 271）。 */
export const SHADOW_VOICE_TOGETHER_GAIN = 3;

/**
 * 限幅器参数：实测调定（Chrome DynamicsCompressor 真实曲线与教科书
 * 膝区公式不符——它会把小信号抬高，故参数只认 OfflineAudioContext
 * 实测，不认公式推导）。增益 3、knee 3、ratio 20 下的实测表：
 *   人声峰 0.15（×3=0.45）→ 输出 0.63（对 0.9 参考 = 70%，过可比 bar）
 *   人声峰 0.25（×3=0.75）→ 输出 0.81
 *   人声峰 0.40（×3=1.20）→ 输出 0.85（未被压扁）
 *   人声峰 0.80（×3=2.40）→ 输出 0.91（< 0.98，无削波）
 *   人声峰 1.00（×3=3.00）→ 输出 0.91（< 0.98，无削波）
 * 阈值 -3dB 曾实测不达标（0.8 人声输出 1.00、满幅输出 1.02 仍削波），
 * 故不用；阈值 -1dB 同理出局（0.8 人声输出 1.08）。
 */
export const VOICE_LIMITER = {
  thresholdDb: -6,
  kneeDb: 3,
  ratio: 20,
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
  createMediaElementSource(el: unknown): MediaSourceLike;
  createGain(): GainNodeLike;
  createDynamicsCompressor(): DynamicsCompressorLike;
  resume(): Promise<void> | void;
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
      /* suspended 上下文也不断播：元素照常出声，只是增益待恢复 */
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
