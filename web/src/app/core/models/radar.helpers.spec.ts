/**
 * 票64 ARC-08c 追加 spec —— radar.helpers(票27 REQ-08 + 票88 过滤口径)。
 *
 * 移植来源(两票报告均明示待本票搬入 Jest):
 * - evidence-27/helper-boundary-test.js:21 例(账户级守卫原始断言集);
 * - evidence-88/helper-boundary-test-27-rerun.js:同集复跑 21 例,其中 2 例
 *   编码旧部分有分行为的期望按票88口径更新,行内标注「票88口径变更」——
 *   本 spec 以 rerun 版为准,2 处标注逐字保留;
 * - evidence-88/helper-boundary-test-88.js:25 例(按维度过滤断言集)。
 * 合并后 46 例,逐例 1:1,it 名保留原断言名以便对账。
 * 不适用类别(同票27/88):字符串/Unicode(无字符串入参)、日期时区(无日期
 * 入参)、文件类(无文件入参)。特征测试锁现状,不改被测函数行为。
 */
import { describe, expect, it } from '@jest/globals';
import type { RadarPoint } from './api.models';
import { hasScoredRadarData, isScoredRadarPoint, weakestRadarPoints } from './radar.helpers';

/** 票27 集的点工厂:dimension 恒 'd'(原集口径)。 */
const P = (current: number, best: number | null): RadarPoint =>
  ({ dimension: 'd', nameZh: 'x', current, best, previous: 0, changeFromPrevious: 0 });
/** 票88 集的点工厂:带 dimension 名。 */
const P88 = (dimension: string, current: number, best: number | null): RadarPoint =>
  ({ dimension, nameZh: 'x', current, best, previous: 0, changeFromPrevious: 0 });
/** 线形:后端无快照时 best 字段直接省略,前端读到 undefined。 */
const U = (dimension: string): RadarPoint =>
  ({ dimension, nameZh: 'x', current: 0 } as unknown as RadarPoint);

const zeroNull: RadarPoint[] = Array.from({ length: 6 }, () => P(0, null));
const zeroUndef: RadarPoint[] = Array.from({ length: 6 }, (_, i) => U('d' + i));

describe('票27 断言集(以票88 rerun 版为准,21 例)', () => {
  // [boundary-null/undefined/空集合]
  it('null radar', () => { expect(hasScoredRadarData(null)).toBe(false); });
  it('undefined radar', () => { expect(hasScoredRadarData(undefined)).toBe(false); });
  it('empty radar', () => { expect(hasScoredRadarData([])).toBe(false); });
  it('weakest null', () => { expect(weakestRadarPoints(null)).toEqual([]); });
  it('weakest empty', () => { expect(weakestRadarPoints([])).toEqual([]); });
  // [REQ-08 bug case:全维 current=0 且 best=null]
  it('all zero/null has', () => { expect(hasScoredRadarData(zeroNull)).toBe(false); });
  it('all zero/null weakest', () => { expect(weakestRadarPoints(zeroNull)).toEqual([]); });
  // [线形:best undefined]
  it('all zero/undef has', () => { expect(hasScoredRadarData(zeroUndef)).toBe(false); });
  it('all zero/undef weakest', () => { expect(weakestRadarPoints(zeroUndef)).toEqual([]); });
  // [boundary-0:评 0 分 best=0 算有数据]
  it('scored zero has', () => { expect(hasScoredRadarData([P(0, 0)])).toBe(true); });
  it('scored zero weakest len [票88口径变更:旧期望3含未评分维]', () => {
    expect(weakestRadarPoints([P(0, 0), P(0, null), P(0, null)]).length).toBe(1);
  });
  // [declined:current 退回 0 但 best=50 算有数据]
  it('declined has', () => { expect(hasScoredRadarData([P(0, 50), P(0, null)])).toBe(true); });
  // [boundary-0与负数:负 current 视为已评分信号]
  it('negative current has', () => { expect(hasScoredRadarData([P(-5, null)])).toBe(true); });
  // [部分有分:一维有分]
  const partial: RadarPoint[] = [P(0, null), P(0, null), P(42, 42), P(0, null), P(0, null), P(0, null)];
  it('partial has', () => { expect(hasScoredRadarData(partial)).toBe(true); });
  it('partial weakest currents [票88口径变更:旧期望[0,0,0]未评分维占位]', () => {
    expect(weakestRadarPoints(partial).map(p => p.current)).toEqual([42]);
  });
  // [排序+slice:全有分种子形态]
  const seed: RadarPoint[] = [P(55, 55), P(62, 70), P(48, 48), P(85, 85), P(35, 35), P(90, 90)];
  it('seed weakest currents', () => {
    expect(weakestRadarPoints(seed).map(p => p.current)).toEqual([35, 48, 55]);
  });
  it('seed weakest bests', () => {
    expect(weakestRadarPoints(seed).map(p => p.best)).toEqual([35, 48, 55]);
  });
  // [入参不突变]
  it('input order unchanged', () => {
    const before = seed.map(p => p.current).join(',');
    weakestRadarPoints(seed);
    expect(seed.map(p => p.current).join(',')).toBe(before);
  });
  // [boundary-count 上限±/0]
  it('count 0', () => { expect(weakestRadarPoints(seed, 0)).toEqual([]); });
  it('count 1', () => {
    expect(weakestRadarPoints(seed, 1).map(p => p.current)).toEqual([35]);
  });
  it('count oversize', () => { expect(weakestRadarPoints(seed, 99).length).toBe(6); });
});

