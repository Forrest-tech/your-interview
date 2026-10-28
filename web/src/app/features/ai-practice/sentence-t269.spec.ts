/**
 * 票 269：一句话模式（计划 PLAN-v2 §3、§6、§7）。
 * 覆盖：选区纯函数（区间词数/还原文本/边界 9-10 词/键盘扩展/端点解析）、
 * 会话状态机（开/关/互斥/锁定/快照）、评分调用点（选区文本路由 + fail-closed 护栏）、
 * 接线结构契约（模板/词典/跟读/上传链路——Node 环境无 DOM，源码断言，见票 264 先例）。
 * 浏览器真机行为（拖选落定、末尾点击抑制、桩 assess 断言）由沙盒走查覆盖，不在此处。
 */
import { describe, expect, it, jest, beforeAll } from '@jest/globals';
import * as fs from 'fs';
import * as path from 'path';
import { signal } from '@angular/core';
import {
  extendSelection, restoreSentenceText, sentenceWordCountOf, truncateSentenceTip,
  wordIndexOfSelectionNode, SENTENCE_MIN_WORDS, SENTENCE_BADGE_TIP_CHARS,
} from './sentence-select.helpers';
import { tokenizePassage } from './dictionary.helpers';
import { PracticeSentenceSession } from './practice-sentence-session';
import { PracticeGradingSession } from './practice-grading-session';
import type { Recording } from '../../core/recorder/recorder.service';

const SRC = __dirname;
const read = (f: string): string => fs.readFileSync(path.join(SRC, f), 'utf8');

// Node 环境无 Audio/localStorage：grading 会话字段初始化期用到，先行打桩。
beforeAll(() => {
  const g = globalThis as unknown as Record<string, unknown>;
  if (!g['Audio']) {
    g['Audio'] = class {
      pause(): void { /* stub */ }
      play(): Promise<void> { return Promise.resolve(); }
      addEventListener(): void { /* stub */ }
    };
  }
  if (!g['localStorage']) {
    const store = new Map<string, string>();
    g['localStorage'] = {
      getItem: (k: string) => store.get(k) ?? null,
      setItem: (k: string, v: string) => { store.set(k, v); },
      removeItem: (k: string) => { store.delete(k); },
    };
  }
});

/** 造一个带 data-wi 的假 span（closest 自返，getAttribute 读 data-wi）。 */
function fakeSpan(wi: string | null): unknown {
  return {
    closest: () => ({
      getAttribute: (name: string) => (name === 'data-wi' ? wi : null),
    }),
  };
}

