/**
 * 票 271：一起听人声增益级 + 人声过小提示 + 麦克风音量表读数（纯函数/假节点）。
 * - 增益图接线：source → gain(设定值) → limiter(生产参数) → destination；
 *   挂接失败（工厂抛错）返回 null，调用方回落原播放；
 *   disconnect 断开三段，重复断开不抛。
 * - 增益常量 > 1（放大非衰减）；限幅阈值在 0~1 线性幅值内（只压峰）。
 * - quietHint：参考存在 + 人声不低于近静音线 + 人声 < 参考 35% 才触发；
 *   近静音（< 5）归票 261 noVoice，不叠加；无参考不触发。
 * - micLevel：最近 12 帧最大值，空为 0，钳到 0~100。
 */
import { describe, expect, it } from '@jest/globals';
import {
  attachVoiceGainStage,
  micLevelFromPeaks,
  SHADOW_VOICE_TOGETHER_GAIN,
  shouldShowQuietHint,
  VOICE_LIMITER,
} from './shadow-voice-gain';
import type {
  DynamicsCompressorLike,
  GainNodeLike,
  MediaSourceLike,
  VoiceGainContextLike,
} from './shadow-voice-gain';

class FakeNode {
  connects: unknown[] = [];
  disconnects = 0;
  connect(node: unknown): void {
    this.connects.push(node);
  }
  disconnect(): void {
    this.disconnects++;
  }
}

class FakeGain extends FakeNode implements GainNodeLike {
  gain = { value: 1 };
}

class FakeCompressor extends FakeNode implements DynamicsCompressorLike {
  threshold = { value: 0 };
  knee = { value: 0 };
  ratio = { value: 1 };
  attack = { value: 0 };
  release = { value: 0 };
}

class FakeSource extends FakeNode implements MediaSourceLike {}

function fakeCtx(): VoiceGainContextLike & {
  gain: FakeGain;
  limiter: FakeCompressor;
  source: FakeSource;
  destination: object;
  resumed: boolean;
} {
  const gain = new FakeGain();
  const limiter = new FakeCompressor();
  const source = new FakeSource();
  const destination = {};
  const ctx: VoiceGainContextLike & {
    gain: FakeGain;
    limiter: FakeCompressor;
    source: FakeSource;
    destination: object;
    resumed: boolean;
  } = {
    destination,
    gain,
    limiter,
    source,
    resumed: false,
    createMediaElementSource(): MediaSourceLike {
      return source;
    },
    createGain(): GainNodeLike {
      return gain;
    },
    createDynamicsCompressor(): DynamicsCompressorLike {
      return limiter;
    },
    resume(): void {
      ctx.resumed = true;
    },
  };
  return ctx;
}

describe('票 271 人声增益级', () => {
  it('接线 source → gain → limiter → destination，增益与限幅参数为生产值', () => {
    const ctx = fakeCtx();
    const stage = attachVoiceGainStage(ctx, {}, 2);
    expect(stage).not.toBeNull();
    expect(stage!.gainValue).toBe(2);
    expect(ctx.gain.gain.value).toBe(2);
    expect(ctx.source.connects).toEqual([ctx.gain]);
    expect(ctx.gain.connects).toEqual([ctx.limiter]);
    expect(ctx.limiter.connects).toEqual([ctx.destination]);
    expect(ctx.limiter.threshold.value).toBe(VOICE_LIMITER.thresholdDb);
    expect(ctx.limiter.knee.value).toBe(VOICE_LIMITER.kneeDb);
    expect(ctx.limiter.ratio.value).toBe(VOICE_LIMITER.ratio);
    expect(ctx.resumed).toBe(true);
  });

  it('disconnect 断开三段，重复断开不抛', () => {
    const ctx = fakeCtx();
    const stage = attachVoiceGainStage(ctx, {}, 2)!;
    stage.disconnect();
    expect(() => stage.disconnect()).not.toThrow();
    expect(ctx.source.disconnects).toBe(2);
    expect(ctx.gain.disconnects).toBe(2);
    expect(ctx.limiter.disconnects).toBe(2);
  });

  it('工厂抛错 / 空上下文 / 非正增益 → null（调用方回落原播放）', () => {
    const bad = fakeCtx();
    bad.createGain = (): GainNodeLike => {
      throw new Error('no gain');
    };
    expect(attachVoiceGainStage(bad, {})).toBeNull();
    expect(attachVoiceGainStage(null, {})).toBeNull();
    expect(attachVoiceGainStage(fakeCtx(), {}, 0)).toBeNull();
  });

  it('增益常量为放大（> 1），限幅阈值在 0~1 线性幅值内', () => {
    expect(SHADOW_VOICE_TOGETHER_GAIN).toBeGreaterThan(1);
    const linear = Math.pow(10, VOICE_LIMITER.thresholdDb / 20);
    expect(linear).toBeGreaterThan(0);
    expect(linear).toBeLessThan(1);
  });
});

describe('票 271 人声过小提示判定', () => {
  it('人声 15 vs 参考 90 → 触发；人声 40 vs 参考 90 → 不触发', () => {
    expect(shouldShowQuietHint(15, 90)).toBe(true);
    expect(shouldShowQuietHint(40, 90)).toBe(false);
  });

  it('近静音（< 5）不触发（归票 261 noVoice）；无参考不触发', () => {
    expect(shouldShowQuietHint(3, 90)).toBe(false);
    expect(shouldShowQuietHint(20, 0)).toBe(false);
    expect(shouldShowQuietHint(0, 0)).toBe(false);
  });

  it('边界：恰为参考 35% 不触发，低于才触发', () => {
    expect(shouldShowQuietHint(35, 100)).toBe(false);
    expect(shouldShowQuietHint(34, 100)).toBe(true);
  });
});

describe('票 271 麦克风音量表读数', () => {
  it('空为 0；取最近 12 帧最大值', () => {
    expect(micLevelFromPeaks([])).toBe(0);
    const peaks = Array.from({ length: 20 }, (_, i) => ({ t: i, v: i === 2 ? 90 : 10 }));
    expect(micLevelFromPeaks(peaks)).toBe(10);
    const recent = Array.from({ length: 20 }, (_, i) => ({ t: i, v: i === 18 ? 77 : 10 }));
    expect(micLevelFromPeaks(recent)).toBe(77);
  });

  it('钳到 0~100', () => {
    expect(micLevelFromPeaks([{ v: 140 }])).toBe(100);
    expect(micLevelFromPeaks([{ v: -5 }])).toBe(0);
  });
});
