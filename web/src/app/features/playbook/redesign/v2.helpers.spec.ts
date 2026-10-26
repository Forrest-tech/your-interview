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
import { entryResultOutcome, formatEnteredDate, isPrepInputPurpose, latestPrepInputAsset, PREP_INPUT_EXPERIENCES, PREP_INPUT_RESUME, prepInputPurposeOf, resolveActionDoneState, resolveRoundNodeStatus, resolveScoreJd, scoreTierKey } from './v2.helpers';

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

/**
 * 票159 备考输入 purpose 规则。
 * 口径源:v2.helpers.ts 的 PREP_INPUT_* 常量与 latestPrepInputAsset —— 同轮同 purpose
 * 取 uploadedAt 最新一份；不同轮、不同 purpose、非备考输入资产一律不入选；删除最新
 * 一份后次新一份即晋升（本函数只取现存最新，晋升是同一规则的自然结果）。
 */
const asset = (id: string, roundId: string | null, uploadedAt: string, purpose?: string) => ({
  id, roundId, uploadedAt, metaJson: purpose ? JSON.stringify({ purpose }) : null,
});

describe('票159 purpose 标签', () => {
  it('类型 -> purpose 映射', () => {
    expect(prepInputPurposeOf('experiences')).toBe(PREP_INPUT_EXPERIENCES);
    expect(prepInputPurposeOf('resume')).toBe(PREP_INPUT_RESUME);
  });
  it('isPrepInputPurpose 只认两个值', () => {
    expect(isPrepInputPurpose(PREP_INPUT_EXPERIENCES)).toBe(true);
    expect(isPrepInputPurpose(PREP_INPUT_RESUME)).toBe(true);
    expect(isPrepInputPurpose('note')).toBe(false);
    expect(isPrepInputPurpose(null)).toBe(false);
    expect(isPrepInputPurpose(undefined)).toBe(false);
  });
});

describe('票159 latestPrepInputAsset(最新一份为准)', () => {
  const a1 = asset('a1', 'r1', '2026-10-20T10:00:00', PREP_INPUT_EXPERIENCES);
  const a2 = asset('a2', 'r1', '2026-10-24T09:00:00', PREP_INPUT_EXPERIENCES);
  const a3 = asset('a3', 'r2', '2026-10-25T09:00:00', PREP_INPUT_EXPERIENCES);
  const a4 = asset('a4', 'r1', '2026-10-25T09:00:00', PREP_INPUT_RESUME);
  const a5 = asset('a5', 'r1', '2026-10-25T09:00:00');
  it('同轮同 purpose 取最新一份', () => {
    expect(latestPrepInputAsset([a1, a2], 'r1', PREP_INPUT_EXPERIENCES)?.id).toBe('a2');
  });
  it('不同轮不入选（即使更新）', () => {
    expect(latestPrepInputAsset([a1, a2, a3], 'r1', PREP_INPUT_EXPERIENCES)?.id).toBe('a2');
  });
  it('不同 purpose 不入选', () => {
    expect(latestPrepInputAsset([a1, a2, a4], 'r1', PREP_INPUT_RESUME)?.id).toBe('a4');
    expect(latestPrepInputAsset([a4], 'r1', PREP_INPUT_EXPERIENCES)).toBeNull();
  });
  it('无 purpose 的 Notes 不入选', () => {
    expect(latestPrepInputAsset([a5], 'r1', PREP_INPUT_EXPERIENCES)).toBeNull();
  });
  it('删除最新一份后次新一份晋升', () => {
    expect(latestPrepInputAsset([a1], 'r1', PREP_INPUT_EXPERIENCES)?.id).toBe('a1');
  });
  it('空/缺失输入 -> null', () => {
    expect(latestPrepInputAsset([], 'r1', PREP_INPUT_EXPERIENCES)).toBeNull();
    expect(latestPrepInputAsset(null, 'r1', PREP_INPUT_EXPERIENCES)).toBeNull();
  });
});

