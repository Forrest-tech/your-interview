/**
 * 票64 ARC-08c 首批 spec —— v2.helpers:entryResultOutcome 展示映射。
 *
 * 口径源:v2.helpers.ts 源码注释与 switch 本体(票05 ARC-14 起结果判读收归
 * 后端单一事实源,前端只做「枚举 → 分组」展示映射)——后端 outcome 值域为
 * SharedContracts InterviewOutcome 枚举名:Pending / Passed / Rejected /
 * Ghosted / Cancelled / NoShow。枚举全值逐个断言,加 null/undefined/未知值。
 * 分组口径:终局枚举 -> Completed 三态;Pending / 无 outcome -> null(In Progress)。
 * 特征测试锁现状,不改被测函数行为。
 */
import { describe, expect, it } from '@jest/globals';
import { entryResultOutcome, resolveActionDoneState } from './v2.helpers';

describe('entryResultOutcome(枚举全值,源码 switch 逐值)', () => {
  // [枚举-终局] Rejected -> rejected
  it('Rejected -> rejected', () => {
    expect(entryResultOutcome('Rejected')).toBe('rejected');
  });
  // [枚举-终局] Passed -> positive
  it('Passed -> positive', () => {
    expect(entryResultOutcome('Passed')).toBe('positive');
  });
  // [枚举-终局] Ghosted -> closed
  it('Ghosted -> closed', () => {
    expect(entryResultOutcome('Ghosted')).toBe('closed');
  });
  // [枚举-终局] Cancelled -> closed
  it('Cancelled -> closed', () => {
    expect(entryResultOutcome('Cancelled')).toBe('closed');
  });
  // [枚举-终局] NoShow -> closed
  it('NoShow -> closed', () => {
    expect(entryResultOutcome('NoShow')).toBe('closed');
  });
  // [枚举-进行中] Pending -> null(In Progress,default 分支)
  it('Pending -> null (in progress)', () => {
    expect(entryResultOutcome('Pending')).toBeNull();
  });
});

describe('entryResultOutcome(null/未知值,boundary)', () => {
  // [boundary-null] 入参签名即含 null -> null
  it('null -> null', () => {
    expect(entryResultOutcome(null)).toBeNull();
  });
  // [boundary-null] undefined -> null
  it('undefined -> null', () => {
    expect(entryResultOutcome(undefined)).toBeNull();
  });
  // [boundary-空] 空串是未知值 -> null
  it('empty string -> null', () => {
    expect(entryResultOutcome('')).toBeNull();
  });
  // [boundary-未知值] 任意未知枚举名 -> null
  it('unknown enum name -> null', () => {
    expect(entryResultOutcome('Offer')).toBeNull();
  });
  // [boundary-大小写敏感] switch 精确匹配:小写变体不命中 -> null(特征锁定;
  // 后端只发枚举原名,前端不做宽容匹配——票05 单一事实源口径)
  it('lowercase variant -> null (case-sensitive, locked)', () => {
    expect(entryResultOutcome('passed')).toBeNull();
    expect(entryResultOutcome('REJECTED')).toBeNull();
  });
  // [boundary-空白] 带首尾空格不 trim -> null(特征锁定,精确匹配口径同上)
  it('padded value -> null (no trim, locked)', () => {
    expect(entryResultOutcome(' Passed ')).toBeNull();
  });
  // [boundary-自由文本] 旧 result 自由文本(如「未通过」)不是入参域 -> null;
  // 前端不许再对 result 原文做关键词判读(源码头注,票05 口径)
  it('free-text result is not interpreted -> null', () => {
    expect(entryResultOutcome('未通过')).toBeNull();
    expect(entryResultOutcome('Rejected by email')).toBeNull();
  });
  // [boundary-Unicode/超长] 超长未知串 -> null,不抛
  it('very long unknown string -> null', () => {
    expect(entryResultOutcome('X'.repeat(10000))).toBeNull();
  });
  // [boundary-0与负数/日期/文件类] 不适用:入参为枚举名字符串,无数值/日期/文件语义
});

describe('resolveActionDoneState(票116 F-H1:toggle 失败后 checkbox 呈现值)', () => {
  const rows = [
    { id: 'a1', isDone: true },
    { id: 'a2', isDone: false },
  ];
  // [回归-F-H1 主形态] 点前 isDone=true、PATCH 失败、回读仍 true -> 呈现 true
  // (改前缺陷:DOM 残留点击翻转后的 false,与标签 done 类/服务端打架)
  it('failed toggle re-read true -> true (regression F-H1)', () => {
    expect(resolveActionDoneState(rows, 'a1', true)).toBe(true);
  });
  // [回归-反向] 点前 false、失败、回读仍 false -> 呈现 false
  it('failed toggle re-read false -> false', () => {
    expect(resolveActionDoneState(rows, 'a2', false)).toBe(false);
  });
  // [服务端真值优先] 回读值与点前回落值不同(如并发已变更) -> 以回读为准
  it('re-read wins over fallback when they differ', () => {
    expect(resolveActionDoneState(rows, 'a1', false)).toBe(true);
    expect(resolveActionDoneState(rows, 'a2', true)).toBe(false);
  });
  // [boundary-缺行] 回读列表无此行 -> 回落点前真值(两向)
  it('missing row -> fallback', () => {
    expect(resolveActionDoneState(rows, 'nope', true)).toBe(true);
    expect(resolveActionDoneState(rows, 'nope', false)).toBe(false);
  });
  // [boundary-空/null] 回读为空数组/null/undefined -> 回落,不抛
  it('empty or null actions -> fallback', () => {
    expect(resolveActionDoneState([], 'a1', true)).toBe(true);
    expect(resolveActionDoneState(null, 'a1', false)).toBe(false);
    expect(resolveActionDoneState(undefined, 'a1', true)).toBe(true);
  });
});
