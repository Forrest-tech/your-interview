/**
 * 票137：Audio Waveform 空条 + 语速/基频指标。
 * 规格 ~/workspace/ticket137-waveform-values/SPEC.md（R-137-1…8，验收 A-1…A-11）。
 *
 * 单测覆盖（Jest）：一位小数 WPM + verdict 带、F0 合成正弦/静音/无浊音、
 * 柱形纯函数、远端取源失败分级、删 Take 清缓存、状态机与模板三态源码对照。
 * 解码路径（AudioContext）jsdom 没有，属活体主证（A-1/A-2/A-9），
 * 与票125 TJ-7 的分工口径一致：DOM 层断言由活体作主证。
 */
import { describe, expect, it } from '@jest/globals';
import { signal, type WritableSignal } from '@angular/core';
import { of, throwError } from 'rxjs';
import { PracticeGradingSession } from './practice-grading-session';
import { computeWaveBarsFromSamples, estimateAvgF0 } from './waveform.helpers';
import type { Recording, RecordingScore } from '../../core/recorder/recorder.service';
import * as fs from 'fs';
import * as path from 'path';

class FakeAudio {
  src = '';
  currentTime = 0;
  onended: (() => void) | null = null;
  play(): Promise<void> { return Promise.resolve(); }
  pause(): void { /* stub */ }
}
(globalThis as { Audio?: unknown }).Audio = FakeAudio;

const DICT: Record<string, { en: string; zh: string }> = {
  'practice.paceSlow': { en: 'Slow', zh: '偏慢' },
  'practice.paceGood': { en: 'Good pace', zh: '适中' },
  'practice.paceFast': { en: 'Fast', zh: '偏快' },
};

function makeScore(over: Partial<RecordingScore> = {}): RecordingScore {
  return {
    pronScore: 80, accuracyScore: 80, fluencyScore: 80, completenessScore: 80,
    prosodyScore: null, recognized: '', words: [], simulated: false, billedSeconds: 60,
    ...over,
  };
}

function makeTake(id: string, score: RecordingScore | null, blob: Blob | null = null): Recording {
  return {
    id, materialId: 'mat-1', createdAt: 1761230000000, duration: 29,
    blob, url: '', score, grading: false, uploaded: true, error: null, version: null,
  } as unknown as Recording;
}

function makeRig(takes: Recording[], activeId: string) {
  const toasts: string[] = [];
  const lang: WritableSignal<'en' | 'zh'> = signal('en');
  const practiceLang: WritableSignal<'en-US' | 'fr-FR' | 'zh-CN'> = signal('en-US');
  let fetchImpl: (id: string) => unknown = () => of(null);
  const host = {
    selectedId: () => 'mat-1',
    recorder: { forMaterial: () => takes, remove: () => { /* stub */ } },
    practiceApi: { fetchRecordingAudio: (id: string) => fetchImpl(id) },
    aiApi: { complete: () => of({ text: '' }) },
    knowledgeApi: { createItem: () => of({}) },
    customQuestion: () => '',
    currentText: () => 'QUESTION-137',
    practiceLang,
    t: (key: string) => DICT[key]?.[lang()] ?? key,
    toast: (msg: string) => { toasts.push(msg); },
    cdr: { detectChanges: () => { /* stub */ } },
    tree: { confirmDialog: () => Promise.resolve(true) },
    tts: {},
  };
  const session = new PracticeGradingSession(host as never);
  session.activeTakeId.set(activeId);
  return { session, toasts, lang, setFetchImpl: (impl: (id: string) => unknown) => { fetchImpl = impl; } };
}

/** 正弦波（Float32Array），供 F0/柱形纯函数测试。 */
function sine(freq: number, seconds: number, sampleRate: number, amp = 0.5): Float32Array {
  const n = Math.floor(seconds * sampleRate);
  const out = new Float32Array(n);
  for (let i = 0; i < n; i++) out[i] = amp * Math.sin((2 * Math.PI * freq * i) / sampleRate);
  return out;
}

