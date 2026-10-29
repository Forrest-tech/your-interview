/**
 * 票 284：扬声器模式录制端增益 + 参考声压低。
 * 覆盖：
 * - buildCaptureGraph：扬声器+running 走增益路（Gain×3 → 限幅 → 目的流，
 *   Analyser tap 限幅后流）；耳机模式原路不动；suspended 时回落原路
 *   （硬性安全规则：图驱动的流在 suspended 下会录出静音）。
 * - ShadowCaptureService：grant() 后 MediaRecorder 录对流；stop() 出 blob；
 *   release 停目的流与原始流音轨；aec/agc getSettings() 核验。
 * - duckedVolume：压低系数与非法输入透传。
 * 运行环境为 node（jest.config.cjs），故 MediaRecorder / rAF 皆为测试替身，
 * AudioContext 经 createCtx 接缝注入假对象；真实浏览器接线另由走查实证。
 */
import { describe, expect, it } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import {
  buildCaptureGraph,
  SHADOW_CAPTURE_SPEAKER_GAIN,
  ShadowCaptureService,
  type CaptureGraphContextLike,
} from './shadow-capture';
import { duckedVolume, SHADOW_REF_DUCK_FACTOR, VOICE_LIMITER } from './shadow-voice-gain';

type FakeNode = {
  kind: string;
  connects: unknown[];
  disconnected: boolean;
  connect(n: unknown): void;
  disconnect(): void;
};

function fakeNode(kind: string): FakeNode {
  const n: FakeNode = {
    kind,
    connects: [],
    disconnected: false,
    connect(target: unknown): void {
      n.connects.push(target);
    },
    disconnect(): void {
      n.disconnected = true;
    },
  };
  return n;
}

type FakeTrack = {
  stopped: boolean;
  stop(): void;
  addEventListener(): void;
  getSettings(): { echoCancellation: boolean; autoGainControl: boolean };
};

function fakeTrack(): FakeTrack {
  const t: FakeTrack = {
    stopped: false,
    stop(): void {
      t.stopped = true;
    },
    addEventListener(): void {
      /* noop */
    },
    getSettings: () => ({ echoCancellation: true, autoGainControl: true }),
  };
  return t;
}

type FakeCtxBundle = {
  ctx: CaptureGraphContextLike & { state: string; sampleRate: number };
  nodes: Record<string, FakeNode>;
  destStream: { tracks: FakeTrack[]; getTracks(): FakeTrack[] };
  resumed: boolean;
  closed: boolean;
};

/** 假 AudioContext：state 可控；resume 成功时翻 running（除非 resumeFails）。 */
function fakeCtx(state: string, resumeFails = false): FakeCtxBundle {
  const nodes: Record<string, FakeNode> = {};
  const destTracks = [fakeTrack()];
  const destStream = {
    tracks: destTracks,
    getTracks: () => destTracks,
  };
  const gain = fakeNode('gain') as FakeNode & { gain: { value: number } };
  (gain as unknown as { gain: { value: number } }).gain = { value: 0 };
  const limiter = fakeNode('limiter') as FakeNode & {
    threshold: { value: number };
    knee: { value: number };
    ratio: { value: number };
    attack: { value: number };
    release: { value: number };
  };
  const lim = limiter as unknown as {
    threshold: { value: number };
    knee: { value: number };
    ratio: { value: number };
    attack: { value: number };
    release: { value: number };
  };
  lim.threshold = { value: 0 };
  lim.knee = { value: 0 };
  lim.ratio = { value: 0 };
  lim.attack = { value: 0 };
  lim.release = { value: 0 };
  const analyser = fakeNode('analyser') as FakeNode & {
    fftSize: number;
    getFloatTimeDomainData(arr: Float32Array): void;
  };
  (analyser as unknown as { fftSize: number }).fftSize = 0;
  (analyser as unknown as { getFloatTimeDomainData(arr: Float32Array): void }).getFloatTimeDomainData =
    (arr: Float32Array) => {
      arr.fill(0.1);
    };
  const bundle: FakeCtxBundle = {
    ctx: null as unknown as FakeCtxBundle['ctx'],
    nodes,
    destStream,
    resumed: false,
    closed: false,
  };
  const ctx = {
    state,
    sampleRate: 48000,
    resume: async (): Promise<void> => {
      bundle.resumed = true;
      if (!resumeFails) (ctx as { state: string }).state = 'running';
    },
    close: async (): Promise<void> => {
      bundle.closed = true;
      (ctx as { state: string }).state = 'closed';
    },
    createMediaStreamSource: () => {
      nodes['src'] = fakeNode('src');
      return nodes['src'];
    },
    createGain: () => {
      nodes['gain'] = gain;
      return gain;
    },
    createDynamicsCompressor: () => {
      nodes['limiter'] = limiter;
      return limiter;
    },
    createAnalyser: () => {
      nodes['analyser'] = analyser;
      return analyser;
    },
    createMediaStreamDestination: () => {
      const dest = fakeNode('dest') as FakeNode & { stream: unknown };
      (dest as unknown as { stream: unknown }).stream = destStream;
      nodes['dest'] = dest;
      return dest;
    },
  };
  bundle.ctx = ctx as unknown as FakeCtxBundle['ctx'];
  return bundle;
}