describe('票 269：选区纯函数', () => {
  it('区间词数 = end - start + 1（分隔符/纯数字不在词序内）', () => {
    const text = 'I have 80 apples, and 14-year-old friends here today now ok go.';
    const tokens = tokenizePassage(text);
    const words = tokens.filter((t) => t.kind === 'word');
    // 80 不算词；14-year-old 取字母段 year-old 算 1 词。
    expect(words.map((w) => w.text)).toEqual([
      'I', 'have', 'apples', 'and', 'year-old', 'friends', 'here', 'today', 'now', 'ok', 'go',
    ]);
    expect(sentenceWordCountOf(text)).toBe(11);
    // 区间切片语义（计划 §3 步骤 1 第 4 条）：起止词 token 之间（含）的 sep 全保留，
    // 末词之后的尾随标点不在区间内（与“按 token 下标区间切数组再 join”一致）。
    expect(restoreSentenceText(tokens, { start: 0, end: 10 }))
      .toBe('I have 80 apples, and 14-year-old friends here today now ok go');
  });

  it('还原文本保留分隔符与标点（与原文断句一致）', () => {
    const text = "Hello, brave new world! This is a test.";
    const tokens = tokenizePassage(text);
    expect(restoreSentenceText(tokens, { start: 1, end: 3 })).toBe('brave new world');
    // 区间内分隔符（含 '! '）保留；末词之后无尾随标点。
    expect(restoreSentenceText(tokens, { start: 1, end: 4 })).toBe('brave new world! This');
    expect(restoreSentenceText(tokens, { start: 0, end: 0 })).toBe('Hello');
  });

  it('边界：9 词不足、10 词放行、11 词放行；脏区间还原为空串', () => {
    const tokens = tokenizePassage('one two three four five six seven eight nine ten eleven');
    expect(restoreSentenceText(tokens, { start: 0, end: 8 }).split(' ').length).toBe(9);
    expect(9 < SENTENCE_MIN_WORDS).toBe(true);
    expect(10 >= SENTENCE_MIN_WORDS).toBe(true);
    expect(restoreSentenceText(tokens, { start: 0, end: 10 }).split(' ').length).toBe(11);
    expect(restoreSentenceText(tokens, { start: 5, end: 99 })).toBe('');
    expect(restoreSentenceText(tokens, { start: 3, end: 2 })).toBe('');
  });

  it('键盘扩展：锚点保持、焦点步进、Home/End、边界钳制', () => {
    // wordCount 11：锚点 2，焦点 2 → Shift+Right → 3；再 Right → 4；Left → 3。
    let r = extendSelection(2, 2, 'ArrowRight', 11);
    expect(r).toEqual({ anchor: 2, focus: 3, start: 2, end: 3 });
    r = extendSelection(r.anchor, r.focus, 'ArrowRight', 11);
    expect(r.focus).toBe(4);
    r = extendSelection(r.anchor, r.focus, 'ArrowLeft', 11);
    expect(r.focus).toBe(3);
    // Home → 0（区间反向仍以锚点为准）；End → wordCount-1；右边界钳制不越界。
    r = extendSelection(5, 5, 'Home', 11);
    expect(r).toEqual({ anchor: 5, focus: 0, start: 0, end: 5 });
    r = extendSelection(5, 5, 'End', 11);
    expect(r).toEqual({ anchor: 5, focus: 10, start: 5, end: 10 });
    r = extendSelection(10, 10, 'ArrowRight', 11);
    expect(r.focus).toBe(10);
    r = extendSelection(0, 0, 'ArrowLeft', 11);
    expect(r.focus).toBe(0);
  });

  it('端点解析：文本节点取父元素、元素节点自身 closest、解析不到为 null', () => {
    const textNode = { nodeType: 3, parentElement: fakeSpan('5') };
    expect(wordIndexOfSelectionNode(textNode)).toBe(5);
    expect(wordIndexOfSelectionNode(fakeSpan('3'))).toBe(3);
    expect(wordIndexOfSelectionNode({ nodeType: 3, parentElement: null })).toBeNull();
    expect(wordIndexOfSelectionNode({ closest: () => null })).toBeNull();
    expect(wordIndexOfSelectionNode(fakeSpan(null))).toBeNull();
    expect(wordIndexOfSelectionNode(fakeSpan('abc'))).toBeNull();
    expect(wordIndexOfSelectionNode(fakeSpan('-1'))).toBeNull();
    expect(wordIndexOfSelectionNode(null)).toBeNull();
  });

  it('badge tooltip 截断：60 字内原样，超长截断 + 省略号', () => {
    const short = 'hello world';
    expect(truncateSentenceTip(short)).toBe(short);
    const long = 'x'.repeat(SENTENCE_BADGE_TIP_CHARS + 10);
    expect(truncateSentenceTip(long)).toBe('x'.repeat(SENTENCE_BADGE_TIP_CHARS) + '…');
  });
});

