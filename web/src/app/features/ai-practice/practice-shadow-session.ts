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
  continuityGateHz,
  decimatePeaks,
  jointDuration,
  medianHz,
  peaksToLinePoints,
  trackTimeToVoiceTime,
} from './shadow-waveform.helpers';
import type { ShadowPeak } from './shadow-waveform.helpers';
import { ShadowCaptureService, loadShadowMicMode, requestMicStream } from './shadow-capture';
import type { ShadowFrame } from './shadow-capture';
import { ShadowPlaybackController } from './shadow-playback';
import type { PlaybackTrack } from './shadow-playback';
import {
  attachVoiceGainStage,
  computeTogetherGain,
  ensureVoiceGainContextRunning,
  micLevelFromPeaks,
  peakMaxValue,
  shouldShowQuietHint,
  duckedVolume,
} from './shadow-voice-gain';
import type { VoiceGainStage } from './shadow-voice-gain';
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
  /**
   * 票 271 C：人声过小提示（非阻塞 note；只在停录时按人声/参考峰值比
   * 判定，见 shouldShowQuietHint；近静音归票 261 noVoice 警告，不叠加）。
   * 下一次 startShadow 时清掉。
   */
  readonly shadowQuietHint = signal<string | null>(null);
  /**
   * 票 271 C：录音中麦克风音量表读数（最近 12 帧峰值最大值，0~100；
   * 只读信号，模板在录音时渲染一条小音量条，不改任何音频）。
   */
  readonly shadowMicLevel = computed(() => micLevelFromPeaks(this.shadowLivePeaks()));
  /**
   * 票 271 A：当前展开行一起听时人声增益级的生效倍数；未挂增益为 0。
   * 仅供走查/验证断言（模板不渲染），增益本身只动听的路径。
   */
  readonly shadowVoiceGain = signal(0);
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

  // ---------- 写失败态（P29g：Stop 写失败时保留重试） ----------
  /** P29g：Stop 写失败标志（写进内存退路而非 IndexedDB 时置位）。 */
  readonly shadowStorageFailed = signal(false);
  // ---------- 待确认态（票 274：停录后先挂起，Submit 才落库进列表） ----------
  /**
   * 票 274：跟读停录（手动/自动同一 stopShadow 路径）不再直接落库；
   * finished take 先挂在内存 pending 态，卡片底部 Submit/Preview/Discard
   * 三钮接影子专属处理：Submit 落库进列表（仅本地，永不上传），
   * Preview 播这条待确认的，Discard 丢干净（无行、无 blob）。
   * pending 只在内存：刷新页面即丢（与常规待提交条同一语义）；
   * 列表只认 shadowMeta（IDB），pending 绝不进列表。
   */
  readonly shadowPendingTake = signal<ShadowTakeRecord | null>(null);
  /** 票 274：待确认 take 试听播放态（卡片底部 Preview 钮图标用）。 */
  readonly shadowPendingPlaying = signal(false);
  /** 票 274：提交进行中门（防 Submit 连点双写）。 */
  private submittingPending = false;
  /** 票259 S2：人声轨上传失败的服务端行 id（仅该行显示“重试传人声”）。 */
  readonly voiceTrackFailedFor = signal<string | null>(null);
  // ---------- 列表与展开 ----------
  private readonly shadowMeta = signal<Map<string, ShadowTakeRecord>>(new Map());
  /**
   * 票 270：shadowMeta 所属素材；只与 shadowMeta.set 在同一同步块内设置
   * （原子配对，中间无 await，门禁不闪烁）。shadowRows 据此过滤旧素材行。
   */
  private readonly shadowMetaMaterial = signal<string | null>(null);
  readonly shadowExpandedId = signal<string | null>(null);
  readonly shadowExpandedPeaks = signal<ShadowPeak[]>([]);
  readonly shadowPlayingId = signal<string | null>(null);
  readonly shadowSolo = signal<'together' | 'voice' | 'reference'>('together');
  readonly shadowVoiceMissing = signal(false);
  readonly shadowRefMissing = signal(false);

  /** 跟读行（meta 驱动；GUID 与本地并存去重后只显 GUID，P26）。 */
  readonly shadowRows = computed<ShadowRowView[]>(() => {
    // 票 270：meta 只在标签与当前素材一致时采用。素材切换后、新刷新未落前，
    // 旧素材行不渲染（仅服务端行）；刷新落点时标签同步更新即恢复。标签为 null
    // （从未刷新）时同样不采用——meta 此时本为空，不影响任何已验证行为。
    const tag = this.shadowMetaMaterial();
    const meta = tag !== null && tag === this.host.selectedId()
      ? [...this.shadowMeta().values()]
      : [];
    const byLocal = new Map<string, ShadowTakeRecord>();
    for (const r of meta) {
      const prev = byLocal.get(r.localTakeId);
      if (!prev || (r.serverId && !prev.serverId)) byLocal.set(r.localTakeId, r);
    }
    const rows: ShadowRowView[] = [];
    for (const r of byLocal.values()) {
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
  /** 票 284：本条参考是否被压低（仅扬声器模式）；停参考时恢复原音量。 */
  private refDucked = false;
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
  /** 票 266：Stop 写失败时记下的 take id（重试写用；成功/重试成功后清掉）。 */
  private lastFailedTakeId: string | null = null;
  private voiceGate = new PitchGate();
  private refGate = new PitchGate();
  private controller: ShadowPlaybackController | null = null;
  /** 票 271 A：一起听人声增益级（随 stopShadowPlayback 断开丢弃）。 */
  private voiceGainStage: VoiceGainStage | null = null;
  /** 票 271 A：增益图共用的 AudioContext（懒建；元素重建不重建它）。 */
  private voiceGainCtx: AudioContext | null = null;
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
    // 票 287：一句话模式开着时跟读开关被禁用（模板 [disabled]），会话层同样守住：
    // 此方向不再自动关一句话模式——调用直接返回，须先关一句话模式再开跟读。
    // 反向不受影响：开一句话模式时 sentenceMode() 仍为 false，关跟读那路照常走。
    if (this.host.sentence?.sentenceMode()) return;
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
      for (const r of rows) if (r.pendingUpload || r.mixStatus === 'pending') pendingKeys.add(r.key);
      await sweepShadowOrphans(materialId, serverIds, pendingKeys);
    }
    const after = await getShadowTakesForMaterial(materialId);
    const map = new Map<string, ShadowTakeRecord>();
    for (const r of after) map.set(r.key, r);
    // 票 270：标签与 map 在同一同步块内设置（原子配对，中间无 await）——
    // 迟到的旧素材刷新若先落点，标签仍指旧素材，门禁照样挡住它；新素材
    // 刷新落点时标签同步更新，行即恢复，无需另起 generation 守卫。
    this.shadowMeta.set(map);
    this.shadowMetaMaterial.set(materialId);
  }

  onMaterialChange(): void {
    this.stopShadowPlayback();
    this.shadowExpandedId.set(null);
    this.shadowStorageFailed.set(false);
    // 票 274：素材切换 → 待确认 take 归属旧素材，一并丢弃（不落库、不留行）；
    // 这条 take 的提示与写失败态同样作废，不留指向已丢 take 的文案。
    if (this.shadowPendingTake()) {
      this.stopPendingPreview();
      this.shadowPendingTake.set(null);
      this.shadowQuietHint.set(null);
      this.shadowError.set(null);
      this.lastFailedTakeId = null;
    }
    this.revokeRefUrl();
    void this.refreshShadowMeta();
  }

  // ---------- 采集 ----------
  async startShadow(): Promise<void> {
    if (this.starting || this.shadowRecording()) return;
    // 票 274：有待确认的跟读 take 时，禁开新录音（先 Submit 或 Discard）。
    // 注意：此门必须在下方清提示之前——否则这条提示会被清掉。
    if (this.shadowPendingTake()) {
      this.shadowError.set(this.host.t('practice.shadow.pendingBlocksRecording'));
      return;
    }
    this.shadowError.set(null);
    this.shadowQuietHint.set(null);
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
      // 票 278：模式每次授权时现读（下一条录音即生效；已授权的流约束
      // 已定格，录中切模式不影响本条）。
      // 票 284：模式在 take 开始时读一次 —— 授权约束、录制端增益、
      // 参考声压低三处共用同一值（读点仍在 grant 之前，278 语义不变）。
      const micMode = loadShadowMicMode();
      const capture = new ShadowCaptureService(
        () => requestMicStream(this.micDeviceId(), micMode),
        (fn) => this.host.ngZone.run(fn),
        (f) => this.onLiveFrame(f),
        () => this.onMidTakeFailure(),
        () => (this.refHowl ? this.refHowl.seek() || 0 : null),
        // 票 265：录制率跟示范条 x1（host.tts.rate），不再另设跟读倍速行。
        this.host.tts.rate(),
        undefined,
        micMode,
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
        // 票 284：扬声器模式下参考声在 take 期间适度压低（0.6×，绝非静音）——
        // 进入麦克风的回声绝对值变小，AEC 少取消、AGC 少向下增益；
        // 参考只在 take 内播放，故构造时压低 = take 期间压低，停参考时恢复。
        // 耳机模式无声学回声，音量保持 1.0 不动。
        volume: micMode === 'speaker' ? duckedVolume(1) : 1,
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
      this.refDucked = micMode === 'speaker';
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
      // 票 271 C：人声不近静音、但远小于参考 → 非阻塞过小提示（不叠加
      // noVoice；提示本身不改音频，修法在耳：近麦/查系统音量/换设备/耳机）。
      const voiceMax = this.takePeaks.reduce((m, p) => Math.max(m, p.v), 0);
      const refMax = this.shadowRefPeaks().reduce((m, p) => Math.max(m, p.v), 0);
      if (shouldShowQuietHint(voiceMax, refMax)) {
        this.shadowQuietHint.set(this.host.t('practice.shadow.quietVoiceHint'));
      }
      const localTakeId = `s_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 7)}`;
      const durationSec = jointDuration(
        this.takeRefDurSec / this.takeRate,
        result.voiceWallSec,
        result.laneShiftSec,
      );
      // 票 266：1x 与非 1x 同一生命周期——Stop 后直接是本地终态行，
      // 不再分 mix/dual 两条路；kind 恒为 dual（双轨本地播放，无需混音文件）。
      const kind = 'dual' as const;
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
        // 票 266：所有 take 都是已保存的本地行（mixStatus 'local'），
        // 无待提交态、无上传；行时长只是本地展示时长。
        kind,
        mixStatus: 'local',
        pendingUpload: false,
        createdAt: Date.now(),
        voiceBlob: result.blob,
        peaks: this.takePeaks,
        f0Contour: null,
        mixBlob: null,
        voiceWallSec: result.voiceWallSec,
        text,
      };
      // 票 274：Stop 后不直接落库——take 挂 pending，由卡片底部
      // Submit 落库进列表 / Discard 丢弃；列表只认 shadowMeta（IDB），
      // pending 期间行不出现。事后 F0 与刷新随提交走（见 submitShadowPending）。
      this.shadowPendingTake.set(rec);
    } finally {
      this.stopping = false;
      this.host.cdr.detectChanges();
    }
  }

  /**
   * 票 274：提交待确认的跟读 take——落库进列表（仅本地，永不上传）。
   * 写失败：pending 保留，用户可再点 Submit 重试（错误提示区重试钮同路）。
   * 写成功：事后 F0 照 P25 缓存，刷新落点后行出现，才清 pending。
   */
  async submitShadowPending(): Promise<void> {
    const rec = this.shadowPendingTake();
    if (!rec || this.submittingPending) return;
    this.submittingPending = true;
    try {
      this.stopPendingPreview();
      const persisted = await putShadowTake(rec);
      if (!persisted) {
        this.shadowStorageFailed.set(true);
        this.lastFailedTakeId = rec.localTakeId;
        this.shadowError.set(this.host.t('practice.shadow.storageFailed'));
        return;
      }
      this.shadowStorageFailed.set(false);
      this.lastFailedTakeId = null;
      this.midTakeFailed = false;
      // 事后 F0 算一次后缓存（P25），失败不阻塞审阅（波形仍在）。
      void this.computeAndCachePostF0(rec).catch(() => undefined);
      await this.refreshShadowMeta();
      this.shadowPendingTake.set(null);
    } finally {
      this.submittingPending = false;
      this.host.cdr.detectChanges();
    }
  }

  /**
   * 票 274：丢弃待确认的跟读 take——无行、无 blob；这条 take 的
   * 提示（过小提示/无声错误/写失败）一并清掉，不留指向已丢 take 的文案。
   */
  discardShadowPending(): void {
    if (!this.shadowPendingTake()) return;
    this.stopPendingPreview();
    this.shadowPendingTake.set(null);
    this.shadowQuietHint.set(null);
    this.shadowError.set(null);
    this.shadowStorageFailed.set(false);
    this.lastFailedTakeId = null;
    this.host.cdr.detectChanges();
  }

  /**
   * 票 274：试听待确认的跟读 take（参考 + 人声双轨；只动听的路径，
   * 与行一起听同一构造：增益级复用 attachTogetherGain，存档不动）。
   * 再点 = 暂停；Submit/Discard/切其他播放时由 stopPendingPreview 收口。
   */
  async previewShadowPending(): Promise<void> {
    const rec = this.shadowPendingTake();
    if (!rec) return;
    if (this.shadowPendingPlaying()) {
      this.stopPendingPreview();
      return;
    }
    this.stopOtherAudio();
    this.stopShadowPlayback();
    const voiceBlob = rec.voiceBlob;
    const cached = rec.refKey ? await getCachedAudio(rec.refKey) : null;
    const refBlob = cached?.blob ?? (this.refKey === rec.refKey ? this.refBlob : null);
    if (!voiceBlob || voiceBlob.size === 0 || !refBlob) {
      this.shadowError.set(this.host.t('practice.shadow.voiceMissing'));
      return;
    }
    const reviewRate = this.shadowReviewRate();
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
    await this.attachTogetherGain(vEl);
    this.controller = new ShadowPlaybackController(
      {
        mode: 'dual',
        tracks: [
          { id: 'voice', el: vEl, durationSec: rec.voiceWallSec },
          { id: 'reference', el: rEl, durationSec: rec.refDurationSec },
        ],
        reviewRate,
        laneShiftSec: rec.laneShiftSec,
        seekOffsetSec: 0,
      },
      (refTime) => {
        this.shadowPlayhead.set(refTime);
      },
    );
    this.controller.seek(0);
    // makeAudioEl 的 ended 处理只清 shadowPlayingId；待确认试听态另清。
    vEl.addEventListener('ended', () => {
      this.shadowPendingPlaying.set(false);
    }, { once: true });
    await this.controller.play();
    this.shadowPendingPlaying.set(true);
  }

  /** 票 274：停待确认 take 的试听（控制器销毁 + URL 回收，走统一收口）。 */
  stopPendingPreview(): void {
    if (!this.shadowPendingPlaying()) return;
    this.shadowPendingPlaying.set(false);
    this.stopShadowPlayback();
  }

  /** P29g：Stop 写失败后的重试写（写进 IDB 即清失败态；仍失败则保留）。 */
  async retryShadowWrite(): Promise<void> {
    // 票 274：待确认 take 的提交写失败 → 重试 = 再提交（pending 仍在内存）。
    const pending = this.shadowPendingTake();
    if (pending && pending.localTakeId === this.lastFailedTakeId) {
      await this.submitShadowPending();
      return;
    }
    if (!this.lastFailedTakeId) return;
    const meta = await this.storeRecord(this.lastFailedTakeId);
    if (!meta) return;
    const persisted = await putShadowTake(meta);
    if (persisted) {
      this.shadowStorageFailed.set(false);
      this.lastFailedTakeId = null;
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
    // 票 274：控制器销毁 = 待确认试听亦停（行播放会先走此处销毁待确认控制器）。
    this.shadowPendingPlaying.set(false);
    // 票 271 A：增益级随播放控制器一起断开（旧元素丢弃，新元素重挂）。
    this.voiceGainStage?.disconnect();
    this.voiceGainStage = null;
    this.shadowVoiceGain.set(0);
    for (const u of [this.expandedVoiceUrl, this.expandedRefUrl]) {
      if (u) URL.revokeObjectURL(u);
    }
    this.expandedVoiceUrl = null;
    this.expandedRefUrl = null;
    this.expandedVoiceEl = null;
    this.expandedRefEl = null;
    this.expandedMixEl = null;
  }

  /**
   * 票 274（自 271 A 抽出复用）+ 票 279（rebase 合并升级）：
   * dual together 人声挂增益级，行一起听与待确认试听共用。
   * ① 自适应增益：人声峰/参考峰取本行已算好的两条 lane（同 0..100
   * 口径），缺失时回落固定值（见模块注）。独奏/混音单轨不挂。
   * ② 挂接前门控：必须先等上下文 running 才挂图——元素一旦被
   * MediaElementSource 捕获，输出只走图，suspended 的图 = 静音，
   * 且断开图不能恢复元素直出；所以 resume 失败/超时时只能不挂图
   * （元素直出、绝不断播），不能先挂后拆。
   * 只动听的路径；挂接失败回落原播放，绝不断播。
   */
  private async attachTogetherGain(vEl: HTMLAudioElement): Promise<void> {
    if (typeof AudioContext === 'undefined') return;
    if (!this.voiceGainCtx) {
      try {
        this.voiceGainCtx = new AudioContext();
      } catch {
        this.voiceGainCtx = null;
      }
    }
    const { gain: togetherGain } = computeTogetherGain(
      peakMaxValue(this.shadowExpandedPeaks()),
      peakMaxValue(this.shadowRefPeaks()),
    );
    const ctx = this.voiceGainCtx;
    const running = ctx ? await ensureVoiceGainContextRunning(ctx, 1000) : false;
    this.voiceGainStage =
      ctx && running ? attachVoiceGainStage(ctx, vEl, togetherGain) : null;
    this.shadowVoiceGain.set(this.voiceGainStage ? this.voiceGainStage.gainValue : 0);
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
    // 票 279（经 274 抽出的方法复用）：dual together 人声挂增益级——
    // 自适应增益 + 挂接前门控的实现见 attachTogetherGain（行一起听与
    // 待确认试听共用同一链路）；挂接失败回落原播放，绝不断播。
    await this.attachTogetherGain(vEl);
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
      // 票 284：压低过的参考声在停/卸前恢复原音量（本 howl 随即 unload，
      // 恢复本身是防御性清理；真正的“不外泄”保证是作用域 —— 压低只施加
      // 于本条 take 的 howl 实例，停时 unload；下一条 take 建新 howl，
      // 扬声器模式下按构造时再次压低（设计如此），非 take 播放不用此 howl，
      // 故压低绝不漏到 take 之外）。
      if (this.refDucked) {
        try {
          this.refHowl.volume(1);
        } catch {
          /* ignore */
        }
        this.refDucked = false;
      }
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
