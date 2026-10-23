/**
 * 票75 spec —— validation-errors.helpers:服务端字段级错误映射纯逻辑。
 *
 * 对照源:后端 ValidationProblemDetails(ServiceDefaults 异常处理器)——
 * title 恒为 "One or more validation errors occurred.",字段信息在
 * errors{字段名:[消息]};ApiClient.rethrow 把原始响应挂在 e.original。
 * 实测钉子(票75 改前复现):自备问题 5000 字符 → errors{"Text":["问题必填"]}
 * —— WithMessage 把长度违规也标成必填,分类必须以送出长度为第一判据。
 * boundary 类别逐条标注(总纲第七节第 3 条)。
 */
import { describe, expect, it } from '@jest/globals';
import {
  classifyFieldError, extractServerFieldErrors, fieldErrorFromServer,
  serverFieldMessages, textLengthError,
} from './validation-errors.helpers';

/** ApiClient 包装后的形状:Error + original=原始 HttpErrorResponse。 */
function apiClientError(errors: Record<string, string[]>) {
  return Object.assign(new Error('One or more validation errors occurred.'), {
    status: 400,
    original: { status: 400, error: { title: 'One or more validation errors occurred.', errors } },
  });
}

/** 未经 ApiClient 的原始形状。 */
function rawError(errors: Record<string, string[]>) {
  return { status: 400, error: { title: 'One or more validation errors occurred.', errors } };
}

describe('extractServerFieldErrors(票75)', () => {
  // [功能] ApiClient 形状(original.error.errors)
  it('ApiClient shape -> dict', () => {
    expect(extractServerFieldErrors(apiClientError({ Text: ['问题必填'] })))
      .toEqual({ Text: ['问题必填'] });
  });
  // [功能] 原始形状(error.errors)
  it('raw shape -> dict', () => {
    expect(extractServerFieldErrors(rawError({ Role: ['x'] }))).toEqual({ Role: ['x'] });
  });
  // [boundary-空/null] null / undefined / 非对象
  it('null/undefined/string -> null', () => {
    expect(extractServerFieldErrors(null)).toBeNull();
    expect(extractServerFieldErrors(undefined)).toBeNull();
    expect(extractServerFieldErrors('boom')).toBeNull();
  });
  // [boundary-空] 无 errors 字段(500 ProblemDetails)
  it('no errors key -> null', () => {
    expect(extractServerFieldErrors({ error: { title: 'Server error' } })).toBeNull();
  });
  // [boundary-坏数据] error 是字符串(非 JSON 响应)
  it('string body -> null', () => {
    expect(extractServerFieldErrors({ error: 'not json' })).toBeNull();
  });
  // [boundary-坏数据] errors 值含非字符串项 → 只保留字符串项
  it('non-string items filtered', () => {
    expect(extractServerFieldErrors(rawError({ Text: ['a', 42 as unknown as string] })))
      .toEqual({ Text: ['a'] });
  });
  // [boundary-空集合] errors 为空对象 → null(无字段可映射)
  it('empty dict -> null', () => {
    expect(extractServerFieldErrors(rawError({}))).toBeNull();
  });
});

describe('serverFieldMessages(票75)', () => {
  // [功能] 字段名大小写不敏感(后端 Text ↔ 前端 text)
  it('case-insensitive field match', () => {
    expect(serverFieldMessages(apiClientError({ Text: ['问题必填'] }), 'text')).toEqual(['问题必填']);
    expect(serverFieldMessages(apiClientError({ Text: ['问题必填'] }), 'TEXT')).toEqual(['问题必填']);
  });
  // [功能] 无关字段 → null(组件据此回落原有通用处理)
  it('unrelated field -> null', () => {
    expect(serverFieldMessages(apiClientError({ Text: ['x'] }), 'role')).toBeNull();
  });
});

