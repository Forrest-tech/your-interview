/**
 * 票127 前端验收(TJ-1…TJ-8;TJ-8 为 F-127-R1 复测缺陷的焦点接续断言)。
 *
 * 基建口径(同 dictionary-ticket126.spec):本仓 Jest = node 环境 + @angular/* 替身,
 * 无 TestBed / DOM 渲染。被测面:
 *   - 跟读会话 DictionaryShadowSession(真实类,依赖以结构替身注入:pronounce/fetchAudio/
 *     recorder 接缝/播放器/objectURL)—— 状态机、迟到丢弃、失败分型全部真跑;
 *   - 纯函数 pickShadowTarget / shadowResultView / isShadowNotRecognized / shadowErrorLabelKey;
 *   - RecorderService.assessShadowRecording(TJ-3:真实类 + 替身 ApiClient 捕获请求体,
 *     decodeToPcm 以实例补丁替身 —— 断言的是请求构造本身:端点/字段集/16k WAV 头,
 *     与 grade() 的同构点逐项对齐);
 *   - DOM/模板/键盘分层以源码契约断言落码(同 126 先例),真实浏览器行为由活体 E1–E6 兜底。
 */
import { readFileSync } from 'fs';
import { join } from 'path';
import { describe, expect, it, jest } from '@jest/globals';
import { Observable, Subject, of, throwError } from 'rxjs';
import * as ngCore from '@angular/core';
import type { DictCardData } from './dictionary.helpers';
import {
  DictionaryShadowSession, DictShadowPhase, ShadowAudioPlayer, isShadowNotRecognized,
  pickShadowTarget, shadowErrorLabelKey, shadowResultView, shadowSettleFocusSelectors,
} from './dictionary-shadow';
import { RecorderService, ShadowAssessOutcome, ShadowRecordingTake } from '../../core/recorder/recorder.service';
import type { DictionaryPronounceDto } from '../../core/api/practice-api.service';

const flush = async (rounds = 8): Promise<void> => {
  for (let i = 0; i < rounds; i++) await new Promise<void>((r) => setTimeout(r, 0));
};

// ---------- 夹具 ----------

function cardDto(lemma: string, us: { available: boolean; kind?: string | null; key?: string | null }): DictCardData {
  return {
    query: lemma,
    lemma,
    matchedVia: 'exact',
    tier: 'open',
    phonetics: { us: '/x/', uk: null },
    posBlocks: [],
    forms: [],
    audio: {
      us: { available: us.available, kind: (us.kind ?? null) as never, key: us.key ?? null },
      uk: { available: false, kind: null, key: null },
    },
    attribution: { source: 'test', audioSource: null },
  };
}

class RecorderStub {
  startCalls = 0;
  cancelCalls = 0;
  assessCalls: { blob: Blob; referenceText: string }[] = [];
  startStatus: 'started' | 'mic-denied' | 'unavailable' = 'started';
  assessOutcome: ShadowAssessOutcome = { kind: 'unavailable' };
  assessImpl: (() => Promise<ShadowAssessOutcome>) | null = null;
  private doneResolve: ((t: ShadowRecordingTake | null) => void) | null = null;

  async startShadowRecording(): Promise<
    | { status: 'started'; done: Promise<ShadowRecordingTake | null> }
    | { status: 'mic-denied' | 'unavailable' }> {
    this.startCalls++;
    if (this.startStatus !== 'started') return { status: this.startStatus };
    const done = new Promise<ShadowRecordingTake | null>((res) => { this.doneResolve = res; });
    return { status: 'started', done };
  }
  stopShadowRecording(): void { /* 完成由 finishRecording 驱动 */ }
  cancelShadowRecording(): void {
    this.cancelCalls++;
    this.doneResolve?.(null);
    this.doneResolve = null;
  }
  finishRecording(take: ShadowRecordingTake | null): void {
    this.doneResolve?.(take);
    this.doneResolve = null;
  }
  async assessShadowRecording(blob: Blob, referenceText: string): Promise<ShadowAssessOutcome> {
    this.assessCalls.push({ blob, referenceText });
    return this.assessImpl ? this.assessImpl() : this.assessOutcome;
  }
}

