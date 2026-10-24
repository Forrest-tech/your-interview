/**
 * 票125:AI Practice 五项评审修复（追问文本来源 / 硬编码中文 i18n /
 * 删外层标题行 / Take 整行点击；第五项 Q-125-1/Q-125-2 仅登记、不在本票动手）。
 * 规格 docs/fix-program/source/ticket125-SPEC.md（R1，独立评分 9.95 PASS）。
 *
 * TJ-1…TJ-5：假 host 注入 PracticeGradingSession 的行为断言。
 * TJ-7：会话状态机 + 源码接线对照（§10.3 末段的替代路径说明：
 *   AiPracticeComponent 依赖面大，完整 TestBed 装配成本过高，故行 handler
 *   与钮 handler 指向同一组件方法的代码对照入本文件，DOM 层断言
 *   （1/5/6/7）由活体 E4/E5 作主证，见实现报告 VERIFY-125.md）。
 * TJ-6（普查闸）为脚本 + 对账，不属 Jest：见 VERIFY-125.md 普查表。
 */
import { describe, expect, it, beforeEach } from '@jest/globals';
import { signal, type WritableSignal } from '@angular/core';
import { of, throwError } from 'rxjs';
import { PracticeGradingSession } from './practice-grading-session';
import type { Recording, RecordingScore } from '../../core/recorder/recorder.service';
import * as fs from 'fs';
import * as path from 'path';

const RECOG = 'RECOG-TEXT-125';

/** 会话字段初始化建一个常驻 Audio（jsdom 无 Audio）——沿票132 的替身口径。 */
class FakeAudio {
  src = '';
  currentTime = 0;
  onended: (() => void) | null = null;
  play(): Promise<void> { return Promise.resolve(); }
  pause(): void { /* stub */ }
}
(globalThis as { Audio?: unknown }).Audio = FakeAudio;

/** 假 host 词典：只含本票新键与复用键，余键原样回显键名。 */
const DICT: Record<string, { en: string; zh: string }> = {
  'practice.noRecognizedText': { en: 'No recognized speech yet — score a recording first.', zh: '还没有识别文本，先完成一次评分再试' },
  'practice.addedToReview': { en: 'Added to Tech Stack review', zh: '已加入 Tech Stack 复习' },
  'practice.addToReviewFailed': { en: "Couldn't add it — please try again.", zh: '加入失败，请重试' },
  'practice.followUpFailed': { en: 'The follow-up failed — check your AI settings.', zh: 'AI 追问失败，请检查 AI 配置' },
  'practice.reviewCategory': { en: 'Speaking practice', zh: '口语练习' },
  'practice.paceSlow': { en: 'Slow', zh: '偏慢' },
  'practice.paceGood': { en: 'Good pace', zh: '适中' },
  'practice.paceFast': { en: 'Fast', zh: '偏快' },
  'practice.oneFixFillersTitle': { en: 'Cut the filler words', zh: '减少填充词' },
  'practice.oneFixFillersDetail': { en: '{count} filler words detected ({per}/min). Next time, pause for a second instead of saying "um". Click an orange word to replay it.', zh: '检测到 {count} 个填充词（{per}/分钟）。下次在想词时停顿 1 秒，而不是说 "um"。点击橙色词回放。' },
  'practice.oneFixPriority': { en: 'Top fix: {label} ({score})', zh: '优先改进：{label}（{score}分）' },
  'practice.oneFixFluency': { en: 'Uneven pace. Listen to your recording at 2x, mark where you stumble, and slow down there on the next take.', zh: '语速不均。试着用 2 倍速听一遍自己的录音，标记卡顿处，重录时在这些地方刻意放慢。' },
  'practice.oneFixAccuracy': { en: 'A few words are mispronounced. Click a red-underlined word below, check its phonetics, and shadow it 3 times.', zh: '个别词发音不准。点击下方红色波浪线词，看音标，跟读 3 遍。' },
  'practice.oneFixPron': { en: 'Overall pronunciation needs work. Shadow 5 minutes a day, focusing on intonation.', zh: '整体发音需加强。每天跟读 5 分钟，重点模仿语调起伏。' },
  'practice.oneFixCompleteness': { en: 'Incomplete — some words were skipped. Check against the original text.', zh: '内容不完整。有漏读的词，对照原文检查。' },
  'practice.oneFixFallback': { en: 'Keep practicing.', zh: '继续练习。' },
  'dim.fluency': { en: 'Fluency', zh: '流畅度' },
  'dim.accuracy': { en: 'Accuracy', zh: '准确度' },
  'dim.pronunciation': { en: 'Pronunciation', zh: '发音' },
  'dim.completeness': { en: 'Completeness', zh: '完整度' },
};

