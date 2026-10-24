import { signal } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import type { DictCardData } from './dictionary.helpers';
import type { DictionaryPronounceDto } from '../../core/api/practice-api.service';
import type {
  RecorderService, ShadowAssessScore, ShadowRecordingTake,
} from '../../core/recorder/recorder.service';

/**
 * 票127:词典 popup 内跟读(规格 §5–§10)。
 *
 * 本类是跟读会话的唯一状态持有者:目标词形解析(§5.2)、状态机(§6)、
 * 评分复用(§7)、失败态(§10)都在这里;卡组件只渲染 view 并回抛动作,
 * PracticeDictionary 只负责卡生命周期接线(open/close → begin/teardown)。
 *
 * 硬口径:
 * - 原声只认美音真人录音(kind ∈ {oxford, commons}),与卡上口音切换解耦(D2);
 *   无 US 录音 → 跟读区不渲染(§4.2);机器音零出现(§4.4)。
 * - 录音与分数仅会话内持有,不落库(D4);关卡/换词 teardown 释放一切,
 *   迟到响应(含目标解析 GET)以代际 token 丢弃(§6.5)。
 * - 失败全部诚实呈现于跟读区内,不静默、不假分、不整卡崩(§10)。
 */

/** 跟读原声解析结果(对所点词形的一次 pronounce?accent=us 调用)。 */
export type ShadowPronounceOutcome =
  | { status: 'hit'; key: string }
  | { status: 'miss' }
  | { status: 'failed' };

/**
 * 目标词形裁定(规格 §5.2 + D1,纯函数,TJ-2 钉死):
 * 所点词形有 US 真人录音 → 目标 = 该词形;否则回落词元(主端点响应的
 * audio.us 可用且 kind 为真人录音两档之一);两级皆无 → 无目标(区不渲染)。
 * 解析请求失败 ≠ 无目标 —— 独立归 'failed'(F-S7),不许静默当无跟读。
 */
export function pickShadowTarget(args: {
  queriedForm: string;
  pronounce: ShadowPronounceOutcome;
  card: DictCardData;
}):
  | { kind: 'target'; text: string; key: string }
  | { kind: 'none' }
  | { kind: 'failed' } {
  const { queriedForm, pronounce, card } = args;
  if (pronounce.status === 'failed') return { kind: 'failed' };
  if (pronounce.status === 'hit') return { kind: 'target', text: queriedForm, key: pronounce.key };
  // miss:回落词元(所点即词元时无第二级可回)
  if (queriedForm.toLowerCase() !== card.lemma.toLowerCase()) {
    const us = card.audio?.us;
    if (us?.available === true && (us.kind === 'oxford' || us.kind === 'commons') && us.key) {
      return { kind: 'target', text: card.lemma, key: us.key };
    }
  }
  return { kind: 'none' };
}

/** F-S4 判型(规格 §10):响应成功但没识别到任何语音。 */
export function isShadowNotRecognized(score: ShadowAssessScore): boolean {
  return (score.recognized ?? '').trim() === '' && (score.words?.length ?? 0) === 0;
}

/** 结果子区视图(规格 §7.3):总分沿用既有归一口径,分项 null 不渲染,逐词只取目标词条目。 */
export interface ShadowResultView {
  /** round(pronScore ?? accuracyScore ?? 0) —— 与 RecorderService.grade 既有口径一致。 */
  total: number;
  /** 在席分项(顺序:准确/流利/完整/韵律;labelKey 为现有报告的 dim.* 键,不重造)。 */
  dims: { labelKey: string; score: number }[];
  recognized: string;
  /** 与目标文本对应的逐词条目(无对应/为空 → 空数组,卡侧整体不渲染逐词行)。 */
  words: { word: string; accuracy: number; errorType: string }[];
  billedSeconds: number | null;
}

export function shadowResultView(score: ShadowAssessScore, targetText: string): ShadowResultView {
  const dims: { labelKey: string; score: number }[] = [];
  const push = (labelKey: string, v: number | null | undefined): void => {
    if (v !== null && v !== undefined) dims.push({ labelKey, score: Math.round(v) });
  };
  push('dim.accuracy', score.accuracyScore);
  push('dim.fluency', score.fluencyScore);
  push('dim.completeness', score.completenessScore);
  push('dim.prosody', score.prosodyScore);
  const target = targetText.toLowerCase();
  return {
    total: Math.round(score.pronScore ?? score.accuracyScore ?? 0),
    dims,
    recognized: score.recognized ?? '',
    words: (score.words ?? []).filter((w) => w.word.toLowerCase() === target),
    billedSeconds: score.billedSeconds ?? null,
  };
}

