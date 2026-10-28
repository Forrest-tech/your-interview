/**
 * 票 250：跟读采集（P28 注入接缝 + P4/P19 启动与中途失败语义）。
 *
 * - 采集服务接受可选 stream provider（构造参数）。生产默认 =
 *   navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true,
 *   noiseSuppression: false } })；沙盒验收传合成流（见本文件末尾）。
 *   除 provider 参数外，生产代码路径不变；无测试专用 UI。
 * - 麦克风优先（P4）：start() 先 await provider，再由 session 起参考；
 *   本服务只负责麦克风侧：MediaRecorder（与 RecorderService 同 pickMime
 *   顺序）+ AnalyserNode 时域采样（60fps 级 rAF）+ 轨道中途 ended 监听
 *   （P19：设备拔线 → onMidTakeFailure，session 停参考、走正常 Stop）。
 * - Zone 陷阱（RecorderService 同款）：MediaRecorder onstop 是属性式
 *   回调，Zone.js 不修补；stop() 的 resolve 经 zoneRun 回到 Angular
 *   Zone（session 传入 NgZone.run；默认恒等函数，单测可用）。
 * - 合成流（P28/P29d，两种构造，均走同一 MediaStreamAudioDestinationNode
 *   路径，recorder/analyser 消费的仍是 MediaStream）：
 *   (i) OscillatorNode（已知频率 + 增益包络，可分浊音/静音段）；
 *   (ii) 预录 blob → decode → AudioBufferSourceNode → destination。
 *   P29d 接缝契约：provider 签名 () => Promise<MediaStream>；测试自持
 *   AudioContext 造合成源，capture 自持 context 经 MediaStreamAudioSourceNode
 *   消费该流（目的流绝不回灌同一 context）；顺序 provider → recorder.start()
 *   → 合成源启动 → 参考启动；清理归测试：停合成轨、断开合成节点、
 *   关闭测试 context（capture context 由应用持有，测试不关）。
 *   断言容差（P29d 数值）：440Hz 振荡 → F0 在 ±15Hz 内；增益开段峰值 > 0；
 *   增益关段峰值 < 5（0~100 尺度）。getSettings()/AEC 不属本接缝，
 *   另由 §8 测试 13 的 mock getSettings() 覆盖。
 *   沙盒中诚实未驱动的仅剩权限弹窗 + 设备枚举（浏览器/OS 表面），
 *   不是整条实时路径。
 */
import { estimateWindowF0, framePeak } from './shadow-waveform.helpers';

export type ShadowStreamProvider = () => Promise<MediaStream>;

export interface ShadowFrame {
  /** 参考音轨时间（秒，由 session 的 Howl seek 供给；无参考时为墙钟映射值）。 */
  trackTimeSec: number;
  /** 0~100 峰值。 */
  peak: number;
  /** 本帧 F0 估计（未门控原始值；门控在 session 层做，保证双侧同门）。 */
  f0: { hz: number; conf: number } | null;
}

export interface ShadowCaptureResult {
  blob: Blob;
  /** 墙钟录音时长（秒）。 */
  voiceWallSec: number;
  /** laneShift（秒）= recorderStartWall − referenceStartWall。 */
  laneShiftSec: number;
  mimeType: string;
}

const MIME_CANDIDATES = [
  'audio/webm;codecs=opus',
  'audio/webm',
  'audio/mp4;codecs=mp4a.40.2',
  'audio/mp4',
  'audio/ogg;codecs=opus',
  'audio/ogg',
];

export function pickShadowMime(): string {
  for (const c of MIME_CANDIDATES) {
    try {
      if (typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(c)) return c;
    } catch {
      /* 某些环境没有 isTypeSupported */
    }
  }
  return '';
}

/**
 * 票 261 §2：麦克风采集约束（唯一口径）。
 * noiseSuppression 恒关（降噪会连人声气声一起吃掉，跟读要的是原声）。
 * autoGainControl 不指定 = 浏览器默认（Chrome 为开），不画蛇添足。
 * 票 278：echoCancellation 由麦克风模式决定 —— 扬声器模式开（跟读时
 * 参考声经扬声器串入麦克风，必须消回声 = 261 原行为）；耳机模式关
 * （戴着耳机本无声学回声可消，回声消除的门控反而把人声切得断断续续）。
 */
