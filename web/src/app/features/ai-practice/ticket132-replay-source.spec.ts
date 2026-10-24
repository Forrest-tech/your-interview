/**
 * 票132:replayAt 取源修复(重载 take 点词回放无声)。
 *
 * 规格 ~/workspace/ticket132/SPEC.md §5 R-F1…R-F8、§9.1 TJ-1…TJ-4。
 * TJ-5(分发守恒)由 ticket122-word-dispatch.spec.ts 的存量断言承担,本文件不重复。
 *
 * 被测断点(规格 §4 根因链):replayAt 旧实现以 blob 的 truthiness 为唯一闸门——
 * 重载 take 经 fromDto 拿到的是 0 字节占位 blob(truthy)→ 对空源 createObjectURL
 * 并 play() 必败,只弹笼统 practice.retryLater,全程不发请求。
 * 新口径:本地有源判据与全仓统一为 blob.size > 0;无真源且 id 为 GUID 时,
 * 点播当刻经 fetchRecordingAudio 鉴权懒拉取,同公式 seek 播放;失败照
 * togglePlay 分级(404 → recGone、其余 → recLoadFail);在途竞态作废(R-F6)。
 *
 * harness 沿票122 的记录型 Audio 替身;URL.createObjectURL/revokeObjectURL
 * 以记录型假实现替换,以便断言「URL 由哪个 blob 建成」「作废时被 revoke」。
 */
import { describe, expect, it, beforeEach } from '@jest/globals';
import { Observable, Subject, of, throwError } from 'rxjs';
import { PracticeGradingSession } from './practice-grading-session';
import type { Recording } from '../../core/recorder/recorder.service';

/** 记录型 Audio 替身:构造/currentTime/play 全部留痕,play 成败可编程。 */
class FakeAudio {
  static instances: FakeAudio[] = [];
  static playImpl: () => Promise<void> = () => Promise.resolve();
  src = '';
  currentTime = 0;
  onended: (() => void) | null = null;
  playCalls = 0;
  constructor(url?: string) {
    this.src = url ?? '';
    FakeAudio.instances.push(this);
  }
  play(): Promise<void> { this.playCalls++; return FakeAudio.playImpl(); }
  pause(): void { /* stub */ }
}

const createdUrls: { blob: Blob; url: string }[] = [];
const revokedUrls: string[] = [];
let urlSeq = 0;

const TAKE_A = 'aaaaaaaa-0000-4000-8000-000000000132';
const TAKE_B = 'bbbbbbbb-0000-4000-8000-000000000132';

interface Rig {
  session: PracticeGradingSession;
  toasts: string[];
  fetchCalls: string[];
  setFetch: (impl: (id: string) => Observable<Blob>) => void;
  takes: Recording[];
}

function makeTake(id: string, blob: Blob): Recording {
  return {
    id, materialId: 'mat-1', createdAt: 1761230000000, duration: 29,
    blob, url: '', score: null, grading: false, uploaded: true, error: null, version: null,
  } as unknown as Recording;
}

function makeRig(takes: Recording[], activeId: string): Rig {
  const toasts: string[] = [];
  const fetchCalls: string[] = [];
  let fetchImpl: (id: string) => Observable<Blob> = () => of(new Blob(['remote-audio']));
  const host = {
    selectedId: () => 'mat-1',
    recorder: { forMaterial: () => takes },
    practiceApi: {
      fetchRecordingAudio: (id: string) => { fetchCalls.push(id); return fetchImpl(id); },
    },
    t: (key: string) => key,
    toast: (msg: string) => { toasts.push(msg); },
  };
  const session = new PracticeGradingSession(host as never);
  session.activeTakeId.set(activeId);
  // 会话字段初始化会建一个常驻 Audio(空 src)——基线清零,其后新增实例
  // 只可能来自 replayAt 的 seek 构造。
  FakeAudio.instances = [];
  return {
    session, toasts, fetchCalls, takes,
    setFetch: (impl) => { fetchImpl = impl; },
  };
}

async function flushMicrotasks(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0));
}