class PlayerStub implements ShadowAudioPlayer {
  played: string[] = [];
  onEnded: (() => void) | null = null;
  onError: (() => void) | null = null;
  play(src: string): void { this.played.push(src); }
  stop(): void { /* 替身 */ }
  end(): void { this.onEnded?.(); }
  fail(): void { this.onError?.(); }
}

interface SessionRig {
  session: DictionaryShadowSession;
  recorder: RecorderStub;
  player: PlayerStub;
  revoked: string[];
}

function makeSession(opts: {
  pronounce?: (w: string) => Observable<DictionaryPronounceDto>;
  fetchAudio?: (k: string) => Observable<Blob>;
} = {}): SessionRig {
  const recorder = new RecorderStub();
  const player = new PlayerStub();
  const revoked: string[] = [];
  let urlSeq = 0;
  const session = new DictionaryShadowSession({
    pronounce: opts.pronounce
      ?? (() => of({ available: true, kind: 'commons', key: 'k-us' } as DictionaryPronounceDto)),
    fetchAudio: opts.fetchAudio ?? (() => of(new Blob(['audio']))),
    recorder: recorder as never,
    createPlayer: () => player,
    createObjectUrl: () => `blob:fake-${++urlSeq}`,
    revokeObjectUrl: (u) => { revoked.push(u); },
  });
  return { session, recorder, player, revoked };
}

/** 开卡到 idle 的标准路径(默认 pronounce 命中 → 目标 = 所点词形)。 */
async function openToIdle(rig: SessionRig, form = 'disappeared', card?: DictCardData): Promise<void> {
  rig.session.begin(form);
  rig.session.attachCard(card ?? cardDto('disappear', { available: true, kind: 'commons', key: 'k-lemma' }));
  await flush();
}

const take = (durationMs = 1500): ShadowRecordingTake =>
  ({ blob: new Blob(['recorded-audio']), durationMs });

const scoreFixture = (over: Partial<Extract<ShadowAssessOutcome, { kind: 'score' }>['score']> = {}):
  Extract<ShadowAssessOutcome, { kind: 'score' }> => ({
    kind: 'score',
    score: {
      pronScore: 87.6, accuracyScore: 90.2, fluencyScore: null, completenessScore: 100,
      prosodyScore: null, recognized: 'disappeared',
      words: [{ word: 'disappeared', accuracy: 91, errorType: 'None' }],
      billedSeconds: 1.2, billedBytes: 38400,
      ...over,
    },
  });

// ---------- TJ-1 入口可见性 ----------

describe('TJ-1(入口可见性:有美音真人录音才出现)', () => {
  it('US 有录音 → 跟读区在席、phase=idle', async () => {
    const rig = makeSession();
    await openToIdle(rig);
    expect(rig.session.view()?.phase).toBe('idle');
    expect(rig.session.view()?.targetText).toBe('disappeared');
  });

  it('仅 UK 有录音(pronounce US 未命中、词元 US 不可用)→ 区不渲染', async () => {
    const rig = makeSession({ pronounce: () => of({ available: false } as DictionaryPronounceDto) });
    await openToIdle(rig, 'disappeared', cardDto('disappear', { available: false }));
    expect(rig.session.view()).toBeNull();
  });

  it('两口音皆无录音 → 区不渲染', async () => {
    const rig = makeSession({ pronounce: () => of({ available: false } as DictionaryPronounceDto) });
    await openToIdle(rig, 'zorblax', cardDto('zorblax', { available: false }));
    expect(rig.session.view()).toBeNull();
  });

  it('负卡态 → 即使 pronounce 命中也不渲染', async () => {
    const rig = makeSession();
    rig.session.begin('zorblax');
    rig.session.attachNoCard();
    await flush();
    expect(rig.session.view()).toBeNull();
  });

  it('解析完成前不渲染入口形态(不闪出再消失,§5.2.5)', async () => {
    const gate = new Subject<DictionaryPronounceDto>();
    const rig = makeSession({ pronounce: () => gate.asObservable() });
    rig.session.begin('disappeared');
    rig.session.attachCard(cardDto('disappear', { available: true, kind: 'commons', key: 'k-lemma' }));
    await flush();
    expect(rig.session.view()).toBeNull();
    gate.next({ available: true, kind: 'commons', key: 'k-form' } as DictionaryPronounceDto);
    await flush();
    expect(rig.session.view()?.phase).toBe('idle');
  });
});