describe('classifyFieldError(票75)', () => {
  // [回归-本票实测] 长度违规被后端误标「问题必填」:送出 5000 > 上限 4000 → tooLong 压过消息文本
  it('over-limit length beats misleading required message', () => {
    expect(classifyFieldError(['问题必填'], { sentLength: 5000, max: 4000 }).kind).toBe('tooLong');
  });
  // [boundary-超长] 恰好上限 → 不算 tooLong(长度判据不触发),回落消息判据
  it('exactly at limit + required message -> required', () => {
    expect(classifyFieldError(['问题必填'], { sentLength: 4000, max: 4000 }).kind).toBe('required');
  });
  // [boundary-超长] 上限 +1 → tooLong(即使消息为空)
  it('limit+1 with empty message -> tooLong', () => {
    expect(classifyFieldError([''], { sentLength: 4001, max: 4000 }).kind).toBe('tooLong');
  });
  // [功能] FluentValidation 英文默认长度消息 → tooLong
  it('english max-length message -> tooLong', () => {
    expect(classifyFieldError(
      ["The length of 'Text' must be 4000 characters or fewer. You entered 5000 characters."],
      {}).kind).toBe('tooLong');
  });
  // [功能] 必填消息 → required
  it('required message -> required', () => {
    expect(classifyFieldError(['The Question field is required.'], { sentLength: 0 }).kind).toBe('required');
    expect(classifyFieldError(['问题必填'], { sentLength: 0 }).kind).toBe('required');
  });
  // [boundary-空] 送出长度 0 且消息不可辨 → required
  it('zero length + opaque message -> required', () => {
    expect(classifyFieldError(['校验失败'], { sentLength: 0 }).kind).toBe('required');
  });
  // [功能] 其它校验(格式类)→ other 并保留原文
  it('other validation keeps backend text', () => {
    const c = classifyFieldError(['官网地址格式不正确'], { sentLength: 12, max: 1024 });
    expect(c.kind).toBe('other');
    expect(c.text).toBe('官网地址格式不正确');
  });
  // [功能] 多条消息以空格拼(only for other)
  it('multiple messages joined for other', () => {
    expect(classifyFieldError(['格式不对', '请检查'], {}).text).toBe('格式不对 请检查');
  });
});

describe('fieldErrorFromServer(票75)', () => {
  // [回归-本票实测] 全链:5000 字符自备问题的真实错误形状 → tooLong
  it('real repro shape -> tooLong', () => {
    const c = fieldErrorFromServer(apiClientError({ Text: ['问题必填'] }), 'Text',
      { sentLength: 5000, max: 4000 });
    expect(c?.kind).toBe('tooLong');
  });
  // [功能] 错误与本字段无关 → null
  it('unrelated -> null', () => {
    expect(fieldErrorFromServer(apiClientError({ Role: ['x'] }), 'Text', {})).toBeNull();
  });
  // [boundary-空/null] 非校验错误(网络断开的 Error) → null,不抛
  it('plain Error -> null', () => {
    expect(fieldErrorFromServer(new Error('network down'), 'Text', {})).toBeNull();
  });
});

describe('textLengthError(票75,提交前拦截)', () => {
  // [boundary-空/null] null/undefined/空串合法(必填另有拦截,不归本函数)
  it('null/undefined/empty -> null', () => {
    expect(textLengthError(null, 4000)).toBeNull();
    expect(textLengthError(undefined, 4000)).toBeNull();
    expect(textLengthError('', 4000)).toBeNull();
  });
  // [boundary-超长] 上限值放行、上限+1 拦
  it('at limit ok, limit+1 tooLong', () => {
    expect(textLengthError('x'.repeat(4000), 4000)).toBeNull();
    expect(textLengthError('x'.repeat(4001), 4000)).toBe('tooLong');
  });
  // [boundary-Unicode] 中文按码元计:4000 汉字放行、4001 拦;emoji 代理对 2 码元
  it('CJK and emoji counted as UTF-16 code units', () => {
    expect(textLengthError('题'.repeat(4000), 4000)).toBeNull();
    expect(textLengthError('题'.repeat(4001), 4000)).toBe('tooLong');
    expect(textLengthError('😀'.repeat(2000), 4000)).toBeNull();
    expect(textLengthError('😀'.repeat(2001), 4000)).toBe('tooLong');
  });
  // [boundary-特殊字符] 特殊字符不影响长度判定
  it('special chars counted plainly', () => {
    expect(textLengthError('<b>&"\' '.repeat(500), 4000)).toBeNull();
  });
});