beforeEach(() => {
  FakeAudio.instances = [];
  FakeAudio.playImpl = () => Promise.resolve();
  createdUrls.length = 0;
  revokedUrls.length = 0;
  (globalThis as { Audio?: unknown }).Audio = FakeAudio;
  (URL as unknown as { createObjectURL: unknown }).createObjectURL = (blob: Blob) => {
    const url = `blob:fake-${++urlSeq}`;
    createdUrls.push({ blob, url });
    return url;
  };
  (URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL = (url: string) => {
    revokedUrls.push(url);
  };
});

describe('票132 TJ-1 本地支路守恒(真 blob:零请求、seek 与失败文案逐字不变)', () => {
  it('size>0 的本地 blob → 不发 fetch,Audio 以本地 blob 的 URL 构造、currentTime 同公式', () => {
    const local = new Blob(['local-audio']);
    const rig = makeRig([makeTake(TAKE_A, local)], TAKE_A);
    rig.session.replayAt(3200);
    expect(rig.fetchCalls).toHaveLength(0);
    expect(FakeAudio.instances).toHaveLength(1);
    expect(createdUrls).toHaveLength(1);
    expect(createdUrls[0].blob).toBe(local);
    expect(FakeAudio.instances[0].src).toBe(createdUrls[0].url);
    expect(FakeAudio.instances[0].currentTime).toBeCloseTo(2.7, 5);
    expect(FakeAudio.instances[0].playCalls).toBe(1);
    expect(rig.toasts).toHaveLength(0);
  });

  it('本地支路 play() 被拒 → toast 恰为 practice.retryLater(基线文案守恒)', async () => {
    FakeAudio.playImpl = () => Promise.reject(new Error('blocked'));
    const rig = makeRig([makeTake(TAKE_A, new Blob(['local-audio']))], TAKE_A);
    rig.session.replayAt(3200);
    await flushMicrotasks();
    expect(rig.toasts).toEqual(['practice.retryLater']);
  });
});

describe('票132 TJ-2 远端支路(0 字节占位 blob + GUID → 点播懒拉取)', () => {
  it('fromDto 形态 take → fetch 恰一次(id 正确),取回 blob 同公式 seek 播放', () => {
    const fetched = new Blob(['remote-audio']);
    const rig = makeRig([makeTake(TAKE_A, new Blob([], { type: 'audio/wav' }))], TAKE_A);
    rig.setFetch(() => of(fetched));
    rig.session.replayAt(3200);
    // 判别性:旧实现下 fetch 调用数 = 0 且 Audio 源为空占位 blob 的 URL,本组断言必红。
    expect(rig.fetchCalls).toEqual([TAKE_A]);
    expect(FakeAudio.instances).toHaveLength(1);
    expect(createdUrls).toHaveLength(1);
    expect(createdUrls[0].blob).toBe(fetched);
    expect(FakeAudio.instances[0].src).toBe(createdUrls[0].url);
    expect(FakeAudio.instances[0].currentTime).toBeCloseTo(2.7, 5);
    expect(FakeAudio.instances[0].playCalls).toBe(1);
    expect(rig.toasts).toHaveLength(0);
  });

  it('无 blob 且 id 非 GUID(未上传临时条)→ 不发请求,toast rec.noAudio(R-F2)', () => {
    const take = makeTake('local-temp-1', undefined as unknown as Blob);
    const rig = makeRig([take], 'local-temp-1');
    rig.session.replayAt(1200);
    expect(rig.fetchCalls).toHaveLength(0);
    expect(FakeAudio.instances).toHaveLength(0);
    expect(rig.toasts).toEqual(['rec.noAudio']);
  });
});

describe('票132 TJ-3 远端失败分级(照 togglePlay:404 → recGone,其余 → recLoadFail)', () => {
  it('fetch 404 → toast 恰为 practice.recGone,不构造 Audio', () => {
    const rig = makeRig([makeTake(TAKE_A, new Blob([]))], TAKE_A);
    rig.setFetch(() => throwError(() => ({ status: 404 })));
    rig.session.replayAt(3200);
    expect(rig.toasts).toEqual(['practice.recGone']);
    expect(FakeAudio.instances).toHaveLength(0);
  });

  it('fetch 500 → toast 为 practice.recLoadFail 组文(非笼统 retryLater),不构造 Audio', () => {
    const rig = makeRig([makeTake(TAKE_A, new Blob([]))], TAKE_A);
    rig.setFetch(() => throwError(() => ({ status: 500, message: 'server boom' })));
    rig.session.replayAt(3200);
    expect(rig.toasts).toHaveLength(1);
    expect(rig.toasts[0].startsWith('practice.recLoadFail')).toBe(true);
    expect(rig.toasts[0]).not.toBe('practice.retryLater');
    expect(FakeAudio.instances).toHaveLength(0);
  });

  it('fetch 回空体(size=0)→ 按取流失败走 recLoadFail 口径,不构造 Audio(R-F3)', () => {
    const rig = makeRig([makeTake(TAKE_A, new Blob([]))], TAKE_A);
    rig.setFetch(() => of(new Blob([])));
    rig.session.replayAt(3200);
    expect(rig.toasts).toHaveLength(1);
    expect(rig.toasts[0].startsWith('practice.recLoadFail')).toBe(true);
    expect(FakeAudio.instances).toHaveLength(0);
  });

  it('远端支路取到源但 play() 被拒 → toast practice.retryLater(F-S4,与本地支路同文案)', async () => {
    FakeAudio.playImpl = () => Promise.reject(new Error('blocked'));
    const rig = makeRig([makeTake(TAKE_A, new Blob([]))], TAKE_A);
    rig.setFetch(() => of(new Blob(['remote-audio'])));
    rig.session.replayAt(3200);
    await flushMicrotasks();
    expect(FakeAudio.instances).toHaveLength(1);
    expect(rig.toasts).toEqual(['practice.retryLater']);
  });
});

describe('票132 TJ-4 在途竞态作废(R-F6:切 take / 连点新词,旧结果不播不弹)', () => {
  it('拉流在途切到另一条 take → 成功结果作废:无 Audio、无 toast、已建 URL 被 revoke', () => {
    const subject = new Subject<Blob>();
    const rig = makeRig(
      [makeTake(TAKE_A, new Blob([])), makeTake(TAKE_B, new Blob([]))], TAKE_A);
    rig.setFetch(() => subject);
    rig.session.replayAt(3200);
    expect(rig.fetchCalls).toEqual([TAKE_A]);
    rig.session.activeTakeId.set(TAKE_B); // 用户在在途期间切走了
    subject.next(new Blob(['late-audio']));
    subject.complete();
    expect(FakeAudio.instances).toHaveLength(0);
    expect(rig.toasts).toHaveLength(0);
    expect(createdUrls).toHaveLength(1);
    expect(revokedUrls).toEqual([createdUrls[0].url]);
  });

  it('拉流在途连点新词 → 旧失败作废不弹 toast;新一次的结果照常出声', () => {
    const first = new Subject<Blob>();
    const second = new Subject<Blob>();
    const queue = [first, second];
    const rig = makeRig([makeTake(TAKE_A, new Blob([]))], TAKE_A);
    rig.setFetch(() => queue.shift()!);
    rig.session.replayAt(1000);
    rig.session.replayAt(3200); // 更新的 replay 发起 → 第一次在途作废
    first.error({ status: 404 });
    expect(rig.toasts).toHaveLength(0); // 旧 404 与用户已无关,不许弹 recGone
    second.next(new Blob(['fresh-audio']));
    second.complete();
    expect(FakeAudio.instances).toHaveLength(1);
    expect(FakeAudio.instances[0].currentTime).toBeCloseTo(2.7, 5);
    expect(rig.toasts).toHaveLength(0);
  });

  it('拉流在途连点新词 → 旧成功作废:不播、其 URL 被 revoke', () => {
    const first = new Subject<Blob>();
    const second = new Subject<Blob>();
    const queue = [first, second];
    const rig = makeRig([makeTake(TAKE_A, new Blob([]))], TAKE_A);
    rig.setFetch(() => queue.shift()!);
    rig.session.replayAt(1000);
    rig.session.replayAt(3200);
    first.next(new Blob(['stale-audio']));
    first.complete();
    expect(FakeAudio.instances).toHaveLength(0);
    expect(createdUrls).toHaveLength(1);
    expect(revokedUrls).toEqual([createdUrls[0].url]);
    second.next(new Blob(['fresh-audio']));
    second.complete();
    expect(FakeAudio.instances).toHaveLength(1);
  });
});