describe('票137 R-137-4 语速：一位小数 WPM + verdict 带作用于一位小数后的值', () => {
  it('87 词 / 36 秒 → 145.0，verdict 适中（原整数口径会给 145，数值断言此前不存在）', () => {
    const words = Array.from({ length: 87 }, (_, i) => ({ word: 'w' + i, accuracy: 90, errorType: '', offsetMs: i * 100 }));
    const rig = makeRig([makeTake('take-1', makeScore({ words, billedSeconds: 36 }))], 'take-1');
    const pace = rig.session.speechPace();
    expect(pace?.wpm).toBe('145.0');
    expect(pace?.verdict).toBe('Good pace');
  });

  it('130 词 / 54 秒 → 144.4（144.444… 一位小数，非整数 144）', () => {
    const words = Array.from({ length: 130 }, (_, i) => ({ word: 'w' + i, accuracy: 90, errorType: '', offsetMs: i * 100 }));
    const rig = makeRig([makeTake('take-1', makeScore({ words, billedSeconds: 54 }))], 'take-1');
    expect(rig.session.speechPace()?.wpm).toBe('144.4');
  });

  it('带边界：110.0 → 适中（不 <110）；160.0 → 适中（≤160）；161.0 → 偏快；无词 → null', () => {
    const mk = (count: number, secs: number) => {
      const words = Array.from({ length: count }, (_, i) => ({ word: 'w' + i, accuracy: 90, errorType: '', offsetMs: i * 100 }));
      return makeRig([makeTake('take-1', makeScore({ words, billedSeconds: secs }))], 'take-1');
    };
    expect(mk(66, 36).session.speechPace()?.wpm).toBe('110.0');
    expect(mk(66, 36).session.speechPace()?.verdict).toBe('Good pace');
    expect(mk(96, 36).session.speechPace()?.wpm).toBe('160.0');
    expect(mk(96, 36).session.speechPace()?.verdict).toBe('Good pace');
    expect(mk(161, 60).session.speechPace()?.verdict).toBe('Fast');
    expect(mk(0, 60).session.speechPace()).toBe(null);
  });
});

describe('票137 R-137-4 基频估计（纯函数，合成信号）', () => {
  it('160 Hz 正弦 @8kHz → 160（周期整采样，无八度误差）', () => {
    expect(estimateAvgF0(sine(160, 1, 8000), 8000)).toBe(160);
  });

  it('200 Hz 正弦 @16kHz → 200（降采样至 8kHz 后仍准）', () => {
    expect(estimateAvgF0(sine(200, 1, 16000), 16000)).toBe(200);
  });

  it('120 Hz 正弦 @8kHz → 合理人声范围（117~123，非精确值断言：自相关偏置所致）', () => {
    const f0 = estimateAvgF0(sine(120, 1, 8000), 8000);
    expect(f0).not.toBe(null);
    expect(f0 as number).toBeGreaterThanOrEqual(117);
    expect(f0 as number).toBeLessThanOrEqual(123);
  });

  it('前后静音 + 中段 160 Hz 正弦 → 160（RMS 门限剔静音帧）', () => {
    const sr = 8000;
    const out = new Float32Array(sr);
    const mid = sine(160, 0.6, sr);
    out.set(mid, Math.floor(0.2 * sr));
    expect(estimateAvgF0(out, sr)).toBe(160);
  });

  it('纯静音 → null（绝不返回 0 Hz）', () => {
    expect(estimateAvgF0(new Float32Array(8000), 8000)).toBe(null);
  });

  it('白噪声（确定性伪随机）→ null（归一化自相关峰值远低于浊音门限）', () => {
    const sr = 8000;
    const out = new Float32Array(2 * sr);
    let seed = 123456789;
    for (let i = 0; i < out.length; i++) {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      out[i] = (seed / 0x7fffffff - 0.5) * 1.0;
    }
    expect(estimateAvgF0(out, sr)).toBe(null);
  });

  it('空声道 / 零采样率 → null', () => {
    expect(estimateAvgF0(new Float32Array(0), 8000)).toBe(null);
    expect(estimateAvgF0(sine(160, 1, 8000), 0)).toBe(null);
  });
});

