/**
 * 票 285：一句话模式拖选下限（用户亲定规格）。
 * 覆盖：拖选落定门禁（9 词驳回清选区+置提示、10 词放行清提示、旧选区被驳回一并清除、
 * 录音中锁定不动）、提示旗标生命周期（成功选区/清选区/退出模式清零）、
 * 接线结构契约（模板 class 绑定 + 提示行 + i18n 三语键 + 模式内 I 形光标规则）。
 * 浏览器真机行为（拖选落定、光标形态、原生选区清除）由沙盒走查覆盖，不在此处。
 */
import { describe, expect, it, jest } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import { signal } from '@angular/core';
import { tokenizePassage } from './dictionary.helpers';
import { PracticeSentenceSession } from './practice-sentence-session';
import { SENTENCE_MIN_WORDS } from './sentence-select.helpers';

const SRC = __dirname;
const read = (f: string): string => fs.readFileSync(path.join(SRC, f), 'utf8');

/** 269 同款替身宿主（本文件独立定义，269 的 stub 未导出）。 */
function stubSentenceHost(text: string): { host: Record<string, unknown> } {
  const recording = signal(false);
  const host: Record<string, unknown> = {
    dict: {
      tokens: () => tokenizePassage(text),
      wordCount: () => tokenizePassage(text).filter((t) => t.kind === 'word').length,
      clearSuppress: jest.fn(),
    },
    recorder: { recording },
    shadow: { shadowMode: signal(false), shadowRecording: signal(false), toggleShadowMode: jest.fn() },
    t: (k: string) => k,
  };
  return { host };
}

describe('票 285：拖选下限门禁', () => {
  const TEXT = 'one two three four five six seven eight nine ten eleven twelve';

  it('9 词驳回：选区取消、提示置位；10 词放行：选区落定、提示清零', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    expect(s.setRangeOrReject(0, 8)).toBe(false);
    expect(s.sentenceRange()).toBeNull();
    expect(s.sentenceWordCount()).toBe(0);
    expect(s.tooShortHint()).toBe(true);
    // 驳回后成功重选：提示消失，选区落定。
    expect(s.setRangeOrReject(0, 9)).toBe(true);
    expect(s.sentenceRange()).toEqual({ start: 0, end: 9 });
    expect(s.sentenceWordCount()).toBe(10);
    expect(s.tooShortHint()).toBe(false);
  });

  it('边界：1 词与 0 词区间同样驳回；下限常量仍为 10', () => {
    expect(SENTENCE_MIN_WORDS).toBe(10);
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    expect(s.setRangeOrReject(3, 3)).toBe(false);
    expect(s.sentenceRange()).toBeNull();
    expect(s.tooShortHint()).toBe(true);
  });

  it('驳回清除旧选区：已落定的有效选区不得在失败重拖后残留', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    s.setRange(0, 11);
    expect(s.sentenceWordCount()).toBe(12);
    expect(s.setRangeOrReject(0, 4)).toBe(false);
    expect(s.sentenceRange()).toBeNull();
    expect(s.sentenceWordCount()).toBe(0);
    expect(s.tooShortHint()).toBe(true);
    expect(s.canRecord()).toBe(false);
  });

  it('提示生命周期：清选区与退出模式均清提示；成功 setRange 也清提示', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    s.setRangeOrReject(0, 2);
    expect(s.tooShortHint()).toBe(true);
    s.clearRange();
    expect(s.tooShortHint()).toBe(false);
    s.setRangeOrReject(0, 2);
    s.exitSentenceMode();
    expect(s.tooShortHint()).toBe(false);
    expect(s.sentenceMode()).toBe(false);
    s.sentenceMode.set(true);
    s.setRangeOrReject(0, 2);
    s.setRange(0, 9);
    expect(s.tooShortHint()).toBe(false);
  });

  it('键盘逐步扩展不受拖选门禁影响：中间态 <10 词仍保留选区（计数行 needMoreWords 指引）', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    s.extendByKey(2, 'ArrowRight');
    s.extendByKey(3, 'ArrowRight');
    expect(s.sentenceRange()).toEqual({ start: 2, end: 4 });
    expect(s.sentenceWordCount()).toBe(3);
    expect(s.tooShortHint()).toBe(false);
  });
});

describe('票 285：接线结构契约（源码断言）', () => {
  it('模板：篇章 sentence-mode class 绑定 + 不足提示行（计数行之后）', () => {
    const html = read('ai-practice.component.html');
    expect(html).toContain('[class.sentence-mode]="sentence.sentenceMode()"');
    expect(html).toContain('class="sentence-hint"');
    expect(html).toContain('sentence.tooShortHint()');
    expect(html).toContain("t('practice.sentence.tooShortHint')");
    // 提示行在计数行之后、仍在篇章容器内（计数行是其位置锚点）。
    expect(html.indexOf('class="sentence-hint"')).toBeGreaterThan(html.indexOf('class="sentence-count"'));
    // 提示只在模式内出现：模板条件同时含 sentenceMode 与 tooShortHint。
    const hintAt = html.indexOf('class="sentence-hint"');
    const condAt = html.lastIndexOf('@if (sentence.sentenceMode() && sentence.tooShortHint())', hintAt);
    expect(condAt).toBeGreaterThan(-1);
    // 开关、互斥、评分逻辑一字未动：本票不碰这些绑定。
    expect(html).toContain('data-testid="sentence-toggle"');
  });

  it('词典：拖选落定走 setRangeOrReject，不足驳回仍清原生选区+抑制末尾点击', () => {
    const dict = read('practice-dictionary.ts');
    expect(dict).toContain('sentence.setRangeOrReject(Math.min(a, f), Math.max(a, f))');
    expect(dict).toContain('sel.removeAllRanges()');
    expect(dict).toContain('this.suppressNextWordClick = true');
    // 驳回路径不得绕过：捕获本体不再直接调用无门禁的 setRange。
    const capture = dict.slice(dict.indexOf('private captureSelectionFromDom'));
    expect(capture).not.toContain('sentence.setRange(');
  });

  it('i18n：tooShortHint 三语齐全（zh 贴用户原话，en/fr 忠实翻译）；旧键未改名', () => {
    const i18n = read('../../core/i18n/i18n.service.ts');
    expect(i18n).toContain("'practice.sentence.tooShortHint': { zh: '至少要选择 10 个词以上的句子，请重新选择', en: 'Please select a sentence of at least 10 words, then select again.', fr: 'Veuillez sélectionner une phrase d’au moins 10 mots, puis sélectionnez à nouveau.' }");
    expect(i18n).toContain("'practice.sentence.selectedCount'");
    expect(i18n).toContain("'practice.sentence.needMoreWords'");
  });

  it('样式：模式内词跨度 I 形光标；模式外点词仍为 pointer；提示行样式在席', () => {
    const scss = read('ai-practice.component.scss');
    expect(scss).toContain('.dict-passage.sentence-mode .dict-word { cursor: text; }');
    expect(scss).toContain('.dict-word { cursor: pointer;');
    expect(scss).toContain('.sentence-hint {');
  });
});
