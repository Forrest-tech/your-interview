/**
 * 票 137：波形柱形与基频估计的纯函数（与 AudioContext 解码解耦，可单测）。
 *
 * 口径（定稿 R-137-3 / R-137-4）：
 * - 柱形：56 根、按峰值归一化到 0~100、下限 6；空声道或全零峰值 → null。
 * - 平均频率：对同一份解码 AudioBuffer 做自相关基频估计 —— 约 50ms 分帧、
 *   RMS 门限剔除静音/非浊音帧、滞后范围对应 50~500 Hz、取浊音帧 F0 的均值、
 *   四舍五入为整数 Hz；无足够浊音帧 → null（绝不返回 0 Hz）。
 *   实现上先把声道降采样到 ≤8 kHz（人声 F0 ≤ 500 Hz，奈奎斯特绰绰有余），
 *   再逐帧自相关；帧数上限 300（均匀抽取），避免长录音在主线程算太久。
 */

/**
 * 票 261 §6：线形波形 —— 数值数组 → SVG polyline points（viewBox 0 0 1000 100）。
 * x 按全长等分（count 只截前 count 个点时 x 位置仍对齐全长，播放态覆盖层用）；
 * y = 100 − v/maxV*100（下沿锚定，与原柱顶一致）；点数 < 2 → ''。
 */
export function valuesToLinePoints(
  values: readonly number[],
  maxV = 100,
  count?: number,
): string {
  const n = values.length;
  if (n < 2 || !(maxV > 0)) return '';
  const vs = count === undefined ? values : values.slice(0, count);
  if (vs.length < 2) return '';
  const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
  return vs
    .map(
      (v, i) =>
        `${((i / (n - 1)) * 1000).toFixed(1)},${(100 - (clamp(v, 0, maxV) / maxV) * 100).toFixed(1)}`,
    )
    .join(' ');
}

/**
 * 票 261 §6：中线对称线形波形 —— 数值数组 → 上下两条 polyline points
 * （viewBox 0 0 1000 100，y = 50 ∓ v/maxV*50；原居中柱形的数据，只换画法）。
 * 返回 [上, 下]；点数 < 2 → ['', '']。
 * 注（票 275）：报告波形已改回单条下沿锚定线（valuesToLinePoints），不再调用
 * 本函数；函数与单测保留（不删覆盖），以备他处复用。
 */
export function valuesToMirroredLinePoints(
  values: readonly number[],
  maxV = 100,
  count?: number,
): [string, string] {
  const n = values.length;
  if (n < 2 || !(maxV > 0)) return ['', ''];
  const vs = count === undefined ? values : values.slice(0, count);
  if (vs.length < 2) return ['', ''];
  const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));
  const upper = vs
    .map((v, i) => `${((i / (n - 1)) * 1000).toFixed(1)},${(50 - (clamp(v, 0, maxV) / maxV) * 50).toFixed(1)}`)
    .join(' ');
  const lower = vs
    .map((v, i) => `${((i / (n - 1)) * 1000).toFixed(1)},${(50 + (clamp(v, 0, maxV) / maxV) * 50).toFixed(1)}`)
    .join(' ');
  return [upper, lower];
}

/** 峰值柱形：与 session.computeWaveBars 现行规则逐字一致。 */
export function computeWaveBarsFromSamples(
  data: ArrayLike<number>,
  bars = 56,
): number[] | null {
  if (!data.length || bars <= 0) return null;
  const block = Math.max(1, Math.floor(data.length / bars));
  const out: number[] = [];
  let peak = 0;
  for (let i = 0; i < bars; i++) {
    let max = 0;
    const start = i * block;
    const end = Math.min(data.length, start + block);
    for (let j = start; j < end; j++) {
      const v = Math.abs(data[j]);
      if (v > max) max = v;
    }
    out.push(max);
    if (max > peak) peak = max;
  }
  if (peak <= 0) return null;
  return out.map((v) => Math.max(6, Math.round((v / peak) * 100)));
}

/** 浊音判定门限：归一化自相关峰值低于此值视为非浊音帧。 */
const VOICED_CORR = 0.5;
/** 绝对静音底线（约 -60 dBFS）：低于此 RMS 的帧一律视为静音。 */
const ABS_SILENCE_RMS = 1e-3;
/** 相对静音门限：低于最响帧 RMS 此比例的帧视为静音/气声。 */
const REL_SILENCE_RATIO = 0.15;

export function estimateAvgF0(
  data: ArrayLike<number>,
  sampleRate: number,
): number | null {
  if (!data.length || !(sampleRate > 0)) return null;

  // 降采样到 ≤8 kHz（块内均值，抗混叠够用——F0 只需基频轮廓）。
  const factor = Math.max(1, Math.floor(sampleRate / 8000));
  const effSr = sampleRate / factor;
  const n = Math.floor(data.length / factor);
  if (n < 2) return null;
  const ds = new Float32Array(n);
  for (let i = 0; i < n; i++) {
    let s = 0;
    for (let k = 0; k < factor; k++) s += data[i * factor + k];
    ds[i] = s / factor;
  }

  const frameLen = Math.min(Math.max(2, Math.round(effSr * 0.05)), n); // 约 50ms（短音频则整段一帧）
  const totalFrames = Math.floor(n / frameLen);
  if (totalFrames < 1) return null;
  const minLag = Math.max(1, Math.floor(effSr / 500));
  const maxLag = Math.min(frameLen - 1, Math.ceil(effSr / 50));
  if (maxLag <= minLag) return null;

  // 先算各帧 RMS，定相对门限（全静音 → maxRms 为 0，直接 null）。
  const frameCount = Math.min(totalFrames, 300);
  const frameIdx: number[] = [];
  for (let k = 0; k < frameCount; k++) {
    frameIdx.push(Math.floor((k * totalFrames) / frameCount));
  }
  let maxRms = 0;
  const rmsList: number[] = [];
  for (const fi of frameIdx) {
    const start = fi * frameLen;
    let energy = 0;
    for (let i = 0; i < frameLen; i++) {
      const v = ds[start + i];
      energy += v * v;
    }
    const rms = Math.sqrt(energy / frameLen);
    rmsList.push(rms);
    if (rms > maxRms) maxRms = rms;
  }
  if (maxRms <= ABS_SILENCE_RMS) return null;
  const rmsGate = Math.max(ABS_SILENCE_RMS, REL_SILENCE_RATIO * maxRms);

  const f0s: number[] = [];
  for (let k = 0; k < frameIdx.length; k++) {
    if (rmsList[k] < rmsGate) continue; // 静音/气声帧
    const start = frameIdx[k] * frameLen;
    let energy = 0;
    for (let i = 0; i < frameLen; i++) {
      const v = ds[start + i];
      energy += v * v;
    }
    if (energy <= 0) continue;
    let bestLag = -1;
    let bestCorr = VOICED_CORR;
    for (let lag = minLag; lag <= maxLag; lag++) {
      let s = 0;
      for (let i = 0; i < frameLen - lag; i++) {
        s += ds[start + i] * ds[start + i + lag];
      }
      const corr = s / energy;
      if (corr > bestCorr) {
        bestCorr = corr;
        bestLag = lag;
      }
    }
    if (bestLag > 0) f0s.push(effSr / bestLag);
  }
  if (!f0s.length) return null;
  const mean = f0s.reduce((a, b) => a + b, 0) / f0s.length;
  return Math.round(mean);
}
