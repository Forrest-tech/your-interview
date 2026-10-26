/**
 * 票130 前端验收(TJ-1 状态机三路 + 错误快路 / TJ-2 取消全集 / TJ-3 已挂出切换不延迟)。
 *
 * 基建口径(同 dictionary-ticket126.spec):本仓 Jest = node 环境 + @angular/* 替身,
 * 无 TestBed / DOM 渲染。构造 = 真实 PracticeDictionary + 记录型宿主替身
 * (openDictPopup/closeDictPopup 调用序、调用时刻的 state、假时钟时间戳)
 * + 可控落定时刻的查词替身(以 setTimeout 在假时钟下安排落定)。
 * 宽度类判据不在本文件(jsdom 无布局,归活体 E-130-H1);本文件只断言
 * 状态机/调用序/显示态,不写假量宽断言。
 *
 * 微瑕折入(评分门 SCORE-R1):
 * - K-1:TJ-2 补「滚动出视口」取消一路(锚点几何替身 + 阈值挂出前复核)。
 * - K-2:retry 窗内显示裁决 —— 旧错误卡保留在席直至新结果落定(不卸下、不换骨架);
 *   TJ-1⑤ 断言窗内 state 恒为旧 error、浮层零重挂。
 */
import { describe, expect, it, jest, beforeEach, afterEach } from '@jest/globals';
import { Observable } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { DICT_POPUP_DELAY_MS, PracticeDictionary } from './practice-dictionary';
import { DictCardState, PassageToken } from './dictionary.helpers';

// ---------- 基建 ----------

type DocHandler = (event: never) => void;
const docHandlers = new Map<string, DocHandler[]>();

beforeEach(() => {
  docHandlers.clear();
  (globalThis as { document?: unknown }).document = {
    addEventListener: (type: string, fn: DocHandler) => {
      const list = docHandlers.get(type) ?? [];
      list.push(fn);
      docHandlers.set(type, list);
    },
    removeEventListener: (type: string, fn: DocHandler) => {
      docHandlers.set(type, (docHandlers.get(type) ?? []).filter((f) => f !== fn));
    },
  };
  jest.useFakeTimers();
});

afterEach(() => {
  jest.useRealTimers();
});

function fireDoc(type: string, event: unknown): void {
  for (const fn of docHandlers.get(type) ?? []) (fn as (e: unknown) => void)(event);
}

/** 在假时钟下于 delayMs 后落定(成功 dto 或失败 status)的查词替身。 */
function timedResponder(delayMs: number, outcome: { dto?: unknown; status?: number }) {
  return () =>
    new Observable((sub) => {
      const t = setTimeout(() => {
        if (outcome.status !== undefined) {
          sub.error(new HttpErrorResponse({ status: outcome.status, statusText: 'ERR' }));
        } else {
          sub.next(outcome.dto);
          sub.complete();
        }
      }, delayMs);
      return () => clearTimeout(t);
    });
}

interface MountCall {
  el: unknown;
  state: DictCardState | null;
}

interface HostRec {
  mounts: MountCall[];
  closes: number;
  refreshes: number;
}

function makeDict(responder: (word: string) => Observable<unknown>): {
  dict: PracticeDictionary;
  rec: HostRec;
  stateAt: () => DictCardState | null;
} {
  const rec: HostRec = { mounts: [], closes: 0, refreshes: 0 };
  let dict!: PracticeDictionary;
  const host = {
    practiceApi: { lookupDictionaryWord: (w: string) => responder(w) },
    readonlyText: () => '',
    openDictPopup: (el: HTMLElement) => {
      rec.mounts.push({ el, state: dict.state() });
    },
    closeDictPopup: () => {
      rec.closes++;
    },
    refreshDictPopupPosition: () => {
      rec.refreshes++;
    },
  };
  dict = new PracticeDictionary(host as never);
  return { dict, rec, stateAt: () => dict.state() };
}

const word = (text: string, wordIndex = 0): PassageToken => ({ kind: 'word', text, wordIndex });

interface ElStub {
  focus: ReturnType<typeof jest.fn>;
  getBoundingClientRect?: () => { top: number; bottom: number };
}

function el(inView = true): ElStub {
  const stub: ElStub = { focus: jest.fn() };
  stub.getBoundingClientRect = () =>
    inView ? { top: 100, bottom: 118 } : { top: 5000, bottom: 5018 };
  (globalThis as { innerHeight?: number }).innerHeight = 900;
  return stub;
}

