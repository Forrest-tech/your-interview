/**
 * 票 250：跟读模式的纯函数（与 AudioContext / MediaRecorder 解耦，可单测）。
 *
 * 口径（用户定稿 + panel 补丁 P1–P13）：
 * - 实时活声峰值：AnalyserNode 时域数据的最大绝对值 → 0~100。
 * - 音高（P2）：仅浊音帧且置信度过阈才给 F0；浊音/低置信帧返回 null（无点 = 不断言）。
 *   事后轮廓同门控，浊音间隙记 null、不断线插值。
 * - 时间轴（P5）：共享轴 = 参考音轨时间。活声墙钟偏移 w 映射到音轨时间
 *   T = (w + laneShift) * rate；反向 w = T / rate - laneShift。
 *   实时采集直接用 Howler seek()（即音轨时间）给每帧打点，不依赖墙钟换算。
 * - 混音排期（P3/P8）：仅 1x 跟读渲染混音；排期 = 参考与活声按 laneShift
 *   对齐后的并集长度，即渲染后混音图的真实时长（durationSeconds 发这个值）。
 */

/** 活声峰值点：t = 参考音轨时间（秒），v = 0~100 归一化峰值。 */
export interface ShadowPeak {
  t: number;
  v: number;
}

/** 实时一帧的峰值：时域数据最大绝对值 → 0~100 取整。静音 → 0。 */
export function framePeak(timeDomain: ArrayLike<number>): number {
  let max = 0;
  for (let i = 0; i < timeDomain.length; i++) {
    const v = Math.abs(timeDomain[i]);
    if (v > max) max = v;
  }
  return Math.round(max * 100);
}

/** 浊音判定门限：与票 137 estimateAvgF0 同口径（归一化自相关峰值）。 */
const VOICED_CORR = 0.5;
/** 绝对静音底线（约 -60 dBFS）。 */
const ABS_SILENCE_RMS = 1e-3;
/** 相对静音门限：低于最响帧 RMS 此比例的帧视为静音/气声（轮廓用）。 */
const REL_SILENCE_RATIO = 0.15;

/**
 * 单窗口基频估计（实时音高条用）。
 * 约 50ms 窗、50~500 Hz、自相关峰值即置信度；非浊音/静音/过短 → null。
 * 降采样到 ≤8 kHz（与票 137 同口径，人声 F0 ≤ 500 Hz 足够）。
 */
export function estimateWindowF0(
  samples: ArrayLike<number>,
  sampleRate: number,
): { hz: number; conf: number } | null {
  if (!samples.length || !(sampleRate > 0)) return null;
  const factor = Math.max(1, Math.floor(sampleRate / 8000));
  const effSr = sampleRate / factor;
  const n = Math.floor(samples.length / factor);
  if (n < 2) return null;
  const ds = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    let s = 0;
    for (let k = 0; k < factor; k++) s += samples[i * factor + k];
    ds[i] = s / factor;
  }
  const frameLen = Math.min(Math.max(2, Math.round(effSr * 0.05)), n);
  if (frameLen < 4) return null;
  let energy = 0;
  for (let i = 0; i < frameLen; i++) energy += ds[i] * ds[i];
  const rms = Math.sqrt(energy / frameLen);
  if (rms <= ABS_SILENCE_RMS) return null;
  const minLag = Math.max(1, Math.floor(effSr / 500));
  const maxLag = Math.min(frameLen - 1, Math.ceil(effSr / 50));
  if (maxLag <= minLag) return null;
  let bestLag = -1;
  let bestCorr = VOICED_CORR;
  for (let lag = minLag; lag <= maxLag; lag++) {
    let s = 0;
    for (let i = 0; i < frameLen - lag; i++) s += ds[i] * ds[i + lag];
    const corr = s / energy;
    if (corr > bestCorr) {
      bestCorr = corr;
      bestLag = lag;
    }
  }
  if (bestLag <= 0) return null;
  return { hz: effSr / bestLag, conf: bestCorr };
}

