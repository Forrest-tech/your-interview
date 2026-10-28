/**
 * 票 250：跟读模式会话（PLAN-PATCHED4 P1–P29 的实现委托类）。
 *
 * 与 PracticeTtsPlayer / PracticeGradingSession 同构：组件只作暴露，
 * 状态机与音频逻辑全在此类。模板绑定的信号与方法均为 public。
 *
 * 关键口径（实现与计划的对应关系，偏差见文件末注）：
 * - 采集：麦克风优先（grant → 起参考 → beginRecording），任一腿失败
 *   整体 abort、无半条 take；采集恒从参考 0 起（P24，seekOffset_capture=0）。
 * - 实时：参考峰值预渲染 + 活声 Analyser 60fps 生长 + 播放头（Howl seek，
 *   200ms）；音高条双侧同门（P23：estimateWindowF0 浊音/置信门 +
 *   medianHz 短中值 + continuityGateHz [0.7,1.4] 连续门）。
 * - 混音：仅 1x；缓存缺失先走既有 Azure TTS 重生成再重试一次（P29h），
 *   两路都失败 = 本地保留 + 显式重录，永不上传纯活声“跟读”行（P26）。
 * - 存储：Stop 即持久化待提交记录（P14），写失败显式报错 + 保留内存态
 *   审阅 + retry-write/Discard（P29g）；孤儿口径与重键走 shadow-store，
 *   重键校验为 decode-based；行时长 = jointDuration（laneShift≈0 时
 *   退化为 §5 max，见 helpers 注）。
 * - 列表（P29a，票258 S1 修订）：行是跟读行 IFF **服务端 IsShadow 为 true
 *   OR 本地 store 有链接**（本地 take id / 持久待提交 / 重键 GUID），
 *   本地链接优先——覆盖“已上传但无标识”的过渡期历史行；两者皆无的行
 *   仍按非跟读处理（P29a fallback 不变）。S1 之前：无本地链接的服务端行
 *   默认非跟读（常规列表），跨设备/清库后混音行即普通服务端行——
 *   S1 之后该句仅适用于 IsShadow 为 false/缺省的行。
 *   跟读模式只显跟读行，常规模式只显非跟读行（P24）；跟读行
 *   永不调用评分路径（本类无任何 grade 调用，模板跟读行无评分钮）。
 * - 非 1x 行（P29b）：本地-only，无服务端行、无 Submit；Stop 即保存
 *   为本地行（mixStatus 'local'），扫除永不删，只由显式行删除删；
 *   行时长是本地展示时长，绝不 post。
 * - 审阅率独立于 take 率（P25）：变速 = 暂停 → 重映射 → 恢复。
 * - 偏差（诚实登记，见末注 D-250-1）：提交不走共享 pendingTake 单槽，
 *   改走同一 uploadRecording FormData 端点直传，理由见末注。
 */
import { computed, signal } from '@angular/core';
import { Howl, Howler } from 'howler';
import { firstValueFrom } from 'rxjs';
import type { AiPracticeComponent } from './ai-practice.component';
import { getCachedAudio, getCachedAudioByMaterial, putCachedAudio, ttsKey } from './tts-audio-cache';
import {
  computeF0Contour,
  computeMixSchedule,
  continuityGateHz,
  decimatePeaks,
  jointDuration,
  medianHz,
  peaksToLinePoints,
  trackTimeToVoiceTime,
} from './shadow-waveform.helpers';
import type { ShadowPeak } from './shadow-waveform.helpers';
import { ShadowCaptureService, requestMicStream } from './shadow-capture';
import type { ShadowFrame } from './shadow-capture';
import { ShadowPlaybackController } from './shadow-playback';
import type { PlaybackTrack } from './shadow-playback';
import {
  deleteShadowTake,
  getShadowTakesForMaterial,
  putShadowTake,
  rekeyShadowTake,
  sweepShadowOrphans,
} from './shadow-store';
import type { ShadowMixStatus, ShadowTakeRecord } from './shadow-store';
import { RecorderService } from '../../core/recorder/recorder.service';
import type { Recording } from '../../core/recorder/recorder.service';

/** 模板用的跟读行视图（驱动自 shadow-store 记录 + 录音条目）。 */
export interface ShadowRowView {
  /** 显示 id：服务端 GUID（已上传）或本地 take id（未上传/本地行）。 */
  id: string;
  key: string;
  localTakeId: string;
  serverId: string | null;
  createdAt: number;
  durationSec: number;
  rate: number;
  kind: 'mix' | 'dual';
  mixStatus: ShadowMixStatus;
  /** 服务端行是否存在（本地行 false）。 */
  uploaded: boolean;
  /** 双轨对齐 laneShift（秒；本地行取 store，服务端-only 行取 DTO，缺省 0）。 */
  laneShiftSec?: number;
  /**
   * 票 261 §1：时间先后号（最早 = #1，永不随显示顺序变）。
   * serverOnlyShadowRows 产出的行先填 0，shadowRows computed 统一编号。
   */
  no: number;
}


/**
 * ★ 票260 S3：服务端行 kind 派生（前后端同一公式，与后端 DTO 注释、assess 拒绝注释同一句话）：
 * IsShadow=true 且 StoragePath 非空 → 跟读-混音行；
 * IsShadow=true 且 StoragePath 为空 且 VoiceStoragePath 非空 → 跟读-only 行；
 * IsShadow=true 且两路径皆空 → 未完成行；其余 → 普通行。
 * 前端以 hasMixTrack（StoragePath 非空）+ hasVoiceTrack（VoiceStoragePath 非空）计算。
 */
export type ServerRecordingKind = 'ordinary' | 'shadow-mix' | 'shadow-only' | 'shadow-unfinished';

export function recordingKind(
  isShadow: boolean | undefined,
  hasMixTrack: boolean | undefined,
  hasVoiceTrack: boolean | undefined,
): ServerRecordingKind {
  if (isShadow !== true) return 'ordinary';
  if (hasMixTrack === true) return 'shadow-mix';
  if (hasVoiceTrack === true) return 'shadow-only';
  return 'shadow-unfinished';
}


/**
 * 票 261 §1：纯函数 —— 按 createdAt 升序编号（最早 = 1）。
 * 显示顺序与编号解耦：列表倒序展示时，号大的新 take 仍在前面。
 */
export function assignShadowNumbers<T extends { id: string; createdAt: number }>(
  rows: readonly T[],
): Map<string, number> {
  const map = new Map<string, number>();
  [...rows].sort((a, b) => a.createdAt - b.createdAt).forEach((r, i) => map.set(r.id, i + 1));
  return map;
}

/** 票 261 §1：纯函数 —— 最新在上（createdAt 降序），不改原数组。 */
export function orderNewestFirst<T extends { createdAt: number }>(rows: readonly T[]): T[] {
  return [...rows].sort((a, b) => b.createdAt - a.createdAt);
}

/** 待提交跟读（Stop 后、提交前的审阅态；仅 mix 行有提交，dual 行无 pending 态）。 */
export interface ShadowPendingView {
  localTakeId: string;
  materialId: string;
  rate: number;
  kind: 'mix' | 'dual';
  durationSec: number;
  refDurationSec: number;
  voiceWallSec: number;
  laneShiftSec: number;
  mixStatus: ShadowMixStatus;
  /** 跟读原文（P29h：缓存缺失时重请求 TTS 用）。 */
  text?: string;
}

/**
 * ★ 票258 S1 分类规则（纯函数，跟读行判定唯一口径，见 S3-PLAN §2.2）：
 * 行是跟读行 IFF 服务端 IsShadow 为 true OR 本地 shadow-store 有链接。
 * 本地链接优先：服务端标识为 false/缺省、但本地有链接的行仍是跟读行
 * （覆盖“已上传但无标识”的过渡期历史行）；两者皆无 = 非跟读行。
 * 无本地链接且 IsShadow 为 false/缺省的行仍按非跟读处理（P29a fallback 不变）。
 */
export function isShadowRow(serverIsShadow: boolean | undefined, hasLocalLink: boolean): boolean {
  return serverIsShadow === true || hasLocalLink;
}

/**
 * ★ 票258 S1：无本地链接的服务端跟读行（另一设备上的 1x 混音行）。
 * 这类行只有服务端混音可播：kind 恒为 'mix'（dual 行无 Submit，不可能
 * 经服务端来）、mixStatus 'done'、localTakeId 为空串（无本地 take）。
 * ★ 票259 S2：take 率取服务端 TakeRate 列（缺省 1 = S1 之前录的旧行，
 * 无对齐参数）；展开时 meta 为 null → 人声道先试取服务端人声轨，
 * 取不到才走既有降级态（“本设备无此人声道”+ solo 禁用），混音经服务端
 * 取回播放；不新增降级文案。
 * ★ 票260 S3：kind 改由 rate 派生（rate===1 → 'mix'，否则 'dual'，与本地
 * take 的 kind 口径一致）；未完成行（无本地链接且无混音、无人声轨）
 * 在别设备不可见 —— 无混音、无人声，列出来就是死行，不列。
 * 兼容：两轨字段皆缺省 = S3 之前的响应（那时服务端行恒有混音），按混音行处理，
 * S1/S2 旧单测与旧响应不受影响；S3 响应恒带两字段，缺省分支不再触发。
 */
