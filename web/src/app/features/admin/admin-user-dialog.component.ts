import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { I18nService } from '../../core/i18n/i18n.service';
import { AdminRole, AdminUser } from '../../core/models/api.models';

export interface AdminUserDialogData {
  mode: 'create' | 'edit';
  /** 编辑模式下传入;新建模式为 undefined。 */
  user?: AdminUser;
  /** 可选角色全集 —— 从 /api/admin/roles 取,不让管理员手打角色名。 */
  roles: AdminRole[];
}

/** 与 POST/PUT /api/admin/users 的 body 对应(password 仅新建时提交)。 */
export interface AdminUserForm {
  email: string;
  displayName: string;
  password?: string;
  roles: string[];
  isActive: boolean;
  requirePasswordChange: boolean;
}

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * 新建 / 编辑用户弹窗。
 *
 * 为什么角色用下拉多选而不是逗号文本框:
 *   手打角色名拼错一个字母,后端(M2.4 起)会直接 400,管理员还得猜哪里错了;
 *   之前是"静默忽略",建出来的账号零权限,更难查。下拉从真实角色列表取值,从根上避免。
 */
@Component({
  selector: 'app-admin-user-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatSlideToggleModule, MatButtonModule, MatIconModule
  ],
  template: `
    <h2 mat-dialog-title>{{ isEdit ? t('admin.editUser') : t('admin.newUser') }}</h2>

    <mat-dialog-content class="ud-body">
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('admin.fieldEmail') }}</mat-label>
        <input matInput name="email" [disabled]="isEdit" [(ngModel)]="email"
               autocomplete="off" placeholder="name@example.com">
        @if (isEdit) {
          <mat-hint>{{ t('admin.colEmail') }}</mat-hint>
        }
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('admin.fieldName') }}</mat-label>
        <input matInput name="displayName" [(ngModel)]="displayName" autocomplete="off">
      </mat-form-field>

      @if (!isEdit) {
        <mat-form-field appearance="outline" class="full">
          <mat-label>{{ t('admin.fieldPassword') }}</mat-label>
          <input matInput [type]="showPwd() ? 'text' : 'password'" name="password"
                 [(ngModel)]="password" autocomplete="new-password">
          <button mat-icon-button matSuffix type="button" tabindex="-1"
                  (click)="showPwd.set(!showPwd())">
            <mat-icon>{{ showPwd() ? 'visibility_off' : 'visibility' }}</mat-icon>
          </button>
          <mat-hint>{{ t('admin.pwdHint') }}</mat-hint>
        </mat-form-field>
      }

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('admin.fieldRoles') }}</mat-label>
        <mat-select multiple [(ngModel)]="roles" name="roles">
          @for (r of data.roles; track r.id) {
            <mat-option [value]="r.name">{{ r.name }}</mat-option>
          }
        </mat-select>
        <mat-hint>{{ t('admin.rolesHint') }}</mat-hint>
      </mat-form-field>

      <div class="toggles">
        <mat-slide-toggle name="isActive" [(ngModel)]="isActive">
          {{ t('admin.activeToggle') }}
        </mat-slide-toggle>
        <mat-slide-toggle name="requirePasswordChange" [(ngModel)]="requirePasswordChange">
          {{ t('admin.forcePwdChange') }}
        </mat-slide-toggle>
      </div>

      @if (error()) {
        <div class="err"><mat-icon>error_outline</mat-icon> {{ error() }}</div>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="primary" [disabled]="!canSave()" (click)="save()">
        {{ isEdit ? t('common.save') : t('common.create') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .ud-body { display: flex; flex-direction: column; min-width: 420px; }
    .full { width: 100%; }
    .toggles { display: flex; flex-direction: column; gap: 10px; margin-top: 2px; }
    .err {
      display: flex; align-items: center; gap: 6px;
      margin-top: 8px; padding: 8px 12px; border-radius: 6px;
      background: #fdecea; color: #b3261e; font-size: 13px;
    }
    .err mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `]
})
export class AdminUserDialogComponent {
  private readonly ref = inject(MatDialogRef<AdminUserDialogComponent, AdminUserForm | null>);
  readonly data = inject<AdminUserDialogData>(MAT_DIALOG_DATA);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  readonly isEdit = this.data.mode === 'edit';
  readonly showPwd = signal(false);
  readonly error = signal<string | null>(null);

  email = this.data.user?.email ?? '';
  displayName = this.data.user?.displayName ?? '';
  password = '';
  roles: string[] = [...(this.data.user?.roles ?? [])];
  isActive = this.data.user?.isActive ?? true;
  requirePasswordChange = this.isEdit
    ? (this.data.user?.mustChangePassword ?? false)
    : true;

  /** 密码强度:与后端 AdminCreateUserCommandValidator 完全一致的四条规则。 */
  readonly pwdOk = computed(() => {
    const p = this.password;
    return p.length >= 12 && /[A-Z]/.test(p) && /[a-z]/.test(p) && /[0-9]/.test(p)
      && /[^A-Za-z0-9]/.test(p);
  });

  canSave(): boolean {
    if (this.displayName.trim().length === 0) return false;
    if (this.roles.length === 0) return false;
    if (this.isEdit) return true;
    return EMAIL_RE.test(this.email.trim()) && this.pwdOk();
  }

  save(): void {
    if (!this.canSave()) {
      this.error.set(
        this.displayName.trim().length === 0 ? this.t('admin.fieldName')
          : this.roles.length === 0 ? this.t('admin.rolesHint')
            : this.isEdit ? this.t('admin.rolesHint')
              : !EMAIL_RE.test(this.email.trim()) ? this.t('admin.fieldEmail')
                : this.t('admin.pwdHint')
      );
      return;
    }
    this.ref.close({
      email: this.email.trim(),
      displayName: this.displayName.trim(),
      ...(this.isEdit ? {} : { password: this.password }),
      roles: this.roles,
      isActive: this.isActive,
      requirePasswordChange: this.requirePasswordChange
    });
  }
}
