/**
 * 票128 前端验收(TJ-1…TJ-4;SPEC §10.2)。
 *
 * 基建口径(同 dictionary-ticket126/127.spec):本仓 Jest = node 环境 + @angular/* 替身,
 * 无 TestBed / DOM 渲染。被测面:
 *   - 纯函数 formPlayable(词形钮渲染判据本体,R-F1/R-F6)—— 真跑;
 *   - 模板接线/点击契约/词形行其余不变以源码契约断言落码(同 126/127 先例),
 *     真实 DOM 行为由活体 E1–E4 兜底。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it } from '@jest/globals';
import { formPlayable } from './dictionary.helpers';
import type { DictAccent, DictCardData } from './dictionary.helpers';

type Form = DictCardData['forms'][number];

const readSrc = (rel: string): string => readFileSync(join(__dirname, rel), 'utf8');

const playableTexts = (forms: Form[], accent: DictAccent): string[] =>
  forms.filter((f) => formPlayable(f, accent)).map((f) => f.text);

// ---------- TJ-1(渲染判据:钮 ⟺ audio[当前口音] === true;切口音即时重评) ----------

describe('TJ-1(渲染判据 R-F1/R-F3)', () => {
  const forms: Form[] = [
    { label: '过去式', text: 'glorped', audio: { us: true, uk: true } },
    { label: '现在分词', text: 'glorping', audio: { us: true, uk: false } },
    { label: '复数', text: 'glorps', audio: { us: false, uk: false } },
  ];

  it('us 口音:钮数 = 2 且归属正确词形', () => {
    expect(playableTexts(forms, 'us')).toEqual(['glorped', 'glorping']);
  });

  it('切 uk:按 uk 布尔重算(仅双口音有的词形留钮)', () => {
    expect(playableTexts(forms, 'uk')).toEqual(['glorped']);
  });

  it('判据只认当前口音那一格(另一口音有无不影响)', () => {
    const ukOnly: Form = { label: '过去式', text: 'blinted', audio: { us: false, uk: true } };
    expect(formPlayable(ukOnly, 'us')).toBe(false);
    expect(formPlayable(ukOnly, 'uk')).toBe(true);
  });
});

// ---------- TJ-2(诚实默认与逐词形隔离,R-F6/F-S1) ----------

describe('TJ-2(诚实默认:缺失/null/非布尔 → 无钮,逐词形隔离)', () => {
  it('audio 整字段缺失(旧后端形态)→ 无钮', () => {
    const legacy: Form = { label: '过去式', text: 'glorped' };
    expect(formPlayable(legacy, 'us')).toBe(false);
    expect(formPlayable(legacy, 'uk')).toBe(false);
  });

  it('audio = null → 无钮', () => {
    const nulled = { label: '过去式', text: 'glorped', audio: null } as unknown as Form;
    expect(formPlayable(nulled, 'us')).toBe(false);
    expect(formPlayable(nulled, 'uk')).toBe(false);
  });

  it('口音值非布尔(字符串/数字)→ 该口音按无钮', () => {
    const dirty = { label: '过去式', text: 'glorped', audio: { us: 'yes', uk: 1 } } as unknown as Form;
    expect(formPlayable(dirty, 'us')).toBe(false);
    expect(formPlayable(dirty, 'uk')).toBe(false);
  });

  it('逐词形隔离:一形数据异常不牵连其余词形', () => {
    const forms = [
      { label: '过去式', text: 'glorped', audio: null } as unknown as Form,
      { label: '现在分词', text: 'glorping', audio: { us: true, uk: false } } as Form,
      { label: '复数', text: 'glorps' } as Form,
    ];
    expect(playableTexts(forms, 'us')).toEqual(['glorping']);
    expect(playableTexts(forms, 'uk')).toEqual([]);
  });
});

// ---------- TJ-3(零探测 + 点击 emit 不变,R-F3/R-F4) ----------

describe('TJ-3(零探测 + 点击契约不变)', () => {
  const card = readSrc('dictionary-card.component.ts');
  const helpers = readSrc('dictionary.helpers.ts');

  it('词形钮被 formHasAudio 判据守卫(模板 @if,非 disabled/hidden 留钮)', () => {
    const formsBlock = card.slice(card.indexOf('@for (form of c.forms'), card.indexOf('class="dict-source"'));
    expect(formsBlock).toContain('@if (formHasAudio(form))');
    const guardAt = formsBlock.indexOf('@if (formHasAudio(form))');
    const btnAt = formsBlock.indexOf('dict-play-sm');
    expect(guardAt).toBeGreaterThanOrEqual(0);
    expect(btnAt).toBeGreaterThan(guardAt); // 钮在判据之内
    expect(formsBlock).not.toContain('disabled');
  });

  it('点击 emit 与现行逐字一致', () => {
    expect(card).toContain(
      "(click)=\"playRequested.emit({ target: 'form', text: form.text, accent: accent() })\"");
    expect(card).toContain("[attr.aria-label]=\"tf('dict.playForm', { form: form.text })\"");
  });

  it('渲染路径零探测:卡组件不引 API/HTTP,可用性只读 DTO', () => {
    expect(card).not.toContain('HttpClient');
    expect(card).not.toContain('practice-api');
    expect(card).not.toContain('lookupDictionaryWord');
    // 前端类型面:DTO 与卡面数据的 forms 都带可选 audio(§5.3)
    expect(helpers).toContain('text: string; audio?: { us: boolean; uk: boolean }');
    expect(readSrc('../../core/api/practice-api.service.ts'))
      .toContain('text: string; audio?: { us: boolean; uk: boolean }');
  });
});

// ---------- TJ-4(词形行其余不变,R-F2) ----------

describe('TJ-4(词形行其余不变:文字/标签恒在、无占位、无词形级无发音文案)', () => {
  const card = readSrc('dictionary-card.component.ts');
  const formsBlock = card.slice(card.indexOf('@for (form of c.forms'), card.indexOf('class="dict-source"'));

  it('label/text 在判据之外恒渲染(DOM 序在 @if 之前)', () => {
    const labelAt = formsBlock.indexOf('dict-form-label');
    const textAt = formsBlock.indexOf('dict-form-text');
    const guardAt = formsBlock.indexOf('@if (formHasAudio(form))');
    expect(labelAt).toBeGreaterThanOrEqual(0);
    expect(textAt).toBeGreaterThan(labelAt);
    expect(guardAt).toBeGreaterThan(textAt);
  });

  it('词形行内无 dict.noAudio 文案、无占位符', () => {
    expect(formsBlock).not.toContain('noAudio');
    expect(formsBlock).not.toContain('dict-none');
    expect(formsBlock).not.toContain('—');
  });
});