/**
 * 逐词 errorType 文案键 —— 与现有报告的既有映射(PracticeGradingSession.errLabel)
 * 逐项同表、同键(err.*),不在 dict.shadow.* 里重造(规格 §7.3)。
 */
export function shadowErrorLabelKey(errorType: string): string {
  switch (errorType) {
    case 'Mispronunciation': return 'err.mispron';
    case 'Omission': return 'err.omission';
    case 'Insertion': return 'err.insertion';
    case 'UnexpectedBreak': return 'err.unexpectedBreak';
    case 'MissingBreak': return 'err.missingBreak';
    case '': return 'err.noData';
    default: return 'err.ok';
  }
}

/** 跟读区失败子型(规格 §10;resolveFailed 单列一相,因其恢复路径是重试解析)。 */
export type DictShadowErrorKind =
  | 'micDenied' | 'scoringUnavailable' | 'scoringFailed' | 'notRecognized' | 'originalFailed';

export type DictShadowPhase =
  | 'idle' | 'playing' | 'recording' | 'recorded' | 'scoring' | 'result' | 'error' | 'resolveFailed';

/** 卡组件消费的跟读区视图快照(view 为 null = 跟读区不渲染:解析中/无目标/非 ok 卡)。 */
export interface DictShadowView {
  phase: DictShadowPhase;
  /** 跟读目标文本(D1 的实际目标;与所点词形不同时如实显示,不冒充)。 */
  targetText: string | null;
  /** 录音中实时秒数。 */
  elapsedSec: number;
  /** 瞬时提示(空录音 F-S6 后回 idle 时在席)。 */
  notice: 'tooShort' | null;
  errorKind: DictShadowErrorKind | null;
  /** error 态下录音是否保留(回放/评分重试可用:F-S2/F-S3)。 */
  hasRecording: boolean;
  /** 原声确认不可播(F-S5):录音类钮禁用(无原声不跟读)。 */
  originalFailed: boolean;
  /** 正在回放的一路(对比回放互斥,§6.4)。 */
  playingWhich: 'original' | 'mine' | null;
  result: ShadowResultView | null;
}

/** 卡 → 会话的动作(与 DictPlayRequest 同例:单一出口 + 可辨识联合)。 */
export type DictShadowAction =
  | { type: 'playOriginal' }
  | { type: 'playMine' }
  | { type: 'record' }
  | { type: 'stop' }
  | { type: 'score' }
  | { type: 'reRecord' }
  | { type: 'again' }
  | { type: 'retryResolve' };

/** 播放器接缝(生产 = HTMLAudioElement 包装;Jest 注入替身驱动 ended/error)。 */
export interface ShadowAudioPlayer {
  play(src: string): void;
  stop(): void;
  onEnded: (() => void) | null;
  onError: (() => void) | null;
}

function defaultPlayerFactory(): ShadowAudioPlayer {
  const audio = new Audio();
  const player: ShadowAudioPlayer = {
    onEnded: null,
    onError: null,
    play(src: string): void {
      audio.src = src;
      void audio.play().catch(() => player.onError?.());
    },
    stop(): void {
      audio.pause();
      audio.removeAttribute('src');
      audio.load();
    },
  };
  audio.addEventListener('ended', () => player.onEnded?.());
  audio.addEventListener('error', () => player.onError?.());
  return player;
}

/** 会话依赖(结构注入:生产由 PracticeDictionary 以宿主的 practiceApi/recorder 填充)。 */
export interface ShadowSessionDeps {
  pronounce: (word: string, accent: 'us' | 'uk') => Observable<DictionaryPronounceDto>;
  fetchAudio: (key: string) => Observable<Blob>;
  recorder: Pick<RecorderService,
    'startShadowRecording' | 'stopShadowRecording' | 'cancelShadowRecording' | 'assessShadowRecording'>;
  createPlayer?: () => ShadowAudioPlayer;
  createObjectUrl?: (blob: Blob) => string;
  revokeObjectUrl?: (url: string) => void;
}

type Phase = 'hidden' | 'resolving' | DictShadowPhase;

export class DictionaryShadowSession {
  /** 跟读区视图;null = 不渲染。 */
  readonly view = signal<DictShadowView | null>(null);

