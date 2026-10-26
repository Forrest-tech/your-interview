/**
 * 票126 前端验收(TJ-1…TJ-7 + 微瑕 M-3/M-5 的 TJ 级断言)。
 *
 * 基建口径(先说清,防误读):本仓 Jest = node 环境 + @angular/* 替身
 * (见 jest.config 头注与 test/jest-stub.ts),无 TestBed / HttpTestingController / DOM 渲染。
 * 规格 §4.4 与 TJ-1 允许「仓内 HTTP 测试机制(如 HttpTestingController **或等价物**)」,
 * 本文件的等价构造是:
 *   真实 ApiClient(真实构造;其 inject(HttpClient) 经替身模块补丁落到假 HttpClient)
 *   + 假 HttpClient 以真实 HttpErrorResponse flush 真实 404/500
 *   → 错误必经**真实 rethrow 转型**(F-1 的接缝本体)再喂**真实 PracticeDictionary** 状态机。
 *   (注:Object.create 绕字段初始化的捷径会漏掉 rethrow 箭头字段,产生假绿,禁用。)
 * 直接给状态机喂合成 HttpErrorResponse 的断言在本文件**不存在**——那正是 119 全绿全灭的盲区形态。
 * DOM 层断言(负卡渲染/Tab 序/浮层节点)以源码契约断言落码(同 ticket122 spec 的模板契约先例),
 * 真实浏览器行为由活体 E1–E8 兜底(见 VERIFY-126)。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it, beforeAll, jest } from '@jest/globals';
import { firstValueFrom, of, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import * as ngCore from '@angular/core';
import { ApiClient } from '../../core/api/api-client';
import { PracticeDictionary } from './practice-dictionary';
import { dictPopupGeometry, negativeCardLines, PassageToken } from './dictionary.helpers';

beforeAll(() => {
  // PracticeDictionary 构造期挂 document 监听;node 环境无 document,给最小替身。
  (globalThis as { document?: unknown }).document = {
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  };
});

/** flush 用的真实 HttpErrorResponse(替身基建已补该类,字段与生产同名同义)。 */
function httpErrorShape(status: number): HttpErrorResponse {
  return new HttpErrorResponse({
    status,
    statusText: status === 404 ? 'Not Found' : 'Server Error',
    error: { title: status === 404 ? 'Not Found' : 'Internal' },
  });
}

/** 真实 ApiClient + 假 HttpClient:responder 决定每次 get 的成败形态。 */
let injectedHttp: unknown = null;
(ngCore as unknown as { inject: unknown }).inject = () => injectedHttp;
function realApiClient(responder: (url: string) => unknown): ApiClient {
  injectedHttp = { get: (url: string) => responder(url) };
  return new ApiClient();
}

interface DictHostStub {
  practiceApi: { lookupDictionaryWord: (w: string) => unknown };
  readonlyText: () => string;
  openDictPopup: (el: HTMLElement) => void;
  closeDictPopup: () => void;
  refreshDictPopupPosition: () => void;
}

function makeDict(responder: (url: string) => unknown): PracticeDictionary {
  const client = realApiClient(responder);
  const host: DictHostStub = {
    // 与 PracticeApi.lookupDictionaryWord 逐字同形的调用(同走 ApiClient.get)
    practiceApi: {
      lookupDictionaryWord: (w: string) =>
        client.get(`/api/assessment/dictionary/${encodeURIComponent(w)}`),
    },
    readonlyText: () => '',
    openDictPopup: () => undefined,
    closeDictPopup: () => undefined,
    refreshDictPopupPosition: () => undefined,
  };
  return new PracticeDictionary(host as never);
}

const word = (text: string, wordIndex = 0): PassageToken => ({ kind: 'word', text, wordIndex });

