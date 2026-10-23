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
export function inject<T>(_token?: unknown): T {
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