/** 推进假时钟并排空微任务(open 的 await 续体)。 */
async function advance(ms: number): Promise<void> {
  jest.advanceTimersByTime(ms);
  for (let i = 0; i < 6; i++) await Promise.resolve();
}

const dtoOf = (lemma: string) => ({ lemma, query: lemma });

// ---------- TJ-1（状态机三路 + 错误快路，§6.2/§6.4） ----------

describe('TJ-1(H3 状态机:落定先到直达 / 阈值先到出壳 / 错误快路)', () => {
  it('① 落定于 50ms → 落定后才挂出且挂出时 state 已是终态,全程无 loading 帧挂出', async () => {
    const { dict, rec } = makeDict(timedResponder(50, { dto: dtoOf('glorp') }));
    const anchor = el();
    void dict.open(word('glorp'), anchor as never);
    await advance(49);
    expect(rec.mounts).toHaveLength(0);
    await advance(1); // 落定点 50ms:挂出恰在落定后、远未及 150ms 阈值
    expect(rec.mounts).toHaveLength(1);
    expect(rec.mounts[0].el).toBe(anchor);
    expect(rec.mounts[0].state?.status).toBe('ok');
    await advance(1000);
    expect(rec.mounts).toHaveLength(1); // 计时器已作废,不再二次挂出
  });

  it('② 落定于 800ms → 149ms 未挂出、150ms 挂出 loading 壳、800ms 原地换终态(不重挂)', async () => {
    const { dict, rec, stateAt } = makeDict(timedResponder(800, { dto: dtoOf('glorp') }));
    void dict.open(word('glorp'), el() as never);
    await advance(149);
    expect(rec.mounts).toHaveLength(0);
    await advance(1); // 恰 150ms:阈值事件挂出 loading 壳
    expect(rec.mounts).toHaveLength(1);
    expect(rec.mounts[0].state?.status).toBe('loading');
    await advance(650);
    expect(stateAt()?.status).toBe('ok');
    expect(rec.mounts).toHaveLength(1); // 原地换内容,浮层未拆重挂
  });

  it('③ 404 于 30ms 落定 → 挂出时 state = notfound 且挂出时刻 <150ms(错误快路不等阈值)', async () => {
    const { dict, rec } = makeDict(timedResponder(30, { status: 404 }));
    void dict.open(word('zorblax'), el() as never);
    await advance(29);
    expect(rec.mounts).toHaveLength(0);
    await advance(1); // 落定点 30ms 即挂出(未等满 150ms):错误快路成立
    expect(rec.mounts).toHaveLength(1);
    expect(rec.mounts[0].state?.status).toBe('notfound');
  });

  it('④ 慢失败于 800ms → 150ms 挂壳、800ms 原地换 error 态', async () => {
    const { dict, rec, stateAt } = makeDict(timedResponder(800, { status: 500 }));
    void dict.open(word('glorp'), el() as never);
    await advance(150);
    expect(rec.mounts).toHaveLength(1);
    expect(rec.mounts[0].state?.status).toBe('loading');
    await advance(650);
    expect(stateAt()?.status).toBe('error');
    expect(rec.mounts).toHaveLength(1);
  });

  it('⑤ K-2:retry 重新进待挂出窗 —— 窗内旧错误卡保留在席(state 恒旧 error、浮层零重挂),新结果落定原地换', async () => {
    let call = 0;
    const responder = () => {
      call++;
      return call === 1
        ? timedResponder(30, { status: 500 })()
        : timedResponder(800, { dto: dtoOf('glorp') })();
    };
    const { dict, rec, stateAt } = makeDict(responder);
    void dict.open(word('glorp'), el() as never);
    await advance(30);
    expect(stateAt()?.status).toBe('error');
    expect(rec.mounts).toHaveLength(1);

    dict.retry();
    // 窗内(0ms 与 150ms 两个时点):旧错误卡仍在席 —— state 不变、浮层不卸不重挂
    expect(stateAt()?.status).toBe('error');
    await advance(150);
    expect(stateAt()?.status).toBe('error');
    expect(rec.mounts).toHaveLength(1);
    expect(rec.closes).toBe(0);
    // 落定时原地换终态
    await advance(650);
    expect(stateAt()?.status).toBe('ok');
    expect(rec.mounts).toHaveLength(1);
  });
});

// ---------- TJ-2（取消全集，§6.3） ----------