export type ShadowMicMode = 'speaker' | 'earphones';

/** 票 278：跟读麦克风模式存储键（与 yi-mic-device 同 localStorage 族；缺省 = 扬声器）。 */
export const SHADOW_MIC_MODE_KEY = 'yi-mic-mode';

/** 票 278：读当前麦克风模式（非法值 / 读失败一律回落扬声器 = 现状）。 */
export function loadShadowMicMode(): ShadowMicMode {
  try {
    return localStorage.getItem(SHADOW_MIC_MODE_KEY) === 'earphones' ? 'earphones' : 'speaker';
  } catch {
    return 'speaker';
  }
}

function micConstraints(micMode: ShadowMicMode): MediaTrackConstraints {
  return {
    echoCancellation: micMode !== 'earphones',
    noiseSuppression: false,
  };
}

/**
 * 票 261 §2：带设备回落的麦克风请求。
 * deviceId 指定时先用 exact 约束；设备已拔/已换导致 OverconstrainedError
 * 时回落到默认设备（绝不因存量 deviceId 失效就整起失败）。
 * 其它错误（NotAllowedError 等）原样上抛，由调用方分级呈现。
 * 票 278：micMode 缺省 = 扬声器（261 原行为）；约束对象按次构造，
 * 授权时定格 —— 此后切模式不影响已授权的流（下一条录音才读新值）。
 */
export async function requestMicStream(
  deviceId: string | null,
  micMode: ShadowMicMode = 'speaker',
): Promise<MediaStream> {
  const mic = micConstraints(micMode);
  if (deviceId) {
    try {
      return await navigator.mediaDevices.getUserMedia({
        audio: { ...mic, deviceId: { exact: deviceId } },
      });
    } catch (e) {
      if ((e as { name?: string } | null)?.name !== 'OverconstrainedError') throw e;
    }
  }
  return navigator.mediaDevices.getUserMedia({ audio: { ...mic } });
}

function defaultProvider(): Promise<MediaStream> {
  return requestMicStream(null);
}

export class ShadowCaptureService {
  private stream: MediaStream | null = null;
  private recorder: MediaRecorder | null = null;
  private chunks: Blob[] = [];
  private ctx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private raf = 0;
  private startedWall = 0;
  private recorderStartedWall = 0;
  private refStartedWall = 0;
  private stopResolve: ((r: ShadowCaptureResult | null) => void) | null = null;
  private stopped = false;

  constructor(
    private readonly provider: ShadowStreamProvider = defaultProvider,
    private readonly zoneRun: (fn: () => void) => void = (fn) => fn(),
    private readonly onFrame: (f: ShadowFrame) => void = () => undefined,
    private readonly onMidTakeFailure: () => void = () => undefined,
    /** session 供给的当前参考音轨时间（Howl seek）；无则用墙钟映射。 */
    private readonly getRefTrackTime: () => number | null = () => null,
    private readonly rate: number = 1,
    /** 票 261 §2：AudioContext 构造接缝（默认 new AudioContext，单测可注入）。 */
    private readonly createCtx: () => AudioContext = () => new AudioContext(),
  ) {}

  /** 是否支持采集（无 mediaDevices/getUserMedia 即不支持，诚实返回 false）。 */
  static supported(): boolean {
    return (
      typeof navigator !== 'undefined' &&
      !!navigator.mediaDevices &&
      typeof navigator.mediaDevices.getUserMedia === 'function' &&
      typeof MediaRecorder !== 'undefined'
    );
  }

  /** 当前流的 echoCancellation 是否生效（P12：getSettings() 核验）。 */
  aecApplied(): boolean | null {
    try {
      const track = this.stream?.getAudioTracks()[0];
      if (!track) return null;
      const s = track.getSettings() as { echoCancellation?: boolean };
      return typeof s.echoCancellation === 'boolean' ? s.echoCancellation : null;
    } catch {
      return null;
    }
  }