// ---------- TJ-2 目标解析 ----------

describe('TJ-2(目标词形解析 D1)', () => {
  it('纯函数:词形命中 → 目标 = 词形', () => {
    const pick = pickShadowTarget({
      queriedForm: 'disappeared',
      pronounce: { status: 'hit', key: 'k-form' },
      card: cardDto('disappear', { available: true, kind: 'commons', key: 'k-lemma' }),
    });
    expect(pick).toEqual({ kind: 'target', text: 'disappeared', key: 'k-form' });
  });

  it('纯函数:词形未命中、词元 US 可用 → 目标 = 词元', () => {
    const pick = pickShadowTarget({
      queriedForm: 'disappeared',
      pronounce: { status: 'miss' },
      card: cardDto('disappear', { available: true, kind: 'oxford', key: 'k-lemma' }),
    });
    expect(pick).toEqual({ kind: 'target', text: 'disappear', key: 'k-lemma' });
  });

  it('纯函数:词形未命中、词元 US 不可用 → none;解析失败 → failed(不混同)', () => {
    expect(pickShadowTarget({
      queriedForm: 'disappeared',
      pronounce: { status: 'miss' },
      card: cardDto('disappear', { available: false }),
    })).toEqual({ kind: 'none' });
    expect(pickShadowTarget({
      queriedForm: 'disappeared',
      pronounce: { status: 'failed' },
      card: cardDto('disappear', { available: true, kind: 'commons', key: 'k' }),
    })).toEqual({ kind: 'failed' });
  });

  it('会话:回落词元时入口如实显示词元文本,送评 referenceText = 词元', async () => {
    const rig = makeSession({ pronounce: () => of({ available: false } as DictionaryPronounceDto) });
    rig.recorder.assessOutcome = scoreFixture({ recognized: 'disappear' });
    await openToIdle(rig);
    expect(rig.session.view()?.targetText).toBe('disappear');
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    expect(rig.session.view()?.phase).toBe('recorded');
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.recorder.assessCalls.map((c) => c.referenceText)).toEqual(['disappear']);
  });

  it('会话:词形命中时送评 referenceText = 所点词形', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = scoreFixture();
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.recorder.assessCalls.map((c) => c.referenceText)).toEqual(['disappeared']);
    expect(rig.session.view()?.phase).toBe('result');
  });
});

// ---------- TJ-3 请求同构 ----------

