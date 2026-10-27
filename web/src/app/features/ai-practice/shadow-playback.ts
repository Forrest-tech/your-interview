/**
 * 票 250：跟读播放控制器（P21 单一接口 + P16 漂移校正 + §3 seek + P29f）。
 *
 * 一个接口覆盖两种播放形态，组件只认 PlaybackSource，绝不按 mix/dual
 * 分支写播放逻辑：
 * - mix：单元素，漂移校正关闭；后台可见性规则不适用（P29f：1x mix
 *   播放不受影响）。
 * - dual：人声元素为主时钟（自然 1x），参考元素为跟随者（按审阅率
 *   R_review 走 preservesPitch）；每 200ms 按 expectedRefTime 纠偏，
 *   超 50ms 才 seek（P16）。
 * - P29f 后台规则（仅 dual）：visibilitychange → hidden 时两元素暂停
 *   （后台不漂移）；visible 时按 §3 seek 公式重映射位置再恢复播放，
 *   200ms 纠偏环随之恢复。用户在 hidden 期间手动暂停会取消自动恢复
 *   （pause() 清 resumePending，见下）。
 * 元素以最小接口注入（AudioElementLike），Jest 可用假元素测漂移/seek/
 * 暂停续播，无需真实 <audio>。真实元素由 session 传入 HTMLAudioElement
 * 与 Howl 的底层 <audio> 节点（preservesPitch 由 session 在创建时重申）。
 * 文档对象同样可注入（DocumentLike，默认全局 document；Jest 传假文档）。
 */
import {
  expectedRefTime,
  needsDriftCorrection,
  seekVoiceTime,
} from './shadow-waveform.helpers';

export interface AudioElementLike {
  currentTime: number;
  duration: number;
  playbackRate: number;
  volume: number;
  paused: boolean;
  play(): Promise<void> | void;
  pause(): void;
}

export type PlaybackMode = 'mix' | 'dual';
export type PlaybackTrackId = 'voice' | 'reference' | 'mix';

export interface PlaybackTrack {
  id: PlaybackTrackId;
  el: AudioElementLike;
  /** 该轨在审阅时的自然时长（秒）：voice = 墙钟时长，reference = 1x 轨长。 */
  durationSec: number;
}

export interface PlaybackSource {
  mode: PlaybackMode;
  tracks: PlaybackTrack[];
  /** 审阅率（独立于 take 率，P25）。 */
  reviewRate: number;
  laneShiftSec: number;
  /** 审阅 seek 起点（参考音轨时间），默认 0。 */
  seekOffsetSec: number;
}

/**
 * seek 的完整逆映射（helpers.seekVoiceTime）：voiceWall =
 * (T − seekOffset)/R − laneShift，再钳到 [0, voiceDur]。§3 文本公式
 * 是 laneShift≈0 时的特例（采集同起，skew 毫秒级），此处用完整式。
 */
/** visibility 监听所需的最小文档面（浏览器 document / Jest 假文档）。 */
export interface DocumentLike {
  hidden: boolean;
  addEventListener(type: string, fn: () => void): void;
  removeEventListener(type: string, fn: () => void): void;
}

export class ShadowPlaybackController {
  private timer: ReturnType<typeof setInterval> | null = null;
  private playing = false;
  /**
   * 播放代次：play() 是异步的（await 元素 play），pause()/destroy() 使
   * 旧代次失效——旧 play 在 await 后醒来时不再起定时器（否则 destroy
   * 后仍有悬空 interval，Jest 无法退出；生产里展开行关闭时同理）。
   */
  private playSeq = 0;
  /** 上次 seek/起播时的 (seekStartRef, voiceStartWall) 锚点。 */
  private anchorRef = 0;
  private anchorVoice = 0;
  private readonly onTime: (refTimeSec: number) => void;
  /** P29f：后台自动暂停后、回到前台时是否恢复播放。 */
  private resumePending = false;
  private readonly doc: DocumentLike | null;
  private readonly onVisibility = () => this.handleVisibility();

  constructor(
    private readonly source: PlaybackSource,
    onTime?: (refTimeSec: number) => void,
    doc?: DocumentLike | null,
  ) {
    this.onTime = onTime ?? (() => undefined);
    this.doc =
      doc ?? (typeof document !== 'undefined' ? (document as unknown as DocumentLike) : null);
    this.doc?.addEventListener('visibilitychange', this.onVisibility);
  }

  /** P29f：hidden → 暂停（仅 dual）；visible + 自动暂停过 → seek 重映射后恢复。 */
  private handleVisibility(): void {
    if (this.source.mode !== 'dual' || !this.doc) return;
    if (this.doc.hidden) {
      if (this.playing) {
        this.pause();
        this.resumePending = true;
      }
      return;
    }
    if (!this.resumePending) return;
    this.resumePending = false;
    const at = this.currentRefTime();
    this.seek(at);
    void this.play();
  }

  get isPlaying(): boolean {
    return this.playing;
  }

  private voiceTrack(): PlaybackTrack | undefined {
    return this.source.tracks.find((t) => t.id === 'voice');
  }

  private refTrack(): PlaybackTrack | undefined {
    return this.source.tracks.find((t) => t.id === 'reference');
  }