describe('票88 断言集(按维度过滤,25 例)', () => {
  // [谓词-未评分] current=0 且 best 空(null / undefined 两线形)
  it('[谓词-未评分] (0,null)', () => { expect(isScoredRadarPoint(P88('a', 0, null))).toBe(false); });
  it('[谓词-未评分] (0,undefined wire)', () => { expect(isScoredRadarPoint(U('a'))).toBe(false); });
  // [谓词-已评分] 评0分/退步/current 非0/负 current
  it('[谓词-已评分] scored-zero (0,0)', () => { expect(isScoredRadarPoint(P88('a', 0, 0))).toBe(true); });
  it('[谓词-已评分] declined (0,50)', () => { expect(isScoredRadarPoint(P88('a', 0, 50))).toBe(true); });
  it('[谓词-已评分] current>0 best空 (30,null)', () => { expect(isScoredRadarPoint(P88('a', 30, null))).toBe(true); });
  it('[谓词-已评分] negative (-5,null)', () => { expect(isScoredRadarPoint(P88('a', -5, null))).toBe(true); });
  // [全无分] weakest 空(票27 零数据口径零回退)
  it('[全无分] weakest []', () => {
    expect(weakestRadarPoints([U('a'), U('b'), U('c'), U('d'), U('e'), U('f')])).toEqual([]);
  });
  it('[全无分] has false', () => {
    expect(hasScoredRadarData([U('a'), P88('b', 0, null)])).toBe(false);
  });
  // [全有分] 与票27 基线逐字一致(升序 slice 3,Best 同行)
  const full: RadarPoint[] = [
    P88('pronunciation', 55, 55), P88('fluency', 62, 70), P88('sentenceIntegrity', 48, 48),
    P88('structure', 85, 85), P88('technicalDepth', 35, 35), P88('relevance', 90, 90),
  ];
  it('[全有分] weakest currents', () => {
    expect(weakestRadarPoints(full).map(p => p.current)).toEqual([35, 48, 55]);
  });
  it('[全有分] weakest bests', () => {
    expect(weakestRadarPoints(full).map(p => p.best)).toEqual([35, 48, 55]);
  });
  it('[全有分] weakest dims', () => {
    expect(weakestRadarPoints(full).map(p => p.dimension))
      .toEqual(['technicalDepth', 'sentenceIntegrity', 'pronunciation']);
  });
  // [部分有分-有分维居高] 有分维分数高于未评分维的 0,只列有分维
  const partialHigh: RadarPoint[] = [
    U('pronunciation'), U('fluency'), U('sentenceIntegrity'), U('structure'),
    P88('technicalDepth', 30, 30), U('relevance'),
  ];
  it('[部分有分-有分维居高] dims', () => {
    expect(weakestRadarPoints(partialHigh).map(p => p.dimension)).toEqual(['technicalDepth']);
  });
  it('[部分有分-有分维居高] no blank best', () => {
    expect(weakestRadarPoints(partialHigh).every(p => p.best != null)).toBe(true);
  });
  // [部分有分-多维] 3 有分 + 3 未评分(wire 形),只列 3 有分维升序
  const partial3: RadarPoint[] = [
    U('a'), P88('b', 70, 80), U('c'), P88('d', 40, 40), P88('e', 55, 60), U('f'),
  ];
  it('[部分有分-多维] currents', () => {
    expect(weakestRadarPoints(partial3).map(p => p.current)).toEqual([40, 55, 70]);
  });
  it('[部分有分-多维] bests', () => {
    expect(weakestRadarPoints(partial3).map(p => p.best)).toEqual([40, 60, 80]);
  });
  // [部分有分-有分维多于count] 4 有分 + 2 未评分,仍 slice 到 3
  const partial4: RadarPoint[] = [
    P88('a', 10, 10), U('b'), P88('c', 20, 20), P88('d', 30, 30), U('e'), P88('f', 40, 40),
  ];
  it('[部分有分-有分维多于count] currents', () => {
    expect(weakestRadarPoints(partial4).map(p => p.current)).toEqual([10, 20, 30]);
  });
  // [单维有分] 清单长 1,best 保留
  it('[单维有分] len', () => {
    expect(weakestRadarPoints([U('a'), U('b'), P88('only', 77, 88), U('c')]).length).toBe(1);
  });
  it('[单维有分] best kept', () => {
    expect(weakestRadarPoints([U('a'), P88('only', 77, 88)])[0].best).toBe(88);
  });
  // [已评分0分维 vs 未评分维] 同 current 靠 best 分辨
  it('[已评分0分维] dims', () => {
    const zeroMix: RadarPoint[] = [P88('scoredZero', 0, 0), U('unscored'), P88('mid', 20, 20)];
    expect(weakestRadarPoints(zeroMix).map(p => p.dimension)).toEqual(['scoredZero', 'mid']);
  });
  // [declined维] current 退到 0 但 best=50 入列居首
  it('[declined维] dims', () => {
    const pts: RadarPoint[] = [P88('dec', 0, 50), P88('mid', 20, 20), U('x')];
    expect(weakestRadarPoints(pts).map(p => p.dimension)).toEqual(['dec', 'mid']);
  });
  // [并列最低] 同分并列保持入参相对序,slice 3
  it('[并列最低] dims stable', () => {
    const tieLow: RadarPoint[] = [
      P88('t1', 40, 40), P88('t2', 40, 45), P88('t3', 40, 50), P88('t4', 40, 55), U('u1'),
    ];
    expect(weakestRadarPoints(tieLow).map(p => p.dimension)).toEqual(['t1', 't2', 't3']);
  });
  // [并列最高] 最高分并列不影响最弱选取
  it('[并列最高] currents', () => {
    const tieHigh: RadarPoint[] = [
      P88('h1', 90, 90), P88('w', 35, 35), P88('h2', 90, 95), P88('m', 48, 48), U('u'),
    ];
    expect(weakestRadarPoints(tieHigh).map(p => p.current)).toEqual([35, 48, 90]);
  });
  // [count边界] 超限不补未评分维;count=1 只取最弱有分维
  it('[count边界] oversize no padding', () => {
    expect(weakestRadarPoints(partial3, 99).length).toBe(3);
  });
  it('[count边界] count=1', () => {
    expect(weakestRadarPoints(partial3, 1).map(p => p.dimension)).toEqual(['d']);
  });
  // [入参不突变] 过滤+排序不改入参顺序与长度
  it('[入参不突变] order+len', () => {
    const before = partial3.map(p => p.dimension).join(',');
    weakestRadarPoints(partial3);
    expect(partial3.map(p => p.dimension).join(',')).toBe(before);
  });
});
