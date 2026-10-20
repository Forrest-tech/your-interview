import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { QaZoomService } from './qa-zoom.service';

/**
 * 字号控件:− / 百分比(点击可直接输入)/ ＋。
 * 与 AI Practice 的 zoom 同一套交互与同一份 localStorage 偏好。
 */
@Component({
  selector: 'app-qa-zoom',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="qa-zoom" role="group" aria-label="问答文本字号">
      <span class="qa-zoom-label">字号</span>
      <button type="button" class="zbtn" (click)="zoom.decrease()"
              [disabled]="zoom.level() <= 80" title="减小">−</button>
      @if (editing()) {
        <input class="zpct-input" type="text" inputmode="numeric"
               [ngModel]="draft()" (ngModelChange)="onDraft($event)"
               (keyup.enter)="commit()" (keyup.escape)="editing.set(false)"
               (blur)="commit()" autofocus>
      } @else {
        <button type="button" class="zpct" (click)="startEdit()" title="点击直接输入百分比">
          {{ zoom.level() }}%
        </button>
      }
      <button type="button" class="zbtn" (click)="zoom.increase()"
              [disabled]="zoom.level() >= 400" title="放大">＋</button>
    </div>
  `,
  styles: [`
    .qa-zoom { display: inline-flex; align-items: center; gap: 5px; }
    .qa-zoom-label { font-size: 11px; color: var(--yi-text-muted, #888); margin-right: 2px; }
    .zbtn { width: 26px; height: 26px; border-radius: 7px; border: 1px solid var(--yi-border, #d0d0d0);
            background: transparent; color: inherit; font-size: 14px; line-height: 1; cursor: pointer; }
    .zbtn:disabled { opacity: .4; cursor: default; }
    .zpct { min-width: 52px; text-align: center; font-size: 12px; font-weight: 700;
            border: 1px solid var(--yi-border, #d0d0d0); border-radius: 7px; padding: 4px 4px;
            background: transparent; color: inherit; cursor: text; }
    .zpct-input { width: 52px; text-align: center; font-size: 12px; font-weight: 700;
                  border: 1px solid var(--yi-accent, #3f51b5); border-radius: 7px; padding: 4px 4px; }
  `]
})
export class QaZoomComponent {
  readonly zoom = inject(QaZoomService);
  readonly editing = signal(false);
  readonly draft = signal('');

  onDraft(v: string): void {
    this.draft.set(v.replace(/[^0-9]/g, ''));
  }

  startEdit(): void {
    this.draft.set(String(this.zoom.level()));
    this.editing.set(true);
  }

  commit(): void {
    if (!this.editing()) return;
    this.editing.set(false);
    const n = Number(this.draft().trim());
    if (Number.isFinite(n) && n > 0) this.zoom.set(n);
  }
}