  private mixTrack(): PlaybackTrack | undefined {
    return this.source.tracks.find((t) => t.id === 'mix');
  }

  /** 当前参考音轨时间（mix：元素时间；dual：参考元素时间）。 */
  currentRefTime(): number {
    const mix = this.mixTrack();
    if (mix) return mix.el.currentTime;
    return this.refTrack()?.el.currentTime ?? 0;
  }

  async play(): Promise<void> {
    const seq = ++this.playSeq;
    const mix = this.mixTrack();
    if (mix) {
      mix.el.playbackRate = 1;
      await mix.el.play();
      if (seq !== this.playSeq) return;
      this.playing = true;
      this.startTimer();
      return;
    }
    const voice = this.voiceTrack();
    const ref = this.refTrack();
    if (!voice || !ref) return;
    voice.el.playbackRate = 1;
    ref.el.playbackRate = this.source.reviewRate;
    // 起播锚点：当前两元素位置即联合起点（seek 后由 seek() 重置）。
    this.anchorRef = ref.el.currentTime;
    this.anchorVoice = voice.el.currentTime;
    await Promise.all([voice.el.play(), ref.el.play()]);
    if (seq !== this.playSeq) return;
    this.playing = true;
    this.startTimer();
  }

  pause(): void {
    this.playSeq++;
    for (const t of this.source.tracks) t.el.pause();
    this.playing = false;
    // 用户手动暂停（含 hidden 期间的手动暂停）取消后台自动恢复。
    // 后台 handler 先调 pause() 再置 resumePending=true，顺序保证恢复。
    this.resumePending = false;
    this.stopTimer();
  }

  /** 停止并归零（不删元素，元素生命周期归 session）。 */
  stop(): void {
    this.pause();
    for (const t of this.source.tracks) t.el.currentTime = 0;
    this.anchorRef = this.source.seekOffsetSec;
    this.anchorVoice = 0;
    this.onTime(this.source.seekOffsetSec);
  }

  /** seek：目标为参考音轨时间 T；人声按完整逆映射钳制（§3 + helpers 注）。 */
  seek(trackTimeSec: number): void {
    const mix = this.mixTrack();
    if (mix) {
      mix.el.currentTime = Math.min(Math.max(trackTimeSec, 0), Math.max(0, mix.durationSec));
      this.onTime(mix.el.currentTime);
      return;
    }
    const voice = this.voiceTrack();
    const ref = this.refTrack();
    if (!voice || !ref) return;
    const refT = Math.min(Math.max(trackTimeSec, 0), Math.max(0, ref.durationSec));
    ref.el.currentTime = refT;
    voice.el.currentTime = seekVoiceTime(
      refT,
      this.source.seekOffsetSec,
      this.source.laneShiftSec,
      this.source.reviewRate,
      voice.durationSec,
    );
    this.anchorRef = refT;
    this.anchorVoice = voice.el.currentTime;
    this.onTime(refT);
  }

  /** 变速（P25）：暂停 → 按 seek 公式重映射位置 → 按新率恢复播放态。 */
  async setRate(rate: number): Promise<void> {
    if (!(rate > 0)) return;
    const wasPlaying = this.playing;
    const at = this.currentRefTime();
    this.pause();
    this.source.reviewRate = rate;
    this.seek(at);
    if (wasPlaying) await this.play();
  }

  /** 独奏/静音：某轨音量 1/0；其余轨不动（session 层决定单轨播放形态）。 */
  setMuted(trackId: PlaybackTrackId, muted: boolean): void {
    const t = this.source.tracks.find((x) => x.id === trackId);
    if (t) t.el.volume = muted ? 0 : 1;
  }

  /**
   * 定时单步（P16 漂移校正 + P21 onTimeUpdate，200ms 定时器与单测共用）。
   * mix 形态同样要发 onTime——否则混音/独奏播放时播放头永不推进
   * （走查实证：已提交 mix 行播放时播放头冻结在 0%）。
   */
  tick(): void {
    // 单元素控制器（mix 合轨、voice 独奏、reference 独奏）都由此发 onTime。
    const single = this.source.tracks.length === 1 ? this.source.tracks[0] : undefined;
    if (single) {
      if (!single.el.paused) this.onTime(single.el.currentTime);
      return;
    }
    if (this.source.mode !== 'dual') return;
    const voice = this.voiceTrack();
    const ref = this.refTrack();
    if (!voice || !ref) return;
    if (voice.el.paused || ref.el.paused) return;
    const expected = expectedRefTime(
      voice.el.currentTime,
      this.anchorVoice,
      this.anchorRef,
      this.source.reviewRate,
    );
    if (needsDriftCorrection(ref.el.currentTime, expected)) {
      ref.el.currentTime = Math.min(Math.max(expected, 0), Math.max(0, ref.durationSec));
    }
    this.onTime(ref.el.currentTime);
  }

  private startTimer(): void {
    this.stopTimer();
    this.timer = setInterval(() => this.tick(), 200);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  destroy(): void {
    this.pause();
    this.doc?.removeEventListener('visibilitychange', this.onVisibility);
  }
}
