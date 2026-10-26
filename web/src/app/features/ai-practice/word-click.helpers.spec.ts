/**
 * 票120:word-click.helpers 纯函数单测(规格验证 ③(a) 的硬断言)。
 *
 * - wordClickAction:三分支(填充词+offset → replay / 填充词无 offset → pick /
 *   普通词 → pick),外加 offset=0 的「无偏移」口径(票122 约定:0 = 无偏移,绝不假 seek)。
 * - hasSeekableFiller:以桩谓词驱动四例 —— 谓词注入是钉死的接缝(S120-R2-3):
 *   helpers 不直达 session 的 FILLERS,填充词知识只经参数进入。
 * - clampFocusIndex:钳制三例(负数 → 0 / 超界 → len-1 / len=0 → -1)。
 */
import { describe, expect, it } from '@jest/globals';
import { clampFocusIndex, hasSeekableFiller, wordClickAction } from './word-click.helpers';

describe('wordClickAction(点击分发)', () => {
  it('填充词且 offsetMs>0 → replay', () => {
    expect(wordClickAction(true, 3200)).toBe('replay');
  });

  it('填充词无 offsetMs(缺失) → pick', () => {
    expect(wordClickAction(true, undefined)).toBe('pick');
    expect(wordClickAction(true, null)).toBe('pick');
  });

  it('填充词 offsetMs=0 → pick(0 即无偏移)', () => {
    expect(wordClickAction(true, 0)).toBe('pick');
  });

  it('普通词 → pick(即便带 offsetMs)', () => {
    expect(wordClickAction(false, 1500)).toBe('pick');
    expect(wordClickAction(false, undefined)).toBe('pick');
  });
});

describe('hasSeekableFiller(桩谓词 w => w === "um")', () => {
  const isUm = (w: string) => w === 'um';

  it('空词条 → false', () => {
    expect(hasSeekableFiller([], isUm)).toBe(false);
  });

  it('填充词无 offset → false', () => {
    expect(hasSeekableFiller([{ word: 'um' }], isUm)).toBe(false);
    expect(hasSeekableFiller([{ word: 'um', offsetMs: 0 }], isUm)).toBe(false);
  });

  it('填充词 offset>0 → true', () => {
    expect(hasSeekableFiller(
      [{ word: 'girls' }, { word: 'um', offsetMs: 3200 }], isUm)).toBe(true);
  });

  it('非填充词带 offset → false', () => {
    expect(hasSeekableFiller([{ word: 'girls', offsetMs: 1500 }], isUm)).toBe(false);
  });
});

describe('clampFocusIndex(焦点索引钳制)', () => {
  it('负数 → 0', () => {
    expect(clampFocusIndex(-3, 5)).toBe(0);
  });

  it('界内 → 原值', () => {
    expect(clampFocusIndex(0, 5)).toBe(0);
    expect(clampFocusIndex(4, 5)).toBe(4);
  });

  it('超界 → len-1', () => {
    expect(clampFocusIndex(9, 5)).toBe(4);
  });

  it('len=0 → -1(无停靠点)', () => {
    expect(clampFocusIndex(0, 0)).toBe(-1);
    expect(clampFocusIndex(3, 0)).toBe(-1);
  });
});