/** 一句话会话的替身宿主（dict/recorder/shadow/t 四项调用时求值）。 */
function stubSentenceHost(text: string): {
  host: Record<string, unknown>;
  recording: ReturnType<typeof signal<boolean>>;
  shadowMode: ReturnType<typeof signal<boolean>>;
  shadowRecording: ReturnType<typeof signal<boolean>>;
  toggleShadow: jest.Mock<() => Promise<void>>;
} {
  const recording = signal(false);
  const shadowMode = signal(false);
  const shadowRecording = signal(false);
  const toggleShadow = jest.fn(async () => { shadowMode.set(false); });
  const host: Record<string, unknown> = {
    dict: {
      tokens: () => tokenizePassage(text),
      wordCount: () => tokenizePassage(text).filter((t) => t.kind === 'word').length,
      clearSuppress: jest.fn(),
    },
    recorder: { recording },
    shadow: { shadowMode, shadowRecording, toggleShadowMode: toggleShadow },
    t: (k: string) => k,
  };
  return { host, recording, shadowMode, shadowRecording, toggleShadow };
}

describe('票 269：一句话会话状态机', () => {
  const TEXT = 'one two three four five six seven eight nine ten eleven twelve';

  it('9 词录音禁用、10 词放行；计数行词数 = 区间长度', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    s.setRange(0, 8);
    expect(s.sentenceWordCount()).toBe(9);
    expect(s.canRecord()).toBe(false);
    expect(s.recordDisabled()).toBe(true);
    s.setRange(0, 9);
    expect(s.sentenceWordCount()).toBe(10);
    expect(s.canRecord()).toBe(true);
    expect(s.recordDisabled()).toBe(false);
    expect(s.sentenceText()).toBe('one two three four five six seven eight nine ten');
  });

  it('录音中锁定：setRange 与 extendByKey 均被忽略', () => {
    const { host, recording } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    s.setRange(0, 9);
    recording.set(true);
    s.setRange(0, 2);
    expect(s.sentenceRange()).toEqual({ start: 0, end: 9 });
    expect(s.extendByKey(9, 'ArrowRight')).toBe(9);
    expect(s.sentenceRange()).toEqual({ start: 0, end: 9 });
  });

  it('键盘扩展锚点保持、方向键折叠清选区、Esc 路径同 clearRange', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    // 焦点词 2 起：Shift+Right 两次 → 锚点 2、区间 2..4。
    expect(s.extendByKey(2, 'ArrowRight')).toBe(3);
    expect(s.extendByKey(3, 'ArrowRight')).toBe(4);
    expect(s.sentenceAnchor()).toBe(2);
    expect(s.sentenceRange()).toEqual({ start: 2, end: 4 });
    // 方向键折叠（dict 分支调用 clearRange）：选区与锚点清零，模式不动。
    s.clearRange();
    expect(s.sentenceRange()).toBeNull();
    expect(s.sentenceAnchor()).toBeNull();
    expect(s.sentenceMode()).toBe(true);
  });

  it('退出模式清选区/锚点/模式与抑制旗标，快照不受影响', () => {
    const { host } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    s.sentenceMode.set(true);
    s.setRange(0, 9);
    s.recordingSentenceText.set('one two three four five six seven eight nine ten');
    s.exitSentenceMode();
    expect(s.sentenceMode()).toBe(false);
    expect(s.sentenceRange()).toBeNull();
    expect(s.sentenceAnchor()).toBeNull();
    expect((host['dict'] as { clearSuppress: jest.Mock }).clearSuppress).toHaveBeenCalled();
    // 待提交 take 的选区身份保留到提交/丢弃，退出模式不清除快照。
    expect(s.recordingSentenceText()).toBe('one two three four five six seven eight nine ten');
  });

  it('互斥：开一句话模式先关跟读；录音中 toggle 被拒', async () => {
    const { host, shadowMode, recording, toggleShadow } = stubSentenceHost(TEXT);
    const s = new PracticeSentenceSession(host as never);
    shadowMode.set(true);
    await s.toggleSentenceMode();
    expect(toggleShadow).toHaveBeenCalled();
    expect(s.sentenceMode()).toBe(true);
    await s.toggleSentenceMode();
    expect(s.sentenceMode()).toBe(false);
    // 录音中 toggle 不翻转。
    s.sentenceMode.set(true);
    recording.set(true);
    await s.toggleSentenceMode();
    expect(s.sentenceMode()).toBe(true);
  });

  it('badgeTipFor：{text} 占位替换 + 截断', () => {
    const { host } = stubSentenceHost(TEXT);
    // 替身宿主的 t 默认返回键本身（含 {text} 占位时才可观测替换），故此处模拟真实 i18n。
    (host as Record<string, unknown>)['t'] = (k: string) =>
      (k === 'practice.sentence.badgeTip' ? '评分句段：{text}' : k);
    const s = new PracticeSentenceSession(host as never);
    const long = 'y'.repeat(SENTENCE_BADGE_TIP_CHARS + 5);
    const tip = s.badgeTipFor({ sentenceText: long });
    expect(tip).toBe('评分句段：' + 'y'.repeat(SENTENCE_BADGE_TIP_CHARS) + '…');
    expect(s.badgeTipFor({ sentenceText: null })).toBe('评分句段：');
    expect(s.badgeTipFor({ sentenceText: 'hello' })).toBe('评分句段：hello');
  });
});