/**
 * 票 173 —— resolveScoreJd 回落链 + scoreTierKey 阈值。
 * 回落链:投递 jdText → 投递 jdSummary → 条目 jdText → 条目 jdSummary;
 * 前两项与 Tracker recomputeMatch 逐字一致,后两项是本票新增的回落。
 * 档位阈值与 Tracker matchTierLabel 逐字一致:>=70 强,>=50 一般,否则弱。
 */
describe('resolveScoreJd(票 173 回落链)', () => {
  it('投递 jdText 优先(即使条目 JD 也存在)', () => {
    expect(resolveScoreJd({ jdText: 'app full', jdSummary: 'app sum' }, { jdText: 'entry full', jdSummary: 'entry sum' })).toBe('app full');
  });
  it('投递 jdText 为空时回落投递 jdSummary', () => {
    expect(resolveScoreJd({ jdText: '  ', jdSummary: 'app sum' }, { jdText: 'entry full' })).toBe('app sum');
  });
  it('投递 JD 全空时回落条目 jdText', () => {
    expect(resolveScoreJd({ jdText: null, jdSummary: '' }, { jdText: 'entry full', jdSummary: 'entry sum' })).toBe('entry full');
  });
  it('条目 jdText 为空时回落条目 jdSummary', () => {
    expect(resolveScoreJd(null, { jdText: '', jdSummary: 'entry sum' })).toBe('entry sum');
  });
  it('投递未关联(application null)时条目 JD 仍可用', () => {
    expect(resolveScoreJd(null, { jdText: 'entry full' })).toBe('entry full');
  });
  it('条目 JD 含 HTML 时按展示口径剥离', () => {
    expect(resolveScoreJd(null, { jdText: '<p>entry full</p>' })).toBe('entry full');
  });
  it('两侧全空 -> 空串(调用方按无分处理,不编造)', () => {
    expect(resolveScoreJd(null, null)).toBe('');
    expect(resolveScoreJd({ jdText: '', jdSummary: '' }, { jdText: '', jdSummary: '' })).toBe('');
  });
});

describe('formatEnteredDate(票 186 进入 Active 日期,时区感知)', () => {
  // 2026-10-26T03:30Z = 10 月 25 日 23:30 EDT:账号时区必须出本地日期 10 月 25 日,
  // 绝不能是 UTC 的次日 10 月 26 日(Simplify 式 UTC 日期 bug 的同类)。
  it('EDT 晚上 23:30 的 ISO 串格式化为本地日期(不是 UTC 次日)', () => {
    expect(formatEnteredDate('2026-10-26T03:30:00.000Z', 'America/Toronto', 'en')).toBe('Oct 25, 2026');
  });
  it('只含日期,不含时间', () => {
    expect(formatEnteredDate('2026-10-26T03:30:00.000Z', 'America/Toronto', 'en')).not.toContain(':');
  });
  it('非法 ISO 不抛错', () => {
    expect(() => formatEnteredDate('not-a-date', 'America/Toronto', 'en')).not.toThrow();
  });
  it('非法时区不抛错(回落浏览器本地日期)', () => {
    expect(() => formatEnteredDate('2026-10-26T03:30:00.000Z', 'Mars/Olympus', 'en')).not.toThrow();
  });
});

describe('scoreTierKey(票 173 档位阈值)', () => {
  it('>=70 强匹配', () => {
    expect(scoreTierKey(100)).toBe('tracker.dStrongMatch');
    expect(scoreTierKey(70)).toBe('tracker.dStrongMatch');
  });
  it('50-69 一般匹配', () => {
    expect(scoreTierKey(69)).toBe('tracker.dMedMatch');
    expect(scoreTierKey(50)).toBe('tracker.dMedMatch');
  });
  it('<50 弱匹配', () => {
    expect(scoreTierKey(49)).toBe('tracker.dWeakMatch');
    expect(scoreTierKey(0)).toBe('tracker.dWeakMatch');
  });
});

