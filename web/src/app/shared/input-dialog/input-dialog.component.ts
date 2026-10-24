import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';

/**
 * 全站统一的单行输入弹窗(票45 REQ-14:替代原生 prompt —— 原生 prompt 在无弹窗
 * 处理的环境会被静默 dismiss,输入看起来"点了没反应",与 F-02 修复标准一致)。
 *
 * 用法:
 *   dialog.open(InputDialogComponent, {
 *     width: '360px', panelClass: 'app-confirm',
 *     data: { title, label, initialValue, confirmText, cancelText, inputType }
 *   });
 * 关闭值:确认 = 输入框当前原始字符串(不做任何解析/校验,口径留给调用方,
 * 与原生 prompt 返回原始字符串一致);取消/Esc/点遮罩 = undefined。
 *
 * 风格基准与 ConfirmDialog 同套(白卡片 · 圆角 12 · 标题 15px/600 #1f2430 ·
 * 取消=描边按钮 · 确认=实心蓝 #2e3a8c)。
 */
export interface InputDialogData {
  title: string;
  /** 输入框上方的字段标签(可含原生 prompt 时代的冒号文案,逐字传入)。 */
  label?: string;
  /** 预填值(原生 prompt 的第二个参数)。 */
  initialValue?: string;
  confirmText: string;
  cancelText: string;
  /** 输入类型,默认 text;数字目标等场景传 number 亦可 —— 返回值仍是原始字符串。 */
  inputType?: string;
  /** 标题图标(默认 edit)。 */
  icon?: string;
}

@Component({
  selector: 'app-input-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatDialogModule],
  template: `
    <div class="cf-wrap">
      <div class="cf-head">
        <mat-icon class="cf-icon">{{ data.icon || 'edit' }}</mat-icon>
        <span class="cf-title">{{ data.title }}</span>
      </div>

      <mat-form-field appearance="outline" class="cf-field">
        @if (data.label) {
          <mat-label>{{ data.label }}</mat-label>
        }
        <input matInput [type]="data.inputType || 'text'" [(ngModel)]="value"
               (keydown.enter)="submit()" cdkFocusInitial>
      </mat-form-field>

      <div class="cf-actions">
        <span class="cf-spacer"></span>
        <button mat-stroked-button class="cf-cancel" mat-dialog-close>
          {{ data.cancelText }}
        </button>
        <button mat-flat-button class="cf-ok" (click)="submit()">
          {{ data.confirmText }}
        </button>
      </div>
    </div>
  `,
  styles: [`
    .cf-wrap { padding: 22px 22px 18px; }
    .cf-head { display: flex; align-items: center; gap: 10px; }
    .cf-icon { font-size: 22px; width: 22px; height: 22px; color: #2e3a8c; }
    .cf-title { font-size: 15px; font-weight: 600; color: #1f2430; line-height: 1.4; }
    .cf-field { display: block; margin: 14px 0 0 32px; }
    .cf-actions button { white-space: nowrap; }
    .cf-actions {
      margin-top: 8px;
      display: flex; align-items: center; justify-content: flex-end; gap: 10px;
      flex-wrap: wrap; row-gap: 8px;
    }
    .cf-spacer { flex: 1 1 auto; }
    .cf-cancel {
      --mdc-outlined-button-label-text-color: #5b6270;
      --mdc-outlined-button-outline-color: #e1e4ec;
      border-radius: 8px;
    }
    .cf-ok {
      --mdc-filled-button-container-color: #2e3a8c;
      border-radius: 8px;
    }
  `]
})
export class InputDialogComponent {
  readonly data = inject<InputDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<InputDialogComponent, string>>(MatDialogRef);
  // MatDialog 显式注入一次,保证 tree-shaking 不会把对话框模块抖掉(同 ConfirmDialog)。
  private readonly _dialog = inject(MatDialog);

  value = this.data.initialValue ?? '';

  /** 确认:回传原始字符串(解析/校验口径由调用方按原生 prompt 时代逐字保留)。 */
  submit(): void {
    this.ref.close(this.value);
  }
}