export function serverOnlyShadowRows(
  recordings: Pick<Recording, 'id' | 'createdAt' | 'duration' | 'isShadow' | 'takeRate' | 'hasVoiceTrack' | 'hasMixTrack' | 'laneShiftSec'>[],
  linkedIds: ReadonlySet<string>,
): ShadowRowView[] {
  const rows: ShadowRowView[] = [];
  for (const r of recordings) {
    if (r.isShadow !== true || linkedIds.has(r.id)) continue;
    // 未完成行（两轨皆明确无）→ 别设备不可见；两字段皆缺省 = 旧响应 → 混音行。
    if (r.hasMixTrack === false && r.hasVoiceTrack === false) continue;
    const rate = r.takeRate ?? 1;
    rows.push({
      id: r.id,
      key: r.id,
      localTakeId: '',
      serverId: r.id,
      createdAt: r.createdAt,
      durationSec: r.duration,
      rate,
      kind: rate === 1 ? 'mix' : 'dual',
      mixStatus: 'done',
      uploaded: true,
      laneShiftSec: r.laneShiftSec ?? 0,
      no: 0, // 票 261 §1：占位，shadowRows computed 统一按时间先后编号。
    });
  }
  return rows;
}

/**
 * 票 264：参考播完是否自动停。三条件缺一即 no-op：未在录音（已手动停）
 * → 不停；正在停（stopShadow 已进入）→ 不双停；触发 end 的 howl 已不是
 * 当前参考（新 take 已重建 refHowl，旧 end 事件迟到）→ 不误停新录音。
 * 暂停不走 Howler onend，故暂停天然不停；参考不可用时 startShadow 在
 * 录音起前已 abort，无录音可停。
 */
export function shouldAutoStopOnRefEnd(
  isRecording: boolean,
  isStopping: boolean,
  isCurrentHowl: boolean,
): boolean {
  return isRecording && !isStopping && isCurrentHowl;
}

/** 音高点门：双侧同一实现（P23 对称门控）。 */
class PitchGate {
  private recent: number[] = [];
  private shown: number[] = [];

  push(candidate: number | null): number | null {
    if (candidate === null || !Number.isFinite(candidate)) return null;
    this.recent.push(candidate);
    if (this.recent.length > 3) this.recent.shift();
    const smoothed = medianHz(this.recent);
    if (smoothed === null) return null;
    const gated = continuityGateHz(smoothed, this.shown);
    if (gated === null) return null;
    this.shown.push(gated);
    if (this.shown.length > 5) this.shown.shift();
    return gated;
  }

  reset(): void {
    this.recent = [];
    this.shown = [];
  }
}

function extOf(mime: string): string {
  const m = (mime || '').toLowerCase();
  if (m.includes('ogg')) return 'ogg';
  if (m.includes('mp4') || m.includes('m4a')) return 'm4a';
  if (m.includes('wav')) return 'wav';
  return 'webm';
}

function howlFormat(mime: string): string[] {
  const t = (mime || '').toLowerCase();
  if (t.includes('mpeg') || t.includes('mp3')) return ['mp3'];
  if (t.includes('wav') || t.includes('wave')) return ['wav'];
  if (t.includes('ogg') || t.includes('opus')) return ['oga', 'ogg'];
  if (t.includes('m4a') || t.includes('mp4') || t.includes('aac')) return ['m4a', 'aac'];
  if (t.includes('flac')) return ['flac'];
  if (t.includes('webm')) return ['webm'];
  return ['mp3'];
}

/** 解码后按 0.1s 桶取峰（参考 lane 用；t = 桶起点，v = 0~100）。 */
function bucketPeaks(data: ArrayLike<number>, sampleRate: number): ShadowPeak[] {
  if (!data.length || !(sampleRate > 0)) return [];
  const bucket = Math.max(1, Math.floor(sampleRate * 0.1));
  const out: ShadowPeak[] = [];
  for (let s = 0; s < data.length; s += bucket) {
    let max = 0;
    const end = Math.min(data.length, s + bucket);
    for (let i = s; i < end; i++) {
      const v = Math.abs(data[i]);
      if (v > max) max = v;
    }
    out.push({ t: s / sampleRate, v: Math.round(max * 100) });
  }
  return out;
}

export class PracticeShadowSession {
  constructor(private readonly host: AiPracticeComponent) {}

  // ---------- 模式与倍速 ----------
  readonly shadowMode = signal(false);
  /** 倍速率表（现仅回放菜单用；录制率跟示范条 x1，票 265）。 */
  readonly shadowRates = [0.5, 0.75, 1, 1.25, 1.5, 2];
  /** 审阅率（当前展开行的 listening 率，独立于 take 率，P25）。 */
  readonly shadowReviewRate = signal(1);

  // ---------- 实时态 ----------
  readonly shadowRecording = signal(false);
  readonly shadowLivePeaks = signal<ShadowPeak[]>([]);
  readonly shadowRefPeaks = signal<ShadowPeak[]>([]);
  readonly shadowPlayhead = signal(0);
  readonly shadowRefDur = signal(0);
  readonly shadowPitchVoice = signal<number | null>(null);
  readonly shadowPitchRef = signal<number | null>(null);
  readonly shadowError = signal<string | null>(null);
  readonly shadowAecWarning = signal(false);
  /**
   * 票 261 §2：实时波形上下文未能启动警告（Analyser 读 0 的可见解释；
   * 录音本身走原始流不受影响，故只警告、不拦截）。
   */
  readonly shadowMeterWarning = signal(false);
  /** 票 261 §2：麦克风设备（enumerateDevices 结果；空 = 未枚举或无设备）。 */
  readonly micDevices = signal<{ deviceId: string; label: string }[]>([]);
  /** 票 261 §2：用户选定的麦克风 deviceId（null = 系统默认），localStorage 持久化。 */
  readonly micDeviceId = signal<string | null>(PracticeShadowSession.loadMicDevice());

  private static loadMicDevice(): string | null {
    try {
      return localStorage.getItem('yi-mic-device') ?? null;
    } catch {
      return null;
    }
  }

  // ---------- 待提交态 ----------
  readonly shadowPending = signal<ShadowPendingView | null>(null);
  readonly shadowMixStatus = signal<ShadowMixStatus>('idle');
  readonly shadowMixProgress = signal(0);
  /** P29g：Stop 写失败标志（写进内存退路而非 IndexedDB 时置位）。 */
  readonly shadowStorageFailed = signal(false);
  /** 票259 S2：人声轨上传失败的服务端行 id（仅该行显示“重试传人声”）。 */
  readonly voiceTrackFailedFor = signal<string | null>(null);
  /** 票260 S3：正在上传到服务器的本地行 localTakeId（上传中禁用该行按钮）。 */
  readonly uploadingToServerFor = signal<string | null>(null);

  // ---------- 列表与展开 ----------
  private readonly shadowMeta = signal<Map<string, ShadowTakeRecord>>(new Map());
  readonly shadowExpandedId = signal<string | null>(null);
  readonly shadowExpandedPeaks = signal<ShadowPeak[]>([]);
  readonly shadowPlayingId = signal<string | null>(null);
  readonly shadowSolo = signal<'together' | 'voice' | 'reference'>('together');
  readonly shadowVoiceMissing = signal(false);
  readonly shadowRefMissing = signal(false);

  /** 跟读行（meta 驱动；GUID 与本地并存去重后只显 GUID，P26）。 */
  readonly shadowRows = computed<ShadowRowView[]>(() => {
    const meta = [...this.shadowMeta().values()];
    const byLocal = new Map<string, ShadowTakeRecord>();
    for (const r of meta) {
      const prev = byLocal.get(r.localTakeId);
      if (!prev || (r.serverId && !prev.serverId)) byLocal.set(r.localTakeId, r);
    }
    const pending = this.shadowPending();
    const rows: ShadowRowView[] = [];
    for (const r of byLocal.values()) {
      if (r.mixStatus === 'pending' && pending && pending.localTakeId === r.localTakeId) continue;
      rows.push({
        id: r.serverId ?? r.key,
        key: r.key,
        localTakeId: r.localTakeId,
        serverId: r.serverId,
        createdAt: r.createdAt,
        durationSec: jointDuration(r.refDurationSec / r.rate, r.voiceWallSec, r.laneShiftSec),
        rate: r.rate,
        kind: r.kind,
        mixStatus: r.mixStatus,
        uploaded: r.serverId !== null,
        laneShiftSec: r.laneShiftSec,
        no: 0, // 票 261 §1：下文统一编号。
      });
    }
    if (pending) {
      rows.push({
        id: pending.localTakeId,
        key: pending.localTakeId,
        localTakeId: pending.localTakeId,
        serverId: null,
        createdAt: Date.now(),
        durationSec: pending.durationSec,
        rate: pending.rate,
        kind: pending.kind,
        mixStatus: pending.mixStatus,
        uploaded: false,
        laneShiftSec: pending.laneShiftSec,
        no: 0, // 票 261 §1：下文统一编号（待提交行时间最新，号最大）。
      });
    }
    // ★ 票258 S1：无本地链接的服务端跟读行（另一设备上的 1x 混音行）
    //   也进跟读列表——这正是“认得出”。已链接的行（本地 meta 的 key /
    //   localTakeId / serverId 任一命中）不重复进列。
    const linkedIds = new Set<string>();
    for (const r of meta) {
      linkedIds.add(r.key);
      linkedIds.add(r.localTakeId);
      if (r.serverId) linkedIds.add(r.serverId);
    }
    rows.push(...serverOnlyShadowRows(this.host.grading.myRecordings(), linkedIds));
    // 票 261 §1：显示最新在上、编号时间先后（最早 #1）——
    // 旧实现用 $index+1 当号，新录一条就把旧号全顶下去，用户看到的
    // #1 永远是最新一条，与“第几次录的”完全对不上。
    const sorted = orderNewestFirst(rows);
    const numbers = assignShadowNumbers(sorted);
    return sorted.map((r) => ({ ...r, no: numbers.get(r.id) ?? 1 }));
  });

