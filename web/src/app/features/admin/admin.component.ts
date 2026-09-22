import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTableModule } from '@angular/material/table';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { AdminRole, AdminStats, AdminUser, AuditLog, Paged } from '../../core/models/api.models';

/** 用户编辑表单 —— 与 PUT /api/admin/users/{id} 的 body 一一对应。 */
interface AdminUserForm {
  displayName: string;
  isActive: boolean;
  requirePasswordChange: boolean;
  roles: string[];
}

/**
 * 重置密码弹窗(内联)。
 * 单独一个弹窗而不是混进编辑表单:重置密码是破坏性动作,混在一起容易误点。
 */
@Component({
  selector: 'app-reset-password-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule
  ],
  template: `
    <h2 mat-dialog-title>重置密码</h2>
    <mat-dialog-content class="rp-body">
      <p class="who">为 <strong>{{ email }}</strong> 设置新密码</p>

      <mat-form-field appearance="outline" class="full">
        <mat-label>新密码</mat-label>
        <input matInput [type]="show() ? 'text' : 'password'"
               name="pw" [(ngModel)]="password" autocomplete="new-password">
        <button mat-icon-button matSuffix type="button" (click)="show.set(!show())" tabindex="-1">
          <mat-icon>{{ show() ? 'visibility_off' : 'visibility' }}</mat-icon>
        </button>
        <mat-hint>至少 8 位,建议含大小写字母与数字</mat-hint>
      </mat-form-field>

      @if (error()) {
        <div class="err"><mat-icon>error_outline</mat-icon> {{ error() }}</div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>取消</button>
      <button mat-raised-button color="warn" [disabled]="!canSave()" (click)="save()">
        确认重置
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .rp-body { min-width: 360px; }
    .who { margin: 0 0 12px; font-size: 13.5px; }
    mat-form-field { width: 100%; }
    .full { width: 100%; }
    .err {
      display: flex; align-items: center; gap: 6px;
      margin-top: 4px; padding: 8px 12px; border-radius: 6px;
      background: #fdecea; color: #b3261e; font-size: 13px;
    }
    .err mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `]
})
export class ResetPasswordDialogComponent {
  private readonly ref = inject(MatDialogRef<ResetPasswordDialogComponent, string | null>);
  readonly email = inject<string>(MAT_DIALOG_DATA);

  readonly show = signal(false);
  readonly error = signal<string | null>(null);

  password = '';

  canSave(): boolean {
    return this.password.length >= 8;
  }

  save(): void {
    if (!this.canSave()) {
      this.error.set('密码至少 8 位');
      return;
    }
    this.ref.close(this.password);
  }
}

/** 用户编辑弹窗(内联)。角色用逗号分隔输入 —— 不引 autocomplete 下拉,避免多拉一次角色接口。 */
@Component({
  selector: 'app-user-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatButtonModule, MatIconModule, MatSlideToggleModule
  ],
  template: `
    <h2 mat-dialog-title>编辑用户</h2>
    <mat-dialog-content class="ud-body">
      <p class="email-line">{{ user.email }}</p>

      <mat-form-field appearance="outline" class="full">
        <mat-label>显示名</mat-label>
        <input matInput name="displayName" [(ngModel)]="form.displayName">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>角色(逗号分隔)</mat-label>
        <input matInput name="roles" [(ngModel)]="rolesText"
               placeholder="如 Admin, Recruiter">
        <mat-hint>留空表示不分配任何角色</mat-hint>
      </mat-form-field>

      <div class="toggles">
        <mat-slide-toggle name="isActive" [(ngModel)]="form.isActive">
          账号启用
        </mat-slide-toggle>
        <mat-slide-toggle name="requirePasswordChange" [(ngModel)]="form.requirePasswordChange">
          下次登录强制改密
        </mat-slide-toggle>
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>取消</button>
      <button mat-raised-button color="primary" (click)="save()">保存</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .ud-body { min-width: 400px; }
    .email-line {
      margin: 0 0 12px; font-size: 12.5px; opacity: 0.6;
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    }
    mat-form-field { width: 100%; }
    .full { width: 100%; }
    .toggles { display: flex; flex-direction: column; gap: 12px; margin-top: 4px; }
  `]
})
export class UserDialogComponent {
  private readonly ref = inject(MatDialogRef<UserDialogComponent, AdminUserForm | null>);
  readonly user = inject<AdminUser>(MAT_DIALOG_DATA);

  form: AdminUserForm = {
    displayName: this.user.displayName ?? '',
    isActive: this.user.isActive ?? true,
    requirePasswordChange: false,
    roles: [...(this.user.roles ?? [])]
  };

  get rolesText(): string {
    return this.form.roles.join(', ');
  }

