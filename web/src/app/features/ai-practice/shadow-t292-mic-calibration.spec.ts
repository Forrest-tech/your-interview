/**
 * 票 292：录前麦克风校准 → 冻结增益（扬声器模式）。
 * 覆盖（方案 §5）：
 * - 纯函数：增益公式（目标 −12、钳制 0/+30、线性换算）、dBFS 换算、
 *   95 百分位帧峰值（nearest-rank）、底噪 pooled RMS、存储值合法性
 *   标准、两门控阈值（含求值顺序 too-quiet 优先）、存储键与非法值回落、
 *   写失败不显示已校准。
 * - 接入：buildCaptureGraph 第 4 可选参数（缺省 3、传 gainLinear 覆盖、
 *   非法值回落 3；耳机路与 suspended 回落不受影响）；284 旧 spec 不动。
 * - 结构契约（源码断言，按 t261/t284/t291 先例）：会话层校准流程、
 *   停播核对、互斥锁、设备/模式切换中止、窗口内播放启动取消、
 *   面板块与过小提示按钮、i18n 三语齐全。
 * 声学校准精度无法在沙盒验证（假麦克风），真机验证 = 用户读一句话
 * 校准 + 下一条 take 波形（见台账诚实缺口）。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import {
  CALIBRATION_GAIN_MAX_DB,
  computeCalibrationGain,
  dbfs,
  evaluateCalibration,
  isValidCalibration,
  loadCalibration,
  percentile95,
  pooledRmsDb,
  saveCalibration,
  calibrationKey,
} from './shadow-mic-calibration';
import {
  buildCaptureGraph,
  SHADOW_CAPTURE_SPEAKER_GAIN,
  type CaptureGraphContextLike,
} from './shadow-capture';

describe('票 292：校准增益公式与度量口径', () => {
  it('目标 −12：峰值 −30 → +18dB，线性 10^(18/20)', () => {
    const { gainDb, gainLinear } = computeCalibrationGain(-30);
    expect(gainDb).toBe(18);
    expect(gainLinear).toBeCloseTo(10 ** (18 / 20), 10);
  });

  it('钳制：峰值 −5 → 0dB（永不衰减）；峰值 −50 → +30dB（顶格）', () => {
    expect(computeCalibrationGain(-5)).toEqual({ gainDb: 0, gainLinear: 1 });
    const top = computeCalibrationGain(-50);
    expect(top.gainDb).toBe(30);
    expect(top.gainLinear).toBeCloseTo(10 ** (30 / 20), 10);
  });

  it('dBFS：满幅 0dB、0.1 → −20dB、0 → −∞（门控自然判 too-quiet）', () => {
    expect(dbfs(1)).toBe(0);
    expect(dbfs(0.1)).toBeCloseTo(-20, 10);
    expect(dbfs(0)).toBe(-Infinity);
  });

  it('95 百分位 nearest-rank：30 帧中单帧瞬态被排除（取第 29 个）', () => {
    const frames = [...Array(29).fill(0.1), 1.0];
    expect(percentile95(frames)).toBe(0.1);
    expect(percentile95([])).toBeNull();
    expect(percentile95([0.5])).toBe(0.5);
  });

  it('底噪 pooled RMS：[0.5, −0.5] → −6.02dB；空 → −∞', () => {
    expect(pooledRmsDb([0.5, -0.5])).toBeCloseTo(-6.02, 2);
    expect(pooledRmsDb([])).toBe(-Infinity);
  });
});

describe('票 292：门控（too-quiet 优先于 unclear）', () => {
  it('两门同立（峰值极低且贴近底噪）→ too-quiet（硬件建议，不是重测）', () => {
    expect(evaluateCalibration(-60, -70)).toBe('too-quiet');
  });

  it('峰值 −40、底噪 −45（差 5dB < 12）→ unclear；峰值 −30、底噪 −60 → ok', () => {
    expect(evaluateCalibration(-40, -45)).toBe('unclear');
    expect(evaluateCalibration(-30, -60)).toBe('ok');
  });

  it('边界：峰值恰 −50 不判 too-quiet（< −50 才判）', () => {
    expect(evaluateCalibration(-50, -100)).toBe('ok');
    expect(evaluateCalibration(-50.0001, -100)).toBe('too-quiet');
  });
});

describe('票 292：存储键与合法性标准', () => {
  const valid = {
    gainLinear: 10 ** (18 / 20),
    gainDb: 18,
    peakDb: -30,
    floorDb: -60,
    calibratedAt: 123,
  };

  it('键：按设备分键，无设备 → default', () => {
    expect(calibrationKey('abc')).toBe('yi-mic-gain-abc');
    expect(calibrationKey(null)).toBe('yi-mic-gain-default');
  });

  it('合法记录通过；未来 calibratedAt 接受（日期仅展示，无过期逻辑）', () => {
    expect(isValidCalibration(valid)).toBe(true);
    expect(isValidCalibration({ ...valid, calibratedAt: Date.now() + 1e9 })).toBe(true);
  });

  it('非法值一律不通过：非有限/≤0/超顶格/换算不一致/峰值底噪非有限/日期非法', () => {
    expect(isValidCalibration(null)).toBe(false);
    expect(isValidCalibration('x')).toBe(false);
    expect(isValidCalibration({ ...valid, gainLinear: NaN })).toBe(false);
    expect(isValidCalibration({ ...valid, gainLinear: 0 })).toBe(false);
    expect(isValidCalibration({ ...valid, gainLinear: 100 })).toBe(false);
    expect(isValidCalibration({ ...valid, gainDb: 18.02 })).toBe(false);
    expect(isValidCalibration({ ...valid, gainDb: 18.005 })).toBe(true);
    expect(isValidCalibration({ ...valid, peakDb: NaN })).toBe(false);
    expect(isValidCalibration({ ...valid, floorDb: Infinity })).toBe(false);
    expect(isValidCalibration({ ...valid, calibratedAt: NaN })).toBe(false);
  });

  it('写失败（setItem 抛错）→ save 返回 false；读失败/非法值 → null', () => {
    const prev = (globalThis as Record<string, unknown>)['localStorage'];
    try {
      const store = new Map<string, string>();
      (globalThis as Record<string, unknown>)['localStorage'] = {
        getItem: (k: string) => store.get(k) ?? null,
        setItem: (k: string, v: string) => {
          store.set(k, v);
        },
        removeItem: (k: string) => {
          store.delete(k);
        },
      };
      expect(saveCalibration('dev', valid)).toBe(true);
      expect(loadCalibration('dev')).toEqual(valid);
      store.set('yi-mic-gain-dev', 'not-json');
      expect(loadCalibration('dev')).toBeNull();
      (globalThis as Record<string, unknown>)['localStorage'] = {
        getItem: () => null,
        setItem: () => {
          throw new Error('denied');
        },
        removeItem: () => undefined,
      };
      expect(saveCalibration('dev', valid)).toBe(false);
      expect(loadCalibration('dev')).toBeNull();
    } finally {
      (globalThis as Record<string, unknown>)['localStorage'] = prev;
    }
  });
});

describe('票 292：增益接入（buildCaptureGraph 第 4 参数）', () => {
  function fakeCtx(state: string): {
    ctx: CaptureGraphContextLike;
    created: string[];
    gainValue: () => number;
  } {
    const created: string[] = [];
    let gainNode: { gain: { value: number } } | null = null;
    const ctx: CaptureGraphContextLike = {
      state,
      createMediaStreamSource: () => ({ connect: () => undefined, disconnect: () => undefined }),
      createGain: () => {
        created.push('gain');
        gainNode = { gain: { value: 0 } };
        return {
          gain: gainNode.gain,
          connect: () => undefined,
          disconnect: () => undefined,
        };
      },
      createDynamicsCompressor: () => ({
        threshold: { value: 0 },
        knee: { value: 0 },
        ratio: { value: 0 },
        attack: { value: 0 },
        release: { value: 0 },
        connect: () => undefined,
        disconnect: () => undefined,
      }),
      createAnalyser: () => ({
        fftSize: 0,
        getFloatTimeDomainData: () => undefined,
      }),
      createMediaStreamDestination: () => ({
        stream: { getTracks: () => [] } as unknown as MediaStream,
        disconnect: () => undefined,
      }),
    };
    return { ctx, created, gainValue: () => gainNode?.gain.value ?? NaN };
  }

  const stream = { getTracks: () => [] } as unknown as MediaStream;

  it('缺省仍为 3（284 旧行为不动）；传 gainLinear 则覆盖', () => {
    expect(SHADOW_CAPTURE_SPEAKER_GAIN).toBe(3);
    const a = fakeCtx('running');
    const g = buildCaptureGraph(a.ctx, stream, 'speaker');
    expect(g.gained).toBe(true);
    expect(a.gainValue()).toBe(3);
    const b = fakeCtx('running');
    buildCaptureGraph(b.ctx, stream, 'speaker', 10 ** (18 / 20));
    expect(b.gainValue()).toBeCloseTo(10 ** (18 / 20), 10);
  });

  it('非法覆盖值（0/负/NaN）回落 3，绝不把坏增益喂给图', () => {
    for (const bad of [0, -2, NaN]) {
      const f = fakeCtx('running');
      buildCaptureGraph(f.ctx, stream, 'speaker', bad);
      expect(f.gainValue()).toBe(3);
    }
  });

  it('耳机路与 suspended 回落不取校准增益（无增益节点、录原始流）', () => {
    const e = fakeCtx('running');
    const eg = buildCaptureGraph(e.ctx, stream, 'earphones', 7.9);
    expect(eg.gained).toBe(false);
    expect(e.created).not.toContain('gain');
    expect(eg.recordStream).toBe(stream);
    const s = fakeCtx('suspended');
    const sg = buildCaptureGraph(s.ctx, stream, 'speaker', 7.9);
    expect(sg.gained).toBe(false);
    expect(sg.recordStream).toBe(stream);
  });
});

describe('票 292：会话层结构契约', () => {
  const session = fs.readFileSync(path.join(__dirname, 'practice-shadow-session.ts'), 'utf8');
  const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
  const tts = fs.readFileSync(path.join(__dirname, 'practice-tts-player.ts'), 'utf8');
  const grading = fs.readFileSync(path.join(__dirname, 'practice-grading-session.ts'), 'utf8');
  const i18n = fs.readFileSync(
    path.join(__dirname, '../../core/i18n/i18n.service.ts'),
    'utf8',
  );

  function region(src: string, marker: string, endMarker?: string): string {
    const start = src.indexOf(marker);
    expect(start).toBeGreaterThanOrEqual(0);
    const rest = src.slice(start);
    if (!endMarker) return rest;
    const end = rest.indexOf(endMarker);
    return end >= 0 ? rest.slice(0, end) : rest;
  }

  /** 方法体前 n 字符（取消调用恒在方法顶部，窗口内即够断言归属）。 */
  function head(src: string, marker: string, n: number): string {
    const start = src.indexOf(marker);
    expect(start).toBeGreaterThanOrEqual(0);
    return src.slice(start, start + n);
  }

  it('校准信号与入口：两入口共用 calibrating 锁、耳机守门', () => {
    expect(session).toContain('micCalibrated = signal');
    expect(session).toContain('micCalibrating = signal');
    expect(session).toContain('micCalibratePhase = signal');
    expect(session).toContain('micCalibrateLevel = signal');
    expect(session).toContain('micCalibrateMessage = signal');
    expect(session).toContain('async startMicCalibration()');
    expect(session).toContain('cancelMicCalibration(');
    expect(session).toContain('isEarphoneMode()');
  });

  it('停播核对：逐项点名（Howl playing / 元素 paused / tts speaking / grading playingId 与 audio.paused / 试听）', () => {
    const stop = region(session, 'private stopAllReferencePlaybackForCalibration', 'async startMicCalibration');
    expect(stop).toContain('playing() === false');
    expect(stop).toContain('paused === true');
    expect(stop).toContain('this.host.tts.speaking()');
    expect(stop).toContain('this.host.grading.playingId()');
    expect(stop).toContain('this.host.grading.audio.paused');
    expect(stop).toContain('previewingPending');
    expect(stop).toContain('stopOtherAudio()');
  });

  it('成功路径同样清 interval（不只取消路径）；取消清全部 pending 计时器与停轨', () => {
    const finish = region(
      session,
      'private finishMicCalibration',
      'private forceQuietHintForWalkthrough',
    );
    expect(finish).toContain('clearMicCalibrationRun()');
    expect(session).toContain('clearMicCalibrationTimer()');
    const cancel = region(session, 'cancelMicCalibration(message', 'private clearMicCalibrationTimer');
    expect(cancel).toContain('clearMicCalibrationRun()');
  });

  it('设备/模式切换 mid-calibration 中止不写值；重校准失败不覆盖旧增益', () => {
    const tick = region(session, 'private calibTick', 'private finishMicCalibration');
    expect(tick).toContain('micCalibrateDeviceChanged');
    expect(tick).toContain('micCalibrateModeChanged');
    const finish = region(
      session,
      'private finishMicCalibration',
      'private forceQuietHintForWalkthrough',
    );
    expect(finish).toContain('saveCalibration(deviceId, record)');
    expect(finish).toContain('micCalibrated.set(record)');
    expect(finish).not.toContain('micCalibrated.set(null)');
  });

  it('take 开始取消进行中校准；take 开始时读一次存储增益传入', () => {
    const start = region(session, 'async startShadow()', 'private async abortStart');
    expect(start).toContain('cancelMicCalibration()');
    expect(start).toContain('loadCalibration(this.micDeviceId())');
    expect(start).toContain('calibratedGain,');
  });

  it('窗口内播放启动取消校准：行播放/待确认试听/恢复播放/材料朗读/常规回放试听录音', () => {
    expect(region(session, 'async toggleShadowPlay', 'seekShadowLane')).toContain('cancelMicCalibration()');
    expect(region(session, 'async previewShadowPending', 'stopPendingPreview')).toContain('cancelMicCalibration()');
    expect(region(session, 'async toggleShadowSolo', 'stopOtherAudio')).toContain('cancelMicCalibration()');
    expect(region(session, 'async setShadowSoloTogether', 'toggleShadowSolo')).toContain('cancelMicCalibration()');
    expect(region(session, 'async setReviewRate', 'refreshShadowMeta')).toContain('cancelMicCalibration()');
    expect(tts).toContain('cancelMicCalibration()');
    expect(head(grading, 'togglePlay(', 1200)).toContain('cancelMicCalibration()');
    expect(head(grading, 'previewPending()', 1200)).toContain('cancelMicCalibration()');
    expect(head(grading, 'async startRecording()', 1200)).toContain('cancelMicCalibration()');
  });

  it('面板块与过小提示按钮：testid 齐全、耳机模式无提示按钮、点击滚动聚焦', () => {
    expect(html).toContain('data-testid="mic-calibrate-block"');
    expect(html).toContain('data-testid="mic-calibrate"');
    expect(html).toContain('data-testid="mic-calibrate-state"');
    expect(html).toContain('data-testid="mic-calibrate-meter"');
    expect(html).toContain('data-testid="quiet-hint-calibrate"');
    expect(html).toContain('focusMicCalibration()');
    expect(html).toContain('practice.shadow.calibrateMic');
    expect(html).toContain('practice.shadow.recalibrateMic');
    expect(html).toContain('practice.shadow.micCalibrateEarphone');
    expect(session).toContain('document.querySelector');
  });

  it('i18n 十二键三语齐全（校准/重新校准按钮分键）', () => {
    for (const key of [
      'practice.shadow.calibrateMic',
      'practice.shadow.recalibrateMic',
      'practice.shadow.micUncalibrated',
      'practice.shadow.micCalibrated',
      'practice.shadow.micCalibrateSilence',
      'practice.shadow.micCalibrateSpeak',
      'practice.shadow.micCalibrateTooQuiet',
      'practice.shadow.micCalibrateUnclear',
      'practice.shadow.micCalibrateError',
      'practice.shadow.micCalibrateDeviceChanged',
      'practice.shadow.micCalibrateModeChanged',
      'practice.shadow.micCalibrateEarphone',
    ]) {
      const line = i18n.split('\n').find((l) => l.includes(`'${key}'`));
      expect(line).toBeDefined();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });

  it('走查接缝：强制提示旗标只影响显隐（forceQuietHintForWalkthrough）', () => {
    expect(session).toContain('shadowForceQuietHint');
    expect(session).toContain('forceQuietHintForWalkthrough()');
    expect(region(session, 'async toggleShadowMode', 'async refreshMicDevices'))
      .toContain('forceQuietHintForWalkthrough');
  });

  it('CALIBRATION_GAIN_MAX_DB 与 10^(30/20) 上限一致', () => {
    expect(CALIBRATION_GAIN_MAX_DB).toBe(30);
    expect(10 ** (CALIBRATION_GAIN_MAX_DB / 20)).toBeCloseTo(31.62, 2);
  });
});