/** 短中值平滑（P2 实时音高点用）：取最近 ≤3 个浊音 Hz 的中值；空 → null。 */
export function medianHz(values: number[]): number | null {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

/**
 * 事后 F0 轮廓（P2）：按 hopSec 分帧逐窗估计；浊音间隙记 null、不插值。
 * 返回每跳一个值（Hz 取整或 null），长度 = ceil(时长 / hopSec)。
 */
export function computeF0Contour(
  data: ArrayLike<number>,
  sampleRate: number,
  hopSec = 0.1,
): (number | null)[] {
  if (!data.length || !(sampleRate > 0) || !(hopSec > 0)) return [];
  const factor = Math.max(1, Math.floor(sampleRate / 8000));
  const effSr = sampleRate / factor;
  const n = Math.floor(data.length / factor);
  const ds = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    let s = 0;
    for (let k = 0; k < factor; k++) s += data[i * factor + k];
    ds[i] = s / factor;
  }
  const frameLen = Math.min(Math.max(2, Math.round(effSr * 0.05)), n);
  const hopLen = Math.max(1, Math.round(effSr * hopSec));
  const minLag = Math.max(1, Math.floor(effSr / 500));
  const maxLag = Math.min(frameLen - 1, Math.ceil(effSr / 50));
  if (maxLag <= minLag) return [];
  // 先算各跳 RMS，定相对门限（与票 137 同口径）。
  const hopCount = Math.max(1, Math.ceil(n / hopLen));
  const rmsList: number[] = [];
  let maxRms = 0;
  for (let h = 0; h < hopCount; h++) {
    const start = h * hopLen;
    const len = Math.min(frameLen, n - start);
    if (len <= 0) {
      rmsList.push(0);
      continue;
    }
    let energy = 0;
    for (let i = 0; i < len; i++) energy += ds[start + i] * ds[start + i];
    const rms = Math.sqrt(energy / len);
    rmsList.push(rms);
    if (rms > maxRms) maxRms = rms;
  }
  const rmsGate = Math.max(ABS_SILENCE_RMS, REL_SILENCE_RATIO * maxRms);
  const out: (number | null)[] = [];
  for (let h = 0; h < hopCount; h++) {
    if (rmsList[h] < rmsGate) {
      out.push(null);
      continue;
    }
    const start = h * hopLen;
    const len = Math.min(frameLen, n - start);
    let energy = 0;
    for (let i = 0; i < len; i++) energy += ds[start + i] * ds[start + i];
    if (energy <= 0) {
      out.push(null);
      continue;
    }
    let bestLag = -1;
    let bestCorr = VOICED_CORR;
    for (let lag = minLag; lag <= Math.min(maxLag, len - 1); lag++) {
      let s = 0;
      for (let i = 0; i < len - lag; i++) s += ds[start + i] * ds[start + i + lag];
      const corr = s / energy;
      if (corr > bestCorr) {
        bestCorr = corr;
        bestLag = lag;
      }
    }
    out.push(bestLag > 0 ? Math.round(effSr / bestLag) : null);
  }
  return out;
}

/**
 * 混音排期（P3/P8）：laneShift > 0 = 活声晚于参考起播；< 0 = 活声先起。
 * 返回两路在混音图中的起播偏移与混音图总长（即 durationSeconds 该发的值）。
 */
export function computeMixSchedule(
  laneShiftMs: number,
  refDurSec: number,
  voiceDurSec: number,
): { refStartSec: number; voiceStartSec: number; mixDurSec: number } {
  const shift = laneShiftMs / 1000;
  const refStartSec = Math.max(0, -shift);
  const voiceStartSec = Math.max(0, shift);
  const mixDurSec = Math.max(refStartSec + refDurSec, voiceStartSec + voiceDurSec);
  return { refStartSec, voiceStartSec, mixDurSec };
}

/** 活声墙钟偏移 → 参考音轨时间（P5 映射，双路同步 seek 用）。 */
export function voiceTimeToTrackTime(
  voiceWallSec: number,
  laneShiftSec: number,
  rate: number,
): number {
  return (voiceWallSec + laneShiftSec) * rate;
}

/** 参考音轨时间 → 活声墙钟偏移（P5 映射，双路同步 seek 用）。 */
export function trackTimeToVoiceTime(
  trackSec: number,
  laneShiftSec: number,
  rate: number,
): number {
  if (!(rate > 0)) return trackSec - laneShiftSec;
  return trackSec / rate - laneShiftSec;
}

/**
 * 票 250 P15/P16/P17/P23/P27（第二轮补丁）：
 * - mapWallToRefAxis：§2 通用公式 x = (w + laneShift) * rate + seekOffset。
 *   采集时 seekOffset_capture = 0（P24：采集恒从参考 0 起），此时退化为
 *   既有 voiceTimeToTrackTime；审阅 seek 时 seekOffset 才非零。
 * - jointDuration：两轨在墙钟上的并集长度 = max(refWall, shift + voiceWall)
 *   − min(0, shift)。laneShift ≈ 0（同起）时退化为 §5 的 max 公式；
 *   shift 非零时以并集为准（§5 max 会低估 shift 造成的尾长），1x 混音的
 *   渲染时长以 computeMixSchedule 的 mixDurSec 为准，两者 shift≈0 时一致。
 * - seek 的完整逆映射：voiceWall = (T − seekOffset) / rate − laneShift，
 *   再钳到 [0, voiceDur]。§3 文本的 (T − seekOffset)/R 是 laneShift≈0
 *   时的特例；此处用完整逆映射，采集同起时两者差 < 启动 skew（毫秒级）。
 */