/** grading 会话的替身宿主（forMaterial/grade/pendingTake 等调用时求值）。 */
function stubGradingHost(rows: Recording[]): {
  host: Record<string, unknown>;
  grade: jest.Mock<(id: string, ref: string, lang: string) => Promise<void>>;
  toast: jest.Mock<(t: string) => void>;
  pendingTake: ReturnType<typeof signal<Recording | null>>;
  submitPending: jest.Mock<() => void>;
  discardPending: jest.Mock<() => void>;
  recordingSentenceText: ReturnType<typeof signal<string | null>>;
  clearSnapshot: jest.Mock<() => void>;
  captureForRecording: jest.Mock<() => void>;
  recorderStart: jest.Mock<() => Promise<string | null>>;
} {
  const grade = jest.fn(async () => undefined);
  const toast = jest.fn(() => undefined);
  const pendingTake = signal<Recording | null>(null);
  const submitPending = jest.fn(() => undefined);
  const discardPending = jest.fn(() => undefined);
  const recordingSentenceText = signal<string | null>(null);
  const clearSnapshot = jest.fn(() => { recordingSentenceText.set(null); });
  const captureForRecording = jest.fn(() => undefined);
  const recorderStart = jest.fn(async () => null);
  const host: Record<string, unknown> = {
    recorder: {
      forMaterial: () => rows,
      grade,
      pendingTake,
      submitPending,
      discardPending,
      recording: signal(false),
      start: recorderStart,
    },
    selectedId: () => 'mat-1',
    currentText: () => 'FULL PASSAGE TEXT',
    practiceLang: () => 'en-US',
    toast,
    t: (k: string) => k,
    tts: { stopSpeak: () => undefined },
    sentence: { recordingSentenceText, clearRecordingSnapshot: clearSnapshot, captureForRecording },
  };
  return { host, grade, toast, pendingTake, submitPending, discardPending, recordingSentenceText, clearSnapshot, captureForRecording, recorderStart };
}

function take(over: Partial<Recording>): Recording {
  return {
    id: 'r1', materialId: 'mat-1', createdAt: 1, duration: 1,
    blob: new Blob([]), url: '', score: null, grading: false, uploaded: true, error: null,
    ...over,
  } as Recording;
}

