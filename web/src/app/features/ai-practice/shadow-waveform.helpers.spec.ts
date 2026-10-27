/**
 * 票 250：跟读纯函数单测（P2 门控 / P5 映射 / P8 排期 / 峰值抽稀）。
 * 合成信号断言，不依赖浏览器音频设备。
 */
import { describe, expect, it } from '@jest/globals';
import {
  axisLength,
  computeF0Contour,
  computeMixSchedule,
  continuityGateHz,
  decimatePeaks,
  dedupeShadowKeys,
  estimateWindowF0,
  expectedRefTime,
  framePeak,
  isShadowOrphan,
  jointDuration,
  mapWallToRefAxis,
  medianHz,
  needsDriftCorrection,
  seekVoiceTime,
  trackTimeToVoiceTime,
  voiceTimeToTrackTime,
} from './shadow-waveform.helpers';

function sine(freqHz: number, sampleRate: number, seconds: number): Float32Array {
  const n = Math.floor(sampleRate * seconds);
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) out[i] = 0.8 * Math.sin((2 * Math.PI * freqHz * i) / sampleRate);
  return out;
}

describe('framePeak', () => {
  it('静音 → 0', () => {
    expect(framePeak(new Float32Array(2048))).toBe(0);
  });
  it('满幅正弦 → 100，半幅 → 50', () => {
    expect(framePeak(sine(200, 48000, 0.05))).toBeGreaterThanOrEqual(79);
    const half = sine(200, 48000, 0.05).map((v) => v * 0.5);
    expect(framePeak(half)).toBeGreaterThanOrEqual(39);
    expect(framePeak(half)).toBeLessThanOrEqual(41);
  });
});

describe('estimateWindowF0（P2 门控）', () => {
  it('200Hz 正弦 → 约 200Hz 且置信度过阈', () => {
    const r = estimateWindowF0(sine(200, 48000, 0.1), 48000);
    expect(r).not.toBeNull();
    expect(Math.abs((r as { hz: number }).hz - 200)).toBeLessThanOrEqual(12);
    expect((r as { conf: number }).conf).toBeGreaterThanOrEqual(0.5);
  });
  it('静音 → null（无点 = 不断言）', () => {
    expect(estimateWindowF0(new Float32Array(2048), 48000)).toBeNull();
  });
  it('过短输入 → null', () => {
    expect(estimateWindowF0(new Float32Array(8), 48000)).toBeNull();
  });
});

describe('medianHz', () => {
  it('取中值；空 → null', () => {
    expect(medianHz([180, 200, 190])).toBe(190);
    expect(medianHz([200])).toBe(200);
    expect(medianHz([])).toBeNull();
  });
});

describe('computeF0Contour（P2 事后轮廓：间隙记 null 不插值）', () => {
  it('1s 200Hz 正弦 → 各跳约 200Hz', () => {
    const c = computeF0Contour(sine(200, 16000, 1), 16000, 0.1);
    expect(c.length).toBeGreaterThanOrEqual(9);
    const voiced = c.filter((v): v is number => v !== null);
    expect(voiced.length).toBeGreaterThanOrEqual(8);
    for (const hz of voiced) expect(Math.abs(hz - 200)).toBeLessThanOrEqual(12);
  });
  it('前半正弦、后半静音 → 前半有值、后半全 null', () => {
    const a = sine(200, 16000, 0.5);
    const data = new Float32Array(16000);
    data.set(a, 0);
    const c = computeF0Contour(data, 16000, 0.1);
    expect(c.slice(0, 4).every((v) => v !== null)).toBe(true);
    expect(c.slice(6).every((v) => v === null)).toBe(true);
  });
});

describe('computeMixSchedule（P3/P8 排期）', () => {
  it('活声晚 0.3s：参考 0 起，活声 0.3 起，图长 22.3', () => {
    expect(computeMixSchedule(300, 21, 22)).toEqual({
      refStartSec: 0,
      voiceStartSec: 0.3,
      mixDurSec: 22.3,
    });
  });
  it('活声早 0.5s：参考 0.5 起，活声 0 起，图长 21.5', () => {
    expect(computeMixSchedule(-500, 21, 20)).toEqual({
      refStartSec: 0.5,
      voiceStartSec: 0,
      mixDurSec: 21.5,
    });
  });
  it('零偏移：图长取较长者', () => {
    expect(computeMixSchedule(0, 21, 22).mixDurSec).toBe(22);
  });
});