/** 墙钟偏移 → 参考音轨时间（含 seekOffset 的通用公式，P15）。 */
export function mapWallToRefAxis(
  voiceWallSec: number,
  laneShiftSec: number,
  rate: number,
  seekOffsetSec = 0,
): number {
  return (voiceWallSec + laneShiftSec) * rate + seekOffsetSec;
}

/** 共享轴总长：参考轨长与当前活声轨长取大（P15 轴扩展，P24 采集 seekOffset=0）。 */
export function axisLength(refDurSec: number, voiceRefSpanSec: number): number {
  return Math.max(refDurSec, voiceRefSpanSec);
}

/** 双路同步的参考期望位置（P16 漂移校正的主时钟公式）。 */
export function expectedRefTime(
  voiceCurrentSec: number,
  voiceStartWallSec: number,
  seekStartRefSec: number,
  rate: number,
): number {
  return seekStartRefSec + (voiceCurrentSec - voiceStartWallSec) * rate;
}

/** 漂移是否超阈（P16：50ms，低于 ~100ms 可感知阈、高于元素抖动）。 */
export function needsDriftCorrection(refCurrentSec: number, expectedSec: number): boolean {
  return Math.abs(refCurrentSec - expectedSec) > 0.05;
}

/** 参考音轨时间 → 活声墙钟偏移（完整逆映射，再钳到 [0, voiceDur]）。 */
export function seekVoiceTime(
  trackSec: number,
  seekOffsetSec: number,
  laneShiftSec: number,
  rate: number,
  voiceDurSec: number,
): number {
  if (!(rate > 0)) return Math.min(Math.max(trackSec - seekOffsetSec - laneShiftSec, 0), Math.max(0, voiceDurSec));
  const w = (trackSec - seekOffsetSec) / rate - laneShiftSec;
  return Math.min(Math.max(w, 0), Math.max(0, voiceDurSec));
}

/** 两轨墙钟并集时长（行 durationSeconds 的通用口径，见文件头注）。 */
export function jointDuration(refWallSec: number, voiceWallSec: number, laneShiftSec: number): number {
  return Math.max(refWallSec, laneShiftSec + voiceWallSec) - Math.min(0, laneShiftSec);
}

/**
 * 连续性门（P27）：浊音点仅当落在最近 ≤5 个已示浊音点的滚动中值
 * [0.7, 1.4] 倍内才显示；历史 < 3 个点时豁免（中值不可信）。
 * 孤立的自信八度错（2x/0.5x 跳变）落在此带外 → 隐藏；持续八度漂移会
 * 把中值本身带走，属 §6 已承认的残余，不在此门消除。
 */
export function continuityGateHz(hz: number, recentShownHz: number[]): number | null {
  if (recentShownHz.length < 3) return hz;
  const med = medianHz(recentShownHz.slice(-5));
  if (med === null || !(med > 0)) return hz;
  return hz >= 0.7 * med && hz <= 1.4 * med ? hz : null;
}

/**
 * 孤儿判定（P14 绑定口径）：只有同时满足“无服务端行、无持久待提交
 * 记录、无待上传/重试项”才是孤儿；任一条件不满足 → 不是孤儿，绝不删。
 * 怀疑时保留（fail-safe keep）是调用方的责任：本函数只做纯判定。
 */
export function isShadowOrphan(
  hasServerRow: boolean,
  hasPersistedPending: boolean,
  hasPendingUploadOrRetry: boolean,
): boolean {
  return !hasServerRow && !hasPersistedPending && !hasPendingUploadOrRetry;
}

/**
 * 去重可见性（P26）：同一 take 的本地记录与 GUID 记录并存时只显 GUID
 * 记录。输入为 {key, serverId, localTakeId}，localTakeId 指向同一 take
 * 的本地键；返回应显示的 key 集合。
 */
export function dedupeShadowKeys(
  records: { key: string; serverId: string | null; localTakeId: string }[],
): string[] {
  const guidByLocal = new Map<string, string>();
  for (const r of records) if (r.serverId) guidByLocal.set(r.localTakeId, r.key);
  return records
    .filter((r) => !(r.serverId === null && guidByLocal.has(r.localTakeId)))
    .map((r) => r.key);
}

/**
 * 峰值抽稀：峰数超上限时成对合并（t 取前者、v 取较大者），直到不超限。
 * 长跟读的存储与绘制保护；合并绝不凭空造峰（只取已观测的最大值）。
 */
export function decimatePeaks(peaks: ShadowPeak[], maxCount: number): ShadowPeak[] {
  if (peaks.length <= maxCount || maxCount < 1) return peaks;
  let cur = peaks;
  while (cur.length > maxCount) {
    const next: ShadowPeak[] = [];
    for (let i = 0; i < cur.length; i += 2) {
      const a = cur[i];
      const b = cur[i + 1];
      next.push(b ? { t: a.t, v: Math.max(a.v, b.v) } : a);
    }
    cur = next;
  }
  return cur;
}