describe('TJ-3(评分请求与 grade 同构 —— 真实 RecorderService + 捕获请求体)', () => {
  function realRecorder(postImpl: (url: string, body: unknown) => Observable<unknown>): RecorderService {
    const fakeDeps = {
      post: (url: string, body: unknown) => postImpl(url, body),
      run: (fn: () => void) => fn(),
    };
    (ngCore as unknown as { inject: unknown }).inject = () => fakeDeps;
    const svc = new RecorderService();
    // 解码替身:48k Float32 输入(评分音频打包的真实输入形态);被测的是打包与请求构造。
    (svc as unknown as { decodeToPcm: unknown }).decodeToPcm =
      async () => ({ data: new Float32Array([0.1, -0.2, 0.3, 0.05]), rate: 48000 });
    return svc;
  }

  it('端点/字段集/language 恒 en-US/wavBase64 为 16k 单声道 WAV', async () => {
    const seen: { url: string; body: Record<string, unknown> }[] = [];
    const svc = realRecorder((url, body) => {
      seen.push({ url, body: body as Record<string, unknown> });
      return of({
        pronScore: 80, accuracyScore: 81, fluencyScore: null, completenessScore: 90,
        prosodyScore: null, recognized: 'disappeared',
        words: [{ word: 'disappeared', accuracy: 82, errorType: 'None' }],
        billedSeconds: 1, billedBytes: 100,
      });
    });
    const outcome = await svc.assessShadowRecording(new Blob(['x']), 'disappeared');
    expect(outcome.kind).toBe('score');
    expect(seen).toHaveLength(1);
    expect(seen[0].url).toBe('/api/assessment/pronunciation/assess');
    expect(Object.keys(seen[0].body).sort()).toEqual(['language', 'referenceText', 'wavBase64']);
    expect(seen[0].body['language']).toBe('en-US');
    expect(seen[0].body['referenceText']).toBe('disappeared');
    const wav = Buffer.from(String(seen[0].body['wavBase64']), 'base64');
    expect(wav.subarray(0, 4).toString('ascii')).toBe('RIFF');
    expect(wav.subarray(8, 12).toString('ascii')).toBe('WAVE');
    expect(wav.readUInt16LE(22)).toBe(1);       // 单声道
    expect(wav.readUInt32LE(24)).toBe(16000);   // 16k 采样率
    if (outcome.kind === 'score') {
      expect(outcome.score.recognized).toBe('disappeared');
      expect(outcome.score.fluencyScore).toBeNull();  // null 保留,不填 0
    }
  });

  it('503 → unavailable;502 → failed(分型供 F-S2/F-S3)', async () => {
    const svc503 = realRecorder(() => throwError(() => ({ status: 503 })));
    expect(await svc503.assessShadowRecording(new Blob(['x']), 'the'))
      .toEqual({ kind: 'unavailable' });
    const svc502 = realRecorder(() => throwError(() => ({ status: 502 })));
    expect(await svc502.assessShadowRecording(new Blob(['x']), 'the'))
      .toEqual({ kind: 'failed', status: 502 });
  });

  it('源码同构:grade 与跟读接缝共用同一端点字面量与字段集', () => {
    const src = readFileSync(join(__dirname, '../../core/recorder/recorder.service.ts'), 'utf8');
    const endpointHits = src.split("'/api/assessment/pronunciation/assess'").length - 1;
    expect(endpointHits).toBe(2); // grade 一处 + assessShadowRecording 一处,不许第三处分叉
    expect(src).toContain('assessShadowRecording');
    expect(src).not.toMatch(/assessShadowRecording[\s\S]{0,400}persistScore/);
  });
});

// ---------- TJ-4 状态机与 teardown ----------

