import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MAT_DIALOG_DATA } from '@angular/material/dialog';

/** 删除确认框的数据 —— 调用方传入已本地化的文案,本框不碰 i18n。 */
export interface QaConfirmDialogData {
  title: string;
  body: string;
  confirmText: string;
  cancelText: string;
  /** 确认按钮是否用警示色,默认 true(删除场景)。 */
  danger?: boolean;
}

/**
 * 通用二次确认框(spec 历史欠账 B7:替代原生 confirm() ——
 * 原生 confirm 会打断自动化测试,且样式不可控)。
 * 用法:dialog.open(QaConfirmDialogComponent, { data: {...} })
 *      .afterClosed().subscribe(ok => { if (ok) ... });
 */
@Component({
  selector: 'app-qa-confirm-dialog',
  standalone: true,
  imports: [MatDialogModule, MatButtonModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="qa-confirm-body">{{ data.body }}</p>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="false">{{ data.cancelText }}</button>
      <button mat-flat-button [color]="data.danger === false ? 'primary' : 'warn'"
              [mat-dialog-close]="true" cdkFocusInitial>{{ data.confirmText }}</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .qa-confirm-body { margin: 8px 0 4px; line-height: 1.6; word-break: break-word; }
  `]
})
export class QaConfirmDialogComponent {
  readonly data = inject<QaConfirmDialogData>(MAT_DIALOG_DATA);
}
