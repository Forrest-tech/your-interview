import { describe, expect, it } from '@jest/globals';
import {
  attachVoiceGainStage,
  computeTogetherGain,
  ensureVoiceGainContextRunning,
  peakMaxValue,
  SHADOW_VOICE_TOGETHER_GAIN,
  TOGETHER_GAIN_FLOOR,
  VOICE_LIMITER,
} from './shadow-voice-gain';
import type {
  DynamicsCompressorLike,
  GainNodeLike,
  MediaSourceLike,
  VoiceGainContextLike,
} from './shadow-voice-gain';

class FakeNode {
  connected: unknown[] = [];
  connect(node: unknown): void {
    this.connected.push(node);
  }
  disconnect(): void {
    this.connected = [];
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

function fakeCtx(state: string): VoiceGainContextLike & {
  gain: FakeGain;
  limiter: FakeCompressor;
  resumed: boolean;
} {
  const gain = new FakeGain();
  const limiter = new FakeCompressor();
  const source = new FakeSource();
  const ctx = {
    destination: {},
    state,
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
      ctx.state = 'running';
    },
  };
  return ctx;
}

describe('票 279 自适应一起听增益', () => {
  it('人声 15 vs 参考 90 → 顶格 3.0（不是 6.0）；人声 71 vs 参考 90 → 约 1.27', () => {
    expect(computeTogetherGain(15, 90)).toEqual({ gain: 3, fallback: false });
    const { gain, fallback } = computeTogetherGain(71, 90);
    expect(fallback).toBe(false);
    expect(gain).toBeCloseTo(90 / 71, 10);
  });

  it('人声已 ≥ 参考 → 1.0（永不衰减）；人声 100 vs 参考 80 → 1.0', () => {
    expect(computeTogetherGain(90, 90)).toEqual({ gain: 1, fallback: false });
    expect(computeTogetherGain(100, 80)).toEqual({ gain: 1, fallback: false });
  });

  it('峰值缺失/非正/非有限 → 回落固定 3.0 且 fallback: true', () => {
    expect(computeTogetherGain(null, 90)).toEqual({
      gain: SHADOW_VOICE_TOGETHER_GAIN,
      fallback: true,
    });
    expect(computeTogetherGain(50, null)).toEqual({
      gain: SHADOW_VOICE_TOGETHER_GAIN,
      fallback: true,
    });
    expect(computeTogetherGain(0, 90)).toEqual({
      gain: SHADOW_VOICE_TOGETHER_GAIN,
      fallback: true,
    });
    expect(computeTogetherGain(50, Number.NaN)).toEqual({
      gain: SHADOW_VOICE_TOGETHER_GAIN,
      fallback: true,
    });
  });

  it('增益恒在 [1.0, 3.0] 内', () => {
    expect(TOGETHER_GAIN_FLOOR).toBe(1);
    expect(SHADOW_VOICE_TOGETHER_GAIN).toBe(3);
    for (const [v, r] of [[1, 100], [5, 95], [30, 60], [99, 100], [100, 1]] as const) {
      const { gain } = computeTogetherGain(v, r);
      expect(gain).toBeGreaterThanOrEqual(1);
      expect(gain).toBeLessThanOrEqual(3);
    }
  });

  it('限幅器比 271 温和：阈值 -3dB（只碰顶端约 3dB），ratio 12（< 20）', () => {
    expect(VOICE_LIMITER.thresholdDb).toBe(-3);
    expect(VOICE_LIMITER.ratio).toBe(12);
    expect(VOICE_LIMITER.ratio).toBeLessThan(20);
  });

  it('自适应增益值确实写进 gain 节点（71 vs 90 → 约 1.27，不是固定 3）', () => {
    const ctx = fakeCtx('running');
    const { gain } = computeTogetherGain(71, 90);
    const stage = attachVoiceGainStage(ctx, {}, gain);
    expect(stage).not.toBeNull();
    expect(ctx.gain.gain.value).toBeCloseTo(90 / 71, 10);
    expect(ctx.limiter.ratio.value).toBe(12);
  });
});

describe('票 279 峰值最大值', () => {
  it('空列 → null；否则取最大 v', () => {
    expect(peakMaxValue([])).toBeNull();
    expect(peakMaxValue([{ v: 0 }, { v: 71 }, { v: 12 }])).toBe(71);
    expect(peakMaxValue([{ v: Number.NaN }, { v: 40 }])).toBe(40);
  });
});

describe('票 279 挂接前门控', () => {
  it('已 running → 立即 true，不调 resume', async () => {
    const ctx = fakeCtx('running');
    await expect(ensureVoiceGainContextRunning(ctx, 50)).resolves.toBe(true);
    expect(ctx.resumed).toBe(false);
  });

  it('suspended + resume 生效 → true', async () => {
    const ctx = fakeCtx('suspended');
    await expect(ensureVoiceGainContextRunning(ctx, 500)).resolves.toBe(true);
    expect(ctx.resumed).toBe(true);
  });

  it('suspended + resume 永不生效 → 超时后 false', async () => {
    const ctx = fakeCtx('suspended');
    ctx.resume = (): void => {
      /* 永不把 state 置为 running，模拟被浏览器挡住的 resume */
    };
    await expect(ensureVoiceGainContextRunning(ctx, 20)).resolves.toBe(false);
  });

  it('无上下文 → false；closed → false', async () => {
    await expect(ensureVoiceGainContextRunning(null, 10)).resolves.toBe(false);
    const ctx = fakeCtx('closed');
    ctx.resume = (): void => {
      /* closed 无法 resume */
    };
    await expect(ensureVoiceGainContextRunning(ctx, 10)).resolves.toBe(false);
  });
});