/**
 * 票 183:resolveRoundNodeStatus 六档 + 四个边界(定稿 §4.2 映射表逐条)。
 * today 固定 2026-10-26(本地日初);日期字串用本地午夜写法,避免
 * 日期-only 字串按 UTC 解析在非 UTC 时区 偏移 一天的干扰,测的是日粒度规则本身。
 */
describe('resolveRoundNodeStatus(票 183 六档状态映射)', () => {
  const TODAY = new Date(2026, 9, 26);
  const PAST = '2020-01-01';
  const FUTURE = '2099-01-01';
  const TODAY_LOCAL = '2026-10-26T00:00:00';

  // [终态优先] Passed / Failed / Cancelled 不受日期影响
  it('Passed + 过去日期 -> passed(终态优先,不翻回 Completed)', () => {
    expect(resolveRoundNodeStatus('Passed', PAST, TODAY)).toBe('passed');
  });
  it('Passed + 未来日期/无日期 -> passed', () => {
    expect(resolveRoundNodeStatus('Passed', FUTURE, TODAY)).toBe('passed');
    expect(resolveRoundNodeStatus('Passed', undefined, TODAY)).toBe('passed');
  });
  it('Failed + 未来日期 -> failed(终态优先)', () => {
    expect(resolveRoundNodeStatus('Failed', FUTURE, TODAY)).toBe('failed');
    expect(resolveRoundNodeStatus('Failed', undefined, TODAY)).toBe('failed');
  });
  it('Cancelled + 未来/过去日期 -> cancelled(终态优先)', () => {
    expect(resolveRoundNodeStatus('Cancelled', FUTURE, TODAY)).toBe('cancelled');
    expect(resolveRoundNodeStatus('Cancelled', PAST, TODAY)).toBe('cancelled');
  });

  // [非终态看日期] Pending + 未来/今天 -> scheduled,过去 -> completed
  it('Pending + 未来日期 -> scheduled', () => {
    expect(resolveRoundNodeStatus('Pending', FUTURE, TODAY)).toBe('scheduled');
  });
  it('Pending + 今天 -> scheduled(今天算未进行)', () => {
    expect(resolveRoundNodeStatus('Pending', TODAY_LOCAL, TODAY)).toBe('scheduled');
  });
  it('Pending + 过去日期 -> completed', () => {
    expect(resolveRoundNodeStatus('Pending', PAST, TODAY)).toBe('completed');
  });

  // [边界] 空 outcome / 未知 outcome 统一走日期分支,不单开分支
  it('空 outcome -> 按日期走(未来 scheduled,过去 completed)', () => {
    expect(resolveRoundNodeStatus('', FUTURE, TODAY)).toBe('scheduled');
    expect(resolveRoundNodeStatus('', PAST, TODAY)).toBe('completed');
  });
  it('未知 outcome -> 按日期走(未来 scheduled,过去 completed)', () => {
    expect(resolveRoundNodeStatus('WeirdValue', FUTURE, TODAY)).toBe('scheduled');
    expect(resolveRoundNodeStatus('WeirdValue', PAST, TODAY)).toBe('completed');
  });

  // [边界] 无日期 / 不可解析日期 -> unscheduled(不抛错)
  it('Pending + 无日期 -> unscheduled', () => {
    expect(resolveRoundNodeStatus('Pending', undefined, TODAY)).toBe('unscheduled');
  });
  it('Pending + 不可解析日期 -> unscheduled', () => {
    expect(resolveRoundNodeStatus('Pending', 'not-a-date', TODAY)).toBe('unscheduled');
    expect(resolveRoundNodeStatus('Pending', '', TODAY)).toBe('unscheduled');
  });
  it('空 outcome + 无日期 -> unscheduled', () => {
    expect(resolveRoundNodeStatus('', undefined, TODAY)).toBe('unscheduled');
  });
});
