/**
 * 票262 前端验收：卡内 Retry 在链耗尽时必须真能重试失败键。
 * 背景：词典弹窗 “Playback failed. Retry.” 时，retryPlay 原先只重发排除集；
 * 排除集已耗尽全部候选（合成不可用）时，重发永远返回 available=false，
 * Retry 按钮在卡内实际无效（只有通过关卡重开才能重取该键）。
 * 本票：手动重试中排除重发若链耗尽，再无排除重发一次（本轮唯一一次）；
 * 有候选时行为不变（仍走排除集前进逻辑，绝不为 Retry 清掉尚有候选的排除集）。
 * Howl 与 ObjectURL 皆替身，不碰真实音频。
 */
import { describe, expect, it, jest } from '@jest/globals';
import { of, throwError } from 'rxjs';
import { PracticeDictionary } from './practice-dictionary';
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
    query: 'forgotten',
    lemma: 'forget',
    matchedVia: 'exc',
    tier: 'open',
    phonetics: { uk: '/fəˈɡɒt.ən/', us: '/fɚˈɡɑː.tən/' },
    posBlocks: [],
    forms: [],
    audio: {
      us: { kind: 'commons', key: 'En-us-forgotten.ogg.mp3', available: true },
      uk: { kind: 'commons', key: 'En-us-ne-forgotten.ogg.mp3', available: true },
    },
    attribution: { source: 'WordNet · ECDICT · Wikimedia Commons' },
    ...overrides,
  };
}

function setup(pronounceImpl: jest.Mock, fetchImpl: jest.Mock): PracticeDictionary {
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
    status: 'ok', query: 'forgotten', card: cardOf(), displayForm: 'forgotten',
  });
  return dict;
}

describe('票262 Retry 链耗尽重取', () => {
  it('懒层失败 + 合成不可用 → error；Retry 排除重发耗尽后无排除重取同一键并播出', async () => {
    const pronounce = jest.fn()
      // 首播：人声键（懒层取流失败 → 自动回落）
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'En-us-forgotten.ogg.mp3' }))
      // 自动回落：排除人声键后无候选（合成不可用）→ error，failedKeys=[人声键]
      .mockReturnValueOnce(of({ available: false, kind: null, key: null }))
      // Retry：排除重发仍耗尽 → 无排除重取同一键
      .mockReturnValueOnce(of({ available: false, kind: null, key: null }))
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'En-us-forgotten.ogg.mp3' }));
    const fetchCalls = { human: 0 };
    const fetch = jest.fn((key: string) => {
      // 首播取流失败一次（模拟 Wikimedia 瞬时失败），Retry 重取时成功。
      if (key !== 'En-us-forgotten.ogg.mp3') return throwError(() => new Error('404'));
      fetchCalls.human++;
      return fetchCalls.human > 1
        ? of(new Blob(['x'], { type: 'audio/mpeg' }))
        : throwError(() => new Error('404'));
    });
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(dict.playStatus().phase).toBe('error');
    expect(dict.playStatus().failedKeys).toEqual(['En-us-forgotten.ogg.mp3']);
    dict.retryPlay();
    await flush();
    expect(pronounce).toHaveBeenNthCalledWith(3, 'forgotten', 'us', {
      excludeKeys: ['En-us-forgotten.ogg.mp3'],
    });
    expect(pronounce).toHaveBeenNthCalledWith(4, 'forgotten', 'us', {});
    expect(dict.playStatus().phase).toBe('idle');
    expect(dict.playStatus().playedKind).toBe('commons');
    expect(dict.playStatus().playedAccent).toBe('us');
    dict.destroy();
  });

  it('Retry 排除重发仍耗尽、无排除重发也无候选 → 仍可见 error，不循环', async () => {
    const pronounce = jest.fn()
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'En-us-forgotten.ogg.mp3' }))
      .mockReturnValueOnce(of({ available: false, kind: null, key: null }))
      .mockReturnValueOnce(of({ available: false, kind: null, key: null }))
      .mockReturnValueOnce(of({ available: false, kind: null, key: null }));
    const fetch = jest.fn(() => throwError(() => new Error('404')));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(dict.playStatus().phase).toBe('error');
    dict.retryPlay();
    await flush();
    expect(pronounce).toHaveBeenCalledTimes(4);
    expect(dict.playStatus().phase).toBe('error');
    // 新一轮失败键为空：下一次 Retry 即为全新尝试，不再背着旧排除集。
    expect(dict.playStatus().failedKeys).toEqual([]);
    dict.destroy();
  });

  it('Retry 排除重发有候选时不触发无排除重发（前进逻辑不变）', async () => {
    const pronounce = jest.fn()
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'human-key' }))
      .mockReturnValueOnce(of({ available: false, kind: null, key: null }))
      .mockReturnValueOnce(of({ available: true, kind: 'commons', key: 'human-key-2' }));
    const fetch = jest.fn((key: string) => (key === 'human-key-2'
      ? of(new Blob(['x'], { type: 'audio/mpeg' }))
      : throwError(() => new Error('404'))));
    const dict = setup(pronounce as never, fetch as never);
    dict.play({ target: 'accent', accent: 'us' });
    await flush();
    expect(dict.playStatus().phase).toBe('error');
    dict.retryPlay();
    await flush();
    expect(pronounce).toHaveBeenCalledTimes(3);
    expect(pronounce).toHaveBeenNthCalledWith(3, 'forgotten', 'us', {
      excludeKeys: ['human-key'],
    });
    expect(dict.playStatus().phase).toBe('idle');
    expect(dict.playStatus().playedKind).toBe('commons');
    dict.destroy();
  });
});