describe('TJ-1(F-1 集成:真实 ApiClient + 真实 404/500 flush → 状态机落态)', () => {
  it('转换后错误形态自证:flush 的是 HttpErrorResponse,rethrow 产出普通 Error + .status 挂载', async () => {
    const flushed = httpErrorShape(404);
    expect(flushed).toBeInstanceOf(HttpErrorResponse);
    const client = realApiClient(() => throwError(() => flushed));
    const err = await firstValueFrom(client.get('/x')).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(Error);
    expect(err instanceof HttpErrorResponse).toBe(false);
    expect((err as { status?: number }).status).toBe(404);
    expect((err as { original?: unknown }).original).toBe(flushed);
    // 旧判定 err instanceof HttpErrorResponse 对转换后错误恒假 —— 这就是 F-1 的接缝
  });

  it('flush 404 → practice-dictionary 落 not-found 态(负卡),不落 error 态', async () => {
    const dict = makeDict(() => throwError(() => httpErrorShape(404)));
    await dict.open(word('zorblax'), null);
    expect(dict.state()).toEqual({ status: 'notfound', query: 'zorblax' });
  });

  it('负卡模型:词头 + 「暂无词条释义」一行,无占位符', () => {
    expect(negativeCardLines('zorblax', '暂无词条释义')).toEqual(['zorblax', '暂无词条释义']);
  });

  it('flush 500 → 落 error 态(错误简卡 + Retry 的状态侧)', async () => {
    const dict = makeDict(() => throwError(() => httpErrorShape(500)));
    await dict.open(word('glorp'), null);
    expect(dict.state()).toEqual({ status: 'error', query: 'glorp' });
  });

  it('flush 200 → 落 ok 态', async () => {
    const dto = { lemma: 'glorp', query: 'glorp' };
    const dict = makeDict(() => of(dto));
    await dict.open(word('glorp'), null);
    expect(dict.state()?.status).toBe('ok');
  });
});

const readSrc = (rel: string): string => readFileSync(join(__dirname, rel), 'utf8');

describe('TJ-3(popup:浮层节点在 overlay 层、篇章流内无占位、Close 末位)', () => {
  it('模板契约:卡只出现在 #dictPopupTpl 内,篇章 .text-box 段无卡节点', () => {
    const html = readSrc('ai-practice.component.html');
    const tplStart = html.indexOf('<ng-template #dictPopupTpl>');
    expect(tplStart).toBeGreaterThan(-1);
    expect(html.slice(0, tplStart)).not.toContain('<app-dictionary-card');
    expect(html.slice(tplStart)).toContain('<app-dictionary-card');
  });

  it('宿主契约:CDK 灵活连接定位(下方首选/上方翻转/右对齐兜底)+ 移动端 sheet + 面板类', () => {
    const ts = readSrc('ai-practice.component.ts');
    expect(ts).toContain('flexibleConnectedTo');
    expect(ts).toContain("overlayY: 'bottom', offsetY: -8");
    expect(ts).toContain("bottom('8px')");
    expect(ts).toContain("'dict-popup-panel'");
    expect(ts).toContain('closeFromScroll()');
  });

  it('宿主契约:内容落定后按真实高度重选位序(E2 翻转的地基,骨架高度会让 CDK 误选)', () => {
    const ts = readSrc('ai-practice.component.ts');
    expect(ts).toContain('refreshDictPopupPosition');
    expect(ts).toContain('updatePosition()');
    expect(readSrc('practice-dictionary.ts')).toContain('refreshDictPopupPosition()');
  });

  it('卡契约:ok 态 Close 钮在 DOM 末位(来源行之后),Tab 序即 DOM 序', () => {
    const card = readSrc('dictionary-card.component.ts');
    const okBlock = card.slice(card.indexOf("@case ('ok')"));
    expect(okBlock.indexOf('dict-source')).toBeGreaterThan(-1);
    expect(okBlock.indexOf('dict-close')).toBeGreaterThan(okBlock.indexOf('dict-source'));
  });

  it('Esc 路径行为:close(true) 把焦点送回触发词', async () => {
    const focus = jest.fn();
    const dict = makeDict(() => of({ lemma: 'glorp', query: 'glorp' }));
    await dict.open(word('glorp'), { focus } as unknown as HTMLElement);
    dict.close(true);
    expect(focus).toHaveBeenCalledTimes(1);
    expect(focus.mock.calls[0][0]).toBeUndefined();
  });

  it('M-5:滚动被动关闭 closeFromScroll 的返焦带 preventScroll(不得引发页面回滚)', async () => {
    const focus = jest.fn();
    const dict = makeDict(() => of({ lemma: 'glorp', query: 'glorp' }));
    await dict.open(word('glorp'), { focus } as unknown as HTMLElement);
    dict.closeFromScroll();
    expect(focus).toHaveBeenCalledWith({ preventScroll: true });
    expect(dict.state()).toBeNull();
  });
});