  /** 票 261 §1：常规列表同样最新在上（takeNo 本就时间先后，不动）。 */
  readonly orderedNonShadowRecordings = computed(() =>
    orderNewestFirst(this.nonShadowRecordings()),
  );

  /** 常规列表（P24：跟读行绝不出现在常规模式，哪怕已上传）。 */
  readonly nonShadowRecordings = computed(() => {
    const meta = [...this.shadowMeta().values()];
    const shadowIds = new Set<string>();
    for (const r of meta) {
      shadowIds.add(r.key);
      shadowIds.add(r.localTakeId);
      if (r.serverId) shadowIds.add(r.serverId);
    }
    // ★ 票258 S1：服务端 IsShadow 为 true 的行同样排除——即使本设备
    //   无本地链接（另一设备上的 1x 跟读行从普通列表消失、进入跟读列表，
    //   是有意的行为变化，见 S3-PLAN §2.2，验收显式断言）。
    return this.host.grading.myRecordings().filter((r) => !isShadowRow(r.isShadow, shadowIds.has(r.id)));
  });

  readonly shadowExpandedRow = computed(() => {
    const id = this.shadowExpandedId();
    if (!id) return null;
    return this.shadowRows().find((r) => r.id === id) ?? null;
  });

  /** 共享轴总长（P15：参考轨长与当前活声轨长取大）。 */
  readonly shadowAxisLength = computed(() => {
    const live = this.shadowLivePeaks();
    const liveSpan = live.length ? live[live.length - 1].t : 0;
    return Math.max(this.shadowRefDur(), liveSpan);
  });

  /** 展开行的共享轴总长（音轨时间口径：参考轨长与活声轨长取大）。 */
  readonly shadowExpandedAxis = computed(() => {
    const row = this.shadowExpandedRow();
    if (!row) return 0;
    const meta = [...this.shadowMeta().values()].find((r) => r.localTakeId === row.localTakeId);
    const refDur = meta?.refDurationSec ?? row.durationSec;
    const voiceSpan = meta ? (meta.voiceWallSec + meta.laneShiftSec) * row.rate : row.durationSec;
    return Math.max(refDur, voiceSpan, 1);
  });

  // ---------- 内部态 ----------
  private capture: ShadowCaptureService | null = null;
  private refHowl: Howl | null = null;
  private refUrl: string | null = null;
  private refKey = '';
  private refBlob: Blob | null = null;
  private refContour: (number | null)[] | null = null;
  private takeRate = 1;
  private takeLaneShiftSec = 0;
  private takeRefDurSec = 0;
  private takeVoiceWallSec = 0;
  private takePeaks: ShadowPeak[] = [];
  private playTimer: ReturnType<typeof setInterval> | null = null;
  private starting = false;
  private stopping = false;
  private midTakeFailed = false;
  private mixCancel = false;
  private voiceGate = new PitchGate();
  private refGate = new PitchGate();
  private controller: ShadowPlaybackController | null = null;
  private expandedVoiceUrl: string | null = null;
  private expandedRefUrl: string | null = null;
  private expandedVoiceEl: HTMLAudioElement | null = null;
  private expandedRefEl: HTMLAudioElement | null = null;
  private expandedMixEl: HTMLAudioElement | null = null;
  private fetchedAudio = new Map<string, string>();
  /** 票259 S2：会话内已取回的人声轨 blob（按服务端行 id 缓存，免重复下载）。 */
  private fetchedVoiceBlobs = new Map<string, Blob>();

  // ---------- 模式 ----------
  async toggleShadowMode(): Promise<void> {
    if (this.shadowRecording()) await this.stopShadow();
    this.stopShadowPlayback();
    const next = !this.shadowMode();
    this.shadowMode.set(next);
    this.shadowError.set(null);
    this.shadowMeterWarning.set(false);
    this.shadowStorageFailed.set(false);
    if (next) {
      // 票 261 §2：进跟读模式即枚举麦克风（授权前 label 可能为空，授权后重枚举）。
      await this.refreshMicDevices();
      await this.refreshShadowMeta();
    }
    this.host.cdr.detectChanges();
  }