  set rolesText(v: string) {
    // 顺手去重去空 —— 手输逗号最容易出现 "Admin, ,Admin"
    this.form.roles = v
      .split(',')
      .map((s) => s.trim())
      .filter((s, i, arr) => s.length > 0 && arr.indexOf(s) === i);
  }

  save(): void {
    this.ref.close({
      ...this.form,
      displayName: this.form.displayName.trim(),
      roles: this.form.roles
    });
  }
}

/**
 * 管理后台:用户 / 角色 / 审计日志三个 tab。
 *
 * 用 MatTabs 而不是三张独立页面:管理员通常要在"改用户 → 查日志确认生效"之间来回切,
 * 页签保留各 tab 的滚动位置与已加载数据,切换成本几乎为零。
 * 数据用懒加载:切到哪个 tab 才拉哪个接口,避免进页面就打三个请求。
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatIconModule, MatButtonModule,
    MatChipsModule, MatTabsModule, MatTableModule, MatFormFieldModule, MatInputModule,
    MatProgressBarModule, MatTooltipModule, MatDialogModule, MatSnackBarModule
  ],
  templateUrl: './admin.component.html',
  styleUrl: './admin.component.scss'
})
export class AdminComponent implements OnInit {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  // -------- 统计 --------
  readonly stats = signal<AdminStats | null>(null);

  // -------- 用户 --------
  readonly users = signal<AdminUser[]>([]);
  readonly usersLoading = signal(false);
  readonly usersError = signal<string | null>(null);
  readonly userSearch = signal('');
  readonly userPage = signal(1);
  readonly userPageSize = signal(20);
  readonly userTotal = signal(0);
  readonly userTotalPages = signal(1);

  readonly userColumns: string[] = [
    'email', 'displayName', 'roles', 'isActive', 'lastLoginAt', 'actions'
  ];

  // -------- 角色 --------
  readonly roles = signal<AdminRole[]>([]);
  readonly rolesLoading = signal(false);
  readonly rolesError = signal<string | null>(null);
  /** 展开的角色 id 集合 —— 权限芯片默认折叠,角色多时页面才不会太长。 */
  readonly expandedRoleIds = signal<string[]>([]);

  // -------- 审计日志 --------
  readonly logs = signal<AuditLog[]>([]);
  readonly logsLoading = signal(false);
  readonly logsError = signal<string | null>(null);
  readonly logPage = signal(1);
  readonly logPageSize = signal(30);
  readonly logTotal = signal(0);
  readonly logTotalPages = signal(1);

  readonly logColumns: string[] = ['occurredAt', 'action', 'entityType', 'userEmail', 'ip', 'detail'];

  // -------- 懒加载标记:避免每次切 tab 都重复请求 --------
  private usersLoaded = false;
  private rolesLoaded = false;
  private logsLoaded = false;

  readonly activeTab = signal(0);

  readonly statRows = computed(() => {
    const s = this.stats();
    if (!s) return [];
    return [
      { label: '用户总数', value: s.userCount, icon: 'people_outline' },
      { label: '活跃用户', value: s.activeUserCount, icon: 'person_outline' },
      { label: '角色数', value: s.roleCount, icon: 'badge' },
      { label: '权限项', value: s.permissionCount, icon: 'vpn_key' },
      { label: '审计事件', value: s.auditEventCount, icon: 'history' }
    ];
  });

  ngOnInit(): void {
    this.loadStats();
    // 默认停在第一个 tab,顺手加载它,免得用户看到空白
    this.onTabChange(0);
  }

  /** tab 切换时才拉数据 —— 三个接口全在进页面时打会拖慢首屏。 */
  onTabChange(index: number): void {
    this.activeTab.set(index);
    if (index === 0 && !this.usersLoaded) this.loadUsers();
    if (index === 1 && !this.rolesLoaded) this.loadRoles();
    if (index === 2 && !this.logsLoaded) this.loadLogs();
  }

  // ============================ 统计 ============================

  loadStats(): void {
    this.api.get<AdminStats>('/api/admin/stats')
      .pipe(catchError(() => of(null)))
      .subscribe((s) => this.stats.set(s));
  }

  // ============================ 用户 ============================

  loadUsers(): void {
    this.usersLoading.set(true);
    this.usersError.set(null);

    this.api.get<Paged<AdminUser>>('/api/admin/users', {
      page: this.userPage(),
      pageSize: this.userPageSize(),
      search: this.userSearch().trim()
    }).subscribe({
      next: (r) => {
        this.users.set(r.items ?? []);
        this.userTotal.set(r.total ?? 0);
        this.userTotalPages.set(r.totalPages ?? 1);
        this.usersLoading.set(false);
        this.usersLoaded = true;
      },
      error: (e: Error) => {
        this.usersError.set(e.message);
        this.users.set([]);
        this.usersLoading.set(false);
      }
    });
  }

  onUserSearchInput(v: string): void {
    this.userSearch.set(v);
  }

  onUserSearchEnter(): void {
    this.userPage.set(1);
    this.loadUsers();
  }

  userGoPage(p: number): void {
    if (p < 1 || p > this.userTotalPages() || p === this.userPage()) return;
    this.userPage.set(p);
    this.loadUsers();
  }

  openEditUser(u: AdminUser): void {
    const ref = this.dialog.open(UserDialogComponent, { data: u, autoFocus: 'first-tabbable' });
    ref.afterClosed().subscribe((form?: AdminUserForm | null) => {
      if (!form) return;
      this.api.put<AdminUser>(`/api/admin/users/${u.id}`, form).subscribe({
        next: () => {
          this.notify('已保存用户');
          this.loadUsers();
          this.loadStats();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  /** 启用/停用:直接发一次 PUT,把当前所有字段一起回传(后端是整体更新语义)。 */
  toggleActive(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    const next = !u.isActive;
    const verb = next ? '启用' : '停用';
    if (!confirm(`确认${verb}账号 ${u.email}?`)) return;

    const body: AdminUserForm = {
      displayName: u.displayName,
      isActive: next,
      requirePasswordChange: false,
      roles: [...(u.roles ?? [])]
    };

    this.api.put<AdminUser>(`/api/admin/users/${u.id}`, body).subscribe({
      next: () => {
        this.notify(`已${verb}`);
        this.loadUsers();
        this.loadStats();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  resetPassword(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    const ref = this.dialog.open(ResetPasswordDialogComponent, {
      data: u.email,
      autoFocus: 'first-tabbable'
    });
    ref.afterClosed().subscribe((pw?: string | null) => {
      if (!pw) return;
      this.api.post<void>(`/api/admin/users/${u.id}/reset-password`, { newPassword: pw })
        .subscribe({
          next: () => this.notify('密码已重置,请通知用户'),
          error: (e: Error) => this.notify(e.message, true)
        });
    });
  }

  deleteUser(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    if (!confirm(`确认删除用户 ${u.email}?此操作不可撤销。`)) return;

    this.api.delete<void>(`/api/admin/users/${u.id}`).subscribe({
      next: () => {
        this.notify('已删除用户');
        this.loadUsers();
        this.loadStats();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  // ============================ 角色 ============================

  loadRoles(): void {
    this.rolesLoading.set(true);
    this.rolesError.set(null);

    this.api.get<AdminRole[]>('/api/admin/roles').subscribe({
      next: (list) => {
        this.roles.set(list ?? []);
        this.rolesLoading.set(false);
        this.rolesLoaded = true;
      },
      error: (e: Error) => {
        this.rolesError.set(e.message);
        this.roles.set([]);
        this.rolesLoading.set(false);
      }
    });
  }

  isRoleExpanded(id: string): boolean {
    return this.expandedRoleIds().includes(id);
  }

  toggleRole(id: string): void {
    this.expandedRoleIds.update((ids) =>
      ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id]
    );
  }

  // ============================ 审计日志 ============================

  loadLogs(): void {
    this.logsLoading.set(true);
    this.logsError.set(null);

    this.api.get<Paged<AuditLog>>('/api/admin/audit', {
      page: this.logPage(),
      pageSize: this.logPageSize()
    }).subscribe({
      next: (r) => {
        this.logs.set(r.items ?? []);
        this.logTotal.set(r.total ?? 0);
        this.logTotalPages.set(r.totalPages ?? 1);
        this.logsLoading.set(false);
        this.logsLoaded = true;
      },
      error: (e: Error) => {
        this.logsError.set(e.message);
        this.logs.set([]);
        this.logsLoading.set(false);
      }
    });
  }

  logGoPage(p: number): void {
    if (p < 1 || p > this.logTotalPages() || p === this.logPage()) return;
    this.logPage.set(p);
    this.loadLogs();
  }

  // ============================ 展示辅助 ============================

  /** 审计动作名彩色标记:危险动作一眼能从滚动列表里挑出来。 */
  actionClass(action: string): string {
    const a = (action || '').toLowerCase();
    if (a.includes('delete') || a.includes('remove')) return 'act-danger';
    if (a.includes('create') || a.includes('add')) return 'act-create';
    if (a.includes('update') || a.includes('edit')) return 'act-update';
    if (a.includes('login') || a.includes('auth')) return 'act-auth';
    return 'act-other';
  }

  private notify(msg: string, isError = false): void {
    this.snack.open(msg, '关闭', {
      duration: isError ? 5000 : 2500,
      horizontalPosition: 'center',
      verticalPosition: 'bottom'
    });
  }

  trackUser(_i: number, u: AdminUser): string {
    return u.id;
  }

  trackLog(_i: number, l: AuditLog): string {
    return l.id;
  }

  trackRole(_i: number, r: AdminRole): string {
    return r.id;
  }
}