interface Rig {
  session: PracticeGradingSession;
  toasts: string[];
  aiCalls: unknown[];
  createCalls: unknown[];
  lang: WritableSignal<'en' | 'zh'>;
  setAiImpl: (impl: (req: never) => unknown) => void;
  setCreateImpl: (impl: (item: never) => unknown) => void;
}

function makeScore(over: Partial<RecordingScore> = {}): RecordingScore {
  return {
    pronScore: 80, accuracyScore: 80, fluencyScore: 80, completenessScore: 80,
    prosodyScore: null, recognized: '', words: [], simulated: false, billedSeconds: 60,
    ...over,
  };
}

function makeTake(id: string, score: RecordingScore | null): Recording {
  return {
    id, materialId: 'mat-1', createdAt: 1761230000000, duration: 29,
    blob: null, url: '', score, grading: false, uploaded: true, error: null, version: null,
  } as unknown as Recording;
}

function makeRig(takes: Recording[], activeId: string): Rig {
  const toasts: string[] = [];
  const aiCalls: unknown[] = [];
  const createCalls: unknown[] = [];
  const lang: WritableSignal<'en' | 'zh'> = signal('en');
  let aiImpl: (req: never) => unknown = () => of({ text: 'FOLLOWUP-Q-125' });
  let createImpl: (item: never) => unknown = () => of({});
  const host = {
    selectedId: () => 'mat-1',
    recorder: { forMaterial: () => takes },
    practiceApi: {},
    aiApi: { complete: (req: never) => { aiCalls.push(req); return aiImpl(req); } },
    knowledgeApi: { createItem: (item: never) => { createCalls.push(item); return createImpl(item); } },
    customQuestion: () => '',
    currentText: () => 'QUESTION-125',
    t: (key: string) => DICT[key]?.[lang()] ?? key,
    toast: (msg: string) => { toasts.push(msg); },
    cdr: { detectChanges: () => { /* stub */ } },
    tts: {},
  };
  const session = new PracticeGradingSession(host as never);
  session.activeTakeId.set(activeId);
  return {
    session, toasts, aiCalls, createCalls, lang,
    setAiImpl: (impl) => { aiImpl = impl; },
    setCreateImpl: (impl) => { createImpl = impl; },
  };
}

const CJK = /[\u4e00-\u9fff]/;

describe('票125 TJ-1 追问真出题', () => {
  it('prompt 装的是 score.recognized 原文，结果置位', async () => {
    const take = makeTake('take-1', makeScore({ recognized: RECOG }));
    const rig = makeRig([take], 'take-1');
    await rig.session.generateFollowUp();
    expect(rig.aiCalls.length).toBe(1);
    const prompt = (rig.aiCalls[0] as { prompt: string }).prompt;
    expect(prompt).toContain(RECOG);
    expect(rig.session.followUpQuestion()).toBe('FOLLOWUP-Q-125');
  });
});

