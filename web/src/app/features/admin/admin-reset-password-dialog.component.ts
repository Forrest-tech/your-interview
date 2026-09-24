import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * 重置密码弹窗。
 *
 * 单独一个弹窗而不是塞进用户编辑表单:重置密码是破坏性动作,
 * 混在常规字段里容易顺手一点就改掉别人的密码。
 */
@Component({
  selector: 'app-admin-reset-password-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule
  ],
  template: `
    <h2 mat-dialog-title>{{ t('admin.resetPwd') }}</h2>
    <mat-dialog-content class="rp-body">
      <p class="who">{{ t('admin.fieldEmail') }}: <strong>{{ email }}</strong></p>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('admin.fieldPassword') }}</mat-label>
        <input matInput [type]="show() ? 'text' : 'password'"
               name="pw" [(ngModel)]="password" autocomplete="new-password">
        <button mat-icon-button matSuffix type="button" tabindex="-1"
                (click)="show.set(!show())">
          <mat-icon>{{ show() ? 'visibility_off' : 'visibility' }}</mat-icon>
        </button>
        <mat-hint>{{ t('admin.pwdHintReset') }}</mat-hint>
      </mat-form-field>

      @if (error()) {
        <div class="err"><mat-icon>error_outline</mat-icon> {{ error() }}</div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="warn" [disabled]="!canSave()" (click)="save()">
        {{ t('admin.resetPwd') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .rp-body { display: flex; flex-direction: column; min-width: 360px; }
    .who { margin: 0 0 12px; font-size: 13.5px; }
    .full { width: 100%; }
    .err {
      display: flex; align-items: center; gap: 6px;
      margin-top: 4px; padding: 8px 12px; border-radius: 6px;
      background: #fdecea; color: #b3261e; font-size: 13px;
    }
    .err mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `]
})
export class AdminResetPasswordDialogComponent {
  private readonly ref = inject(MatDialogRef<AdminResetPasswordDialogComponent, string | null>);
  readonly email = inject<string>(MAT_DIALOG_DATA);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  readonly show = signal(false);
  readonly error = signal<string | null>(null);

  password = '';

  canSave(): boolean {
    return this.password.length >= 8;
  }

  save(): void {
    if (!this.canSave()) {
      this.error.set(this.t('admin.pwdHintReset'));
      return;
    }
    this.ref.close(this.password);
  }
}
