/**
 * 票254 前端验收（PLAN-PATCHED §4 + panel 绑定项）：
 * displayForm 词头规则、块中文判定、合成钮可见性、口音钮播所点词形、
 * 懒层失败 → 合成自动回落（标签切换）、首次 pronounce 失败可见 error、
 * 手动重试带失败键全集排除。Howl 与 ObjectURL 皆替身，不碰真实音频。
 */
import { describe, expect, it, jest } from '@jest/globals';
import { of, throwError } from 'rxjs';
import { PracticeDictionary } from './practice-dictionary';
import {
  accentButtonVisible, blockHasChinese, displayHeadword,
} from './dictionary.helpers';
import type { DictCardData } from './dictionary.helpers';

jest.mock('howler', () => ({
  Howl: jest.fn().mockImplementation(() => ({
    play: jest.fn(),
    unload: jest.fn(),
    on: jest.fn(),
  })),
}));

const flush = async (): Promise<void> => {
  for (let i = 0; i < 5; i++) await new Promise((r) => setTimeout(r, 0));
};

function cardOf(overrides: Partial<DictCardData> = {}): DictCardData {
  return {
    query: 'purchases',
    lemma: 'purchase',
    matchedVia: 'morphy',
    tier: 'open',
    phonetics: { uk: '/ˈpɜːtʃəs/', us: '/ˈpɝːtʃəs/' },
    posBlocks: [],
    forms: [],
    audio: {
      us: { kind: 'synthetic', key: 'neural-purchases-us.mp3', available: true },
      uk: { kind: null, key: null, available: false },
    },
    attribution: { source: 'WordNet · ECDICT · Wikimedia Commons' },
    ...overrides,
  };
}