describe('票125 TJ-2 追问无文本必反馈', () => {
  it('recognized 为空：零请求、toast 出 noRecognizedText、loading 全程 false', async () => {
    const take = makeTake('take-1', makeScore({ recognized: '' }));
    const rig = makeRig([take], 'take-1');
    await rig.session.generateFollowUp();
    expect(rig.aiCalls.length).toBe(0);
    expect(rig.toasts).toEqual(['No recognized speech yet — score a recording first.']);
    expect(rig.session.followUpLoading()).toBe(false);
    expect(rig.session.followUpQuestion()).toBe(null);
  });

  it('score 为 null 变体：同样零请求有反馈', async () => {
    const take = makeTake('take-1', null);
    const rig = makeRig([take], 'take-1');
    await rig.session.generateFollowUp();
    expect(rig.aiCalls.length).toBe(0);
    expect(rig.toasts.length).toBe(1);
    expect(rig.session.followUpLoading()).toBe(false);
  });
});

describe('票125 TJ-3/TJ-4 加入复习', () => {
  it('TJ-3：myAnswer = recognized 切片、category 取当前语言值、成功 toast 键化', async () => {
    const recognized = 'R'.repeat(1100);
    const take = makeTake('take-1', makeScore({ recognized }));
    const rig = makeRig([take], 'take-1');
    await rig.session.addToReview();
    expect(rig.createCalls.length).toBe(1);
    const item = rig.createCalls[0] as { myAnswer: string; category: string };
    expect(item.myAnswer).toBe(recognized.slice(0, 1000));
    expect(item.myAnswer.length).toBe(1000);
    expect(item.category).toBe('Speaking practice');
    expect(rig.toasts).toEqual(['Added to Tech Stack review']);
  });

  it('TJ-4：无文本不建卡且有反馈', async () => {
    const take = makeTake('take-1', makeScore({ recognized: '   ' }));
    const rig = makeRig([take], 'take-1');
    await rig.session.addToReview();
    expect(rig.createCalls.length).toBe(0);
    expect(rig.toasts).toEqual(['No recognized speech yet — score a recording first.']);
  });
});

describe('票125 TJ-5 computed 双语', () => {
  it('填充词路径：en 下 verdict/title/detail 无 CJK 且等于键表 en 值，切 zh 后重算为 zh 值', () => {
    const words = [
      { word: 'um', accuracy: 90, errorType: '', offsetMs: 100 },
      { word: 'um', accuracy: 90, errorType: '', offsetMs: 500 },
      { word: 'uh', accuracy: 90, errorType: '', offsetMs: 900 },
      { word: 'like', accuracy: 90, errorType: '', offsetMs: 1300 },
      { word: 'hello', accuracy: 95, errorType: '', offsetMs: 1700 },
    ];
    const take = makeTake('take-1', makeScore({ words, billedSeconds: 60 }));
    const rig = makeRig([take], 'take-1');
    const paceEn = rig.session.speechPace();
    const fixEn = rig.session.oneFix();
    expect(paceEn?.verdict).toBe('Slow');
    expect(fixEn?.title).toBe('Cut the filler words');
    expect(fixEn?.detail).toBe('4 filler words detected (4/min). Next time, pause for a second instead of saying "um". Click an orange word to replay it.');
    expect(CJK.test(`${paceEn?.verdict}${fixEn?.title}${fixEn?.detail}`)).toBe(false);
    rig.lang.set('zh');
    expect(rig.session.speechPace()?.verdict).toBe('偏慢');
    expect(rig.session.oneFix()?.title).toBe('减少填充词');
    expect(rig.session.oneFix()?.detail).toContain('检测到 4 个填充词');
  });

  it('最弱维路径：title 用 dim.* 复用键的当前语言值', () => {
    const take = makeTake('take-1', makeScore({
      words: [], fluencyScore: 70, accuracyScore: 90, pronScore: 90, completenessScore: 90,
    }));
    const rig = makeRig([take], 'take-1');
    expect(rig.session.oneFix()?.title).toBe('Top fix: Fluency (70)');
    rig.lang.set('zh');
    expect(rig.session.oneFix()?.title).toBe('优先改进：流畅度（70分）');
  });
});