class FakeRecorder {
  static instances: FakeRecorder[] = [];
  static isTypeSupported(): boolean {
    return true;
  }
  stream: unknown;
  mimeType: string;
  state = 'inactive';
  ondataavailable: ((e: { data: Blob }) => void) | null = null;
  onstop: (() => void) | null = null;
  constructor(stream: unknown, opts?: { mimeType?: string }) {
    this.stream = stream;
    this.mimeType = opts?.mimeType ?? '';
    FakeRecorder.instances.push(this);
  }
  start(): void {
    this.state = 'recording';
  }
  stop(): void {
    this.state = 'inactive';
    this.ondataavailable?.({ data: new Blob(['x'], { type: 'audio/webm' }) });
    this.onstop?.();
  }
}

(globalThis as unknown as { MediaRecorder: typeof FakeRecorder }).MediaRecorder = FakeRecorder;
(globalThis as unknown as { requestAnimationFrame: () => number }).requestAnimationFrame = () => 0;
(globalThis as unknown as { cancelAnimationFrame: () => void }).cancelAnimationFrame = () => undefined;

function fakeStream(track: FakeTrack): MediaStream {
  return {
    getAudioTracks: () => [track],
    getTracks: () => [track],
  } as unknown as MediaStream;
}

describe('票 284 buildCaptureGraph', () => {
  it('扬声器 + running：Gain×3 → 限幅 → 目的流，Analyser tap 限幅后流', () => {
    const bundle = fakeCtx('running');
    const raw = fakeStream(fakeTrack());
    const graph = buildCaptureGraph(bundle.ctx, raw, 'speaker');
    expect(graph.gained).toBe(true);
    expect(graph.recordStream).toBe(bundle.destStream);
    expect(graph.outputStream).toBe(bundle.destStream);
    expect((bundle.nodes['gain'] as unknown as { gain: { value: number } }).gain.value).toBe(
      SHADOW_CAPTURE_SPEAKER_GAIN,
    );
    expect(SHADOW_CAPTURE_SPEAKER_GAIN).toBe(3);
    const lim = bundle.nodes['limiter'] as unknown as {
      threshold: { value: number };
      knee: { value: number };
      ratio: { value: number };
      attack: { value: number };
      release: { value: number };
    };
    expect(lim.threshold.value).toBe(VOICE_LIMITER.thresholdDb);
    expect(lim.knee.value).toBe(VOICE_LIMITER.kneeDb);
    expect(lim.ratio.value).toBe(VOICE_LIMITER.ratio);
    expect(lim.attack.value).toBe(VOICE_LIMITER.attackSec);
    expect(lim.release.value).toBe(VOICE_LIMITER.releaseSec);
    expect(bundle.nodes['src'].connects).toEqual([bundle.nodes['gain']]);
    expect(bundle.nodes['gain'].connects).toEqual([bundle.nodes['limiter']]);
    expect(bundle.nodes['limiter'].connects).toEqual([
      bundle.nodes['dest'],
      bundle.nodes['analyser'],
    ]);
    expect((bundle.nodes['analyser'] as unknown as { fftSize: number }).fftSize).toBe(2048);
  });

  it('耳机模式：原路不动，不建增益/限幅/目的节点', () => {
    const bundle = fakeCtx('running');
    const raw = fakeStream(fakeTrack());
    const graph = buildCaptureGraph(bundle.ctx, raw, 'earphones');
    expect(graph.gained).toBe(false);
    expect(graph.recordStream).toBe(raw);
    expect(graph.outputStream).toBe(null);
    expect(bundle.nodes['gain']).toBeUndefined();
    expect(bundle.nodes['limiter']).toBeUndefined();
    expect(bundle.nodes['dest']).toBeUndefined();
    expect(bundle.nodes['src'].connects).toEqual([bundle.nodes['analyser']]);
  });

  it('扬声器 + suspended：回落原路（绝不录图驱动的静音流）', () => {
    const bundle = fakeCtx('suspended');
    const raw = fakeStream(fakeTrack());
    const graph = buildCaptureGraph(bundle.ctx, raw, 'speaker');
    expect(graph.gained).toBe(false);
    expect(graph.recordStream).toBe(raw);
    expect(bundle.nodes['gain']).toBeUndefined();
    expect(bundle.nodes['src'].connects).toEqual([bundle.nodes['analyser']]);
  });
});

