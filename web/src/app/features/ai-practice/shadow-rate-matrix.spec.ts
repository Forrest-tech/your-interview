/**
 * 票 250 P29c：倍速率矩阵纯函数单测（走查只覆盖对角 + 抽查对角外，
 * 完整 3×3 笛卡尔由本文件覆盖）。
 *
 * - 采集映射用 R_take：axis = (wall + laneShift) × R_take（seekOffset=0）。
 * - 审阅映射用 R_review：seek 逆映射 (T − seekOffset)/R_review − laneShift，
 *   与 mapWallToRefAxis 互逆（钳制区间内）。
 * - 行时长 §5：laneShift=0 时 jointDuration = max(refDur/R_take, voiceWall)。
 */
import { describe, expect, it } from '@jest/globals';
import {
  axisLength,
  expectedRefTime,
  jointDuration,
  mapWallToRefAxis,
  needsDriftCorrection,
  seekVoiceTime,
} from './shadow-waveform.helpers';

const RATES = [0.5, 1, 2];

describe('shadow rate matrix (P29c)', () => {
  it('全 3×3 (R_take, R_review)：审阅 seek 与映射互逆（钳制区间内）', () => {
    for (const rTake of RATES) {
      for (const rReview of RATES) {
        // 采集：wall 样本按 R_take 落轴。
        for (const w of [0, 3.7, 12.25]) {
          const s = 0.5;
          const axis = mapWallToRefAxis(w, s, rTake);
          expect(axis).toBeCloseTo((w + s) * rTake, 9);
          // 审阅：同一轴位置按 R_review 逆映射回 wall，钳制语义如实断言
          // （轴位置 / R_review − laneShift < 0 时钳到 0，不是恒等）。
          const raw = axis / rReview - s;
          const back = seekVoiceTime(axis, 0, s, rReview, 60);
          expect(back).toBeCloseTo(Math.min(Math.max(raw, 0), 60), 9);
        }
        // R_take = R_review 时往返恒等（同一率下映射与其逆映射互逆）。
        for (const w of [0, 5, 20]) {
          const s = 0.25;
          const axis = mapWallToRefAxis(w, s, rTake);
          const back = seekVoiceTime(axis, 0, s, rTake, 60);
          expect(back).toBeCloseTo(w, 9);
        }
      }
    }
  });

  it('漂移期望公式按 R_review 线性：expectedRef = start + (voice − voiceStart) × R', () => {
    for (const rReview of RATES) {
      expect(expectedRefTime(7, 2, 3, rReview)).toBeCloseTo(3 + 5 * rReview, 9);
    }
    // 50ms 阈与率无关（阈是绝对时间差）。
    expect(needsDriftCorrection(10, 10.051)).toBe(true);
    expect(needsDriftCorrection(10, 10.049)).toBe(false);
  });

  it('行时长 §5 按 R_take：max(refDur/R_take, voiceWall)（laneShift=0）', () => {
    expect(jointDuration(20 / 0.5, 12, 0)).toBeCloseTo(40, 9);
    expect(jointDuration(20 / 1, 12, 0)).toBeCloseTo(20, 9);
    expect(jointDuration(20 / 2, 12, 0)).toBeCloseTo(12, 9);
  });

  it('轴长取大：max(refDur, voiceSpan)', () => {
    expect(axisLength(21, 44)).toBe(44);
    expect(axisLength(21, 10)).toBe(21);
  });
});
