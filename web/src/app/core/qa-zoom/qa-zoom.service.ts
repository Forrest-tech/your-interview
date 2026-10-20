import { Injectable, computed, signal } from '@angular/core';

/**
 * Q&A 文本字号缩放(2026-10-20 详情页 v2)。
 *
 * 与 AI Practice 共用同一份偏好:localStorage 键 `user_practice_zoom`,
 * 存的是纯百分比数字(80–400)。AI Practice 里调过,这里读到的就是同一个值,反之亦然。
 *
 * 作用范围靠 CSS 约定:只有带 .qa-text 的元素(问题/回答/标准答案等正文)
 * 才吃 --qa-scale 变量;按钮、页签、标签等界面框架不带这个类,纹丝不动。
 */
@Injectable({ providedIn: 'root' })
export class QaZoomService {
  static readonly KEY = 'user_practice_zoom';
  static readonly MIN = 80;
  static readonly MAX = 400;
  static readonly STEP = 10;

  readonly level = signal(QaZoomService.read());

  /** 缩放比例(1 = 100%)。模板绑到容器的 [style.--qa-scale]。 */
  readonly scale = computed(() => this.level() / 100);

  private static read(): number {
    try {
      const raw = localStorage.getItem(QaZoomService.KEY);
      if (!raw) return 100;
      const n = Number(raw);
      if (!Number.isFinite(n)) return 100;
      return Math.min(QaZoomService.MAX, Math.max(QaZoomService.MIN, Math.round(n)));
    } catch {
      return 100;
    }
  }

  set(pct: number): void {
    const v = Math.min(QaZoomService.MAX,
      Math.max(QaZoomService.MIN, Math.round(pct)));
    this.level.set(v);
    try { localStorage.setItem(QaZoomService.KEY, String(v)); } catch { /* 隐私模式等:仅本次会话生效 */ }
  }

  decrease(): void { this.set(this.level() - QaZoomService.STEP); }
  increase(): void { this.set(this.level() + QaZoomService.STEP); }
}