describe('TJ-4(状态机全程 + teardown 清理)', () => {
  it('主干:idle → recording → recorded → scoring → result → again → idle(录音已清)', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = scoreFixture();
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    expect(rig.session.view()?.phase).toBe('recording');
    rig.session.action({ type: 'stop' });
    rig.recorder.finishRecording(take());
    await flush();
    expect(rig.session.view()?.phase).toBe('recorded');
    expect(rig.session.view()?.hasRecording).toBe(true);
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.session.view()?.phase).toBe('result');
    rig.session.action({ type: 'again' });
    expect(rig.session.view()?.phase).toBe('idle');
    expect(rig.session.view()?.hasRecording).toBe(false);
    expect(rig.session.view()?.result).toBeNull();
  });

  it('scoring 途中 teardown → 迟到评分不得写入(§6.5)', async () => {
    const rig = makeSession();
    await openToIdle(rig);
    let resolveAssess!: (o: ShadowAssessOutcome) => void;
    rig.recorder.assessImpl = () => new Promise<ShadowAssessOutcome>((res) => { resolveAssess = res; });
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.session.view()?.phase).toBe('scoring');
    rig.session.teardown();
    expect(rig.session.view()).toBeNull();
    resolveAssess(scoreFixture());
    await flush();
    expect(rig.session.view()).toBeNull();
  });

  it('目标解析在途时换词 → 迟到解析不得改动新卡目标文本与显隐(§6.5)', async () => {
    const gates = new Map<string, Subject<DictionaryPronounceDto>>();
    const rig = makeSession({
      pronounce: (w) => {
        const s = new Subject<DictionaryPronounceDto>();
        gates.set(w, s);
        return s.asObservable();
      },
    });
    rig.session.begin('disappeared');       // 第一词解析挂起
    rig.session.begin('the');               // 换词(teardown + 新解析)
    rig.session.attachCard(cardDto('the', { available: true, kind: 'commons', key: 'k-the' }));
    gates.get('the')!.next({ available: true, kind: 'commons', key: 'k-the' } as DictionaryPronounceDto);
    await flush();
    expect(rig.session.view()?.targetText).toBe('the');
    // 第一词的迟到响应到达 —— 不得回写
    gates.get('disappeared')!.next({ available: true, kind: 'commons', key: 'k-form' } as DictionaryPronounceDto);
    await flush();
    expect(rig.session.view()?.targetText).toBe('the');
    expect(rig.session.view()?.phase).toBe('idle');
  });

  it('关卡释放 object URL(本人录音 + 原声),重开同词必 idle 无残留', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = scoreFixture();
    await openToIdle(rig);
    rig.session.action({ type: 'playOriginal' });
    await flush();
    expect(rig.player.played).toEqual(['blob:fake-1']);
    rig.player.end();
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'playMine' });
    expect(rig.player.played).toEqual(['blob:fake-1', 'blob:fake-2']);
    rig.session.teardown();
    expect(rig.revoked).toEqual(expect.arrayContaining(['blob:fake-1', 'blob:fake-2']));
    // 重开同词
    await openToIdle(rig);
    expect(rig.session.view()?.phase).toBe('idle');
    expect(rig.session.view()?.hasRecording).toBe(false);
    expect(rig.session.view()?.result).toBeNull();
  });

  it('录音中 teardown → recorder.cancel 被调、done 的 null 兑现不回写状态', async () => {
    const rig = makeSession();
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    expect(rig.session.isRecording).toBe(true);
    rig.session.teardown();
    await flush();
    expect(rig.recorder.cancelCalls).toBe(1);
    expect(rig.session.view()).toBeNull();
  });

  it('重录弃旧时机:起录失败(mic-denied)旧录音保留;采集真正开始才弃旧(§6.5 注)', async () => {
    const rig = makeSession();
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    expect(rig.session.view()?.hasRecording).toBe(true);
    // 重录但权限被拒 → 旧录音还在
    rig.recorder.startStatus = 'mic-denied';
    rig.session.action({ type: 'reRecord' });
    await flush();
    expect(rig.session.view()?.errorKind).toBe('micDenied');
    expect(rig.session.view()?.hasRecording).toBe(true);
    // 权限恢复 → 采集开始 → 旧录音弃、新段接管
    rig.recorder.startStatus = 'started';
    rig.session.action({ type: 'record' });
    await flush();
    expect(rig.session.view()?.phase).toBe('recording');
    rig.recorder.finishRecording(take(2000));
    await flush();
    expect(rig.session.view()?.phase).toBe('recorded');
  });
});

// ---------- TJ-5 结果呈现 ----------

describe('TJ-5(结果呈现 §7.3)', () => {
  const full = scoreFixture().score;

  it('全维度:总分归一 round(pronScore)、分项全在席、逐词只取目标词', () => {
    const v = shadowResultView({
      ...full, fluencyScore: 70.4, prosodyScore: 60.6,
      words: [
        { word: 'disappeared', accuracy: 91, errorType: 'None' },
        { word: 'the', accuracy: 50, errorType: 'Omission' },
      ],
    }, 'disappeared');
    expect(v.total).toBe(88);
    expect(v.dims.map((d) => d.labelKey)).toEqual(['dim.accuracy', 'dim.fluency', 'dim.completeness', 'dim.prosody']);
    expect(v.dims.map((d) => d.score)).toEqual([90, 70, 100, 61]);
    expect(v.recognized).toBe('disappeared');
    expect(v.words).toEqual([{ word: 'disappeared', accuracy: 91, errorType: 'None' }]);
    expect(v.billedSeconds).toBe(1.2);
  });

  it('null 分项不渲染(不填 0、不编造)', () => {
    const v = shadowResultView(full, 'disappeared'); // fluency/prosody 为 null
    expect(v.dims.map((d) => d.labelKey)).toEqual(['dim.accuracy', 'dim.completeness']);
  });

  it('F-S4 边界:recognized 非空但 words 空 → 走 result、逐词行缺席;双空 → notRecognized 型', () => {
    const v = shadowResultView({ ...full, recognized: 'disappeared', words: [] }, 'disappeared');
    expect(v.words).toEqual([]);
    expect(isShadowNotRecognized({ ...full, recognized: 'disappeared', words: [] })).toBe(false);
    expect(isShadowNotRecognized({ ...full, recognized: '', words: [] })).toBe(true);
    expect(isShadowNotRecognized({ ...full, recognized: ' ', words: [] })).toBe(true);
    expect(isShadowNotRecognized(full)).toBe(false);
  });

  it('errorType 文案键与现有报告 err.* 映射同表', () => {
    expect(shadowErrorLabelKey('Mispronunciation')).toBe('err.mispron');
    expect(shadowErrorLabelKey('Omission')).toBe('err.omission');
    expect(shadowErrorLabelKey('Insertion')).toBe('err.insertion');
    expect(shadowErrorLabelKey('UnexpectedBreak')).toBe('err.unexpectedBreak');
    expect(shadowErrorLabelKey('MissingBreak')).toBe('err.missingBreak');
    expect(shadowErrorLabelKey('')).toBe('err.noData');
    expect(shadowErrorLabelKey('None')).toBe('err.ok');
  });
});

