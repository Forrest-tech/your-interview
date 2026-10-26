import { Component, Input, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { QaZoomService } from './qa-zoom.service';
import { I18nService } from '../i18n/i18n.service';

/**
 * 字号控件:− / 百分比(点击可直接输入)/ ＋。
 * 与 AI Practice 的 zoom 同一套交互与同一份 localStorage 偏好。
 * compact 模式（票 194，Prep report 页头专用）：只隐藏“字号”名，百分比数字显示按票 197 恢复（与完整版同一元素、同一服务值）；
 * Interview record 页仍用完整版，不受影响。
 */
@Component({
  selector: 'app-qa-zoom',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <div class="qa-zoom" role="group" [attr.aria-label]="i18n.t('zoom.aria')">
      @if (!compact) {
        <span class="qa-zoom-label">{{ i18n.t('zoom.label') }}</span>
      }
      <button type="button" class="zbtn" (click)="zoom.decrease()"
              [disabled]="zoom.level() <= 80" [title]="i18n.t('zoom.decrease')">−</button>
      @if (editing()) {
        <input class="zpct-input" type="text" inputmode="numeric"
               [ngModel]="draft()" (ngModelChange)="onDraft($event)"
               (keyup.enter)="commit()" (keyup.escape)="editing.set(false)"
               (blur)="commit()" autofocus>
      } @else {
        <button type="button" class="zpct" (click)="startEdit()" [title]="i18n.t('zoom.inputTitle')">
          {{ zoom.level() }}%
        </button>
      }
      <button type="button" class="zbtn" (click)="zoom.increase()"
              [disabled]="zoom.level() >= 400" [title]="i18n.t('zoom.increase')">＋</button>
    </div>
  `,
  styles: [`
    .qa-zoom { display: inline-flex; align-items: center; gap: 5px; }
    .qa-zoom-label { font-size: 11px; color: var(--yi-text-muted, #475569); margin-right: 2px; }
    .zbtn { display: inline-flex; align-items: center; justify-content: center;
            width: 36px; height: 36px; border-radius: 10px; border: 1px solid var(--yi-border, #e2e8f0);
            background: #ffffff; color: var(--yi-text-muted, #475569); font-size: 19px; line-height: 1; cursor: pointer;
            transition: background .15s ease, border-color .15s ease, color .15s ease; }
    .zbtn:hover { background: #f6f8fb; border-color: var(--yi-border, #e2e8f0); color: var(--yi-text, #0f172a); }
    .zbtn:disabled { opacity: .4; cursor: default; }
    .zpct { min-width: 52px; text-align: center; font-size: 12px; font-weight: 700;
            border: 1px solid var(--yi-border, #e2e8f0); border-radius: 7px; padding: 4px 4px;
            background: transparent; color: inherit; cursor: text; }
    .zpct-input { width: 52px; text-align: center; font-size: 12px; font-weight: 700;
                  border: 1px solid var(--yi-accent, #2563eb); border-radius: 7px; padding: 4px 4px; }
  `]
})
export class QaZoomComponent {
  @Input() compact = false;
  readonly zoom = inject(QaZoomService);
  readonly i18n = inject(I18nService);
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