describe('TJ-4(已查词零描边,用户裁定 (b))', () => {
  it('篇章样式无 underline dotted、无 .dict-looked 规则;模板无 dict-looked 类绑定', () => {
    const scss = readSrc('ai-practice.component.scss');
    expect(scss).not.toContain('underline dotted');
    expect(scss).not.toContain('.dict-looked');
    expect(readSrc('ai-practice.component.html')).not.toContain('dict-looked');
  });
});

describe('TJ-5(S-2②:无录音词条 → 无播放钮 + 「暂无发音」)', () => {
  it('dict.noAudio 三语齐备且逐字合规格 §10', () => {
    const i18n = readSrc('../../core/i18n/i18n.service.ts');
    expect(i18n).toContain(
      "'dict.noAudio': { zh: '暂无发音', en: 'No pronunciation available', fr: 'Aucune prononciation disponible' }");
  });

  it('卡契约:两口音皆不可用时渲染 dict.noAudio 文案行', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card).toContain('@if (!cardHasAnyAudio(c))');
    expect(card).toContain("t('dict.noAudio')");
  });
});

describe('TJ-6(S-2③:负卡无任何发音呈现)', () => {
  it('卡契约:notfound 块只有词头与无词条文案,无播放钮、无重试、无发音区', () => {
    const card = readSrc('dictionary-card.component.ts');
    const block = card.slice(card.indexOf("@case ('notfound')"), card.indexOf("@case ('ok')"));
    expect(block).toContain("t('dict.noEntry')");
    expect(block).not.toContain('playRequested');
    expect(block).not.toContain('dict-play-btn');
    expect(block).not.toContain('dict.retry');
    expect(block).not.toContain('dict.speakWord');
  });

  it('词典三文件无浏览器朗读 API 调用(§8.1 grep 级断言的产品面)', () => {
    for (const f of ['dictionary-card.component.ts', 'practice-dictionary.ts', 'dictionary.helpers.ts']) {
      expect(readSrc(f)).not.toContain('speechSynthesis');
    }
  });
});