// ---------- TJ-6 失败态呈现 ----------

describe('TJ-6(失败态 F-S1…F-S7 逐型)', () => {
  it('F-S1 麦克风被拒:不进 recording,error/micDenied;重试恢复', async () => {
    const rig = makeSession();
    rig.recorder.startStatus = 'mic-denied';
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    expect(rig.session.view()?.phase).toBe('error');
    expect(rig.session.view()?.errorKind).toBe('micDenied');
    rig.recorder.startStatus = 'started';
    rig.session.action({ type: 'record' });
    await flush();
    expect(rig.session.view()?.phase).toBe('recording');
    rig.session.teardown();
  });

  it('F-S2 assess 503:录音保留、评分可直接重试成功', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = { kind: 'unavailable' };
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.session.view()?.phase).toBe('error');
    expect(rig.session.view()?.errorKind).toBe('scoringUnavailable');
    expect(rig.session.view()?.hasRecording).toBe(true);
    rig.recorder.assessOutcome = scoreFixture();
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.session.view()?.phase).toBe('result');
    expect(rig.recorder.assessCalls).toHaveLength(2);
    expect(rig.recorder.assessCalls[0].blob).toBe(rig.recorder.assessCalls[1].blob); // 同段录音重评,不重录
  });

  it('F-S3 assess 502:error/scoringFailed,不显示分数', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = { kind: 'failed', status: 502 };
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.session.view()?.errorKind).toBe('scoringFailed');
    expect(rig.session.view()?.result).toBeNull();
  });

  it('F-S4 空识别:error/notRecognized,不显示分数', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = scoreFixture({ recognized: '', words: [] });
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'score' });
    await flush();
    expect(rig.session.view()?.errorKind).toBe('notRecognized');
    expect(rig.session.view()?.result).toBeNull();
  });

  it('F-S5 原声取流失败:error/originalFailed 且录音被禁', async () => {
    const rig = makeSession({ fetchAudio: () => throwError(() => new Error('404')) });
    await openToIdle(rig);
    rig.session.action({ type: 'playOriginal' });
    await flush();
    expect(rig.session.view()?.phase).toBe('error');
    expect(rig.session.view()?.errorKind).toBe('originalFailed');
    expect(rig.session.view()?.originalFailed).toBe(true);
    rig.session.action({ type: 'record' });
    await flush();
    expect(rig.recorder.startCalls).toBe(0);
  });

  it('F-S6 空录音(<300ms):丢弃回 idle + tooShort 提示,不送评', async () => {
    const rig = makeSession();
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take(200));
    await flush();
    expect(rig.session.view()?.phase).toBe('idle');
    expect(rig.session.view()?.notice).toBe('tooShort');
    expect(rig.session.view()?.hasRecording).toBe(false);
    expect(rig.recorder.assessCalls).toHaveLength(0);
  });

  it('F-S7 目标解析失败:resolveFailed 在席(不静默消失),重试解析恢复', async () => {
    let fail = true;
    const rig = makeSession({
      pronounce: () => fail
        ? throwError(() => new Error('net'))
        : of({ available: true, kind: 'commons', key: 'k-us' } as DictionaryPronounceDto),
    });
    rig.session.begin('disappeared');
    rig.session.attachCard(cardDto('disappear', { available: true, kind: 'commons', key: 'k-lemma' }));
    await flush();
    expect(rig.session.view()?.phase).toBe('resolveFailed');
    fail = false;
    rig.session.action({ type: 'retryResolve' });
    await flush();
    expect(rig.session.view()?.phase).toBe('idle');
  });

  it('对比回放互斥:播原声毕回原状态;播我的录音与原声共用单播放器', async () => {
    const rig = makeSession();
    rig.recorder.assessOutcome = scoreFixture();
    await openToIdle(rig);
    rig.session.action({ type: 'record' });
    await flush();
    rig.recorder.finishRecording(take());
    await flush();
    rig.session.action({ type: 'playOriginal' });
    await flush();
    expect(rig.session.view()?.phase).toBe('playing');
    expect(rig.session.view()?.playingWhich).toBe('original');
    rig.player.end();
    expect(rig.session.view()?.phase).toBe('recorded');
    rig.session.action({ type: 'playMine' });
    expect(rig.session.view()?.playingWhich).toBe('mine');
    rig.player.end();
    expect(rig.session.view()?.phase).toBe('recorded');
  });
});

