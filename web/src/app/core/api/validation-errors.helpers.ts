/**
 * 服务端字段级校验错误的**纯映射逻辑**(票75,口径参照票12 Knowledge 弹窗
 * applyServerError)。
 *
 * 背景:后端 FluentValidation 失败时统一回 ValidationProblemDetails ——
 * title 恒为通用英文 "One or more validation errors occurred.",真正的
 * 字段级信息在 errors{字段名:[消息]} 字典里。此前 Tracker 一系表单只读
 * title/detail(readError),把字段信息整份吞掉,用户只看到通用弹框、
 * 不知道哪个字段错了、上限是多少。本文件把「从错误对象里抠出某字段的
 * 消息并分类」抽成纯函数:组件拿到分类后按当前 locale 呈现本地化文案
 * (超限走 i18n 的 validation.tooLong 并带上限数字),不再透出通用标题。
 *
 * 分类为什么不能只看后端消息文本:Jobs 的若干校验器用 WithMessage 把
 * 整条链的消息写死成一句中文(如自备问题 Text 的 NotEmpty 与
 * MaximumLength(4000) 共用「问题必填」)—— 长度违规回来的也是「必填」。
 * 所以分类以**实际送出长度 vs 上限**为第一判据,消息文本只作第二判据与
 * 兜底原文。长度按 JS string length(UTF-16 码元)计,与 .NET string
 * Length / FluentValidation MaximumLength 同口径(票79 先例)。
 */

export type FieldErrorKind = 'tooLong' | 'required' | 'other';

export interface ClassifiedFieldError {
  kind: FieldErrorKind;
  /** kind='other' 时的后端原文(多条以空格拼);其余 kind 为空串,文案由组件本地化。 */
  text: string;
}

/**
 * 从任意错误对象里抠 ValidationProblemDetails 的 errors 字典。
 * 兼容两种形状:
 *  - ApiClient.rethrow 包装后:原始 HttpErrorResponse 挂在 e.original,
 *    字典在 e.original.error.errors;
 *  - 未经 ApiClient 的原始错误:e.error.errors。
 * 抠不到(非对象 / errors 非对象 / 值非数组)一律返回 null,不抛错。
 */
export function extractServerFieldErrors(e: unknown): Record<string, string[]> | null {
  const err = e as {
    original?: { error?: unknown } | null;
    error?: unknown;
  } | null;
  if (!err || typeof err !== 'object') return null;
  const bodies: unknown[] = [
    (err.original as { error?: unknown } | null | undefined)?.error,
    err.error,
  ];
  for (const body of bodies) {
    if (!body || typeof body !== 'object') continue;
    const errors = (body as { errors?: unknown }).errors;
    if (!errors || typeof errors !== 'object' || Array.isArray(errors)) continue;
    const out: Record<string, string[]> = {};
    for (const [k, v] of Object.entries(errors as Record<string, unknown>)) {
      if (Array.isArray(v)) out[k] = v.filter((x): x is string => typeof x === 'string');
    }
    return Object.keys(out).length > 0 ? out : null;
  }
  return null;
}

/** 取某个字段的消息(字段名大小写不敏感 —— 后端 PascalCase、前端 camelCase 混用)。 */
export function serverFieldMessages(e: unknown, field: string): string[] | null {
  const errors = extractServerFieldErrors(e);
  if (!errors) return null;
  const want = field.toLowerCase();
  for (const [k, v] of Object.entries(errors)) {
    if (k.toLowerCase() === want) return v.length > 0 ? v : null;
  }
  return null;
}

const TOO_LONG_RE = /maximum length|max length|characters or fewer|or fewer|too long|exceed|过长|长度/i;
const REQUIRED_RE = /required|must not be empty|not be empty|is empty|必填|不能为空|不可为空/i;

/**
 * 把某字段的后端消息分类。判据顺序(见文件头注):
 *  1. 实际送出长度 > 上限 → tooLong(压过后端误标的「必填」类消息);
 *  2. 消息文本命中长度类措辞 → tooLong;
 *  3. 消息文本命中必填类措辞,或送出长度为 0 → required;
 *  4. 其余 → other,保留后端原文。
 */
export function classifyFieldError(
  messages: readonly string[],
  opts: { sentLength?: number | null; max?: number | null } = {}
): ClassifiedFieldError {
  const text = messages.join(' ').trim();
  const { sentLength, max } = opts;
  if (sentLength != null && max != null && sentLength > max) {
    return { kind: 'tooLong', text: '' };
  }
  if (TOO_LONG_RE.test(text)) return { kind: 'tooLong', text: '' };
  if (REQUIRED_RE.test(text)) return { kind: 'required', text: '' };
  if (sentLength === 0) return { kind: 'required', text: '' };
  return { kind: 'other', text };
}

/**
 * 一步到位:某字段有服务端错误时返回分类,没有(错误与本字段无关)返回
 * null —— 组件据此决定「落字段级提示」还是「回落原有通用处理」,保证
 * 非字段级失败(500、网络等)的呈现不回退。
 */
export function fieldErrorFromServer(
  e: unknown,
  field: string,
  opts: { sentLength?: number | null; max?: number | null } = {}
): ClassifiedFieldError | null {
  const messages = serverFieldMessages(e, field);
  if (!messages) return null;
  return classifyFieldError(messages, opts);
}

/**
 * 提交前的前端长度拦截(票12 validate() 同半边):返回 'tooLong' 表示
 * 不该发请求、直接在字段下落本地化提示;null 表示长度合法。
 * 长度以**将实际送出的值**(组件 trim 后)计,与后端 MaximumLength 对齐。
 */
export function textLengthError(sentValue: string | null | undefined, max: number): 'tooLong' | null {
  if (sentValue == null) return null;
  return sentValue.length > max ? 'tooLong' : null;
}