describe('P5 速率映射（往返一致）', () => {
  it('0.5x 下 T=10 ↔ 墙钟 19.8（laneShift 0.2）', () => {
    expect(trackTimeToVoiceTime(10, 0.2, 0.5)).toBeCloseTo(19.8, 6);
    expect(voiceTimeToTrackTime(19.8, 0.2, 0.5)).toBeCloseTo(10, 6);
  });
  it('1x 下映射退化为平移', () => {
    expect(trackTimeToVoiceTime(10, 0.2, 1)).toBeCloseTo(9.8, 6);
  });
});

describe('decimatePeaks', () => {
  it('超限成对合并：t 取前者、v 取较大者', () => {
    const peaks = [
      { t: 0, v: 10 },
      { t: 1, v: 90 },
      { t: 2, v: 20 },
      { t: 3, v: 30 },
      { t: 4, v: 40 },
      { t: 5, v: 50 },
      { t: 6, v: 60 },
      { t: 7, v: 70 },
    ];
    const out = decimatePeaks(peaks, 4);
    expect(out.length).toBe(4);
    expect(out[0]).toEqual({ t: 0, v: 90 });
    expect(out[1]).toEqual({ t: 2, v: 30 });
  });
  it('不超限原样返回', () => {
    const peaks = [{ t: 0, v: 10 }];
    expect(decimatePeaks(peaks, 4)).toBe(peaks);
  });
});

describe('P15/P16/P17 第二轮纯函数', () => {
  it('mapWallToRefAxis：采集 seekOffset=0 时退化为既有映射', () => {
    expect(mapWallToRefAxis(19.8, 0.2, 0.5)).toBeCloseTo(10, 6);
    expect(mapWallToRefAxis(19.8, 0.2, 0.5, 0)).toBeCloseTo(10, 6);
    expect(mapWallToRefAxis(5, 0, 2, 3)).toBeCloseTo(13, 6);
  });
  it('axisLength：活声超长时轴扩展，否取参考长', () => {
    expect(axisLength(21, 25)).toBe(25);
    expect(axisLength(21, 10)).toBe(21);
  });
  it('expectedRefTime / needsDriftCorrection：50ms 阈', () => {
    expect(expectedRefTime(5, 0, 0, 2)).toBeCloseTo(10, 6);
    expect(expectedRefTime(5, 2, 4, 0.5)).toBeCloseTo(5.5, 6);
    expect(needsDriftCorrection(10.04, 10)).toBe(false);
    expect(needsDriftCorrection(10.06, 10)).toBe(true);
  });
  it('seekVoiceTime：完整逆映射并钳制', () => {
    expect(seekVoiceTime(10, 0, 0.2, 0.5, 30)).toBeCloseTo(19.8, 6);
    expect(seekVoiceTime(100, 0, 0, 1, 22)).toBe(22);
    expect(seekVoiceTime(-5, 0, 0, 1, 22)).toBe(0);
  });
  it('jointDuration：shift=0 退化为 max；shift 非零取并集', () => {
    expect(jointDuration(21, 22, 0)).toBe(22);
    expect(jointDuration(21, 22, 0.3)).toBeCloseTo(22.3, 6);
    expect(jointDuration(21, 20, -0.5)).toBeCloseTo(21.5, 6);
  });
  it('continuityGateHz：<3 点豁免；带内通过；2x 跳变隐藏', () => {
    expect(continuityGateHz(400, [200, 205])).toBe(400);
    expect(continuityGateHz(210, [200, 205, 198, 202, 201])).toBe(210);
    expect(continuityGateHz(400, [200, 205, 198, 202, 201])).toBeNull();
    expect(continuityGateHz(100, [200, 205, 198, 202, 201])).toBeNull();
  });
  it('isShadowOrphan：任一存活条件即非孤儿', () => {
    expect(isShadowOrphan(false, false, false)).toBe(true);
    expect(isShadowOrphan(true, false, false)).toBe(false);
    expect(isShadowOrphan(false, true, false)).toBe(false);
    expect(isShadowOrphan(false, false, true)).toBe(false);
  });
  it('dedupeShadowKeys：GUID 与本地并存只显 GUID', () => {
    const rows = [
      { key: 'local-1', serverId: null, localTakeId: 'local-1' },
      { key: 'guid-1', serverId: 'guid-1', localTakeId: 'local-1' },
      { key: 'local-2', serverId: null, localTakeId: 'local-2' },
    ];
    expect(dedupeShadowKeys(rows)).toEqual(['guid-1', 'local-2']);
  });
});