describe('票137 R-137-3 柱形纯函数（与 session 现行规则一致）', () => {
  it('等幅信号 → 56 根全 100', () => {
    const bars = computeWaveBarsFromSamples(sine(160, 1, 8000));
    expect(bars?.length).toBe(56);
    expect(bars?.every((b) => b === 100)).toBe(true);
  });

  it('前半静音、后半满幅 → 前半下限 6、后半 100', () => {
    const data = new Float32Array(5600);
    for (let i = 2800; i < 5600; i++) data[i] = 0.8;
    const bars = computeWaveBarsFromSamples(data);
    expect(bars?.length).toBe(56);
    expect(bars?.slice(0, 28).every((b) => b === 6)).toBe(true);
    expect(bars?.slice(28).every((b) => b === 100)).toBe(true);
  });

  it('全零 / 空 → null', () => {
    expect(computeWaveBarsFromSamples(new Float32Array(8000))).toBe(null);
    expect(computeWaveBarsFromSamples(new Float32Array(0))).toBe(null);
  });
});

describe('票137 R-137-5 远端取源失败分级（waveAudioSourceDetailed）', () => {
  const GUID = '123e4567-e89b-12d3-a456-426614174000';

  it('本地 blob（size > 0）优先，不发请求', async () => {
    const blob = new Blob(['x'], { type: 'audio/webm' });
    const rig = makeRig([makeTake(GUID, null, blob)], GUID);
    rig.setFetchImpl(() => { throw new Error('must not fetch'); });
    const r = await rig.session.waveAudioSourceDetailed(rig.session.activeTake() as never);
    expect(r.kind).toBe('blob');
  });

  it('远端 404 → gone', async () => {
    const rig = makeRig([makeTake(GUID, null)], GUID);
    rig.setFetchImpl(() => throwError(() => ({ status: 404 })));
    const r = await rig.session.waveAudioSourceDetailed(rig.session.activeTake() as never);
    expect(r).toEqual({ kind: 'gone' });
  });

  it('远端非 404 失败 → unavailable；远端空体 → unavailable；非 GUID 无源 → unavailable', async () => {
    const rig = makeRig([makeTake(GUID, null)], GUID);
    rig.setFetchImpl(() => throwError(() => ({ status: 500 })));
    expect(await rig.session.waveAudioSourceDetailed(rig.session.activeTake() as never)).toEqual({ kind: 'unavailable' });
    rig.setFetchImpl(() => of(new Blob([])));
    expect(await rig.session.waveAudioSourceDetailed(rig.session.activeTake() as never)).toEqual({ kind: 'unavailable' });
    const rig2 = makeRig([makeTake('temp-local', null)], 'temp-local');
    expect(await rig2.session.waveAudioSourceDetailed(rig2.session.activeTake() as never)).toEqual({ kind: 'unavailable' });
  });

  it('waveAudioSource 原契约保留：blob → Blob，失败 → null', async () => {
    const blob = new Blob(['x'], { type: 'audio/webm' });
    const rig = makeRig([makeTake(GUID, null, blob)], GUID);
    expect(await rig.session.waveAudioSource(rig.session.activeTake() as never)).toBe(blob);
    rig.setFetchImpl(() => throwError(() => ({ status: 404 })));
    const rig2 = makeRig([makeTake(GUID, null)], GUID);
    rig2.setFetchImpl(() => throwError(() => ({ status: 404 })));
    expect(await rig2.session.waveAudioSource(rig2.session.activeTake() as never)).toBe(null);
  });
});

describe('票137 A-8 删 Take 清波形缓存', () => {
  it('removeRecording 后 waveCache 无该条目', async () => {
    const rig = makeRig([makeTake('take-A', makeScore({})), makeTake('take-B', makeScore({}))], 'take-A');
    rig.session.waveCache.set('take-A', { bars: [50], avgHz: 120 });
    rig.session.removeRecording('take-A');
    await Promise.resolve();
    expect(rig.session.waveCache.get('take-A')).toBeUndefined();
  });
});

