/**
 * 票79 spec —— playbook-local.helpers:starStoryTitleError(STAR 故事标题校验)。
 *
 * 对照源:后端 PlaybookLocalDataHandlers 的 StarStoryLimits.TitleMax = 500 与
 * validator 口径(Title NotEmpty + MaximumLength(500))——前端纯函数逐项镜像:
 * NotEmpty 拒空/纯空白;MaximumLength 按原始串 UTF-16 码元计长(.NET string
 * Length 与 JS string length 同为 UTF-16 码元数,两端一致)。
 * boundary 类别逐条标注(总纲第七节第 3 条)。
 */
import { describe, expect, it } from '@jest/globals';
import { STAR_ACTION_MIN_CHARS, STAR_STORY_TITLE_MAX, starStoryBodyError, starStoryTitleError } from './playbook-local.helpers';

describe('starStoryTitleError(票79)', () => {
  // [boundary-空] 空串
  it('empty string -> required', () => {
    expect(starStoryTitleError('')).toBe('required');
  });
  // [boundary-空/null] null 输入
  it('null -> required', () => {
    expect(starStoryTitleError(null)).toBe('required');
  });
  // [boundary-空/null] undefined 输入
  it('undefined -> required', () => {
    expect(starStoryTitleError(undefined)).toBe('required');
  });
  // [boundary-空白] 纯空格
  it('spaces only -> required', () => {
    expect(starStoryTitleError('     ')).toBe('required');
  });
  // [boundary-空白] 制表/换行混合空白
  it('tab/newline only -> required', () => {
    expect(starStoryTitleError(' \t\n\r ')).toBe('required');
  });
  // [功能] 普通标题通过
  it('normal title -> null', () => {
    expect(starStoryTitleError('主导 DealTrax 迁移')).toBeNull();
  });
  // [功能] 前后带空格的正常标题通过(内容非空即可,保存不由本函数改写)
  it('padded title -> null', () => {
    expect(starStoryTitleError('  Cut API latency  ')).toBeNull();
  });
  // [boundary-Unicode] emoji/引号/尖括号标题通过(D1 既有形态)
  it('emoji and quotes title -> null', () => {
    expect(starStoryTitleError('Emoji 🚀 "quoted" & <b>故事</b> ✅')).toBeNull();
  });
  // [boundary-超长] 恰好上限 500 -> 通过(上限值)
  it('exactly 500 chars -> null', () => {
    expect(starStoryTitleError('x'.repeat(STAR_STORY_TITLE_MAX))).toBeNull();
  });
  // [boundary-超长] 上限 +1 = 501 -> tooLong
  it('501 chars -> tooLong', () => {
    expect(starStoryTitleError('x'.repeat(STAR_STORY_TITLE_MAX + 1))).toBe('tooLong');
  });
  // [boundary-超长/Unicode] 中文标题按码元计长:500 个汉字通过、501 个超限
  it('CJK at limit and over limit', () => {
    expect(starStoryTitleError('题'.repeat(500))).toBeNull();
    expect(starStoryTitleError('题'.repeat(501))).toBe('tooLong');
  });
  // [boundary-超长/Unicode] emoji 为代理对(2 码元):250 个 = 500 码元通过,251 个超限
  it('emoji surrogate pairs counted as UTF-16 code units', () => {
    expect(starStoryTitleError('🚀'.repeat(250))).toBeNull();
    expect(starStoryTitleError('🚀'.repeat(251))).toBe('tooLong');
  });
  // [boundary-优先级] 纯空白且超长 -> required 优先(先报必填,不报超长)
  it('whitespace over limit -> required wins', () => {
    expect(starStoryTitleError(' '.repeat(600))).toBe('required');
  });
});

/**
 * ML-FIX-PREP(R-27) spec —— playbook-local.helpers:starStoryBodyError(STAR 故事正文校验)。
 * 口径取自 Prep「行为题 STAR」behavioral.p2 既有要求:S/T/A/R 四项均必填、
 * Result 带数字、Action 占一半且为具体复数动作(Action 长度为机械代理,见实现注释)。
 * 判定顺序:S→T→A→R 必填,随后 Result 数字、最后 Action 长度。
 */
describe('starStoryBodyError(ML-FIX-PREP·R-27)', () => {
  const valid = {
    situation: '遗留单体改动风险高',
    task: '主导 strangler 迁移',
    action: '先抽 deal 域做 POC,再按域拆服务并灰度上线',
    result: '迁移后发布周期从 2 周缩到 2 天',
  };
  // [功能] 四项齐全、Result 带数字、Action 足长 -> 通过
  it('valid story -> null', () => {
    expect(starStoryBodyError(valid)).toBeNull();
  });
  // [boundary-必填] 每项空白各报对应字段 required(按 S→T→A→R 顺序首个失败)
  it('blank situation -> S required', () => {
    expect(starStoryBodyError({ ...valid, situation: '   ' })).toEqual({ field: 'S', kind: 'required' });
  });
  it('blank task -> T required', () => {
    expect(starStoryBodyError({ ...valid, task: '' })).toEqual({ field: 'T', kind: 'required' });
  });
  it('blank action -> A required (before vague check)', () => {
    expect(starStoryBodyError({ ...valid, action: null })).toEqual({ field: 'A', kind: 'required' });
  });
  it('blank result -> R required (before number check)', () => {
    expect(starStoryBodyError({ ...valid, result: undefined })).toEqual({ field: 'R', kind: 'required' });
  });
  // [boundary-必填顺序] 多项空白时报顺序首个(S 优先于 R)
  it('blank S and R -> S required wins', () => {
    expect(starStoryBodyError({ ...valid, situation: '', result: '' })).toEqual({ field: 'S', kind: 'required' });
  });
  // [功能] Result 无数字 -> resultNeedsNumber
  it('result without digit -> resultNeedsNumber', () => {
    expect(starStoryBodyError({ ...valid, result: '上线后系统更稳定' })).toEqual({ field: 'R', kind: 'resultNeedsNumber' });
  });
  // [功能] Result 全角数字同样通过
  it('result with full-width digit -> null', () => {
    expect(starStoryBodyError({ ...valid, result: '发布周期缩到２天' })).toBeNull();
  });
  // [boundary-空泛代理] Action 恰好阈值 -1 -> actionTooVague,恰好阈值 -> 通过
  it('action length boundary around STAR_ACTION_MIN_CHARS', () => {
    expect(starStoryBodyError({ ...valid, action: 'x'.repeat(STAR_ACTION_MIN_CHARS - 1) })).toEqual({ field: 'A', kind: 'actionTooVague' });
    expect(starStoryBodyError({ ...valid, action: 'x'.repeat(STAR_ACTION_MIN_CHARS) })).toBeNull();
  });
  // [boundary-优先级] Result 无数字且 Action 过短 -> 先报 Result 数字
  it('no digit and vague action -> resultNeedsNumber wins', () => {
    expect(starStoryBodyError({ ...valid, action: '做了', result: '更稳定' })).toEqual({ field: 'R', kind: 'resultNeedsNumber' });
  });
});