// ---------- TJ-7 键盘/焦点衔接(源码契约;真实键盘行为由 E6 兜底) ----------

describe('TJ-7(键盘/焦点/Esc 分层 —— 源码契约)', () => {
  const card = readFileSync(join(__dirname, 'dictionary-card.component.ts'), 'utf8');
  const dict = readFileSync(join(__dirname, 'practice-dictionary.ts'), 'utf8');

  it('卡内 Tab 序:跟读区位于发音行之后、词性组之前,Close 仍在 DOM 末位', () => {
    const okStart = card.indexOf("@case ('ok')");
    const okBlock = card.slice(okStart);
    const iPhon = okBlock.indexOf('dict-phon-row');
    const iShadow = okBlock.indexOf('dict-shadow');
    const iPos = okBlock.indexOf('dict-pos-block');
    const iClose = okBlock.lastIndexOf('dict-close');
    expect(iPhon).toBeGreaterThan(-1);
    expect(iShadow).toBeGreaterThan(iPhon);
    expect(iPos).toBeGreaterThan(iShadow);
    expect(iClose).toBeGreaterThan(iPos);
  });

  it('Esc 分层:录音态 Esc 先停录(发 stop 动作),不发 closeRequested', () => {
    const kd = card.slice(card.indexOf('protected onCardKeydown'));
    const iGuard = kd.indexOf("this.shadow?.phase === 'recording'");
    const iStop = kd.indexOf("{ type: 'stop' }");
    const iClose = kd.indexOf('closeRequested.emit()');
    expect(iGuard).toBeGreaterThan(-1);
    expect(iStop).toBeGreaterThan(iGuard);
    expect(iClose).toBeGreaterThan(iStop);
  });

  it('录音态 Close = 关卡路径内先 teardown(停录丢弃)再关', () => {
    const close = dict.slice(dict.indexOf('close(returnFocus: boolean)'));
    expect(close.indexOf('this.shadow.teardown()')).toBeGreaterThan(-1);
    expect(close.indexOf('this.shadow.teardown()')).toBeLessThan(close.indexOf('closeDictPopup()'));
  });

  it('§9.4 手势守卫:点外关闭与点词换词都先查 isRecording', () => {
    expect(dict).toContain('if (this.shadow.isRecording) return;');
    const docClick = dict.slice(dict.indexOf('docClickHandler'), dict.indexOf('readonly tokens'));
    expect(docClick).toContain('isRecording');
    const wordClick = dict.slice(dict.indexOf('onWordClick'), dict.indexOf('async open('));
    expect(wordClick).toContain('isRecording');
  });

  it('模板接线:shadow 视图与动作都绑到卡上', () => {
    const html = readFileSync(join(__dirname, 'ai-practice.component.html'), 'utf8');
    expect(html).toContain('[shadow]="dict.shadow.view()"');
    expect(html).toContain('(shadowAction)="dict.shadowAction($event)"');
  });

  it('i18n:dict.shadow.* 18 键三语齐全', () => {
    const i18n = readFileSync(join(__dirname, '../../core/i18n/i18n.service.ts'), 'utf8');
    const keys = [
      'title', 'playOriginal', 'record', 'recording', 'stop', 'score', 'scoring', 'reRecord',
      'again', 'myRecording', 'original', 'micDenied', 'scoringUnavailable', 'scoringFailed',
      'notRecognized', 'originalFailed', 'resolveFailed', 'tooShort',
    ];
    expect(keys).toHaveLength(18);
    for (const k of keys) {
      const line = i18n.split('\n').find((l) => l.includes(`'dict.shadow.${k}'`));
      if (!line) throw new Error(`dict.shadow.${k} 缺失`);
      expect(line).toBeTruthy();
      expect(line).toContain('zh:');
      expect(line).toContain('en:');
      expect(line).toContain('fr:');
    }
  });

  it('机器音禁令不回退:词典三文件内无浏览器朗读 API 调用', () => {
    for (const src of [card, dict, readFileSync(join(__dirname, 'dictionary-shadow.ts'), 'utf8')]) {
      expect(src).not.toContain('speechSynthesis');
    }
  });
});

