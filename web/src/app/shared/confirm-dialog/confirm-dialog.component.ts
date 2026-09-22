import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

/**
 * 全站统一的确认弹窗(2026-09-20,Forrest:所有弹窗样式/字体/颜色保持一致)。
 *
 * 用法:
 *   dialog.open(ConfirmDialogComponent, {
 *     width: '360px', panelClass: 'app-confirm',
 *     data: { title, body, confirmText, cancelText, danger }
 *   });
 * 关闭值:true = 用户点了确认;其他(取消/Esc/点遮罩)一律视为不确认。
 *
 * 风格基准(与站点一致):
 *   白卡片 · 圆角 12 · 标题 15px/600 #1f2733 · 正文 13px #5a6472 ·
 *   取消=描边按钮 · 确认=实心蓝 #2f6fed(危险操作为红 #e5484d)。
 */
export interface ConfirmDialogData {
  title: string;
  body?: string;
  confirmText: string;
  cancelText: string;
  /** true = 危险操作(删除),确认按钮用红色。 */
  danger?: boolean;
  /** 标题图标(默认 help_outline;danger 时 delete_outline)。 */
  icon?: string;
  /**
   * 可选的第三个按钮 —— 关闭值为 'discard'。
   * 用于"保存并刷新 / 放弃并刷新 / 留在本页"这种三选一场景
   * (2026-09-23 Forrest:刷新前的未保存拦截)。
   */
  discardText?: string;
  /** discard 按钮是否按危险色显示(放弃改动 = 会丢东西)。 */
  discardDanger?: boolean;
}

@Component({
  selector: 'app-confirm-dialog',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatIconModule, MatDialogModule],
  template: `
    <div class="cf-wrap">
      <div class="cf-head">
        <mat-icon class="cf-icon" [class.is-danger]="data.danger">
          {{ data.danger ? 'delete_outline' : (data.icon || 'help_outline') }}
        </mat-icon>
        <span class="cf-title">{{ data.title }}</span>
      </div>

      @if (data.body) {
        <p class="cf-body">{{ data.body }}</p>
      }

      <div class="cf-actions">
        @if (data.discardText) {
          <button mat-button class="cf-discard"
                  [class.is-danger]="data.discardDanger"
                  [mat-dialog-close]="'discard'">
            {{ data.discardText }}
          </button>
        }
        <span class="cf-spacer"></span>
        <button mat-stroked-button class="cf-cancel" mat-dialog-close>
          {{ data.cancelText }}
        </button>
        <button mat-flat-button class="cf-ok"
                [class.is-danger]="data.danger"
                [mat-dialog-close]="true" cdkFocusInitial>
          {{ data.confirmText }}
        </button>
      </div>
    </div>
  `,
  styles: [`
    .cf-wrap { padding: 22px 22px 18px; }
    .cf-head { display: flex; align-items: center; gap: 10px; }
    .cf-icon { font-size: 22px; width: 22px; height: 22px; color: #2f6fed; }
    .cf-icon.is-danger { color: #e5484d; }
    .cf-title { font-size: 15px; font-weight: 600; color: #1f2733; line-height: 1.4; }
    .cf-body {
      margin: 10px 0 0 32px;
      font-size: 13px; line-height: 1.65; color: #5a6472;
    }
    .cf-actions {
      margin-top: 20px;
      display: flex; align-items: center; justify-content: flex-end; gap: 10px;
    }
    .cf-spacer { flex: 1 1 auto; }
    .cf-discard {
      --mdc-text-button-label-text-color: #5a6472;
      border-radius: 8px;
    }
    .cf-discard.is-danger {
      --mdc-text-button-label-text-color: #e5484d;
    }
    .cf-cancel {
      --mdc-outlined-button-label-text-color: #5a6472;
      --mdc-outlined-button-outline-color: #dde3ec;
      border-radius: 8px;
    }
    .cf-ok {
      --mdc-filled-button-container-color: #2f6fed;
      border-radius: 8px;
    }
    .cf-ok.is-danger {
      --mdc-filled-button-container-color: #e5484d;
    }
  `]
})
export class ConfirmDialogComponent {
  readonly data = inject<ConfirmDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<ConfirmDialogComponent>>(MatDialogRef);
  // MatDialog 显式注入一次,保证 tree-shaking 不会把对话框模块抖掉。
  private readonly _dialog = inject(MatDialog);
}
