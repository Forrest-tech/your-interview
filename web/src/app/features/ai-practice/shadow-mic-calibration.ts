/**
 * 票 292：录前麦克风校准 → 冻结增益（扬声器模式）。
 *
 * 背景（研究报告 §3 配方）：固定 ×3 仅 +9.5dB，此前是 Chrome AGC 在补
 * 20–40dB；291 关 AGC 后低灵敏度麦克风必然回到原形。成熟做法是录前
 * 校准 → 冻结增益（0 到 +30dB），录制中途不再重估；低于约 −50dBFS
 * 峰值时给硬件建议而非继续加数字增益。
 *
 * 本模块只含纯函数 + 存储（Jest 可测）：度量口径、门控、增益公式、
 * 存储键与合法性标准全部钉死在此处，不留惯例推断。会话层（校准流程、
 * 停播核对、互斥锁）见 practice-shadow-session.ts。
 *
 * 诚实局限（评分 minor，实装时注记，不改度量）：
 * - 95 百分位 nearest-rank 在约 30 帧里只排除顶端约 1 帧，故 2–3 帧
 *   的瞬态（咳嗽拖尾）仍可能定下峰值；失效方向安全（增益偏小，
 *   过小提示 + 重新校准兜底），不因此改口径。
 * - 12dB unclear 门无法区分语音与大间歇噪声（帧峰 95 百分位 ≈ RMS +
 *   11–12dB  crest，静音用户在吵房间里可能勉强过门）；失效方向受
 *   +30 钳制 + 下游限幅器 + 重新校准约束，不因此改口径。
 */

/** 目标峰值（dBFS）：校准要把正常跟读音量抬到的位置。 */
export const CALIBRATION_TARGET_DBFS = -12;
/** 增益钳制（dB）：下限 0 = 永不衰减（只放大）；上限 +30（报告配方）。 */
export const CALIBRATION_GAIN_MIN_DB = 0;
export const CALIBRATION_GAIN_MAX_DB = 30;
/** too-quiet 门（dBFS）：低于此峰值 = 硬件问题，不存增益（报告值）。 */
export const CALIBRATION_TOO_QUIET_DBFS = -50;
/**
 * unclear 门（dB）：峰值 − 底噪 < 12 → 没听清或环境太吵，重测。
 * 自选参数，不是研究报告值（报告的 6dB 是另一语境的运行时 RMS
 * 防抽吸规则；此处峰值−底噪用 12dB，方向偏保守）。
 */
export const CALIBRATION_MIN_PEAK_OVER_FLOOR_DB = 12;

/** dBFS 换算：满幅 1.0；x ≤ 0 时为 −∞（门控比较自然判 too-quiet）。 */
export function dbfs(x: number): number {
  if (!(x > 0)) return -Infinity;
  return 20 * Math.log10(x);
}

/**
 * 帧峰值 95 百分位（nearest-rank，不插值）：升序后取第
 * ceil(0.95·n) 个（1-based）；插值会把单帧瞬态部分重新引入。
 * 空数组 → null（调用方按测量失败处理，不存增益）。
 */
export function percentile95(values: readonly number[]): number | null {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.max(0, Math.ceil(0.95 * sorted.length) - 1)];
}

/** 全部采样 pooled 的 RMS（sqrt(mean(x²))），再转 dBFS。 */
export function pooledRmsDb(samples: ArrayLike<number>): number {
  if (!samples.length) return -Infinity;
  let sum = 0;
  for (let i = 0; i < samples.length; i++) sum += samples[i] * samples[i];
  return dbfs(Math.sqrt(sum / samples.length));
}

/** 增益公式：gainDb = −12 − measuredPeakDb，钳制 [0, +30]，转线性。 */
export function computeCalibrationGain(measuredPeakDb: number): {
  gainLinear: number;
  gainDb: number;
} {
  const raw = CALIBRATION_TARGET_DBFS - measuredPeakDb;
  const gainDb = Math.min(CALIBRATION_GAIN_MAX_DB, Math.max(CALIBRATION_GAIN_MIN_DB, raw));
  return { gainLinear: 10 ** (gainDb / 20), gainDb };
}

export type CalibrationGate = 'ok' | 'too-quiet' | 'unclear';

/**
 * 门控（求值顺序钉死）：too-quiet 优先于 unclear —— 两门同立时
 * （峰值极低且贴近底噪）显示硬件建议而非重测；两 outcome 皆不存增益。
 */
export function evaluateCalibration(peakDb: number, floorDb: number): CalibrationGate {
  if (peakDb < CALIBRATION_TOO_QUIET_DBFS) return 'too-quiet';
  if (peakDb - floorDb < CALIBRATION_MIN_PEAK_OVER_FLOOR_DB) return 'unclear';
  return 'ok';
}

/** 校准记录（localStorage 只存这 5 个数字，绝不存音频）。 */
export interface MicCalibrationRecord {
  gainLinear: number;
  gainDb: number;
  peakDb: number;
  floorDb: number;
  calibratedAt: number;
}

/** 存储键：按设备分键；无设备选择时 'default'。 */
export function calibrationKey(deviceId: string | null): string {
  return `yi-mic-gain-${deviceId ?? 'default'}`;
}

/**
 * 存储值合法性标准（钉死）：任一不成立即视为未校准。
 * - gainLinear 有限数、> 0、≤ 10^(30/20)（≈31.62）；
 * - gainDb 有限数，且与 gainLinear 换算一致（0.01dB 舍入误差内）；
 * - peakDb / floorDb 有限数；
 * - calibratedAt 有限数时间戳；未来值接受（日期仅展示，无过期逻辑读它）。
 */
export function isValidCalibration(value: unknown): value is MicCalibrationRecord {
  if (typeof value !== 'object' || value === null) return false;
  const v = value as Partial<MicCalibrationRecord>;
  if (
    typeof v.gainLinear !== 'number' || !Number.isFinite(v.gainLinear) ||
    !(v.gainLinear > 0) || v.gainLinear > 10 ** (CALIBRATION_GAIN_MAX_DB / 20)
  ) {
    return false;
  }
  if (typeof v.gainDb !== 'number' || !Number.isFinite(v.gainDb)) return false;
  if (Math.abs(v.gainDb - 20 * Math.log10(v.gainLinear)) > 0.01) return false;
  if (typeof v.peakDb !== 'number' || !Number.isFinite(v.peakDb)) return false;
  if (typeof v.floorDb !== 'number' || !Number.isFinite(v.floorDb)) return false;
  if (typeof v.calibratedAt !== 'number' || !Number.isFinite(v.calibratedAt)) return false;
  return true;
}

/** 读存储：失败 / 非法值一律视为未校准（返回 null）。 */
export function loadCalibration(deviceId: string | null): MicCalibrationRecord | null {
  try {
    const raw = localStorage.getItem(calibrationKey(deviceId));
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    return isValidCalibration(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * 写存储：成功 true；setItem 抛错 → false（调用方显示错误 note，
 * 状态保持未校准——绝不显示“已校准”；重校准失败不覆盖旧增益由调用方保证）。
 */
export function saveCalibration(
  deviceId: string | null,
  record: MicCalibrationRecord,
): boolean {
  try {
    localStorage.setItem(calibrationKey(deviceId), JSON.stringify(record));
    return true;
  } catch {
    return false;
  }
}