describe('票125 TJ-7 状态机 + 接线对照', () => {
  let rig: Rig;
  beforeEach(() => {
    const a = makeTake('take-a', makeScore({ recognized: 'A' }));
    const b = makeTake('take-b', null);
    rig = makeRig([a, b], 'take-a');
  });

  it('会话 toggleTakeReport：同条开合、切行展开、无分 take 同样可开', () => {
    const s = rig.session;
    expect(s.isTakeReportOpen('take-a')).toBe(true); // reportOpen 默认 true
    s.toggleTakeReport('take-a');
    expect(s.isTakeReportOpen('take-a')).toBe(false);
    s.toggleTakeReport('take-a');
    expect(s.isTakeReportOpen('take-a')).toBe(true);
    s.toggleTakeReport('take-b');
    expect(s.isTakeReportOpen('take-b')).toBe(true);
    expect(s.isTakeReportOpen('take-a')).toBe(false);
  });

  it('源码对照：行接线改走 toggleTakeReport + Space + aria-expanded，5 处 stopPropagation 齐备', () => {
    const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
    expect(html).toContain('(click)="toggleTakeReport(r.id)"');
    expect(html).toContain('(keyup.enter)="toggleTakeReport(r.id)"');
    expect(html).toContain('onRowSpaceKey($event, r.id)');
    const ariaCount = (html.match(/\[attr\.aria-expanded\]="isTakeReportOpen\(r\.id\)"/g) ?? []).length;
    expect(ariaCount).toBeGreaterThanOrEqual(2); // 行本体 + hi-view 钮
    for (const h of [
      '(click)="$event.stopPropagation(); togglePlay(r)"',
      '(click)="$event.stopPropagation(); seek(r, $event)"',
      '(click)="$event.stopPropagation(); gradeRecording(r.id)"',
      '(click)="$event.stopPropagation(); toggleTakeReport(r.id)"',
      '(click)="$event.stopPropagation(); removeRecording(r.id)"',
    ]) expect(html).toContain(h);
  });

  it('源码对照：.rep-title 整块已删，Score Results 标题只剩内层一个', () => {
    const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
    expect(html).not.toContain('rep-title');
    expect(html).not.toContain('rep-toggle');
    expect(html).not.toContain('toggleReport()');
    const scoreResultsCount = (html.match(/practice\.scoreResults/g) ?? []).length;
    expect(scoreResultsCount).toBe(1);
  });

  it('源码对照：两端 toggleReport 已删、collapseFocusTarget 的 section 分支已清', () => {
    const comp = fs.readFileSync(path.join(__dirname, 'ai-practice.component.ts'), 'utf8');
    const session = fs.readFileSync(path.join(__dirname, 'practice-grading-session.ts'), 'utf8');
    expect(comp).not.toContain('toggleReport(): void');
    expect(session).not.toContain('toggleReport(): void');
    expect(comp).not.toContain("closest('section.report-section')");
    expect(comp).toContain('onRowSpaceKey');
  });

  it('源码对照：取文本走类型化 score.recognized，旧 as any 形态零残留', () => {
    const session = fs.readFileSync(path.join(__dirname, 'practice-grading-session.ts'), 'utf8');
    expect(session).toContain('take?.score?.recognized ?? ');
    expect(session).not.toContain('(take as any)?.transcript');
    expect(session).not.toContain('(take as any)?.text');
  });

  it('源码对照：HTML 十处文案已键化', () => {
    const html = fs.readFileSync(path.join(__dirname, 'ai-practice.component.html'), 'utf8');
    for (const key of [
      'practice.customQuestionLabel', 'practice.clearTooltip', 'practice.addToReviewBtn',
      'practice.followUpBtn', 'practice.followUpLabel', 'practice.followUpAnother',
      'practice.fillerStats', 'practice.fillerReplayHint', 'practice.wordsPerMinute',
      'practice.fillerWord', 'practice.fillerClickReplay',
    ]) expect(html).toContain(key);
  });
});