  /**
   * 票 261 §2：枚举麦克风设备（授权前 label 为空属浏览器行为，照常列出；
   * 授权后重枚举一次即可拿到 label）。存量 deviceId 已不在列表中 = 设备
   * 已拔/已换，清掉选择、回落默认，绝不拿失效 id 去撞 exact 约束。
   */
  async refreshMicDevices(): Promise<void> {
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      const mics = devices
        .filter((d) => d.kind === 'audioinput')
        .map((d, i) => ({ deviceId: d.deviceId, label: d.label || `Mic ${i + 1}` }));
      this.micDevices.set(mics);
      const selected = this.micDeviceId();
      if (selected && !mics.some((m) => m.deviceId === selected)) this.setMicDevice(null);
    } catch {
      /* 枚举失败 = 不提供选设备（默认设备路径不受影响） */
    }
  }

  /** 票 261 §2：选定麦克风（null = 系统默认），持久化。 */
  setMicDevice(deviceId: string | null): void {
    this.micDeviceId.set(deviceId);
    try {
      if (deviceId) localStorage.setItem('yi-mic-device', deviceId);
      else localStorage.removeItem('yi-mic-device');
    } catch {
      /* 存储不可用不影响本次选择 */
    }
  }

  /** 票 261 §6：线形波形 points（模板直接绑定，空 = 不画线）。 */
  liveRefLinePoints(): string {
    return peaksToLinePoints(this.shadowRefPeaks(), this.shadowAxisLength());
  }
  liveVoiceLinePoints(): string {
    return peaksToLinePoints(this.shadowLivePeaks(), this.shadowAxisLength());
  }
  expandedRefLinePoints(): string {
    return peaksToLinePoints(this.shadowRefPeaks(), this.shadowExpandedAxis());
  }
  expandedVoiceLinePoints(): string {
    return peaksToLinePoints(this.shadowExpandedPeaks(), this.shadowExpandedAxis());
  }

  async setReviewRate(r: number): Promise<void> {
    this.shadowReviewRate.set(r);
    if (this.controller && this.shadowPlayingId()) {
      await this.controller.setRate(r);
    }
  }

  // ---------- 列表同步（含扫除） ----------
  async refreshShadowMeta(): Promise<void> {
    const materialId = this.host.selectedId();
    if (!materialId) return;
    const rows = await getShadowTakesForMaterial(materialId);
    // P14 门禁：扫除只能在服务端列表已权威载入时跑。列表在途（loading）
    // 或从未成功载入时，服务端 id 集合不可信——走查实证：reload 后立刻
    // toggle 会在列表未到前扫除，把已链接的提交行连同种子行一起删掉。
    // 此时跳过扫除（有疑问则保留），下一次刷新（列表已到）再扫。
    // 关键：serverIds 与 pendingKeys 必须在门禁通过后、扫除前一刻现取——
    // 若在首个 await 之前快照，列表会在 await 期间到达，而扫除仍拿着
    // 快照时的空集合跑（TOCTOU，走查实证的真凶：门禁放行 + 空集合）。
    const listReady =
      !this.host.recorder.loading() && this.host.recorder.hasLoadedRecordings(materialId);
    if (listReady) {
      const serverIds = new Set(this.host.recorder.recordings().map((r) => r.id));
      const pendingKeys = new Set<string>();
      const pending = this.shadowPending();
      if (pending) pendingKeys.add(pending.localTakeId);
      for (const r of rows) if (r.pendingUpload || r.mixStatus === 'pending') pendingKeys.add(r.key);
      await sweepShadowOrphans(materialId, serverIds, pendingKeys);
    }
    const after = await getShadowTakesForMaterial(materialId);
    const map = new Map<string, ShadowTakeRecord>();
    for (const r of after) map.set(r.key, r);
    this.shadowMeta.set(map);
    // P10：导航离开后回来，持久化的待提交记录恢复为待提交态（可重渲/提交）。
    if (!this.shadowPending()) {
      const restored = after
        .filter((r) => r.mixStatus === 'pending' || r.mixStatus === 'ready' || r.mixStatus === 'failed')
        .sort((a, b) => b.createdAt - a.createdAt)[0];
      if (restored && !restored.serverId) {
        this.shadowPending.set({
          localTakeId: restored.localTakeId,
          materialId: restored.materialId,
          rate: restored.rate,
          kind: restored.kind,
          durationSec: jointDuration(
            restored.refDurationSec / restored.rate,
            restored.voiceWallSec,
            restored.laneShiftSec,
          ),
          refDurationSec: restored.refDurationSec,
          voiceWallSec: restored.voiceWallSec,
          laneShiftSec: restored.laneShiftSec,
          mixStatus: restored.mixStatus,
          text: restored.text,
        });
        this.shadowMixStatus.set(restored.mixStatus);
        this.refKey = restored.refKey;
      }
    }
  }

  onMaterialChange(): void {
    this.stopShadowPlayback();
    this.shadowExpandedId.set(null);
    this.shadowPending.set(null);
    this.shadowStorageFailed.set(false);
    this.revokeRefUrl();
    void this.refreshShadowMeta();
  }

  // ---------- 采集 ----------
  async startShadow(): Promise<void> {
    if (this.starting || this.shadowRecording()) return;
    this.shadowError.set(null);
    this.shadowStorageFailed.set(false);
    const materialId = this.host.selectedId();
    if (!materialId) return;
    if (this.host.recorder.recording()) {
      this.shadowError.set(this.host.t('practice.shadow.busyRecording'));
      return;
    }
    if (!ShadowCaptureService.supported()) {
      this.shadowError.set(this.host.t('practice.shadow.unsupported'));
      return;
    }
    this.starting = true;
    try {
      // 参考音频：必须已缓存（Azure TTS 字节）。未缓存 = 先播一次示范朗读，
      // 绝不伪造参考音轨。
      const text = (this.host.currentText() || '').trim();
      if (!text) {
        this.shadowError.set(this.host.t('practice.shadow.noText'));
        return;
      }
      const key = ttsKey(text, '', this.host.practiceLang());
      const cached = (await getCachedAudio(key)) ?? (await getCachedAudioByMaterial(materialId));
      if (!cached) {
        this.shadowError.set(this.host.t('practice.shadow.noRefAudio'));
        return;
      }
      this.host.tts.stopSpeak();
      this.host.grading.stopPlayback();
      this.host.grading.pausePendingPreview?.();

      // P4 麦克风优先：先授权，授权失败则整起 abort（绝不单放参考）。
      // 票 261 §2：provider 带用户选定的设备（null = 系统默认；失效 id
      // 由 requestMicStream 回落默认，见 shadow-capture）。
      const capture = new ShadowCaptureService(
        () => requestMicStream(this.micDeviceId()),
        (fn) => this.host.ngZone.run(fn),
        (f) => this.onLiveFrame(f),
        () => this.onMidTakeFailure(),
        () => (this.refHowl ? this.refHowl.seek() || 0 : null),
        // 票 265：录制率跟示范条 x1（host.tts.rate），不再另设跟读倍速行。
        this.host.tts.rate(),
      );
      this.capture = capture;
      try {
        await capture.grant();
      } catch (e) {
        this.capture = null;
        const name = (e as { name?: string } | null)?.name ?? '';
        this.shadowError.set(
          name === 'NotAllowedError'
            ? this.host.t('practice.shadow.micDenied')
            : this.host.t('practice.shadow.micFailed'),
        );
        return;
      }
      // 授权后重枚举一次：此时 label 已可读，设备下拉显示真名。
      void this.refreshMicDevices();
      // AEC 核验（P12）：未生效 → 可见警告，永不拦截。
      this.shadowAecWarning.set(capture.aecApplied() === false);
      // 票 261 §2：采集上下文未能启动 → 活声道读 0 的可见解释（只警告，
      // 录音本身走原始流不受影响，绝不因此 abort）。
      this.shadowMeterWarning.set(capture.ctxRunning() === false);
      this.takeRate = this.host.tts.rate(); // 票 265：take 率 = 开录时示范条 x1。
      this.refKey = cached.key;
      this.refBlob = cached.blob;

      // 参考峰值 + F0 轮廓预渲染（与缓存字节同一份解码，零重复请求）。
      const decoded = await this.decodeAudio(cached.blob);
      if (decoded) {
        this.shadowRefPeaks.set(bucketPeaks(decoded.data, decoded.sampleRate));
        this.refContour = computeF0Contour(decoded.data, decoded.sampleRate, 0.1);
        this.takeRefDurSec = decoded.data.length / decoded.sampleRate;
        this.shadowRefDur.set(this.takeRefDurSec);
      } else {
        this.shadowRefPeaks.set([]);
        this.refContour = null;
        this.takeRefDurSec = 0;
        this.shadowRefDur.set(0);
      }

      // 起参考（恒从 0，P24）+ 同起录音机。
      const url = URL.createObjectURL(cached.blob);
      this.revokeRefUrl();
      this.refUrl = url;
      const refStartWall = Date.now();
      let refStarted = false;
      const howl = new Howl({
        src: [url],
        format: howlFormat(cached.blob.type),
        html5: true,
        rate: this.takeRate,
        onload: () => {
          // 解码失败时的时长回落：Howl 自身 duration()。
          const d = howl.duration();
          if (d > 0 && !(this.takeRefDurSec > 0)) {
            this.takeRefDurSec = d;
            this.shadowRefDur.set(d);
          }
        },
        onplay: () => {
          refStarted = true;
          this.startPlayTimer();
        },
        onend: () => {
          // 票 264（用户令）：参考播完 → 自动停，停后状态与手动停止完全一致
          // （同一 stopShadow 路径）。取代旧注记的轴扩展语义“播完不停”。
          // 暂停不触发 onend，故暂停不停；参考不可用时 startShadow 已 abort，
          // 无录音可停；重播（新 take）会替换 refHowl，旧 end 事件被身份门挡下。
          this.stopPlayTimer();
          if (
            shouldAutoStopOnRefEnd(
              this.shadowRecording(),
              this.stopping,
              this.refHowl === howl,
            )
          ) {
            void this.stopShadow();
          }
        },
        onloaderror: () => {
          if (!refStarted) void this.abortStart(this.host.t('practice.shadow.refPlayFailed'));
        },
        onplayerror: () => {
          if (!refStarted) void this.abortStart(this.host.t('practice.shadow.refPlayFailed'));
        },
      });
      this.refHowl = howl;
      this.keepPitch(howl);
      this.unlockHowlerAudio();
      howl.play();
      capture.beginRecording(refStartWall);
      this.voiceGate.reset();
      this.refGate.reset();
      this.shadowLivePeaks.set([]);
      this.shadowPlayhead.set(0);
      this.shadowRecording.set(true);
      // 看门狗：1.2s 后仍未真正出声 = 自动播放拦截 → 整体 abort。
      setTimeout(() => {
        if (this.shadowRecording() && !refStarted && this.refHowl === howl) {
          void this.abortStart(this.host.t('practice.shadow.refPlayFailed'));
        }
      }, 1200);
    } finally {
      this.starting = false;
      this.host.cdr.detectChanges();
    }
  }

  private async abortStart(message: string): Promise<void> {
    this.capture?.cancel();
    this.capture = null;
    this.stopRefHowl();
    this.stopPlayTimer();
    this.shadowRecording.set(false);
    this.shadowError.set(message);
    this.host.cdr.detectChanges();
  }

  private onMidTakeFailure(): void {
    if (!this.shadowRecording() || this.stopping) return;
    // P19：参考立刻停，活声按正常 Stop 保留为半条（不静默丢弃）。
    this.midTakeFailed = true;
    this.stopRefHowl();
    void this.stopShadow();
  }

  private onLiveFrame(f: ShadowFrame): void {
    const peaks = [...this.shadowLivePeaks(), { t: f.trackTimeSec, v: f.peak }];
    this.shadowLivePeaks.set(peaks.length > 800 ? decimatePeaks(peaks, 600) : peaks);
    const voiceDot = this.voiceGate.push(f.f0 ? f.f0.hz : null);
    this.shadowPitchVoice.set(voiceDot);
    const refIdx = this.refContour ? Math.floor(f.trackTimeSec / 0.1) : -1;
    const refCandidate = this.refContour && refIdx >= 0 && refIdx < this.refContour.length
      ? this.refContour[refIdx]
      : null;
    this.shadowPitchRef.set(this.refGate.push(refCandidate));
  }

  private startPlayTimer(): void {
    this.stopPlayTimer();
    this.playTimer = setInterval(() => {
      if (this.refHowl) this.shadowPlayhead.set(this.refHowl.seek() || 0);
      this.host.cdr.detectChanges();
    }, 200);
  }

  private stopPlayTimer(): void {
    if (this.playTimer !== null) {
      clearInterval(this.playTimer);
      this.playTimer = null;
    }
  }

  async stopShadow(): Promise<void> {
    if (!this.shadowRecording() || this.stopping) return;
    this.stopping = true;
    try {
      const result = await this.capture?.stop();
      this.capture = null;
      this.stopRefHowl();
      this.stopPlayTimer();
      this.shadowRecording.set(false);
      if (!result) {
        this.shadowError.set(this.host.t('practice.shadow.emptyTake'));
        return;
      }
      const materialId = this.host.selectedId();
      if (!materialId) return;
      this.takeLaneShiftSec = result.laneShiftSec;
      this.takeVoiceWallSec = result.voiceWallSec;
      this.takePeaks = decimatePeaks(this.shadowLivePeaks(), 600);
      // 票 261 §2：整条 take 的活声峰值都 < 5/100 = 基本没收到声音 ——
      // 非阻塞警告（take 照常保存/待提交），让用户立刻知道要查麦克风，
      // 而不是对着一条静音 take 猜。正常说话峰值在 20~80 区间，阈值 5
      // 只抓近乎全静音，误报面极小。
      if (this.takePeaks.reduce((m, p) => Math.max(m, p.v), 0) < 5) {
        this.shadowError.set(this.host.t('practice.shadow.noVoice'));
      }
      const localTakeId = `s_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 7)}`;
      const durationSec = jointDuration(
        this.takeRefDurSec / this.takeRate,
        result.voiceWallSec,
        result.laneShiftSec,
      );
      const kind = this.takeRate === 1 ? 'mix' : 'dual';
      const text = (this.host.currentText() || '').trim() || undefined;
      const rec: ShadowTakeRecord = {
        key: localTakeId,
        localTakeId,
        serverId: null,
        materialId,
        rate: this.takeRate,
        laneShiftSec: result.laneShiftSec,
        refDurationSec: this.takeRefDurSec,
        refKey: this.refKey,
        // P29b：dual 行是已保存的本地行（mixStatus 'local'），不是待提交；
        // 它没有服务端行、没有 Submit，行时长只是本地展示时长。
        kind,
        mixStatus: kind === 'dual' ? 'local' : 'pending',
        pendingUpload: false,
        createdAt: Date.now(),
        voiceBlob: result.blob,
        peaks: this.takePeaks,
        f0Contour: null,
        mixBlob: null,
        voiceWallSec: result.voiceWallSec,
        text,
      };
      // P29g：Stop 必须确认持久化写成功；写进内存退路 = 失败，显式报错、
      // 保留内存态审阅（pending 仍置位），由用户选 retry-write 或 Discard。
      const persisted = await putShadowTake(rec);
      if (!persisted) {
        this.shadowStorageFailed.set(true);
        this.shadowError.set(this.host.t('practice.shadow.storageFailed'));
      } else {
        this.shadowStorageFailed.set(false);
      }
      if (kind === 'dual') {
        // dual 行直接进跟读列表（meta 刷新后可见），不设 pending 态。
        this.shadowMixStatus.set('local');
        this.host.toast(
          this.host.t(
            this.midTakeFailed ? 'practice.shadow.partialKeptLocal' : 'practice.shadow.savedLocal',
          ),
        );
        this.midTakeFailed = false;
      } else {
        this.shadowPending.set({
          localTakeId,
          materialId,
          rate: this.takeRate,
          kind,
          durationSec,
          refDurationSec: this.takeRefDurSec,
          voiceWallSec: result.voiceWallSec,
          laneShiftSec: result.laneShiftSec,
          mixStatus: 'pending',
          text,
        });
        this.shadowMixStatus.set('pending');
        if (this.midTakeFailed) {
          this.host.toast(this.host.t('practice.shadow.partialKept'));
          this.midTakeFailed = false;
        }
      }
      // 事后 F0 算一次后缓存（P25），失败不阻塞审阅（波形仍在）。
      void this.computeAndCachePostF0(rec).catch(() => undefined);
      await this.refreshShadowMeta();
    } finally {
      this.stopping = false;
      this.host.cdr.detectChanges();
    }
  }

  /** P29g：Stop 写失败后的重试写（写进 IDB 即清失败态；仍失败则保留）。 */
  async retryShadowWrite(): Promise<void> {
    const pending = this.shadowPending();
    if (!pending) return;
    const meta = await this.storeRecord(pending.localTakeId);
    if (!meta) return;
    const persisted = await putShadowTake(meta);
    if (persisted) {
      this.shadowStorageFailed.set(false);
      this.shadowError.set(null);
    }
    this.host.cdr.detectChanges();
  }

  private async computeAndCachePostF0(rec: ShadowTakeRecord): Promise<void> {
    if (!rec.voiceBlob) return;
    const decoded = await this.decodeAudio(rec.voiceBlob);
    if (!decoded) return;
    const contour = computeF0Contour(decoded.data, decoded.sampleRate, 0.1);
    await putShadowTake({ ...rec, f0Contour: contour });
  }

  // ---------- 混音（仅 1x） ----------
  async retryShadowMix(): Promise<void> {
    await this.renderMix();
  }

  /**
   * P29h：缓存缺失时先走既有 Azure TTS 路径重生成参考（同文本/同语种，
   * 等效混音，不声称字节相同），缓存后重试一次混音；重生成也失败则
   * 本地保留 + 显式重录，永不上传纯活声“跟读”行。
   */
  private async regenerateReference(
    meta: ShadowTakeRecord | null,
    pending: ShadowPendingView,
  ): Promise<boolean> {
    const text =
      meta?.text ??
      (pending.materialId === this.host.selectedId()
        ? (this.host.currentText() || '').trim() || null
        : null);
    if (!text) return false;
    const lang = this.host.practiceLang();
    const key = meta?.refKey ?? ttsKey(text, '', lang);
    try {
      const res = await firstValueFrom(
        this.host.practiceApi.synthesize(text, undefined, 1, false, lang),
      );
      if (!res?.blob || res.blob.size === 0) return false;
      const typedBlob = res.blob.type ? res.blob : new Blob([res.blob], { type: 'audio/mpeg' });
      await putCachedAudio({
        key,
        blob: typedBlob,
        voice: res.voice,
        lang,
        savedAt: Date.now(),
        materialId: pending.materialId,
        text,
      });
      this.refKey = key;
      this.refBlob = typedBlob;
      return true;
    } catch {
      return false;
    }
  }

  private async renderMix(allowRegen = true): Promise<Blob | null> {
    const pending = this.shadowPending();
    if (!pending || pending.kind !== 'mix') return null;
    this.mixCancel = false;
    this.shadowMixStatus.set('rendering');
    this.shadowMixProgress.set(0);
    const started = Date.now();
    const timeoutMs = 60000;
    try {
      const meta = await this.storeRecord(pending.localTakeId);
      const cached = await getCachedAudio(meta?.refKey ?? this.refKey);
      const refBlob = cached?.blob ?? this.refBlob;
      if (!refBlob) {
        // P29h：缓存缺失先重生成再重试一次；两路都失败 = 本地保留 + 重录。
        if (allowRegen && (await this.regenerateReference(meta, pending))) {
          return this.renderMix(false);
        }
        this.shadowMixStatus.set('failed');
        this.shadowError.set(this.host.t('practice.shadow.regenFailed'));
        return null;
      }
      const voiceBlob = meta?.voiceBlob;
      if (!voiceBlob) {
        this.shadowMixStatus.set('failed');
        this.shadowError.set(this.host.t('practice.shadow.voiceMissing'));
        return null;
      }
      const voiceDec = await this.decodeAudio(voiceBlob);
      const refDec = await this.decodeAudio(refBlob);
      if (!voiceDec || !refDec) {
        this.shadowMixStatus.set('failed');
        this.shadowError.set(this.host.t('practice.shadow.mixFailed'));
        return null;
      }
      const schedule = computeMixSchedule(
        pending.laneShiftSec * 1000,
        pending.refDurationSec,
        pending.voiceWallSec,
      );
      const ctx = new AudioContext();
      const dest = ctx.createMediaStreamDestination();
      const vBuf = ctx.createBuffer(1, voiceDec.data.length, voiceDec.sampleRate);
      vBuf.copyToChannel(voiceDec.data as Float32Array, 0);
      const rBuf = ctx.createBuffer(1, refDec.data.length, refDec.sampleRate);
      rBuf.copyToChannel(refDec.data as Float32Array, 0);
      const vSrc = ctx.createBufferSource();
      vSrc.buffer = vBuf;
      vSrc.connect(dest);
      const rSrc = ctx.createBufferSource();
      rSrc.buffer = rBuf;
      rSrc.connect(dest);
      const mime = pickMimeForMix();
      const recorder = mime
        ? new MediaRecorder(dest.stream, { mimeType: mime })
        : new MediaRecorder(dest.stream);
      const chunks: Blob[] = [];
      recorder.ondataavailable = (e) => {
        if (e.data && e.data.size > 0) chunks.push(e.data);
      };
      const done = new Promise<Blob>((resolve, reject) => {
        recorder.onstop = () => {
          resolve(new Blob(chunks, { type: recorder.mimeType || 'audio/webm' }));
        };
        const timer = setTimeout(() => {
          try {
            recorder.stop();
          } catch {
            /* ignore */
          }
          reject(new Error('mix-timeout'));
        }, timeoutMs);
        // 渲染进度（约 1x 时长）与后台暂停/恢复（P9）。
        const prog = setInterval(() => {
          const el = (Date.now() - started) / 1000;
          this.shadowMixProgress.set(Math.min(100, Math.round((el / Math.max(1, schedule.mixDurSec)) * 100)));
          if (this.mixCancel) {
            clearInterval(prog);
            clearTimeout(timer);
            try {
              recorder.stop();
            } catch {
              /* ignore */
            }
            reject(new Error('mix-cancelled'));
          }
        }, 250);
        const onVis = () => {
          try {
            if (document.hidden) {
              void ctx.suspend();
              if (recorder.state === 'recording') recorder.pause();
            } else {
              void ctx.resume();
              if (recorder.state === 'paused') recorder.resume();
            }
          } catch {
            /* 后台暂停是尽力而为 */
          }
        };
        document.addEventListener('visibilitychange', onVis);
        vSrc.start(ctx.currentTime + schedule.voiceStartSec);
        rSrc.start(ctx.currentTime + schedule.refStartSec);
        recorder.start();
        // 停表 = 混音长 + 50ms：50ms 在 §5 的 100ms 校验容差内（尾部静音
        // 不会把校验拖垮），又给 setTimeout 的迟到留了余量。注意：此前
        // +500ms 会让解码出的混音比并集时长长 0.5s，恒触发 mixFailed，
        // 1x 提交在走查里从未成功上传（走查实证，见票 250  ledger）。
        setTimeout(() => {
          document.removeEventListener('visibilitychange', onVis);
          clearInterval(prog);
          clearTimeout(timer);
          if (recorder.state !== 'inactive') {
            try {
              recorder.stop();
            } catch {
              /* ignore */
            }
          }
        }, Math.ceil(schedule.mixDurSec * 1000) + 50);
      });
      let mixBlob: Blob;
      try {
        mixBlob = await done;
      } catch {
        this.shadowMixStatus.set(this.mixCancel ? 'pending' : 'failed');
        if (!this.mixCancel) this.shadowError.set(this.host.t('practice.shadow.mixFailed'));
        return null;
      } finally {
        void ctx.close().catch(() => undefined);
      }
      // 渲染时长校验（§5：与并集时长差 >100ms = 混音失败，重试）。
      const mixDec = await this.decodeAudio(mixBlob);
      const joint = jointDuration(pending.refDurationSec, pending.voiceWallSec, pending.laneShiftSec);
      if (!mixDec || Math.abs(mixDec.data.length / mixDec.sampleRate - joint) > 0.1) {
        this.shadowMixStatus.set('failed');
        this.shadowError.set(this.host.t('practice.shadow.mixFailed'));
        return null;
      }
      if (meta) await putShadowTake({ ...meta, mixBlob, mixStatus: 'ready' });
      this.shadowMixStatus.set('ready');
      this.shadowMixProgress.set(100);
      this.shadowPending.set({ ...pending, mixStatus: 'ready' });
      return mixBlob;
    } catch {
      this.shadowMixStatus.set('failed');
      this.shadowError.set(this.host.t('practice.shadow.mixFailed'));
      return null;
    }
  }

  cancelShadowMix(): void {
    this.mixCancel = true;
  }

  // ---------- 提交 / 丢弃 ----------
  async submitShadowPending(): Promise<void> {
    const pending = this.shadowPending();
    // P29b：dual 行没有 Submit（上传纯活声会误表行时长与双轨上下文）。
    if (!pending || pending.kind !== 'mix') return;
    this.shadowError.set(null);
    const materialId = this.host.selectedId();
    if (!materialId) return;
    if (!RecorderService.isGuid(materialId)) {
      this.shadowError.set(this.host.t('practice.shadow.needSavedMaterial'));
      return;
    }
    const payload: Blob | null =
      this.shadowMixStatus() === 'ready'
        ? (await this.storeRecord(pending.localTakeId))?.mixBlob ?? null
        : await this.renderMix();
    if (!payload) return; // renderMix 已置 failed + 错误（缓存缺失时绝不上传）
    const mime = payload.type || 'audio/webm';
    this.shadowMixStatus.set('uploading');
    const metaBefore = await this.storeRecord(pending.localTakeId);
    if (metaBefore) await putShadowTake({ ...metaBefore, pendingUpload: true });
    // ★ 票259 S2：人声 blob 取自提交前记录（重键后键已变，事后取不到旧键）。
    const pendingVoiceBlob = metaBefore?.voiceBlob ?? null;
    try {
      const dto = await firstValueFrom(
        this.host.practiceApi.uploadRecording(
          materialId,
          payload,
          pending.durationSec,
          `shadow-${Date.now()}.${extOf(mime)}`,
          mime,
          // ★ 票258 S1：跟读混音上传带服务端标识，别设备据此认出跟读行。
          true,
        ),
      );
      const url = URL.createObjectURL(payload);
      const entry: Recording = {
        id: dto.id,
        materialId,
        createdAt: new Date(dto.createdAt).getTime(),
        duration: pending.durationSec,
        blob: payload,
        url,
        score: null,
        grading: false,
        uploaded: true,
        error: null,
        // ★ 票258 S1：服务端回传的标识透传（缺省 false）。
        isShadow: dto.isShadow ?? false,
      };
      this.host.recorder.recordings.update((list) => [entry, ...list]);
      // 重键（P18 + P29g decode-based 校验）：失败则保留本地记录 +
      // 服务端行关联（serverId 写回），行仍可通过 serverId 识别为跟读行，
      // 不丢声轨。
      const ok = await rekeyShadowTake(pending.localTakeId, dto.id, async (b) => {
        const d = await this.decodeAudio(b);
        return d ? d.data.length / d.sampleRate : 0;
      });
      if (!ok) {
        const cur = await this.storeRecord(pending.localTakeId);
        if (cur) await putShadowTake({ ...cur, serverId: dto.id, pendingUpload: true });
        this.shadowError.set(this.host.t('practice.shadow.rekeyFailed'));
      }
      // ★ 票259 S2：混音行已落库 → 跟传人声轨（人声跟人走）。
      //   人声失败不影响混音行：行留在 S1 态（混音 + 降级人声道），仅置
      //   失败标志 + toast，行上的重试钮只重传人声轨、绝不重传混音。
      if (pendingVoiceBlob && pendingVoiceBlob.size > 0) {
        await this.uploadVoiceTrackFor(dto.id, pending.localTakeId,
          pendingVoiceBlob, pending.rate, pending.laneShiftSec);
      }
      this.shadowPending.set(null);
      this.shadowMixStatus.set('done');
      this.host.toast(this.host.t('practice.shadow.submitted'));
      await this.refreshShadowMeta();
    } catch (e) {
      const meta = await this.storeRecord(pending.localTakeId);
      if (meta) await putShadowTake({ ...meta, pendingUpload: true });
      this.shadowMixStatus.set('pending');
      this.shadowError.set(
        this.host.t('practice.shadow.uploadFailed') +
          ' · ' +
          String((e as { message?: string } | null)?.message ?? e ?? '').slice(0, 120),
      );
    } finally {
      this.host.cdr.detectChanges();
    }
  }

  /**
   * 票259 S2：上传一条已落库跟读行的人声轨（提交时跟传 + 行重试共用）。
   * 成功后校验-丢弃：服务端 GET 回读 size > 0 才删本地 voiceBlob
   * （copy-then-verify-then-delete；peaks/F0 保留，即时渲染不受影响；
   * 校验失败则保留本地 blob，人声仍可用）。上传失败 → 置失败标志 +
   * toast，混音行本身不受影响；重试只重传人声轨。
   * @returns 上传是否成功（校验失败仍算成功：服务端已有声轨）。
   */
  private async uploadVoiceTrackFor(serverId: string, localTakeId: string,
    voiceBlob: Blob, rate: number, laneShiftSec: number): Promise<boolean> {
    const mime = voiceBlob.type || 'audio/webm';
    try {
      await firstValueFrom(this.host.practiceApi.uploadVoiceTrack(serverId, voiceBlob,
        `shadow-voice-${Date.now()}.${extOf(mime)}`, mime, rate, laneShiftSec));
    } catch {
      this.voiceTrackFailedFor.set(serverId);
      this.host.toast(this.host.t('practice.shadow.voiceUploadFailed'));
      return false;
    }
    if (this.voiceTrackFailedFor() === serverId) this.voiceTrackFailedFor.set(null);
    try {
      const verify = await firstValueFrom(this.host.practiceApi.fetchVoiceTrack(serverId));
      if (verify.size > 0) {
        this.fetchedVoiceBlobs.set(serverId, verify);
        const rec = await this.storeRecord(serverId) ?? await this.storeRecord(localTakeId);
        if (rec) await putShadowTake({ ...rec, voiceBlob: null });
        await this.refreshShadowMeta();
      }
    } catch {
      // 校验取回失败：本地 blob 保留（不删），展开时仍可从服务端取回。
    }
    return true;
  }

  /** 票259 S2：人声轨上传重试（只重传人声轨，绝不重传混音）。 */
  async retryVoiceTrack(row: ShadowRowView): Promise<void> {
    if (!row.serverId || !row.localTakeId) return;
    const rec = await this.storeRecord(row.serverId) ?? await this.storeRecord(row.localTakeId);
    const voiceBlob = rec?.voiceBlob ?? null;
    if (!voiceBlob || voiceBlob.size === 0) return;
    await this.uploadVoiceTrackFor(row.serverId, row.localTakeId,
      voiceBlob, row.rate, rec?.laneShiftSec ?? 0);
  }

  /**
   * 票260 S3（§4.7 迁移）：把一条本地-only 双轨行上传到服务器。
   * S3 范围内唯一新增的 UI 控件（用户定夺“后期录音文件都会放在服务器上”；
   * 不自动批量上传，批量“上传全部”不在范围）。
   * 两步创建：POST shadow-rows（元数据，行 = 未完成）→ POST voice-track
   * （人声 blob，成功后行 = 完整跟读行）→ 本地记录按 P18 重键为服务端 GUID。
   * 人声失败 → 行留在未完成态 + 失败标志（行上重试钮只重传人声轨），
   * 本地 blob 保留，行仍可在录制设备播放；不删本地记录。
   * 成功后本地人声 blob 按 §3.2 规则可丢弃（uploadVoiceTrackFor 内
   * 服务端 GET 回读 size > 0 才删，peaks/F0 保留）。
   */
  async uploadShadowRowToServer(row: ShadowRowView): Promise<void> {
    if (row.serverId || row.kind !== 'dual' || !row.localTakeId) return;
    if (this.uploadingToServerFor()) return;
    const materialId = this.host.selectedId();
    if (!materialId) return;
    const rec = await this.storeRecord(row.localTakeId);
    const voiceBlob = rec?.voiceBlob ?? null;
    if (!voiceBlob || voiceBlob.size === 0) return;
    this.uploadingToServerFor.set(row.localTakeId);
    try {
      const dto = await firstValueFrom(this.host.practiceApi.createShadowRow(
        materialId, row.durationSec, row.rate, rec?.laneShiftSec ?? 0));
      // 先写回 serverId 关联（重键失败时行仍可通过 serverId 识别，不丢声轨）。
      const ok = await rekeyShadowTake(row.localTakeId, dto.id, async (b) => {
        const d = await this.decodeAudio(b);
        return d ? d.data.length / d.sampleRate : 0;
      });
      if (!ok) {
        const cur = await this.storeRecord(row.localTakeId);
        if (cur) await putShadowTake({ ...cur, serverId: dto.id, pendingUpload: true });
      }
      const uploaded = await this.uploadVoiceTrackFor(dto.id, row.localTakeId,
        voiceBlob, row.rate, rec?.laneShiftSec ?? 0);
      if (!uploaded) {
        // 人声失败：行已是服务端未完成行，失败标志驱动行上重试（只重传人声轨）。
        this.voiceTrackFailedFor.set(dto.id);
      } else {
        this.host.toast(this.host.t('practice.shadow.uploadedToServer'));
      }
      await this.refreshShadowMeta();
    } catch {
      this.shadowError.set(this.host.t('practice.shadow.uploadToServerFailed'));
    } finally {
      this.uploadingToServerFor.set(null);
      this.host.cdr.detectChanges();
    }
  }

  async discardShadowPending(): Promise<void> {
    const pending = this.shadowPending();
    if (!pending) return;
    // 票 261 §7：丢弃待提交 take 也要先确认（与行删除同口径）。
    const ok = await this.host.tree.confirmDialog({
      title: this.host.t('dialog.deleteRecTitle'),
      confirmText: this.host.t('dialog.deleteConfirm'),
      danger: true,
    });
    if (ok !== true) return;
    await deleteShadowTake(pending.localTakeId);
    this.shadowPending.set(null);
    this.shadowMixStatus.set('idle');
    this.shadowError.set(null);
    this.shadowStorageFailed.set(false);
    await this.refreshShadowMeta();
  }

  /**
   * 票 261 §7：删除跟读行必须先确认。
   * 服务端行走既有删除确认（grading.removeRecording）；本地行此前直接删库
   * （用户截图的“删除没有提示框”正是这一路），现补同一确认弹窗。
   */
  async removeShadowRow(row: ShadowRowView): Promise<void> {
    if (row.serverId) {
      this.host.grading.removeRecording(row.serverId);
      return;
    }
    const ok = await this.host.tree.confirmDialog({
      title: this.host.t('dialog.deleteRecTitle'),
      confirmText: this.host.t('dialog.deleteConfirm'),
      danger: true,
    });
    if (ok !== true) return;
    await deleteShadowTake(row.key);
    if (this.shadowExpandedId() === row.id) {
      this.stopShadowPlayback();
      this.shadowExpandedId.set(null);
    }
    await this.refreshShadowMeta();
  }

  // ---------- 展开与回放 ----------
  async expandShadowRow(row: ShadowRowView): Promise<void> {
    if (this.shadowExpandedId() === row.id) {
      this.shadowExpandedId.set(null);
      this.stopShadowPlayback();
      return;
    }
    this.stopShadowPlayback();
    this.shadowExpandedId.set(row.id);
    this.shadowExpandedPeaks.set([]);
    this.shadowRefPeaks.set([]);
    this.shadowReviewRate.set(row.rate);
    this.shadowSolo.set('together');
    this.shadowVoiceMissing.set(false);
    this.shadowRefMissing.set(false);
    await this.buildController(row, 'together', 0);
  }

  async toggleShadowPlay(row: ShadowRowView): Promise<void> {
    if (this.shadowPlayingId() === row.id) {
      this.controller?.pause();
      this.shadowPlayingId.set(null);
      return;
    }
    if (this.shadowExpandedId() !== row.id) {
      await this.expandShadowRow(row);
    } else if (!this.controller) {
      await this.buildController(row, this.shadowSolo(), 0);
    }
    if (!this.controller) return;
    this.stopOtherAudio();
    await this.controller.play();
    this.shadowPlayingId.set(row.id);
  }

  seekShadowLane(row: ShadowRowView, ev: MouseEvent): void {
    const target = ev.currentTarget as HTMLElement | null;
    if (!target) return;
    const rect = target.getBoundingClientRect();
    const ratio = rect.width > 0 ? (ev.clientX - rect.left) / rect.width : 0;
    const axis = this.shadowExpandedId() === row.id && this.shadowExpandedAxis() > 0
      ? this.shadowExpandedAxis()
      : Math.max(row.durationSec, 1);
    const t = Math.min(Math.max(ratio, 0), 1) * axis;
    if (!this.controller || this.shadowExpandedId() !== row.id) {
      void this.expandShadowRow(row).then(() => this.controller?.seek(t));
      return;
    }
    this.controller.seek(t);
  }

  /** 回到一起听（独奏按钮的反向动作：点“一起听”即恢复双轨/混音）。 */
  async setShadowSoloTogether(): Promise<void> {
    const row = this.shadowExpandedRow();
    if (!row || this.shadowSolo() === 'together') return;
    this.shadowSolo.set('together');
    const at = this.controller ? this.controller.currentRefTime() : 0;
    const wasPlaying = this.shadowPlayingId() === row.id;
    await this.buildController(row, 'together', at);
    if (wasPlaying && this.controller) {
      await this.controller.play();
      this.shadowPlayingId.set(row.id);
    }
  }

  async toggleShadowSolo(track: 'voice' | 'reference'): Promise<void> {
    const row = this.shadowExpandedRow();
    if (!row) return;
    const next = this.shadowSolo() === track ? 'together' : track;
    this.shadowSolo.set(next);
    const at = this.controller ? this.controller.currentRefTime() : 0;
    const wasPlaying = this.shadowPlayingId() === row.id;
    await this.buildController(row, next, at);
    if (wasPlaying && this.controller) {
      await this.controller.play();
      this.shadowPlayingId.set(row.id);
    }
  }

  private stopOtherAudio(): void {
    this.host.tts.stopSpeak();
    this.host.grading.stopPlayback();
    this.host.grading.pausePendingPreview?.();
  }

  private stopShadowPlayback(): void {
    this.controller?.destroy();
    this.controller = null;
    this.shadowPlayingId.set(null);
    for (const u of [this.expandedVoiceUrl, this.expandedRefUrl]) {
      if (u) URL.revokeObjectURL(u);
    }
    this.expandedVoiceUrl = null;
    this.expandedRefUrl = null;
    this.expandedVoiceEl = null;
    this.expandedRefEl = null;
    this.expandedMixEl = null;
  }

  private async buildController(
    row: ShadowRowView,
    solo: 'together' | 'voice' | 'reference',
    atRefTime: number,
  ): Promise<void> {
    this.stopShadowPlayback();
    const meta = this.shadowMeta().get(row.key) ??
      [...this.shadowMeta().values()].find((r) => r.localTakeId === row.localTakeId) ??
      null;
    const entry = this.host.recorder.recordings().find((r) => r.id === row.id) ?? null;
    // 声轨：只认 store 里的活声 blob。录音条目 blob 绝不能喂给声轨——
    // 对跟读行它永远是混音 payload（提交时原样回填），不是活声；拿它当
    // 声轨会把混音误标为活声，掩盖 §4 的“活声不可用”态（走查实证）。
    // 服务端拉取同理：mix 行取回的是混音，走 expandedMixEl，不进声轨。
    let voiceBlob: Blob | null = meta?.voiceBlob ?? null;
    if ((!voiceBlob || voiceBlob.size === 0) && row.serverId) {
      try {
        const fetched = this.fetchedAudio.get(row.serverId) ??
          URL.createObjectURL(await firstValueFrom(this.host.practiceApi.fetchRecordingAudio(row.serverId)));
        this.fetchedAudio.set(row.serverId, fetched);
        // 服务端取回的是上传 payload：mix 行 = 混音，dual 行 = 活声。
        if (row.kind === 'mix') {
          this.expandedMixEl = this.makeAudioEl(fetched);
        } else {
          voiceBlob = await (await fetch(fetched)).blob();
        }
      } catch {
        voiceBlob = null;
      }
    }
    // ★ 票259 S2：人声道仍缺失且行已落库 → 试取服务端人声轨（另一设备打开）。
    //   取到则解码供声轨用；404/失败 → 保持既有降级态（文案一字不变）。
    let fetchedVoiceDec: { data: ArrayLike<number>; sampleRate: number } | null = null;
    if ((!voiceBlob || voiceBlob.size === 0) && row.serverId) {
      try {
        const cached = this.fetchedVoiceBlobs.get(row.serverId);
        const blob = cached ??
          await firstValueFrom(this.host.practiceApi.fetchVoiceTrack(row.serverId));
        if (!cached) this.fetchedVoiceBlobs.set(row.serverId, blob);
        if (blob.size > 0) {
          const dec = await this.decodeAudio(blob);
          if (dec) {
            voiceBlob = blob;
            fetchedVoiceDec = dec;
          }
        }
      } catch {
        // 取不到人声轨 = 降级态（旧行/仅混音行），不报错。
      }
    }
    this.shadowVoiceMissing.set(!voiceBlob || voiceBlob.size === 0);
    // 峰值：本地缓存优先（丢弃后仍保留）；无缓存则用取回人声的解码结果。
    this.shadowExpandedPeaks.set(
      meta?.peaks?.length ? meta.peaks :
        fetchedVoiceDec ? bucketPeaks(fetchedVoiceDec.data, fetchedVoiceDec.sampleRate) : []);
    // 参考：缓存优先（与 take 的 refKey 一致）；缺失 = 参考 lane 不可用态。
    // ★ 票258 S1：meta 为 null 的服务端-only 行（无本地 take）不得借用
    //   上一行残留的 this.refBlob——仅当 meta 存在且 refKey 命中时才回落。
    let refBlob: Blob | null = null;
    if (meta) {
      const cached = await getCachedAudio(meta.refKey);
      refBlob = cached?.blob ?? null;
    }
    if (!refBlob && meta && this.refBlob && this.refKey === meta.refKey) refBlob = this.refBlob;
    this.shadowRefMissing.set(!refBlob);
    if (refBlob) {
      const dec = await this.decodeAudio(refBlob);
      if (dec) this.shadowRefPeaks.set(bucketPeaks(dec.data, dec.sampleRate));
    }

    const reviewRate = this.shadowReviewRate();
    // ★ 票260 S3：服务端-only 行无本地 meta 时，laneShift 取行上的 DTO 值
    //   （serverOnlyShadowRows 已从 TakeRate/LaneShiftSec 透传），不再恒为 0。
    const laneShift = meta?.laneShiftSec ?? row.laneShiftSec ?? 0;
    const refDur = meta?.refDurationSec ?? row.durationSec;
    const voiceDur = meta?.voiceWallSec ?? row.durationSec;

    if (solo === 'voice') {
      if (!voiceBlob) return;
      const url = URL.createObjectURL(voiceBlob);
      this.expandedVoiceUrl = url;
      const el = this.makeAudioEl(url);
      this.expandedVoiceEl = el;
      const tracks: PlaybackTrack[] = [{ id: 'voice', el, durationSec: voiceDur }];
      // 单轨独奏走 mix 形态（单元素，无漂移校正）。
      this.controller = new ShadowPlaybackController(
        {
          mode: 'mix',
          tracks: [{ id: 'mix', el, durationSec: voiceDur }],
          reviewRate: 1,
          laneShiftSec: laneShift,
          seekOffsetSec: 0,
        },
        (wall) => {
          this.shadowPlayhead.set(Math.max(0, (wall + laneShift) * reviewRate));
        },
      );
      void tracks;
      this.controller.seek(trackTimeToVoiceTime(atRefTime, laneShift, reviewRate));
      return;
    }
    if (solo === 'reference') {
      if (!refBlob) return;
      const url = URL.createObjectURL(refBlob);
      this.expandedRefUrl = url;
      const el = this.makeAudioEl(url);
      el.playbackRate = reviewRate;
      (el as HTMLAudioElement & { preservesPitch?: boolean }).preservesPitch = true;
      this.expandedRefEl = el;
      this.controller = new ShadowPlaybackController(
        {
          mode: 'mix',
          tracks: [{ id: 'mix', el, durationSec: refDur }],
          reviewRate,
          laneShiftSec: laneShift,
          seekOffsetSec: 0,
        },
        (t) => {
          this.shadowPlayhead.set(t);
        },
      );
      this.controller.seek(atRefTime);
      return;
    }
    // together
    if (row.kind === 'mix') {
      let mixUrl: string | null = null;
      if (entry && entry.blob && entry.blob.size > 0) {
        mixUrl = URL.createObjectURL(entry.blob);
      } else if (this.expandedMixEl) {
        mixUrl = this.expandedMixEl.src;
      } else if (row.serverId && this.fetchedAudio.has(row.serverId)) {
        mixUrl = this.fetchedAudio.get(row.serverId) ?? null;
      }
      if (mixUrl) {
        // 混音一起听（精确单轨）——见下方 mix 构造后 return；无混音时
        // （如待提交的 mix 行）落到 dual 构造，用本地双轨一起听。
        const el = this.makeAudioEl(mixUrl);
        this.expandedMixEl = el;
        this.controller = new ShadowPlaybackController(
          {
            mode: 'mix',
            tracks: [{ id: 'mix', el, durationSec: row.durationSec }],
            reviewRate: 1,
            laneShiftSec: laneShift,
            seekOffsetSec: 0,
          },
          (t) => {
            this.shadowPlayhead.set(Math.max(0, t - Math.max(0, -laneShift)));
          },
        );
        this.controller.seek(atRefTime);
        return;
      }
    }
    // dual together
    if (!voiceBlob || !refBlob) {
      this.shadowError.set(this.host.t('practice.shadow.voiceMissing'));
      return;
    }
    const vUrl = URL.createObjectURL(voiceBlob);
    const rUrl = URL.createObjectURL(refBlob);
    this.expandedVoiceUrl = vUrl;
    this.expandedRefUrl = rUrl;
    const vEl = this.makeAudioEl(vUrl);
    const rEl = this.makeAudioEl(rUrl);
    rEl.playbackRate = reviewRate;
    (rEl as HTMLAudioElement & { preservesPitch?: boolean }).preservesPitch = true;
    this.expandedVoiceEl = vEl;
    this.expandedRefEl = rEl;
    this.controller = new ShadowPlaybackController(
      {
        mode: 'dual',
        tracks: [
          { id: 'voice', el: vEl, durationSec: voiceDur },
          { id: 'reference', el: rEl, durationSec: refDur },
        ],
        reviewRate,
        laneShiftSec: laneShift,
        seekOffsetSec: 0,
      },
      (refTime) => {
        this.shadowPlayhead.set(refTime);
      },
    );
    this.controller.seek(atRefTime);
  }

  private makeAudioEl(url: string): HTMLAudioElement {
    const el = new Audio(url);
    el.preservesPitch = true;
    el.addEventListener('ended', () => {
      this.shadowPlayingId.set(null);
      this.controller?.pause();
    });
    return el;
  }

  // ---------- 小工具 ----------
  private async storeRecord(localTakeId: string): Promise<ShadowTakeRecord | null> {
    const rows = await getShadowTakesForMaterial(this.host.selectedId() ?? '');
    return rows.find((r) => r.localTakeId === localTakeId) ?? null;
  }

  private async decodeAudio(blob: Blob): Promise<{ data: Float32Array; sampleRate: number } | null> {
    try {
      const ctx = new AudioContext();
      const buf = await blob.arrayBuffer();
      const audio = await ctx.decodeAudioData(buf);
      const data = audio.getChannelData(0);
      const sr = audio.sampleRate;
      await ctx.close();
      return { data, sampleRate: sr };
    } catch {
      return null;
    }
  }

  private keepPitch(h: Howl): void {
    try {
      type PitchNode = HTMLAudioElement & {
        preservesPitch?: boolean;
        mozPreservesPitch?: boolean;
        webkitPreservesPitch?: boolean;
      };
      const node = (h as unknown as { _sounds?: { _node?: PitchNode }[] })._sounds?.[0]?._node;
      if (!node) return;
      node.preservesPitch = true;
      node.mozPreservesPitch = true;
      node.webkitPreservesPitch = true;
    } catch {
      /* 老浏览器无此属性则退化 */
    }
  }

  private unlockHowlerAudio(): void {
    try {
      const ctx = (Howler as unknown as { ctx?: AudioContext }).ctx;
      if (ctx && ctx.state !== 'running') void ctx.resume();
    } catch {
      /* ignore */
    }
  }

  private stopRefHowl(): void {
    if (this.refHowl) {
      try {
        this.refHowl.stop();
        this.refHowl.unload();
      } catch {
        /* ignore */
      }
      this.refHowl = null;
    }
  }

  private revokeRefUrl(): void {
    if (this.refUrl) {
      URL.revokeObjectURL(this.refUrl);
      this.refUrl = null;
    }
  }

  destroy(): void {
    this.stopShadowPlayback();
    this.stopRefHowl();
    this.stopPlayTimer();
    this.revokeRefUrl();
    this.capture?.cancel();
    this.capture = null;
  }
}