  /**
   * 票 261 §2：采集用 AudioContext 是否在跑（null = 未建）。
   * 诊断依据：grant() 在授权弹窗之后建 context，Chrome 自动播放策略
   * 会把它起成 suspended —— 此时 Analyser 读到的全是 0，活声道一片死寂，
   * 而 MediaRecorder（直录原始流）不受影响；两路表现不一致正是用户报的
   * “录音没收进声音”在界面上的样子。调用方据此给可见警告。
   */
  ctxRunning(): boolean | null {
    if (!this.ctx) return null;
    try {
      return this.ctx.state === 'running';
    } catch {
      return null;
    }
  }

  /**
   * 麦克风授权（P4 麦克风优先的第一步）：只拿流、不启动录音机。
   * session 在 grant 成功后起参考，再调 beginRecording —— 两路同起，
   * laneShift 记录剩余的毫秒级启动 skew。抛出的错误原样上抛
   * （NotAllowedError 等由 session 分级呈现）。
   */
  async grant(): Promise<void> {
    this.stopped = false;
    this.chunks = [];
    this.stream = await this.provider();
    const track = this.stream.getAudioTracks()[0];
    if (track) {
      track.addEventListener('ended', () => {
        if (!this.stopped) this.onMidTakeFailure();
      });
    }
    this.ctx = this.createCtx();
    // 票 261 §2：授权弹窗断了用户手势链，Chrome 会把此后建的 context
    // 起成 suspended —— 必须显式 resume（本站已有 sticky activation，
    // 即点击 Start，因此 resume 应成功；失败则由调用方警告，不拦截录音）。
    if (this.ctx.state === 'suspended') {
      try {
        await this.ctx.resume();
      } catch {
        /* 调用方经 ctxRunning() 核验并警告 */
      }
    }
    const src = this.ctx.createMediaStreamSource(this.stream);
    this.analyser = this.ctx.createAnalyser();
    this.analyser.fftSize = 2048;
    src.connect(this.analyser);
    const mime = pickShadowMime();
    this.recorder = this.stream
      ? mime
        ? new MediaRecorder(this.stream, { mimeType: mime })
        : new MediaRecorder(this.stream)
      : null;
    if (this.recorder) {
      this.recorder.ondataavailable = (e) => {
        if (e.data && e.data.size > 0) this.chunks.push(e.data);
      };
      this.recorder.onstop = () => this.zoneRun(() => this.finalize());
    }
  }

  /** grant 之后启动录音机与采样。referenceStartWall = 参考起播墙钟时刻。 */
  beginRecording(referenceStartWallMs: number): void {
    if (!this.recorder) return;
    this.refStartedWall = referenceStartWallMs;
    this.recorderStartedWall = Date.now();
    this.startedWall = this.recorderStartedWall;
    this.recorder.start();
    this.sampleLoop();
  }

  /**
   * 兼容旧调用：授权后立即以当前时刻为参考起点开始（单测/简单调用方）。
   */
  async start(referenceStartWallMs: number): Promise<void> {
    await this.grant();
    this.beginRecording(referenceStartWallMs);
  }

  private sampleLoop(): void {
    if (this.stopped || !this.analyser || !this.ctx) return;
    const td = new Float32Array(this.analyser.fftSize);
    this.analyser.getFloatTimeDomainData(td);
    const refT = this.getRefTrackTime();
    const wallSec = (Date.now() - this.recorderStartedWall) / 1000;
    const laneShiftSec = (this.recorderStartedWall - this.refStartedWall) / 1000;
    const trackTimeSec = refT !== null ? refT : (wallSec + laneShiftSec) * this.rate;
    this.onFrame({
      trackTimeSec,
      peak: framePeak(td),
      f0: estimateWindowF0(td, this.ctx.sampleRate),
    });
    this.raf = requestAnimationFrame(() => this.sampleLoop());
  }

  /** 停止并等待 onstop 落定；返回 null = 空录音/已丢弃。 */
  stop(): Promise<ShadowCaptureResult | null> {
    this.stopped = true;
    cancelAnimationFrame(this.raf);
    return new Promise((resolve) => {
      this.stopResolve = resolve;
      if (this.recorder && this.recorder.state !== 'inactive') {
        this.recorder.stop();
      } else {
        this.finalize();
      }
    });
  }

