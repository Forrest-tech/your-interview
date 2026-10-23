/**
 * 票119 词典纯函数单测(SPEC §7 前端单测):
 * 切分保标点 / 折叠视图与 foldedCount / roving 键盘全路径(索引层) /
 * 负结果简卡无「—」占位 / 口音切换禁用逻辑。
 */
import { describe, expect, it } from '@jest/globals';
import {
  tokenizePassage,
  lookupKeyOf,
  foldView,
  nextRovingIndex,
  isRovingKey,
  accentButtonVisible,
  anyAccentAvailable,
  negativeCardLines,
  DictAudioOption,
} from './dictionary.helpers';

describe('tokenizePassage 切分保标点', () => {
  it('拼接全部 token 恒等于原文(空白与标点不丢)', () => {
    const text = 'Teenage girls disappeared, and the year-old map wasn\'t there.';
    const tokens = tokenizePassage(text);
    expect(tokens.map((t) => t.text).join('')).toBe(text);
  });

  it('词序连续,sep 的 wordIndex 为 -1', () => {
    const tokens = tokenizePassage('one, two  three');
    const words = tokens.filter((t) => t.kind === 'word');
    expect(words.map((w) => w.text)).toEqual(['one', 'two', 'three']);
    expect(words.map((w) => w.wordIndex)).toEqual([0, 1, 2]);
    expect(tokens.filter((t) => t.kind === 'sep').every((t) => t.wordIndex === -1)).toBe(true);
  });

  it('连字符词与缩略词整体成词(不拆)', () => {
    const words = tokenizePassage('a year-old boy can\'t go')
      .filter((t) => t.kind === 'word')
      .map((t) => t.text);
    expect(words).toEqual(['a', 'year-old', 'boy', "can't", 'go']);
  });

  it('空文本与纯标点', () => {
    expect(tokenizePassage('')).toEqual([]);
    expect(tokenizePassage('  ')).toEqual([{ kind: 'sep', text: '  ', wordIndex: -1 }]);
  });

  it('行首行尾标点各自成 sep token', () => {
    const tokens = tokenizePassage('"Hello," she said.');
    expect(tokens[0]).toEqual({ kind: 'sep', text: '"', wordIndex: -1 });
    expect(tokens[tokens.length - 1].text).toBe('.');
  });
});

describe('lookupKeyOf', () => {
  it('大小写折叠', () => {
    expect(lookupKeyOf('Disappeared')).toBe('disappeared');
    expect(lookupKeyOf('Year-Old')).toBe('year-old');
  });
});

describe('foldView 折叠展开/收起', () => {
  it('默认展开前 2 条,余者折叠并给出计数', () => {
    expect(foldView(5, false)).toEqual({ visible: 2, folded: 3 });
    expect(foldView(3, false)).toEqual({ visible: 2, folded: 1 });
  });

  it('展开后全显,折叠数为 0', () => {
    expect(foldView(5, true)).toEqual({ visible: 5, folded: 0 });
  });

  it('不足 2 条与 0 条无折叠', () => {
    expect(foldView(2, false)).toEqual({ visible: 2, folded: 0 });
    expect(foldView(1, false)).toEqual({ visible: 1, folded: 0 });
    expect(foldView(0, false)).toEqual({ visible: 0, folded: 0 });
  });
});

describe('nextRovingIndex 键盘全路径(索引层)', () => {
  it('左右方向键相邻移动,两端停住不回绕', () => {
    expect(nextRovingIndex(0, 'ArrowRight', 5)).toBe(1);
    expect(nextRovingIndex(4, 'ArrowRight', 5)).toBe(4);
    expect(nextRovingIndex(0, 'ArrowLeft', 5)).toBe(0);
    expect(nextRovingIndex(3, 'ArrowLeft', 5)).toBe(2);
  });

  it('上下方向键同线性移动', () => {
    expect(nextRovingIndex(1, 'ArrowDown', 5)).toBe(2);
    expect(nextRovingIndex(1, 'ArrowUp', 5)).toBe(0);
  });

  it('Home/End 到首尾词', () => {
    expect(nextRovingIndex(3, 'Home', 5)).toBe(0);
    expect(nextRovingIndex(1, 'End', 5)).toBe(4);
  });

  it('非导航键不移动;无词时为 -1', () => {
    expect(nextRovingIndex(2, 'a', 5)).toBe(2);
    expect(nextRovingIndex(0, 'ArrowRight', 0)).toBe(-1);
    expect(isRovingKey('ArrowRight')).toBe(true);
    expect(isRovingKey('Enter')).toBe(false);
  });
});

describe('口音切换禁用逻辑', () => {
  const device: DictAudioOption = { kind: 'device', available: false };
  const commons: DictAudioOption = { kind: 'commons', key: 'k1', available: true };

  it('available=false 的口音不渲染钮(device 不算可用)', () => {
    const audio = { us: device, uk: device };
    expect(accentButtonVisible(audio, 'us')).toBe(false);
    expect(accentButtonVisible(audio, 'uk')).toBe(false);
    expect(anyAccentAvailable(audio)).toBe(false);
  });

  it('有录音的口音渲染钮', () => {
    const audio = { us: commons, uk: device };
    expect(accentButtonVisible(audio, 'us')).toBe(true);
    expect(accentButtonVisible(audio, 'uk')).toBe(false);
    expect(anyAccentAvailable(audio)).toBe(true);
  });

  it('音频信息缺失时一律不可用', () => {
    expect(accentButtonVisible(null, 'us')).toBe(false);
    expect(accentButtonVisible(undefined, 'uk')).toBe(false);
    expect(anyAccentAvailable(null)).toBe(false);
  });
});

describe('负结果简卡', () => {
  it('只有词头与无词条文案,绝不含「—」占位符', () => {
    const lines = negativeCardLines('zorblax', '暂无词条释义');
    expect(lines).toEqual(['zorblax', '暂无词条释义']);
    expect(lines.join(' ')).not.toContain('—');
    expect(lines.join(' ')).not.toContain('--');
  });
});