function pickMimeForMix(): string {
  const cands = [
    'audio/webm;codecs=opus',
    'audio/webm',
    'audio/mp4;codecs=mp4a.40.2',
    'audio/mp4',
    'audio/ogg;codecs=opus',
    'audio/ogg',
  ];
  for (const c of cands) {
    try {
      if (typeof MediaRecorder !== 'undefined' && MediaRecorder.isTypeSupported(c)) return c;
    } catch {
      /* ignore */
    }
  }
  return '';
}

/**
 * 偏差登记（实现 vs 计划）：
 * - D-250-1 提交路径：计划 §3 写“混音走既有 pendingTake → Submit →
 *   FormData 上传”。实现改走同一 uploadRecording FormData 端点直传，
 *   不占用共享 pendingTake 单槽。理由：pendingTake 单槽会被常规录音
 *   的 discard/submit 顶掉，且上传成功后的 GUID 替换发生在服务内部，
 *   跟读 store 的重键无法确定性地拿到新 GUID；直传在 subscribe 回调里
 *   直接拿到 dto.id，可做 copy-verify-delete 重键。服务端形态、字段
 *   （durationSeconds 走 §5 并集时长）与既有上传逐字一致，后端零改动。
 *   代价：跟读提交按钮是跟读面板自己的按钮，不是常规 Submit 钮；
 *   常规录音流程一字未动。
 * - D-250-2 行时长：计划 §5 max 公式是 laneShift≈0 的特例；实现用
 *   jointDuration（并集，含 shift），1x 混音校验以并集为准（与
 *   computeMixSchedule 的 mixDurSec 数学等价）。采集同起时两者一致。
 * - D-250-3 seek：计划 §3 的 (T−seekOffset)/R 是 laneShift≈0 的特例；
 *   控制器用完整逆映射 (T−seekOffset)/R − laneShift，采集同起时一致。
 * - D-250-4（P29b 落地）：非 1x 行无 Submit。计划 P29b 明言 dual 行
 *   “没有可提交的东西”，审阅流之外不需要 Submit 控件——实现上 dual
 *   行 Stop 后直接进跟读列表，不设 pending 态、不出提交按钮；
 *   submitShadowPending 另有 kind 守卫，dual 调用直接返回。dual 行的
 *   §5 时长只作列表展示，不 post。
 * - P29d 顺序说明：接缝级顺序（provider → recorder.start() →
 *   合成源启动 → 参考启动）由 capture 的 grant/beginRecording 分离
 *   保证，走查 harness 按此顺序直驱 capture；生产 session 仍是
 *   grant → 起参考 → beginRecording 的同起顺序，laneShift 吸收毫秒级
 *   skew（§1 同起规则不变，接缝顺序与生产顺序的差在 skew 内）。
 */