describe('票 269：评分调用点（gradeRecording）', () => {
  it('一句话 take 送选区文本，不读当前全文', async () => {
    const sentence = 'one two three four five six seven eight nine ten';
    const { host, grade } = stubGradingHost([take({ id: 'r1', isSentence: true, sentenceText: sentence })]);
    const g = new PracticeGradingSession(host as never);
    await g.gradeRecording('r1');
    expect(grade).toHaveBeenCalledTimes(1);
    expect(grade).toHaveBeenCalledWith('r1', sentence, 'en-US');
  });

  it('普通 take 送当前全文（一字不动）', async () => {
    const { host, grade } = stubGradingHost([take({ id: 'r1', isSentence: false })]);
    const g = new PracticeGradingSession(host as never);
    await g.gradeRecording('r1');
    expect(grade).toHaveBeenCalledWith('r1', 'FULL PASSAGE TEXT', 'en-US');
  });

  it('fail-closed：isSentence 为真而选区缺失 → 拒评、grade 永不调用、toast 提示', async () => {
    for (const missing of ['', '   ', null, undefined]) {
      const { host, grade, toast } = stubGradingHost([
        take({ id: 'r1', isSentence: true, sentenceText: missing as string | null }),
      ]);
      const g = new PracticeGradingSession(host as never);
      await g.gradeRecording('r1');
      expect(grade).not.toHaveBeenCalled();
      expect(toast).toHaveBeenCalledWith('practice.sentence.missingSelection');
    }
  });

  it('提交透传快照：pendingTake 打上 isSentence/sentenceText 后才 submit，快照随即清除', () => {
    const snap = 'one two three four five six seven eight nine ten';
    const pt = take({ id: 'r_local', uploaded: false });
    const { host, pendingTake, submitPending, recordingSentenceText, clearSnapshot } = stubGradingHost([]);
    pendingTake.set(pt);
    recordingSentenceText.set(snap);
    const g = new PracticeGradingSession(host as never);
    g.submitPending();
    expect(submitPending).toHaveBeenCalledTimes(1);
    expect(pendingTake()?.isSentence).toBe(true);
    expect(pendingTake()?.sentenceText).toBe(snap);
    expect(clearSnapshot).toHaveBeenCalled();
  });

  it('提交纵深防御：快照不足 10 词 → 拒绝提交、submitPending 不调用', () => {
    const pt = take({ id: 'r_local', uploaded: false });
    const { host, pendingTake, submitPending, recordingSentenceText, toast } = stubGradingHost([]);
    pendingTake.set(pt);
    recordingSentenceText.set('one two three');
    const g = new PracticeGradingSession(host as never);
    g.submitPending();
    expect(submitPending).not.toHaveBeenCalled();
    expect(toast).toHaveBeenCalled();
    expect(pendingTake()?.isSentence).toBeUndefined();
  });

  it('丢弃清快照', async () => {
    const pt = take({ id: 'r_local', uploaded: false });
    const { host, pendingTake, recordingSentenceText, discardPending } = stubGradingHost([]);
    pendingTake.set(pt);
    recordingSentenceText.set('one two three four five six seven eight nine ten');
    const g = new PracticeGradingSession(host as never);
    g.discardPending();
    expect(discardPending).toHaveBeenCalled();
    expect(recordingSentenceText()).toBeNull();
  });

  it('新录音开始清旧快照、成功后重新捕获；失败则不捕获', async () => {
    const { host, recordingSentenceText, clearSnapshot, captureForRecording, recorderStart } = stubGradingHost([]);
    recordingSentenceText.set('stale snapshot');
    const g = new PracticeGradingSession(host as never);
    await g.startRecording();
    expect(clearSnapshot).toHaveBeenCalled();
    expect(captureForRecording).toHaveBeenCalledTimes(1);
    // 麦克风失败：recError 置位、不捕获（快照保持清除态，不残留旧快照）。
    recorderStart.mockResolvedValueOnce('mic denied');
    await g.startRecording();
    expect(captureForRecording).toHaveBeenCalledTimes(1);
    expect(g.recError()).toBe('mic denied');
  });
});