  private phase: Phase = 'hidden';
  /** 代际 token:关卡/换词递增,一切在途异步结果按它丢弃(§6.5)。 */
  private token = 0;
  private queriedForm = '';
  private pronounceOutcome: ShadowPronounceOutcome | null = null;
  private card: DictCardData | null = null;
  private cardOk = false;
  private cardGone = false;
  private target: { text: string; key: string } | null = null;
  private resumePhase: 'idle' | 'recorded' | 'result' | 'error' = 'idle';
  private playingWhich: 'original' | 'mine' | null = null;
  private errorKind: DictShadowErrorKind | null = null;
  private originalFailed = false;
  private notice: 'tooShort' | null = null;
  private myBlob: Blob | null = null;
  private myUrl: string | null = null;
  private resultScore: ShadowAssessScore | null = null;
  private recordStartedAt = 0;
  private elapsedTimer: ReturnType<typeof setInterval> | null = null;
  private elapsedSec = 0;
  private player: ShadowAudioPlayer | null = null;
  private originalUrl: string | null = null;

  constructor(private readonly deps: ShadowSessionDeps) {}

  /** 是否正在录音(PracticeDictionary 据此执行 §9.4 的点外/换词口径)。 */
  get isRecording(): boolean {
    return this.phase === 'recording';
  }

  /** 是否正在送评(宿主关卡走 teardown 时,迟到评分按 token 丢弃)。 */
  get isScoring(): boolean {
    return this.phase === 'scoring';
  }

  // ---------- 目标解析(§5.2:开卡后一次,与卡内容加载并行) ----------

  begin(queriedForm: string): void {
    this.teardown();
    const my = this.token;
    this.queriedForm = queriedForm;
    this.phase = 'resolving';
    this.publish();
    void this.runPronounce(my);
  }

  /** 卡内容落定(ok)时由宿主调入;负卡/错误卡走 attachNoCard。 */
  attachCard(card: DictCardData): void {
    this.card = card;
    this.cardOk = true;
    this.settleResolution();
  }

  attachNoCard(): void {
    this.cardGone = true;
    this.phase = 'hidden';
    this.publish();
  }

  /** F-S7 恢复:重新解析(仅 resolveFailed 态)。 */
  retryResolve(): void {
    if (this.phase !== 'resolveFailed') return;
    const my = this.token;
    this.pronounceOutcome = null;
    this.phase = 'resolving';
    this.publish();
    void this.runPronounce(my);
  }

  private async runPronounce(my: number): Promise<void> {
    let outcome: ShadowPronounceOutcome;
    try {
      const dto = await firstValueFrom(this.deps.pronounce(this.queriedForm, 'us'));
      outcome = dto.available && dto.key && (dto.kind === 'oxford' || dto.kind === 'commons')
        ? { status: 'hit', key: dto.key }
        : { status: 'miss' };
    } catch {
      outcome = { status: 'failed' };
    }
    if (my !== this.token) return; // 迟到丢弃:卡已关/已换词(§6.5,含解析 GET)
    this.pronounceOutcome = outcome;
    this.settleResolution();
  }

  private settleResolution(): void {
    if (this.phase !== 'resolving' || !this.cardOk || !this.card || !this.pronounceOutcome) return;
    const pick = pickShadowTarget({
      queriedForm: this.queriedForm, pronounce: this.pronounceOutcome, card: this.card,
    });
    if (pick.kind === 'failed') {
      this.phase = 'resolveFailed';
    } else if (pick.kind === 'target') {
      this.target = { text: pick.text, key: pick.key };
      this.phase = 'idle';
    } else {
      this.phase = 'hidden';
    }
    this.publish();
  }

  // ---------- 动作分发 ----------

  action(a: DictShadowAction): void {
    switch (a.type) {
      case 'playOriginal': void this.playOriginal(); break;
      case 'playMine': this.playMine(); break;
      case 'record': void this.startRecording(); break;
      case 'stop': this.stopRecording(); break;
      case 'score': void this.score(); break;
      case 'reRecord': void this.startRecording(); break;
      case 'again': this.again(); break;
      case 'retryResolve': this.retryResolve(); break;
    }
  }

  // ---------- 对比回放(§6.4:单播放器 = 天然互斥) ----------

