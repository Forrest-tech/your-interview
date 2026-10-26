import { describe, expect, it } from '@jest/globals';
import {
  SHOT_SCALE_DEFAULT,
  SHOT_SCALE_MAX,
  SHOT_SCALE_MIN,
  clampShotScale,
  shotDisplayWidth,
  stepShotScale,
} from './email-shot-dialog.component';

/**
 * 票 225：截图弹窗缩放纯函数（占位数值，与真实截图内容无关）。
 * 194 铁律：百分比数字恒等于实际缩放比例——宽度公式与标签同取 scale。
 */
describe('email shot dialog zoom', () => {
  it('clamps scale to 50%..300%', () => {
    expect(clampShotScale(49)).toBe(SHOT_SCALE_MIN);
    expect(clampShotScale(301)).toBe(SHOT_SCALE_MAX);
    expect(clampShotScale(100)).toBe(100);
    expect(clampShotScale(Number.NaN)).toBe(SHOT_SCALE_DEFAULT);
  });

  it('steps by 25% and stops at bounds', () => {
    expect(stepShotScale(100, 1)).toBe(125);
    expect(stepShotScale(100, -1)).toBe(75);
    expect(stepShotScale(300, 1)).toBe(300);
    expect(stepShotScale(50, -1)).toBe(50);
    expect(stepShotScale(290, 1)).toBe(300);
  });

  it('display width equals natural width times scale', () => {
    expect(shotDisplayWidth(800, 100)).toBe(800);
    expect(shotDisplayWidth(800, 50)).toBe(400);
    expect(shotDisplayWidth(800, 125)).toBe(1000);
    expect(shotDisplayWidth(800, 300)).toBe(2400);
    expect(shotDisplayWidth(null, 100)).toBeNull();
    expect(shotDisplayWidth(0, 100)).toBeNull();
  });
});