  /** 丢弃：不产生结果（session 的 Discard 语义由 store 层执行）。 */
  cancel(): void {
    this.stopped = true;
    cancelAnimationFrame(this.raf);
    if (this.recorder && this.recorder.state !== 'inactive') {
      const rec = this.recorder;
      this.recorder = null;
      rec.onstop = null;
      try {
        rec.stop();
      } catch {
        /* 已停 */
      }
    }
    this.chunks = [];
    this.release();
    const resolve = this.stopResolve;
    this.stopResolve = null;
    if (resolve) resolve(null);
  }

  private finalize(): void {
    const mime = this.recorder?.mimeType || this.chunks[0]?.type || 'audio/webm';
    const blob = new Blob(this.chunks, { type: mime });
    const voiceWallSec = Math.max(0, (Date.now() - this.startedWall) / 1000);
    const laneShiftSec = (this.recorderStartedWall - this.refStartedWall) / 1000;
    this.chunks = [];
    this.recorder = null;
    this.release();
    const resolve = this.stopResolve;
    this.stopResolve = null;
    if (resolve) {
      resolve(blob.size === 0 ? null : { blob, voiceWallSec, laneShiftSec, mimeType: mime });
    }
  }

  private release(): void {
    try {
      this.stream?.getTracks().forEach((t) => t.stop());
    } catch {
      /* ignore */
    }
    this.stream = null;
    if (this.ctx) {
      void this.ctx.close().catch(() => undefined);
      this.ctx = null;
    }
    this.analyser = null;
  }
}

/**
 * 合成流 (i)：已知频率正弦 → MediaStreamAudioDestinationNode。
 * 返回 gain 节点供调用方切分浊音/静音段（P29d 容差断言用）；
 * stop() 停振荡、断开全部合成节点、停目的流音轨（P29d 清理）。
 * 注意：osc 在构造时不启动——调用方按 P29d 顺序在 recorder.start()
 * 之后显式调 start()，再起参考。start/stop 均可重复调（幂等保护）。
 */
export function syntheticOscillatorStream(
  freqHz: number,
  ctx: AudioContext,
): { stream: MediaStream; gain: GainNode; start: () => void; stop: () => void } {
  const osc = ctx.createOscillator();
  osc.frequency.value = freqHz;
  osc.type = 'sine';
  const gain = ctx.createGain();
  gain.gain.value = 0.8;
  const dest = ctx.createMediaStreamDestination();
  osc.connect(gain);
  gain.connect(dest);
  let started = false;
  let stopped = false;
  const stop = () => {
    if (stopped) return;
    stopped = true;
    try {
      if (started) osc.stop();
    } catch {
      /* ignore */
    }
    try {
      osc.disconnect();
      gain.disconnect();
      dest.disconnect();
      dest.stream.getTracks().forEach((t) => t.stop());
    } catch {
      /* ignore */
    }
  };
  return {
    stream: dest.stream,
    gain,
    start: () => {
      if (started || stopped) return;
      started = true;
      osc.start();
    },
    stop,
  };
}

/**
 * 合成流 (ii)：预录 blob → 解码 → AudioBufferSourceNode →
 * MediaStreamAudioDestinationNode。返回的 stream 可直接作 provider。
 * stop() 停源、断开节点、停目的流音轨（P29d 清理）。
 */
export async function syntheticDecodedBlobStream(
  blob: Blob,
  ctx: AudioContext,
): Promise<{ stream: MediaStream; stop: () => void }> {
  const buf = await blob.arrayBuffer();
  const audio = await ctx.decodeAudioData(buf);
  const src = ctx.createBufferSource();
  src.buffer = audio;
  const dest = ctx.createMediaStreamDestination();
  src.connect(dest);
  src.start();
  let stopped = false;
  return {
    stream: dest.stream,
    stop: () => {
      if (stopped) return;
      stopped = true;
      try {
        src.stop();
      } catch {
        /* ignore */
      }
      try {
        src.disconnect();
        dest.disconnect();
        dest.stream.getTracks().forEach((t) => t.stop());
      } catch {
        /* ignore */
      }
    },
  };
}
