/**
 * 票131 前端验收(TJ-131-1…TJ-131-8;SPEC §9.1/§9.2)。
 *
 * 基建口径(同 126/127/128):Jest = node 环境 + @angular/* 替身，无 TestBed / DOM 渲染。
 * 被测面：
 *   - 纯函数 tabEnabled / cardHasAnyAudio（R-T1/R-T2）——真跑；
 *   - 模板接线（页签绑定、音标行条件、状态行条件、词元钮隔离、记忆机制）以源码契约
 *     断言落码，真实 DOM 行为由活体 E1–E5 兜底。
 * 存量 supersede（§9.2 审计，逐条登记，见 VERIFY-131）：
 *   - S-131-1（K-1 预登记）：126 TJ-7/M-3 的 tab disabled 源码契约改钉 tabEnabled 绑定；
 *   - S-131-2：126「两口音皆不可用时渲染 dict.noAudio 文案行」的状态行源码契约改钉
 *     cardHasAnyAudio 绑定；两处皆只改绑定名、断言意图不变，其余存量断言不许动。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';
import { accentButtonVisible, cardHasAnyAudio, tabEnabled } from './dictionary.helpers';
import type { DictCardData } from './dictionary.helpers';

type Card = Pick<DictCardData, 'audio' | 'forms'>;

const readSrc = (rel: string): string => readFileSync(join(__dirname, rel), 'utf8');

const audioOf = (us: boolean, uk: boolean): Card['audio'] => ({
  us: { kind: 'commons', key: 'u', available: us },
  uk: { kind: 'commons', key: 'k', available: uk },
});

// reached 型夹具（规格 C-131-7）：词元 us 有、uk 无；forms uk 仅 reaches 有。
const reachedCard: Card = {
  audio: audioOf(true, false),
  forms: [
    { label: '原形', text: 'reach', audio: { us: true, uk: false } },
    { label: '第三人称单数', text: 'reaches', audio: { us: false, uk: true } },
    { label: '过去式', text: 'reached', audio: { us: true, uk: false } },
    { label: '现在分词', text: 'reaching', audio: { us: true, uk: false } },
  ],
};

// ---------- TJ-131-1（纯函数矩阵 R-T1/R-T2） ----------

describe('TJ-131-1（放行判据纯函数矩阵）', () => {
  it('reached 型：词元 uk 无但 reaches uk 有 → uk 页签放行、us 放行、整卡有声', () => {
    expect(tabEnabled(reachedCard, 'uk')).toBe(true);
    expect(tabEnabled(reachedCard, 'us')).toBe(true);
    expect(cardHasAnyAudio(reachedCard)).toBe(true);
  });

  it('词元与全部词形皆无 → 两页签皆禁、整卡无声（第 7 节等价形态）', () => {
    const card: Card = {
      audio: audioOf(false, false),
      forms: [
        { label: '原形', text: 'wug', audio: { us: false, uk: false } },
        { label: '复数', text: 'wugs', audio: { us: false, uk: false } },
      ],
    };
    expect(tabEnabled(card, 'us')).toBe(false);
    expect(tabEnabled(card, 'uk')).toBe(false);
    expect(cardHasAnyAudio(card)).toBe(false);
  });

  it('词元 us 有、词形皆无 → 退化为词元判据（us 放行、uk 禁）', () => {
    const card: Card = {
      audio: audioOf(true, false),
      forms: [{ label: '原形', text: 'wug', audio: { us: false, uk: false } }],
    };
    expect(tabEnabled(card, 'us')).toBe(true);
    expect(tabEnabled(card, 'uk')).toBe(false);
    expect(cardHasAnyAudio(card)).toBe(true);
  });

  it('词元皆无、仅某词形 us 有 → 仅 us 放行（词形侧析取）', () => {
    const card: Card = {
      audio: audioOf(false, false),
      forms: [
        { label: '原形', text: 'wug', audio: { us: true, uk: false } },
        { label: '复数', text: 'wugs', audio: { us: false, uk: false } },
      ],
    };
    expect(tabEnabled(card, 'us')).toBe(true);
    expect(tabEnabled(card, 'uk')).toBe(false);
    expect(cardHasAnyAudio(card)).toBe(true);
  });

  it('forms 为空数组 → 退化为词元判据', () => {
    const card: Card = { audio: audioOf(false, true), forms: [] };
    expect(tabEnabled(card, 'us')).toBe(false);
    expect(tabEnabled(card, 'uk')).toBe(true);
    expect(cardHasAnyAudio(card)).toBe(true);
  });

  it('脏值格：某词形 audio.uk 为字符串 "true" → 不构成放行（R-T2 严格口径）', () => {
    const card: Card = {
      audio: audioOf(false, false),
      forms: [
        { label: '原形', text: 'wug', audio: { us: false, uk: 'true' as unknown as boolean } },
      ],
    };
    expect(tabEnabled(card, 'uk')).toBe(false);
    expect(cardHasAnyAudio(card)).toBe(false);
  });

  it('混搭格：词形 audio 缺失 / null → 该词形不构成放行（F-S1/F-S2）', () => {
    const card: Card = {
      audio: audioOf(false, false),
      forms: [
        { label: '原形', text: 'wug' },
        { label: '复数', text: 'wugs', audio: null as unknown as { us: boolean; uk: boolean } },
      ],
    };
    expect(tabEnabled(card, 'us')).toBe(false);
    expect(tabEnabled(card, 'uk')).toBe(false);
    expect(cardHasAnyAudio(card)).toBe(false);
  });
});

// ---------- TJ-131-2（组件：页签与标注 R-T5，零请求 R-T3） ----------

describe('TJ-131-2（页签绑定与标注跟随新判据）', () => {
  const card = readSrc('dictionary-card.component.ts');

  it('tab 的 disabled/title/aria-label 三处绑定一律以 tabEnabled 为据', () => {
    expect(card).toContain('[disabled]="!tabEnabled(c, acc)"');
    expect(card).toContain('[attr.title]="!tabEnabled(c, acc) ? noAudioLabel(acc) : null"');
    expect(card).toContain(
      `[attr.aria-label]="!tabEnabled(c, acc) ? noAudioLabel(acc) : (acc === 'us' ? t('dict.accentUs') : t('dict.accentUk'))"`,
    );
  });

  it('旧词元级 tab 绑定已无残留（无可点页签带暂无发音标注的中间态）', () => {
    expect(card).not.toContain('[disabled]="!accentVisible(c, acc)"');
    expect(card).not.toContain('[attr.title]="!accentVisible(c, acc) ? noAudioLabel(acc) : null"');
  });

  it('判据零请求：tabEnabled 纯函数体内无取流/网络调用；页签点击仍只走 setAccent', () => {
    const helpers = readSrc('dictionary.helpers.ts');
    const fn = helpers.slice(
      helpers.indexOf('export function tabEnabled('),
      helpers.indexOf('export function cardHasAnyAudio('),
    );
    expect(fn).not.toContain('fetch');
    expect(fn).not.toContain('HttpClient');
    const setAccent = card.slice(card.indexOf('protected setAccent('));
    const setAccentBody = setAccent.slice(0, setAccent.indexOf('\n  }'));
    expect(setAccentBody).toContain('this.accent.set(acc)');
    expect(setAccentBody).toContain('saveDictAccent(acc)');
    expect(setAccentBody).not.toContain('fetch');
  });
});

// ---------- TJ-131-3（判别：词元钮隔离 R-T4） ----------

describe('TJ-131-3（页签放行 ≠ 词元钮出现）', () => {
  it('纯函数判别：reached uk 下 tabEnabled 为真而词元钮判据为假', () => {
    expect(tabEnabled(reachedCard, 'uk')).toBe(true);
    expect(accentButtonVisible(reachedCard.audio, 'uk')).toBe(false);
  });

  it('模板判别：词元播放钮仍由 accentVisible 门控，不跟随 tabEnabled', () => {
    const card = readSrc('dictionary-card.component.ts');
    const lemmaBtn = card.slice(
      // 票254:phoneticOf 现回 { text, lemmaFallback }，模板变量随之改名（渲染门控意图不变）。
      card.indexOf("@if (phoneticOf(c, acc); as ph)"),
      card.indexOf('dict-play-btn'),
    );
    expect(lemmaBtn).toContain('@if (accentVisible(c, acc))');
    expect(lemmaBtn).not.toContain('tabEnabled');
  });
});

// ---------- TJ-131-4（音标行渲染 R-T6） ----------

describe('TJ-131-4（音标行渲染条件跟随新判据）', () => {
  it('音标行条件为 phoneticOf ∨ tabEnabled；旧词元级条件无残留', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card).toContain('@if (phoneticOf(c, acc) || tabEnabled(c, acc))');
    expect(card).not.toContain('@if (phoneticOf(c, acc) || accentVisible(c, acc))');
  });

  it('IPA 文本本身仍由 phoneticOf 渲染（与音频判据无关）', () => {
    const card = readSrc('dictionary-card.component.ts');
    // 票254:phoneticOf 现回 { text, lemmaFallback }，模板变量随之改名（IPA 仍由 phoneticOf 渲染）。
    expect(card).toContain('@if (phoneticOf(c, acc); as ph)');
  });
});

// ---------- TJ-131-5（整卡状态行 R-T7） ----------

describe('TJ-131-5（整卡状态行卡级触发）', () => {
  it('状态行条件为 !cardHasAnyAudio；旧词元级 anyAccent 绑定无残留', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card).toContain('@if (!cardHasAnyAudio(c))');
    expect(card).not.toContain('@if (!anyAccent(c))');
  });

  it('纯函数边界：词元皆无 + 某词形有声 → 状态行不渲染；皆无 → 渲染', () => {
    const someFormAudio: Card = {
      audio: audioOf(false, false),
      forms: [{ label: '原形', text: 'wug', audio: { us: true, uk: false } }],
    };
    const noneAudio: Card = {
      audio: audioOf(false, false),
      forms: [{ label: '原形', text: 'wug', audio: { us: false, uk: false } }],
    };
    expect(cardHasAnyAudio(someFormAudio)).toBe(true);
    expect(cardHasAnyAudio(noneAudio)).toBe(false);
  });
});

// ---------- TJ-131-6（持久化机制零改动；P-1/P-2 DOM 态由活体 E3 兜底） ----------

describe('TJ-131-6（口音持久化机制零改动、无自动回落）', () => {
  it('记忆读写点唯一且无自动切换代码：accent.set 全仓组件内仅 setAccent 一处', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card.match(/accent\.set\(/g)?.length).toBe(1);
    expect(card).toContain('loadDictAccent()');
    expect(card).toContain('saveDictAccent');
    expect(card).not.toContain("accent.set('us')");
    expect(card).not.toContain("accent.set('uk')");
  });

  it('记忆键仍为 dict.accent（126 §8.5 键名钉死）', () => {
    const helpers = readSrc('dictionary.helpers.ts');
    expect(helpers).toContain("DICT_ACCENT_STORAGE_KEY = 'dict.accent'");
  });
});

// ---------- TJ-131-7（混搭降级 F-S1；词形钮判据 128 §6 不动） ----------

describe('TJ-131-7（混搭降级与词形钮判据不动）', () => {
  it('纯函数：旧后端形态（forms 项无 audio）→ 页签退化为词元判据', () => {
    const card: Card = {
      audio: audioOf(true, false),
      forms: [{ label: '原形', text: 'wug' }, { label: '复数', text: 'wugs' }],
    };
    expect(tabEnabled(card, 'us')).toBe(true);
    expect(tabEnabled(card, 'uk')).toBe(false);
  });

  it('模板：词形钮仍由 formHasAudio 门控，不跟随 tabEnabled', () => {
    const card = readSrc('dictionary-card.component.ts');
    const formsBlock = card.slice(card.indexOf('dict-forms'), card.indexOf('dict-source'));
    expect(formsBlock).toContain('@if (formHasAudio(form))');
    expect(formsBlock).not.toContain('tabEnabled');
  });
});

// ---------- TJ-131-8（存量审计：supersede 登记与存量契约） ----------

describe('TJ-131-8（存量审计）', () => {
  it('supersede 两项已落码：126 契约改钉新绑定，意图断言保留', () => {
    const spec126 = readSrc('dictionary-ticket126.spec.ts');
    expect(spec126).toContain('[disabled]="!tabEnabled(c, acc)"');
    expect(spec126).toContain('@if (!cardHasAnyAudio(c))');
    expect(spec126).toContain('noAudioLabel(acc)');
    expect(spec126).toContain("t('dict.noAudio')");
  });

  it('存量纯函数契约不动：accentButtonVisible / anyAccentAvailable 仍被 126 前置用例钉住', () => {
    const helpersSpec = readSrc('dictionary.helpers.spec.ts');
    expect(helpersSpec).toContain('accentButtonVisible(');
    expect(helpersSpec).toContain('anyAccentAvailable(');
  });

  it('128 词形钮契约不动：词形行仍由 formHasAudio 守卫', () => {
    const spec128 = readSrc('dictionary-ticket128.spec.ts');
    expect(spec128).toContain('formHasAudio');
  });
});