describe('票137 状态机与模板三态（源码对照，活体作主证）', () => {
  it('session：waveStatus 四态信号 + waveFailReason + waveAvgHz + waveCache 存 {bars, avgHz}', () => {
    const session = fs.readFileSync(path.join(__dirname, 'practice-grading-session.ts'), 'utf8');
    expect(session).toContain(`waveStatus = signal<'idle' | 'loading' | 'ready' | 'failed'>('idle')`);
    expect(session).toContain(`waveFailReason = signal<'gone' | 'unavailable' | null>(null)`);
    expect(session).toContain(`waveAvgHz = signal<number | null>(null)`);
    expect(session).toContain(`waveCache = new Map<string, { bars: number[]; avgHz: number | null }>()`);
    expect(session).toContain(`waveCache.delete(id)`);
  });

  it('组件 effect：loading 先行、缓存命中与解码成功置 ready、失败分级置 failed、竞态守卫含失败写入', () => {
    const comp = fs.readFileSync(path.join(__dirname, 'ai-practice.component.ts'), 'utf8');
    expect(comp).toContain(`waveStatus.set('loading')`);
    expect(comp).toContain(`waveStatus.set('ready')`);
    expect(comp).toContain(`waveFailReason.set(src.kind === 'gone' ? 'gone' : 'unavailable')`);
    expect(comp).toContain(`waveFailReason.set('unavailable')`);
    const effectIdx = comp.indexOf(`waveStatus.set('loading')`);
    const guardIdx = comp.indexOf(`waveTargetId() !== id) return;`);
    expect(effectIdx).toBeGreaterThan(-1);
    expect(guardIdx).toBeGreaterThan(-1);
  });

  it('模板：三种态分流，.wave-strip 只在 ready 分支内；失败分支无 strip、有分级文案', () => {
    const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
    const loadingIdx = html.indexOf(`waveStatus() === 'loading'`);
    const readyIdx = html.indexOf(`waveStatus() === 'ready'`);
    const failedIdx = html.indexOf(`waveStatus() === 'failed'`);
    const stripIdx = html.indexOf(`class="wave-strip"`);
    const failIdx = html.indexOf(`data-testid="wave-fail"`);
    expect(loadingIdx).toBeGreaterThan(-1);
    expect(readyIdx).toBeGreaterThan(loadingIdx);
    expect(stripIdx).toBeGreaterThan(readyIdx);
    expect(failedIdx).toBeGreaterThan(stripIdx);
    expect(failIdx).toBeGreaterThan(failedIdx);
    expect(html).toContain(`mat-progress-bar mode="indeterminate"`);
    expect(html).toContain(`waveFailReason() === 'gone' ? 'practice.recGone' : 'practice.waveUnavailable'`);
    expect(html).toContain(`data-testid="wave-metrics"`);
    expect(html).toContain(`practice.avgFrequency`);
  });

  it('i18n：新键 practice.waveUnavailable 与 practice.avgFrequency 三语齐全', () => {
    const i18n = fs.readFileSync(path.join(__dirname, '../../core/i18n/i18n.service.ts'), 'utf8');
    for (const key of ['practice.waveUnavailable', 'practice.avgFrequency']) {
      const line = i18n.split('\n').find((l) => l.includes(`'${key}'`));
      expect(line).toBeDefined();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });

  it('scss：未播放柱色不再等于条带底色 #e2f1e8（第二成因已修）', () => {
    const scss = fs.readFileSync(path.join(__dirname, 'ai-practice.component.scss'), 'utf8');
    const barBlock = scss.slice(scss.indexOf('.ws-bar {'));
    const bgLine = barBlock.split('\n').find((l) => l.includes('background:'));
    expect(bgLine).toBeDefined();
    expect(bgLine).not.toContain('#e2f1e8');
  });
});