describe('票 284 ShadowCaptureService', () => {
  it('扬声器模式：MediaRecorder 录目的流；stop 出 blob；释放停两路音轨', async () => {
    FakeRecorder.instances = [];
    const bundle = fakeCtx('running');
    const rawTrack = fakeTrack();
    const raw = fakeStream(rawTrack);
    const svc = new ShadowCaptureService(
      async () => raw,
      (fn) => fn(),
      () => undefined,
      () => undefined,
      () => null,
      1,
      () => bundle.ctx as unknown as AudioContext,
      'speaker',
    );
    await svc.grant();
    expect(FakeRecorder.instances).toHaveLength(1);
    expect(FakeRecorder.instances[0].stream).toBe(bundle.destStream);
    expect(svc.aecApplied()).toBe(true);
    expect(svc.agcApplied()).toBe(true);
    svc.beginRecording(Date.now());
    const result = await svc.stop();
    expect(result).not.toBeNull();
    expect(result?.blob.size).toBeGreaterThan(0);
    expect(bundle.destStream.tracks[0].stopped).toBe(true);
    expect(rawTrack.stopped).toBe(true);
  });

  it('耳机模式：MediaRecorder 录原始流（278 现状原样）', async () => {
    FakeRecorder.instances = [];
    const bundle = fakeCtx('running');
    const raw = fakeStream(fakeTrack());
    const svc = new ShadowCaptureService(
      async () => raw,
      (fn) => fn(),
      () => undefined,
      () => undefined,
      () => null,
      1,
      () => bundle.ctx as unknown as AudioContext,
      'earphones',
    );
    await svc.grant();
    expect(FakeRecorder.instances).toHaveLength(1);
    expect(FakeRecorder.instances[0].stream).toBe(raw);
  });

  it('扬声器模式 + resume 失败仍 suspended：回落原路，不录静音', async () => {
    FakeRecorder.instances = [];
    const bundle = fakeCtx('suspended', true);
    const raw = fakeStream(fakeTrack());
    const svc = new ShadowCaptureService(
      async () => raw,
      (fn) => fn(),
      () => undefined,
      () => undefined,
      () => null,
      1,
      () => bundle.ctx as unknown as AudioContext,
      'speaker',
    );
    await svc.grant();
    expect(bundle.resumed).toBe(true);
    expect(FakeRecorder.instances[0].stream).toBe(raw);
  });
});

describe('票 284 duckedVolume', () => {
  it('系数 0.6：1 → 0.6，0.5 → 0.3；非法输入透传', () => {
    expect(SHADOW_REF_DUCK_FACTOR).toBe(0.6);
    expect(duckedVolume(1)).toBeCloseTo(0.6);
    expect(duckedVolume(0.5)).toBeCloseTo(0.3);
    expect(duckedVolume(NaN)).toBeNaN();
  });
});

describe('票 284 会话参考声压低（结构契约）', () => {
  const session = fs.readFileSync(
    path.join(__dirname, 'practice-shadow-session.ts'),
    'utf8',
  );

  it('take 的参考 howl 仅扬声器模式压低（0.6×，绝非静音），耳机模式 1.0 不动', () => {
    expect(session).toContain('duckedVolume');
    expect(session).toContain("volume: micMode === 'speaker' ? duckedVolume(1) : 1");
  });

  it('压低只施加于本条 take 的 howl，停参考时恢复原音量并清标记', () => {
    expect(session).toContain("this.refDucked = micMode === 'speaker'");
    expect(session).toContain('this.refHowl.volume(1)');
    expect(session).toContain('this.refDucked = false');
  });

  it('同一 micMode 值共用三处：授权约束、录制端增益、参考声压低', () => {
    expect(session).toContain('const micMode = loadShadowMicMode()');
    expect(session).toContain('requestMicStream(this.micDeviceId(), micMode)');
    expect(session).toContain('micMode,');
  });
});
