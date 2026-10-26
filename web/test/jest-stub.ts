/**
 * 票64 ARC-08c:Jest 专用替身模块 —— 只被 web/jest.config.cjs 的
 * moduleNameMapper 引用,产品构建（ng build / tsc app 口径）不引用本文件。
 *
 * 用途:intake-panel.component.ts 是真组件文件,值导入 @angular/* 与
 * PlaybookV2Api / PracticeApi / OcrApiService / I18nService。首批 spec 只测
 * 同文件里的纯函数（extractInterviewWhen / adaptLocalOcrEmailText）,不实例化
 * 组件、不走 TestBed,故把这些模块级依赖映射到此处的最小替身,使被测文件
 * 能在 Node 环境加载。替身只保证「类定义期」求值不炸（装饰器、imports 数组
 * 里的模块类名）;任何替身成员真被调用都会表现为平凡空实现,spec 不依赖它。
 */

// ---- @angular/core ----
export function Component(_opts?: unknown): (target: unknown) => unknown {
  return (target: unknown) => target;
}
export function Injectable(_opts?: unknown): (target: unknown) => unknown {
  return (target: unknown) => target;
}
export function Output(): (target: unknown, key?: string) => void {
  return () => undefined;
}
export function Input(): (target: unknown, key?: string) => void {
  return () => undefined;
}
/* ★ ML-FIX-UI:PracticeMaterialTree 的导入链用到 @ViewChild/@HostListener
   (material-transfer-dialog / material-tree),与既有 Input/Output 同款空实现。 */
export function ViewChild(_selector?: unknown): (target: unknown, key?: string) => void {
  return () => undefined;
}
export function HostListener(_event?: unknown): (target: unknown, key?: string, descriptor?: unknown) => unknown {
  return (_t: unknown, _k?: string, d?: unknown) => d;
}
export class DestroyRef {}
export class EventEmitter<T = unknown> {
  emit(_value?: T): void { /* stub */ }
  subscribe(): { unsubscribe(): void } { return { unsubscribe: () => undefined }; }
}
export function signal<T>(initial: T): {
  (): T; set(v: T): void; update(f: (v: T) => T): void; asReadonly(): () => T;
} {
  let v = initial;
  const fn = (() => v) as {
    (): T; set(nv: T): void; update(f: (v: T) => T): void; asReadonly(): () => T;
  };
  fn.set = (nv: T) => { v = nv; };
  fn.update = (f: (x: T) => T) => { v = f(v); };
  fn.asReadonly = () => fn;
  return fn;
}
export function computed<T>(fn: () => T): () => T {
  return () => fn();
}
export function effect(_fn: () => void): void { /* stub:不执行 */ }

/**
 * ★ 票124:inject 的**可安装钩子**。
 *
 * 背景:票124 的 F1 要测**真实** PracticeApi(真实源文件,不走替身),
 * 而真实 ApiClient 在构造期调 inject(HttpClient) 取真实 HttpClient。
 * 替身环境里没有 DI 容器 —— 于是给 inject 一个可注入的钩子:
 *   spec 调 setInjectProvider(() => fakeHttpClient) 后,
 *   真实源文件里的 inject(HttpClient) 就会拿到假 HttpClient。
 *
 * 口径(与 ticket126 的做法同源):**只换注入值,不改产品代码**。
 * 未安装钩子时 inject 照旧抛错 —— 保持"替身环境不许随便实例化"的原纪律。
 */
let injectProvider: ((token?: unknown) => unknown) | null = null;

/** 安装 inject 的取值源(返回 null = 撤销安装)。 */
export function setInjectProvider(fn: ((token?: unknown) => unknown) | null): void {
  injectProvider = fn;
}

export function inject<T>(_token?: unknown): T {
  if (injectProvider) return injectProvider(_token) as T;
  throw new Error('jest-stub: inject() called outside a stubbed component test scope');
}
export function input<T>(_initial?: T): () => T | undefined {
  return () => undefined;
}
export function output<T>(): EventEmitter<T> {
  return new EventEmitter<T>();
}

// ---- @angular/common / forms / material ----
export class CommonModule {}
export class FormsModule {}

// ---- @angular/common/http(票126 TJ-1 补:让真实 ApiClient 能在替身环境跑通) ----
// HttpParams:ApiClient.toParams 只用 new + set(链式返回新实例口径以 this 近似,spec 不依赖参数内容)。
export class HttpParams {
  private readonly map = new Map<string, string>();
  set(key: string, value: string): HttpParams {
    this.map.set(key, value);
    return this;
  }
  get(key: string): string | null { return this.map.get(key) ?? null; }
}
// HttpErrorResponse:最小真实类 —— 使「原生错误 instanceof 判定」与 ApiClient rethrow
// 转型在 spec 里可区分(TJ-1 的接缝本体);字段与真实类同名同义。
export class HttpErrorResponse {
  readonly status: number;
  readonly statusText: string;
  readonly error: unknown;
  constructor(init: { status?: number; statusText?: string; error?: unknown } = {}) {
    this.status = init.status ?? 0;
    this.statusText = init.statusText ?? '';
    this.error = init.error ?? null;
  }
}
export class HttpClient {}
export class HttpResponse<T = unknown> { body: T | null = null; }
export class MatButtonModule {}
export class MatIconModule {}
export class MatSnackBar {
  open(): void { /* stub */ }
}

// ---- 被 intake 值导入的服务（类名占位,spec 不实例化） ----
export class PlaybookV2Api {}
export class PracticeApi {}
export class OcrApiService {}
export class I18nService {
  lang(): string { return 'en'; }
  t(key: string): string { return key; }
}