describe('票 269：接线结构契约（源码断言）', () => {
  it('模板：模式按钮/高亮绑定/计数行/badge/报告行/录音键门禁均在席', () => {
    const html = read('ai-practice.component.html');
    expect(html).toContain('data-testid="sentence-toggle"');
    expect(html).toContain('[class.sentence-sel]="sentence.inSentenceRange(tok.wordIndex)"');
    expect(html).toContain('(mouseup)="dict.onPassageMouseUp()"');
    expect(html).toContain('(touchend)="dict.onPassageTouchEnd($event)"');
    expect(html).toContain('class="sentence-count"');
    expect(html).toContain('[disabled]="sentence.recordDisabled()"');
    expect(html).toContain('class="sentence-badge shadow-badge"');
    expect(html).toContain('data-testid="sentence-graded-line"');
    // P-RECONFIRM：按钮在跟读开关后、Previous 前（P-A 落位，非 P-B 回退）。
    const toggleAt = html.indexOf('data-testid="shadow-toggle"');
    const sentenceAt = html.indexOf('data-testid="sentence-toggle"');
    const prevAt = html.indexOf("{{ t('practice.prev') }}");
    expect(toggleAt).toBeGreaterThan(-1);
    expect(sentenceAt).toBeGreaterThan(toggleAt);
    expect(prevAt).toBeGreaterThan(sentenceAt);
  });

  it('词典：抑制旗标首行消费 + mousedown 清零 + reset 清零 + 捕获门禁', () => {
    const dict = read('practice-dictionary.ts');
    const onWordClick = dict.slice(dict.indexOf('onWordClick(token'));
    expect(onWordClick.slice(0, 400)).toContain('suppressNextWordClick');
    expect(dict).toContain("document.addEventListener('mousedown', this.docMousedownHandler)");
    expect(dict).toContain('captureSelectionFromDom');
    expect(dict).toContain('sel.isCollapsed');
    expect(dict).toContain('wordIndexOfSelectionNode(sel.anchorNode)');
    expect(dict).toContain('wordIndexOfSelectionNode(sel.focusNode)');
    expect(dict).toContain('sel.removeAllRanges()');
    expect(dict).toContain('sentence.extendByKey(wi, event.key)');
    expect(dict).toContain('sentence.clearRange()');
    // Shift 分支在 roving 分支之前（否则 Shift+方向键只会移动焦点）。
    expect(dict.indexOf('event.shiftKey && isRovingKey(event.key)'))
      .toBeLessThan(dict.indexOf('if (isRovingKey(event.key)) {'));
  });

  it('互斥双向接线：开跟读关一句话、开一句话关跟读', () => {
    const shadow = read('practice-shadow-session.ts');
    expect(shadow).toContain('this.host.sentence?.exitSentenceMode()');
    const session = read('practice-sentence-session.ts');
    expect(session).toContain('await this.host.shadow.toggleShadowMode()');
  });

  it('上传链路：一句话字段随 FormData 落库（recorder → api → 后端 DTO 映射）', () => {
    const recorder = read('../../core/recorder/recorder.service.ts');
    expect(recorder).toContain('isSentence: d.isSentence ?? false');
    expect(recorder).toContain('sentenceText: d.sentenceText ?? null');
    expect(recorder).toContain('referenceText: sc.referenceText');
    expect(recorder).toContain('rec.isSentence ? true : undefined');
    const api = read('../../core/api/practice-api.service.ts');
    expect(api).toContain("fd.append('isSentence', String(isSentence))");
    expect(api).toContain("fd.append('sentenceText', sentenceText)");
  });

  it('组件接线：编辑态退出模式、换素材复位、录音门禁 toast', () => {
    const comp = read('ai-practice.component.ts');
    expect(comp).toContain('readonly sentence = new PracticeSentenceSession(this)');
    expect(comp).toContain('this.sentence.exitSentenceMode()');
    expect(comp).toContain('queueMicrotask(() => this.sentence.exitSentenceMode())');
    expect(comp).toContain("this.t('practice.sentence.needMoreWords')");
  });
});