function setup(pronounceImpl: jest.Mock, fetchImpl: jest.Mock): PracticeDictionary {
  // 本工程 Jest 为 node 环境：PracticeDictionary 构造期只用 document 的监听注册，替身即可。
  (globalThis as unknown as { document: unknown }).document = {
    addEventListener: jest.fn(),
    removeEventListener: jest.fn(),
  };
  (globalThis.URL as unknown as { createObjectURL: unknown }).createObjectURL =
    jest.fn(() => 'blob:test');
  (globalThis.URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL = jest.fn();
  const host = {
    practiceApi: {
      lookupDictionaryWord: jest.fn(),
      pronounceDictionaryWord: pronounceImpl,
      fetchDictionaryAudio: fetchImpl,
    },
    recorder: {
      startShadowRecording: jest.fn(),
      stopShadowRecording: jest.fn(),
      cancelShadowRecording: jest.fn(),
      assessShadowRecording: jest.fn(),
    },
    readonlyText: () => '',
    closeDictPopup: jest.fn(),
    openDictPopup: jest.fn(),
    refreshDictPopupPosition: jest.fn(),
  };
  const dict = new PracticeDictionary(host as never);
  dict.state.set({
    status: 'ok', query: 'purchases', card: cardOf(), displayForm: 'Purchases',
  });
  return dict;
}

describe('票254 P3 displayHeadword', () => {
  it('所点词形小写后等于词元 → 显示词元原样（句首大写 Purchase 不变体）', () => {
    expect(displayHeadword('Purchase', 'purchase')).toBe('purchase');
    expect(displayHeadword('purchase', 'purchase')).toBe('purchase');
  });
  it('所点词形非词元 → 显示所点原文（大小写原样）', () => {
    expect(displayHeadword('Purchases', 'purchase')).toBe('Purchases');
    expect(displayHeadword('purchases', 'purchase')).toBe('purchases');
  });
});

describe('票254 P6 blockHasChinese', () => {
  it('ZhLines 非空 → 有中文', () => {
    expect(blockHasChinese({ zhLines: ['消失'], senses: [{ glossZh: null }] })).toBe(true);
  });
  it('义项 GlossZh 非空 → 有中文', () => {
    expect(blockHasChinese({ zhLines: [], senses: [{ glossZh: '消失' }] })).toBe(true);
  });
  it('两者全无 → 无中文（显式“暂无中文释义”行）', () => {
    expect(blockHasChinese({ zhLines: [], senses: [{ glossZh: null }, {}] })).toBe(false);
  });
});

describe('票254 §4 播放钮与回落', () => {
  it('合成能力 available=true → 口音钮可见（kind synthetic 不再被当作无发音）', () => {
    expect(accentButtonVisible(
      { us: { kind: 'synthetic', key: 'neural-x-us.mp3', available: true } }, 'us')).toBe(true);
  });

  it('口音钮播所点词形 query，不播词元 lemma', async () => {
    const pronounce = jest.fn(() => of({
      available: true, kind: 'commons', key: 'human-key',
    }));
    const fetch = jest.fn(() => of(new Blob(['x'], { type: 'audio/mpeg' })));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(pronounce).toHaveBeenCalledWith('purchases', 'us', {});
    expect(fetch).toHaveBeenCalledWith('human-key', 'purchases', 'us');
    expect(dict.playStatus().playedKind).toBe('commons');
    expect(dict.playStatus().playedAccent).toBe('us');
    dict.destroy();
  });

  it('词形钮播词形原文', async () => {
    const pronounce = jest.fn(() => of({
      available: true, kind: 'commons', key: 'human-key',
    }));
    const fetch = jest.fn(() => of(new Blob(['x'], { type: 'audio/mpeg' })));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'form', text: 'forgotten', accent: 'uk' });
    await flush();
    expect(pronounce).toHaveBeenCalledWith('forgotten', 'uk', {});
    dict.destroy();
  });

  it('懒层取流失败 → 一次自动回落：排除失败键、播合成、标签切换为 synthetic', async () => {
    const pronounce = jest.fn()
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'human-key' }))
      .mockReturnValueOnce(of({
        available: true, kind: 'synthetic', key: 'neural-purchases-us.mp3',
      }));
    const fetch = jest.fn((key: string) => (key === 'human-key'
      ? throwError(() => new Error('404'))
      : of(new Blob(['x'], { type: 'audio/mpeg' }))));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(pronounce).toHaveBeenNthCalledWith(2, 'purchases', 'us', { excludeKeys: ['human-key'] });
    expect(fetch).toHaveBeenCalledWith('neural-purchases-us.mp3', 'purchases', 'us');
    expect(dict.playStatus().phase).toBe('idle');
    expect(dict.playStatus().playedKind).toBe('synthetic');
    expect(dict.playStatus().playedAccent).toBe('us');
    dict.destroy();
  });

  it('首次 pronounce 即失败 → 可见 error 态，永不静默', async () => {
    const pronounce = jest.fn(() => throwError(() => new Error('network')));
    const fetch = jest.fn(() => of(new Blob(['x'], { type: 'audio/mpeg' })));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(dict.playStatus().phase).toBe('error');
    expect(dict.playStatus().word).toBe('purchases');
    expect(dict.playStatus().accent).toBe('us');
    dict.destroy();
  });

  it('手动重试带失败键全集排除', async () => {
    const pronounce = jest.fn()
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'human-key' }))
      .mockReturnValueOnce(of({
        available: true, kind: 'synthetic', key: 'neural-purchases-us.mp3',
      }))
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'human-key-2' }));
    const fetch = jest.fn((key: string) => (key === 'human-key-2'
      ? of(new Blob(['x'], { type: 'audio/mpeg' }))
      : throwError(() => new Error('404'))));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(dict.playStatus().phase).toBe('error');
    expect(dict.playStatus().failedKeys).toEqual(['human-key', 'neural-purchases-us.mp3']);
    dict.retryPlay();
    await flush();
    expect(pronounce).toHaveBeenNthCalledWith(3, 'purchases', 'us', {
      excludeKeys: ['human-key', 'neural-purchases-us.mp3'],
    });
    expect(dict.playStatus().phase).toBe('idle');
    expect(dict.playStatus().playedKind).toBe('commons');
    dict.destroy();
  });
});
