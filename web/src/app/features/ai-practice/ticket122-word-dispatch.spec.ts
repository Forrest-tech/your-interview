/**
 * 票122 前端分发补测(组件 spec 口径)。
 *
 * 被测分发 = 模板逐字表达式(ai-practice.component.html 逐词 span):
 *   (click)="wordClickAction(isFiller(w.word), w.offsetMs) === 'replay'
 *             ? replayAt(w.offsetMs!) : pickWord($index)"
 * 沿革:票122 落地时 word-click.helpers 尚未存在,分发是模板内联三元式,
 * 本 spec 按上代表达式留痕;票120 落地后分发条件抽成纯函数 wordClickAction
 * (行为逐字不变),本 spec 按其自带规则「改分发必须先改本 spec」同步改钉:
 * 本地求值改用真实 wordClickAction,契约断言改钉新表达式。
 * 本 spec 不走 TestBed(本仓 Jest 口径),而是:
 *  1) 用真实 PracticeGradingSession 的 isFiller/replayAt/pickWord 按上式求值,
 *     断言重载语境三例的去向与副作用;
 *  2) 契约断言:模板文件必须逐字含上式 —— 模板若改分发,本 spec 先红,防静默背离。
 *
 * take 形态 = 落库读出 DTO 经 fromDto 适配后的重载形态(blob 为空占位、
 * url 为 ''),words 即 WordScoreDto[] 整组透传(scoreFromDto 无字段裁剪)。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { of } from 'rxjs';
import { describe, expect, it, beforeEach } from '@jest/globals';
import { PracticeGradingSession } from './practice-grading-session';
import { wordClickAction } from './word-click.helpers';
import type { Recording, RecordingScore } from '../../core/recorder/recorder.service';
import type { WordScoreDto } from '../../core/api/practice-api.service';

/** 记录型 Audio 替身:replayAt 内部 new Audio(url) 的落点在此留痕。 */
class FakeAudio {
  static instances: FakeAudio[] = [];
  src = '';
  currentTime = 0;
  onended: (() => void) | null = null;
  constructor(url?: string) {
    this.src = url ?? '';
    FakeAudio.instances.push(this);
  }
  play(): Promise<void> { return Promise.resolve(); }
  pause(): void { /* stub */ }
}

interface FakeHost {
  selectedId: () => string;
  recorder: { forMaterial: (id: string) => Recording[] };
  t: (key: string) => string;
  toast: (msg: string) => void;
  toasts: string[];
}

function makeSession(words: WordScoreDto[]): { session: PracticeGradingSession; host: FakeHost } {
  const score: RecordingScore = {
    pronScore: 88, accuracyScore: 87, fluencyScore: 87, completenessScore: 90.6,
    prosodyScore: null, recognized: 'teenage um girls', words,
    simulated: false, billedSeconds: 28.6, billedBytes: null, version: null,
  } as RecordingScore;
  const take = {
    id: '7c9e1220-0000-4000-8000-000000000122', materialId: 'mat-1',
    createdAt: 1761230000000, duration: 29,
    blob: new Blob([], { type: 'audio/wav' }), url: '',
    score, grading: false, uploaded: true, error: null, version: null,
  } as unknown as Recording;
  const toasts: string[] = [];
  const host = {
    selectedId: () => 'mat-1',
    recorder: { forMaterial: () => [take] },
    // 票132 supersede 接缝(规格 §9.1 登记):replayAt 对本地无真源的重载
    // take 改走鉴权懒拉取,宿主替身补 practiceApi——fetch 同步回真 blob,
    // 分发去向/seek/不开 pop 的断言口径不变。
    practiceApi: { fetchRecordingAudio: (_id: string) => of(new Blob(['real-audio'])) },
    t: (key: string) => key,
    toast: (msg: string) => { toasts.push(msg); },
    toasts,
  } as unknown as FakeHost;
  const session = new PracticeGradingSession(host as never);
  session.activeTakeId.set(take.id);
  // 会话自身字段初始化会建一个常驻 Audio(空 src)——基线清零,其后新增的
  // 实例只可能来自 replayAt 的 seek 构造。
  FakeAudio.instances = [];
  return { session, host };
}

/** 与模板逐字同形的分发求值(纯函数判定 + 会话的真实三方法)。 */
function dispatch(session: PracticeGradingSession, w: WordScoreDto, index: number): void {
  if (wordClickAction(session.isFiller(w.word), w.offsetMs) === 'replay') session.replayAt(w.offsetMs!);
  else session.pickWord(index);
}

beforeEach(() => {
  FakeAudio.instances = [];
  (globalThis as { Audio?: unknown }).Audio = FakeAudio;
});

describe('票122 重载语境逐词分发(模板表达式口径)', () => {
  it('重载填充词 offsetMs>0 → replay:音频 seek 到 offsetMs/1000−0.5,不开 word-pop', () => {
    const words: WordScoreDto[] = [
      { word: 'teenage', accuracy: 88, errorType: 'None', offsetMs: 120, durationMs: 430 },
      { word: 'um', accuracy: 0, errorType: 'Insertion', offsetMs: 3200, durationMs: 180 },
    ];
    const { session } = makeSession(words);
    dispatch(session, words[1], 1);
    expect(FakeAudio.instances).toHaveLength(1);
    expect(FakeAudio.instances[0].currentTime).toBeCloseTo(2.7, 5);
    expect(session.pickedWord()).toBeNull();
  });

  it('重载旧记录填充词(offsetMs 缺失) → pick:开 word-pop,不构造音频', () => {
    const words: WordScoreDto[] = [
      { word: 'um', accuracy: 0, errorType: 'Insertion' },
    ];
    const { session } = makeSession(words);
    dispatch(session, words[0], 0);
    expect(session.pickedWord()).toBe(0);
    expect(FakeAudio.instances).toHaveLength(0);
  });

  it('重载旧记录填充词(offsetMs=0) → pick:0 即「无偏移」,绝不假 seek', () => {
    const words: WordScoreDto[] = [
      { word: 'um', accuracy: 0, errorType: 'Insertion', offsetMs: 0, durationMs: 0 },
    ];
    const { session } = makeSession(words);
    dispatch(session, words[0], 0);
    expect(session.pickedWord()).toBe(0);
    expect(FakeAudio.instances).toHaveLength(0);
  });

  it('非填充词即便带 offsetMs → pick:isFiller 短路,偏移不触发回放', () => {
    const words: WordScoreDto[] = [
      { word: 'girls', accuracy: 91, errorType: 'None', offsetMs: 1500, durationMs: 300 },
    ];
    const { session } = makeSession(words);
    dispatch(session, words[0], 0);
    expect(session.pickedWord()).toBe(0);
    expect(FakeAudio.instances).toHaveLength(0);
  });

  it('契约:模板逐字含现行分发表达式(改分发必须先改本 spec)', () => {
    const html = readFileSync(join(__dirname, 'ai-practice.component.html'), 'utf8');
    expect(html).toContain(
      '(click)="wordClickAction(isFiller(w.word), w.offsetMs) === \'replay\' ? replayAt(w.offsetMs!) : pickWord($index)"');
  });
});