  private ensurePlayer(): ShadowAudioPlayer {
    if (!this.player) {
      this.player = (this.deps.createPlayer ?? defaultPlayerFactory)();
      this.player.onEnded = () => this.onPlaybackEnded();
      this.player.onError = () => this.onPlaybackError();
    }
    return this.player;
  }

  private async playOriginal(): Promise<void> {
    if (!this.target) return;
    if (this.phase !== 'idle' && this.phase !== 'recorded'
      && this.phase !== 'result' && this.phase !== 'error') return;
    const my = this.token;
    this.resumePhase = this.phase;
    this.phase = 'playing';
    this.playingWhich = 'original';
    this.publish();
    let blob: Blob | null = null;
    try {
      blob = await firstValueFrom(this.deps.fetchAudio(this.target.key));
    } catch {
      blob = null;
    }
    if (my !== this.token) return;
    if (!blob || blob.size === 0) {
      this.failOriginal();
      return;
    }
    if (this.originalUrl) this.revokeUrl(this.originalUrl);
    this.originalUrl = this.createUrl(blob);
    this.ensurePlayer().play(this.originalUrl);
  }

  private playMine(): void {
    if (!this.myBlob) return;
    if (this.phase !== 'recorded' && this.phase !== 'result' && this.phase !== 'error') return;
    this.resumePhase = this.phase;
    if (!this.myUrl) this.myUrl = this.createUrl(this.myBlob);
    this.phase = 'playing';
    this.playingWhich = 'mine';
    this.publish();
    this.ensurePlayer().play(this.myUrl);
  }

  private onPlaybackEnded(): void {
    if (this.phase !== 'playing') return;
    this.phase = this.resumePhase;
    this.playingWhich = null;
    this.publish();
  }

  private onPlaybackError(): void {
    if (this.phase !== 'playing') return;
    const which = this.playingWhich;
    this.playingWhich = null;
    if (which === 'original') {
      this.failOriginal();
    } else {
      this.phase = this.resumePhase;
      this.publish();
    }
  }

  /** F-S5:原声不可播 —— 区内诚实呈现,录音钮禁用(无原声不跟读)。 */
  private failOriginal(): void {
    this.originalFailed = true;
    this.errorKind = 'originalFailed';
    this.phase = 'error';
    this.publish();
  }

  // ---------- 录音(§6.3:复用 RecorderService 接缝;30s 上限在服务侧自动停) ----------

  private async startRecording(): Promise<void> {
    if (this.originalFailed) return; // F-S5:无原声不跟读
    if (this.phase !== 'idle' && this.phase !== 'recorded'
      && this.phase !== 'result' && this.phase !== 'error') return;
    if (!this.target) return;
    const my = this.token;
    this.player?.stop();
    let started: Awaited<ReturnType<ShadowSessionDeps['recorder']['startShadowRecording']>>;
    try {
      started = await this.deps.recorder.startShadowRecording();
    } catch {
      started = { status: 'unavailable' } as const;
    }
    if (my !== this.token) {
      // 等权限期间卡已关/换词:刚起的这一路立即丢弃,不留尾巴。
      if (started.status === 'started') this.deps.recorder.cancelShadowRecording();
      return;
    }
    if (started.status !== 'started') {
      // F-S1:权限被拒/无设备('unavailable' 同归此呈现:无法使用麦克风,重试即恢复路径)。
      // 采集从未开始 —— 旧录音按 §6.5 注保留不动。
      this.errorKind = 'micDenied';
      this.phase = 'error';
      this.publish();
      return;
    }
    // 采集真正开始的这一刻才弃旧(§6.5 注:误点重录不丢上一段)。
    this.clearMyRecording();
    this.resultScore = null;
    this.errorKind = null;
    this.notice = null;
    this.phase = 'recording';
    this.recordStartedAt = Date.now();
    this.elapsedSec = 0;
    this.publish();
    this.elapsedTimer = setInterval(() => {
      this.elapsedSec = Math.floor((Date.now() - this.recordStartedAt) / 1000);
      if (this.phase === 'recording') this.publish();
    }, 250);
    void started.done.then((take) => this.onRecordingDone(take, my));
  }

  private stopRecording(): void {
    if (this.phase !== 'recording') return;
    this.deps.recorder.stopShadowRecording();
  }

