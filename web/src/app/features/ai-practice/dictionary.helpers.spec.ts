/**
 * 票119 词典纯函数单测(SPEC §7 前端单测):
 * 切分保标点 / 折叠视图与 foldedCount / roving 键盘全路径(索引层) /
 * 负结果简卡无「—」占位 / 口音切换禁用逻辑。
 * 票126 增补:TJ-2 分词用例表(R-T1…R-T5)/ errorStatusOf(F-1 判定)/
 * 口音记忆(TJ-7 单元层)/ lemmaRoleLabels(§9.1 数据侧)/ M-1 单元夹具(the 的 UK 在席)。
 */
import { describe, expect, it, beforeEach } from '@jest/globals';
import {
  tokenizePassage,
  lookupKeyOf,
  foldView,
  nextRovingIndex,
  isRovingKey,
  accentButtonVisible,
  anyAccentAvailable,
  negativeCardLines,
  errorStatusOf,
  loadDictAccent,
  saveDictAccent,
  lemmaRoleLabels,
  DICT_ACCENT_STORAGE_KEY,
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
  const none: DictAudioOption = { kind: null, available: false };
  const commons: DictAudioOption = { kind: 'commons', key: 'k1', available: true };

  it('available=false 的口音不渲染钮(无真人录音不算可用)', () => {
    const audio = { us: none, uk: none };
    expect(accentButtonVisible(audio, 'us')).toBe(false);
    expect(accentButtonVisible(audio, 'uk')).toBe(false);
    expect(anyAccentAvailable(audio)).toBe(false);
  });

  it('有录音的口音渲染钮', () => {
    const audio = { us: commons, uk: none };
    expect(accentButtonVisible(audio, 'us')).toBe(true);
    expect(accentButtonVisible(audio, 'uk')).toBe(false);
    expect(anyAccentAvailable(audio)).toBe(true);
  });

  it('M-1(S-1 专属验收,单元夹具):the 的 UK 真人录音在本批后仍在席', () => {
    // §8.6:美音优先 ≠ 删 UK 呈现;the 的 Commons UK 档(复核已验在席)必须照常可播
    const theAudio = {
      us: { kind: 'commons', key: 'k-us', available: true } as DictAudioOption,
      uk: { kind: 'commons', key: 'k-uk', available: true } as DictAudioOption,
    };
    expect(accentButtonVisible(theAudio, 'uk')).toBe(true);
    expect(accentButtonVisible(theAudio, 'us')).toBe(true);
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

describe('TJ-2 分词用例表(票126 §5.2,逐例断言跨度序列)', () => {
  const spansOf = (input: string): string[] =>
    tokenizePassage(input).filter((t) => t.kind === 'word').map((t) => t.text);

  const cases: [string, string[]][] = [
    ['Teenage', ['Teenage']],
    ['girls', ['girls']],
    ["don't", ["don't"]],
    ['well-known', ['well-known']],
    ['year-old', ['year-old']],
    ['14-year-old', ['year-old']],
    ['5-year', ['year']],
    ['80', []],
    ['80%', []],
    ['3.5', []],
    ['1,000', []],
    ['20th', []],
    ['a1', []],
  ];
  for (const [input, expected] of cases) {
    it(`「${input}」→ [${expected.join(', ')}]`, () => {
      expect(spansOf(input)).toEqual(expected);
    });
  }

  it('含数字/标点的整段拼接仍恒等于原文(切分不吞字符)', () => {
    const text = 'Last year, 80% of 14-year-old girls scored 3.5, up 1,000 points — a 20th-century record, don\'t doubt it.';
    expect(tokenizePassage(text).map((t) => t.text).join('')).toBe(text);
    const spans = spansOf(text);
    expect(spans).toContain('year-old');
    expect(spans).not.toContain('80');
    expect(spans).not.toContain('14-year-old');
  });
});

describe('errorStatusOf(F-1 判定,票126 §4.2)', () => {
  it('经 ApiClient 转换后的错误(普通 Error + .status)→ 读出状态码', () => {
    const converted = Object.assign(new Error('数据不存在'), { status: 404, original: {} });
    expect(errorStatusOf(converted)).toBe(404);
    expect(errorStatusOf(Object.assign(new Error('x'), { status: 500 }))).toBe(500);
  });

  it('原生形态(带 .status 的对象)→ 读出状态码', () => {
    expect(errorStatusOf({ status: 404 })).toBe(404);
    expect(errorStatusOf({ status: 0 })).toBe(0);
  });

  it('无状态码的错误 → null(落 error 态,不误判负卡)', () => {
    expect(errorStatusOf(new Error('boom'))).toBeNull();
    expect(errorStatusOf(null)).toBeNull();
    expect(errorStatusOf(undefined)).toBeNull();
    expect(errorStatusOf('nope')).toBeNull();
  });
});

describe('TJ-7 口音记忆(单元层,票126 §8.5)', () => {
  const store = new Map<string, string>();
  beforeEach(() => {
    store.clear();
    (globalThis as { localStorage?: unknown }).localStorage = {
      getItem: (k: string) => (store.has(k) ? store.get(k)! : null),
      setItem: (k: string, v: string) => { store.set(k, v); },
    };
  });

  it('键名钉死 dict.accent(M-6)', () => {
    expect(DICT_ACCENT_STORAGE_KEY).toBe('dict.accent');
  });

  it('无记忆默认 US;存 UK 后读回 UK;存 US 后读回 US', () => {
    expect(loadDictAccent()).toBe('us');
    saveDictAccent('uk');
    expect(store.get('dict.accent')).toBe('uk');
    expect(loadDictAccent()).toBe('uk');
    saveDictAccent('us');
    expect(loadDictAccent()).toBe('us');
  });

  it('脏值不认(只认 uk,其余一律 US)', () => {
    store.set('dict.accent', 'fr');
    expect(loadDictAccent()).toBe('us');
  });
});

describe('lemmaRoleLabels(票126 §9.1:归元标注由 forms 数据生成)', () => {
  const forms = [
    { label: '原形', text: 'disappear' },
    { label: '第三人称单数', text: 'disappears' },
    { label: '过去式/过去分词', text: 'disappeared' },
    { label: '现在分词', text: 'disappearing' },
  ];

  it('所查词形的角色 label 被取出(大小写不敏感)', () => {
    expect(lemmaRoleLabels(forms, 'disappeared')).toEqual(['过去式/过去分词']);
    expect(lemmaRoleLabels(forms, 'Disappears')).toEqual(['第三人称单数']);
  });

  it('forms 里没有的词形 → 空(标注退回只有 formOf 文案)', () => {
    expect(lemmaRoleLabels(forms, 'zorblax')).toEqual([]);
  });
});