describe('TJ-2(待挂出态取消全集:永不孤儿挂出)', () => {
  const SLOW = 800;

  it('同词再点(toggle 关) → 取消,推进 1000ms 永不挂出', async () => {
    const { dict, rec } = makeDict(timedResponder(SLOW, { dto: dtoOf('glorp') }));
    const anchor = el();
    const ev = { currentTarget: anchor } as never;
    dict.onWordClick(word('glorp'), ev);
    await advance(80);
    dict.onWordClick(word('glorp'), ev);
    expect(dict.state()).toBeNull();
    await advance(1000);
    expect(rec.mounts).toHaveLength(0);
  });

  it('点另一词 → 旧代际取消,仅新词按其自身计时挂出(锚点 = 新词)', async () => {
    const { dict, rec, stateAt } = makeDict(timedResponder(SLOW, { dto: dtoOf('x') }));
    const a = el();
    const b = el();
    void dict.open(word('alpha'), a as never);
    await advance(80);
    void dict.open(word('beta', 1), b as never);
    await advance(70); // A 的原阈值点(150ms)已过 —— A 永不挂出
    expect(rec.mounts).toHaveLength(0);
    await advance(80); // B 的阈值点(80+150=230ms)
    expect(rec.mounts).toHaveLength(1);
    expect(rec.mounts[0].el).toBe(b);
    await advance(1000);
    expect(rec.mounts).toHaveLength(1);
    expect(stateAt()?.status).toBe('ok');
    expect(stateAt()?.query).toBe('beta');
  });

  it('Esc(待挂出态,焦点在触发词) → 取消并焦点回触发词,永不挂出', async () => {
    const { dict, rec } = makeDict(timedResponder(SLOW, { dto: dtoOf('glorp') }));
    const anchor = el();
    void dict.open(word('glorp'), anchor as never);
    await advance(80);
    fireDoc('keydown', { key: 'Escape' });
    expect(dict.state()).toBeNull();
    expect(anchor.focus).toHaveBeenCalledTimes(1);
    await advance(1000);
    expect(rec.mounts).toHaveLength(0);
  });

  it('点外关闭 → 取消,永不挂出', async () => {
    const { dict, rec } = makeDict(timedResponder(SLOW, { dto: dtoOf('glorp') }));
    void dict.open(word('glorp'), el() as never);
    await advance(80);
    fireDoc('click', { target: { closest: () => null } });
    expect(dict.state()).toBeNull();
    await advance(1000);
    expect(rec.mounts).toHaveLength(0);
  });

  it('K-1:滚动出视口 → 阈值到时挂出前复核命中,不挂出且按 closeFromScroll 收口(焦点 preventScroll)', async () => {
    const { dict, rec } = makeDict(timedResponder(SLOW, { dto: dtoOf('glorp') }));
    const anchor = el(false); // 锚点已在视口外(top 5000 > 视口高 900)
    void dict.open(word('glorp'), anchor as never);
    await advance(80);
    fireDoc('scroll', {});
    await advance(70); // 阈值到时:复核锚点出视口 → 不挂出
    expect(rec.mounts).toHaveLength(0);
    expect(dict.state()).toBeNull();
    expect(anchor.focus).toHaveBeenCalledWith({ preventScroll: true });
    await advance(1000);
    expect(rec.mounts).toHaveLength(0);
  });

  it('reset(换素材) → 取消,永不挂出', async () => {
    const { dict, rec } = makeDict(timedResponder(SLOW, { dto: dtoOf('glorp') }));
    void dict.open(word('glorp'), el() as never);
    await advance(80);
    dict.reset();
    await advance(1000);
    expect(rec.mounts).toHaveLength(0);
    expect(dict.state()).toBeNull();
  });
});

// ---------- TJ-3（已挂出切换不延迟，§6.5） ----------

describe('TJ-3(已挂出切换:不进延迟窗)', () => {
  it('内容卡已挂出时点另一词 → 同步重锚挂出、新词 loading 立即可见', async () => {
    const { dict, rec, stateAt } = makeDict((w: string) =>
      w === 'alpha'
        ? timedResponder(50, { dto: dtoOf('alpha') })()
        : timedResponder(800, { dto: dtoOf('beta') })(),
    );
    const a = el();
    const b = el();
    void dict.open(word('alpha'), a as never);
    await advance(50);
    expect(rec.mounts).toHaveLength(1);
    expect(stateAt()?.status).toBe('ok');

    void dict.open(word('beta', 1), b as never);
    // 不推进时钟:重锚与 loading 态必须在同一同步帧内成立
    expect(rec.mounts).toHaveLength(2);
    expect(rec.mounts[1].el).toBe(b);
    expect(rec.mounts[1].state?.status).toBe('loading');
    await advance(800);
    expect(stateAt()?.status).toBe('ok');
    expect(stateAt()?.query).toBe('beta');
  });
});