// ---------- TJ-8 F-127-R1 评分 settle 焦点接续(纯函数 + 源码契约;真实键盘行为由活体兜底) ----------

describe('TJ-8(F-127-R1 评分 settle 焦点接续)', () => {
  const card = readFileSync(join(__dirname, 'dictionary-card.component.ts'), 'utf8');

  it('失败路径(F-S2/F-S3):error 态继任钮优先序 = 评分重试钮 → 重录 → 解析重试', () => {
    expect(shadowSettleFocusSelectors('error')).toEqual([
      '[data-shadow-score]', '[data-shadow-rerecord]', '[data-shadow-retry]',
    ]);
  });

  it('成功路径(result):继任钮优先序 = 再练一次 → 我的录音 → 重录', () => {
    expect(shadowSettleFocusSelectors('result')).toEqual([
      '[data-shadow-again]', '[data-shadow-play-mine]', '[data-shadow-rerecord]',
    ]);
  });

  it('其余相位不接续(空表):接续只发生在评分 settle 这一跳', () => {
    const others: DictShadowPhase[] = [
      'idle', 'playing', 'recording', 'recorded', 'scoring', 'resolveFailed',
    ];
    for (const p of others) expect(shadowSettleFocusSelectors(p)).toEqual([]);
  });

  it('源码契约:进入 scoring 时捕获「焦点在卡内」,settle 到 error/result 才接续', () => {
    const hoc = card.slice(card.indexOf("if (changes['shadow'])"), card.indexOf('private maybeFocusFirst'));
    expect(hoc).toContain("cur?.phase === 'scoring' && prev?.phase !== 'scoring'");
    expect(hoc).toContain('this.shadowFocusAfterScore = !!root && root.contains(document.activeElement)');
    expect(hoc).toContain("prev?.phase === 'scoring' && cur?.phase !== 'scoring'");
    expect(hoc).toContain("cur?.phase === 'error' || cur?.phase === 'result'");
    expect(hoc).toContain('shadowSettleFocusSelectors(phase)');
  });

  it('源码契约:settle 接续不抢焦点——焦点已在卡内时直接返回', () => {
    const hoc = card.slice(card.indexOf('prev?.phase === \'scoring\''), card.indexOf('private maybeFocusFirst'));
    expect(hoc).toContain('if (active && active !== document.body && root.contains(active)) return;');
  });

  it('源码契约:result 态「再练一次」钮带 data-shadow-again(接续首选锚点)', () => {
    const iResult = card.indexOf("@case ('result')");
    const resultCase = card.slice(iResult, card.indexOf("@case ('error')", iResult));
    expect(resultCase).toContain('data-shadow-again');
  });
});