  private onRecordingDone(take: ShadowRecordingTake | null, my: number): void {
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
    if (my !== this.token) return;
    if (this.phase !== 'recording') return;
    if (!take) {
      // 被取消的兑现只来自 teardown(已自理);此处兜底回 idle。
      this.phase = 'idle';
      this.publish();
      return;
    }
    if (take.durationMs < 300) {
      // F-S6:空录音 —— 丢弃该段、不送评,回 idle 并提示。
      this.phase = 'idle';
      this.notice = 'tooShort';
      this.publish();
      return;
    }
    this.myBlob = take.blob;
    this.phase = 'recorded';
    this.publish();
  }

  // ---------- 评分(§7:复用 assess 链,不落库) ----------

  private async score(): Promise<void> {
    if (!this.myBlob || !this.target) return;
    const fromRecorded = this.phase === 'recorded';
    const fromRetry = this.phase === 'error'
      && (this.errorKind === 'scoringUnavailable' || this.errorKind === 'scoringFailed');
    if (!fromRecorded && !fromRetry) return;
    const my = this.token;
    const blob = this.myBlob;
    this.phase = 'scoring';
    this.publish();
    let outcome: Awaited<ReturnType<ShadowSessionDeps['recorder']['assessShadowRecording']>>;
    try {
      outcome = await this.deps.recorder.assessShadowRecording(blob, this.target.text);
    } catch {
      outcome = { kind: 'failed', status: null } as const;
    }
    if (my !== this.token) return; // 迟到丢弃:关卡/换词后分数不得写入(§6.5)
    if (outcome.kind === 'score') {
      if (isShadowNotRecognized(outcome.score)) {
        this.errorKind = 'notRecognized';
        this.phase = 'error';
      } else {
        this.resultScore = outcome.score;
        this.errorKind = null;
        this.phase = 'result';
      }
    } else if (outcome.kind === 'unavailable') {
      this.errorKind = 'scoringUnavailable'; // F-S2:录音保留,可直接重试评分
      this.phase = 'error';
    } else {
      this.errorKind = 'scoringFailed'; // F-S3(含解码失败:评分没跑成,如实归失败)
      this.phase = 'error';
    }
    this.publish();
  }

  /** 再练一次:清分数与录音,回入口形态(§6.2)。 */
  private again(): void {
    if (this.phase !== 'result') return;
    this.clearMyRecording();
    this.resultScore = null;
    this.errorKind = null;
    this.phase = 'idle';
    this.publish();
  }

  // ---------- teardown(§6.5:关卡/换词的硬性清理) ----------

  teardown(): void {
    this.token++;
    if (this.phase === 'recording') this.deps.recorder.cancelShadowRecording();
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
    this.player?.stop();
    this.clearMyRecording();
    if (this.originalUrl) {
      this.revokeUrl(this.originalUrl);
      this.originalUrl = null;
    }
    this.phase = 'hidden';
    this.queriedForm = '';
    this.pronounceOutcome = null;
    this.card = null;
    this.cardOk = false;
    this.cardGone = false;
    this.target = null;
    this.resumePhase = 'idle';
    this.playingWhich = null;
    this.errorKind = null;
    this.originalFailed = false;
    this.notice = null;
    this.resultScore = null;
    this.elapsedSec = 0;
    this.publish();
  }

  private clearMyRecording(): void {
    if (this.myUrl) {
      this.revokeUrl(this.myUrl);
      this.myUrl = null;
    }
    this.myBlob = null;
  }

  private createUrl(blob: Blob): string {
    if (this.deps.createObjectUrl) return this.deps.createObjectUrl(blob);
    return typeof URL !== 'undefined' && 'createObjectURL' in URL ? URL.createObjectURL(blob) : '';
  }

  private revokeUrl(url: string): void {
    if (this.deps.revokeObjectUrl) {
      this.deps.revokeObjectUrl(url);
      return;
    }
    if (typeof URL !== 'undefined' && 'revokeObjectURL' in URL) URL.revokeObjectURL(url);
  }

  private publish(): void {
    if (this.phase === 'hidden' || this.phase === 'resolving' || this.cardGone) {
      this.view.set(null);
      return;
    }
    this.view.set({
      phase: this.phase,
      targetText: this.target?.text ?? null,
      elapsedSec: this.elapsedSec,
      notice: this.notice,
      errorKind: this.errorKind,
      hasRecording: this.myBlob !== null,
      originalFailed: this.originalFailed,
      playingWhich: this.playingWhich,
      result: this.phase === 'result' && this.resultScore && this.target
        ? shadowResultView(this.resultScore, this.target.text)
        : null,
    });
  }
}