describe('F-126-R1(复测缺陷:popup 双侧不足高度钳制,规格 §6.2 末条)', () => {
  // 复测 r08 的实测几何:视口高 560、末行词顶 308/词底 ≈318、卡高 392(=70vh 上限)。
  const defect = { viewportHeight: 560, anchorTop: 308, anchorBottom: 318, naturalHeight: 500 };

  it('缺陷原形(无钳制):只翻转不钳高 → 卡顶 = 308 − 8 − 392 = −92,词头/Close 溢出视口上方', () => {
    // 这行算术即复测实测值 −92:翻转与间距都对,缺的只是高度钳制。
    expect(defect.anchorTop - 8 - 392).toBe(-92);
  });

  it('双侧不足(上 300 > 下 234)→ 取较大侧上方,卡高钳至 300,卡顶 ≥ 0 且卡底 ≤ 视口高', () => {
    const g = dictPopupGeometry(defect);
    expect(g.side).toBe('above');
    expect(g.sideSpace).toBe(300);
    expect(g.vhCap).toBe(392);
    expect(g.clamped).toBe(true);
    expect(g.maxHeight).toBe(300);
    expect(g.cardTop).toBe(0);
    expect(g.cardBottom).toBe(300);
    expect(g.cardTop).toBeGreaterThanOrEqual(0);
    expect(g.cardBottom).toBeLessThanOrEqual(defect.viewportHeight);
  });

  it('词头与 Close 在视口内可达:二者都在卡顶起始段(词头行/Close 顶距 10px),卡顶 ≥ 0 即整段在视口内', () => {
    const g = dictPopupGeometry(defect);
    // 卡内布局:词头行自卡顶起(14px padding + 22px 词头),Close 钮 absolute top:10px——
    // 均落在卡顶 + 48px 以内;卡顶 ≥ 0 且该段底 ≤ 视口高 ⇒ 二者可见可点。
    expect(g.cardTop).toBeGreaterThanOrEqual(0);
    expect(g.cardTop + 48).toBeLessThanOrEqual(defect.viewportHeight);
  });

  it('宿主定侧模式(side=above)与自选侧同钳制(CDK 落位后宿主按实测侧落值)', () => {
    const g = dictPopupGeometry({ ...defect, side: 'above' });
    expect(g.maxHeight).toBe(300);
    expect(g.cardTop).toBe(0);
    expect(g.cardBottom).toBe(300);
  });

  it('双侧不足但下方较大 → 取下方钳高,卡底恰抵视口底缘、不溢出', () => {
    const g = dictPopupGeometry({ viewportHeight: 560, anchorTop: 200, anchorBottom: 210, naturalHeight: 500 });
    expect(g.side).toBe('below');
    expect(g.sideSpace).toBe(342);
    expect(g.maxHeight).toBe(342);
    expect(g.cardTop).toBe(218);
    expect(g.cardBottom).toBe(560);
    expect(g.cardBottom).toBeLessThanOrEqual(560);
  });

  it('单侧充足不钳制:下方装得下整卡 → 下方位、maxHeight 回 70vh 上限、clamped=false', () => {
    const g = dictPopupGeometry({ viewportHeight: 900, anchorTop: 100, anchorBottom: 118, naturalHeight: 500 });
    expect(g.side).toBe('below');
    expect(g.clamped).toBe(false);
    expect(g.maxHeight).toBe(630);
    expect(g.renderedHeight).toBe(500);
    expect(g.cardTop).toBe(126);
    expect(g.cardBottom).toBe(626);
  });

  it('下方不足、上方装得下 → 翻转上方且不钳高(既有翻转行为不回归)', () => {
    const g = dictPopupGeometry({ viewportHeight: 560, anchorTop: 400, anchorBottom: 418, naturalHeight: 350 });
    expect(g.side).toBe('above');
    expect(g.clamped).toBe(false);
    expect(g.renderedHeight).toBe(350);
    expect(g.cardBottom).toBe(392); // 词顶 − 8
    expect(g.cardTop).toBe(42);
  });

  it('宿主契约:落位收尾走 dictPopupGeometry 钳高(inline max-height),开卡/落定/滚动三路都接', () => {
    const ts = readSrc('ai-practice.component.ts');
    expect(ts).toContain('dictPopupGeometry({');
    expect(ts).toContain('applyDictPopupClamp');
    expect(ts).toContain("querySelector<HTMLElement>('.dict-card')");
    expect(ts).toContain('card.scrollHeight');
    expect(ts).toContain('card.style.maxHeight');
    // 滚动路径:updatePosition 后同步重算钳高(锚点位置变 → 可用空间变)
    const scrollBlock = ts.slice(ts.indexOf('dictScrollHandler'));
    expect(scrollBlock).toContain('applyDictPopupClamp()');
  });
});

describe('TJ-7/M-3(F-4:口音记忆 + UK 无录音 tab 禁用标注)', () => {
  it('卡契约:口音初值读记忆、切换落记忆、无换词复位', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card).toContain('loadDictAccent()');
    expect(card).toContain('saveDictAccent');
    expect(card).not.toContain("accent.set('us')");
  });

  it('卡契约:音标行序跟随当前口音(当前口音行在前 —— §6.8 首焦点口径的地基)', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card).toContain("this.accent() === 'uk' ? ['uk', 'us'] : ['us', 'uk']");
  });

  it('M-3:无录音口音的 tab 带 [disabled] 与 noAudioLabel 标注(title + aria-label)', () => {
    const card = readSrc('dictionary-card.component.ts');
    expect(card).toContain('[disabled]="!tabEnabled(c, acc)"');
    expect(card).toContain('noAudioLabel(acc)');
  });
});
