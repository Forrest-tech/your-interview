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
import { STAR_STORY_TITLE_MAX, starStoryTitleError } from './playbook-local.helpers';

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
