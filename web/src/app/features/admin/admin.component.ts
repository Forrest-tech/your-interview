import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, of } from 'rxjs';

import { ApiClient } from '../../core/api/api-client';
import { AuthService } from '../../core/auth/auth.service';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  AdminPermission, AdminRole, AdminStats, AdminUser, AuditLog, Paged
} from '../../core/models/api.models';
import { AdminResetPasswordDialogComponent } from './admin-reset-password-dialog.component';
import {
  AdminRoleDialogComponent, AdminRoleForm
} from './admin-role-dialog.component';
import { AdminUserDialogComponent, AdminUserForm } from './admin-user-dialog.component';

/**
 * 管理后台:统计 + 用户 / 角色 / 审计日志三个页签。
 *
 * ★ M2.4 补全说明:
 *   之前这个页面只是"能看"——统计卡的字段名是凭空写的(全是 undefined),
 *   审计日志的对象/IP 两列恒为 —,而后端早已提供的新建用户、角色权限编辑、
 *   解锁账号等能力在界面上根本没有入口。这一轮把它们全部补齐,并加上
 *   "管理员不能把自己锁在门外"的自我保护。
 *
 * 用页签而不是三张独立页面:管理员通常要在"改用户 → 查日志确认生效"之间来回切,
 * 页签保留各 tab 的滚动位置与已加载数据,切换成本几乎为零。
 * 数据懒加载:切到哪个 tab 才拉哪个接口,避免进页面就打一堆请求。
 */
@Component({
  selector: 'app-admin',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatCardModule, MatIconModule, MatButtonModule,
    MatChipsModule, MatTabsModule, MatTableModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatProgressBarModule, MatTooltipModule, MatDialogModule, MatSnackBarModule
  ],
  templateUrl: './admin.component.html',
  styleUrl: './admin.component.scss'
})
export class AdminComponent implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  t = (key: string): string => this.i18n.t(key);
  /** 带占位符的文案(Angular 模板里拿不到全局 String());计数或邮箱/角色名都走它。 */
  tn = (key: string, n: string | number): string => this.i18n.tn(key, n);

  // -------- 权限:按钮级显隐,与后端 perm 策略同源 --------
  readonly canWriteUsers = computed(() => this.auth.can('admin.users.write'));
  readonly canWriteRoles = computed(() => this.auth.can('admin.roles.write'));
  private readonly meId = computed(() => this.auth.user()?.id ?? null);

  // -------- 统计 --------
  readonly stats = signal<AdminStats | null>(null);

  // -------- 用户 --------
  readonly users = signal<AdminUser[]>([]);
  readonly usersLoading = signal(false);
  readonly usersError = signal<string | null>(null);
  readonly userSearch = signal('');
  readonly userRoleFilter = signal('');
  readonly userStatusFilter = signal('');
  readonly userPage = signal(1);
  readonly userPageSize = signal(20);
  readonly userTotal = signal(0);
  readonly userTotalPages = signal(1);

  readonly userColumns: string[] = [
    'email', 'displayName', 'roles', 'isActive', 'lastLoginAt', 'actions'
  ];

  // -------- 角色 --------
  readonly roles = signal<AdminRole[]>([]);
  readonly permissions = signal<AdminPermission[]>([]);
  readonly rolesLoading = signal(false);
  readonly rolesError = signal<string | null>(null);
  /** 展开的角色 id 集合 —— 权限芯片默认折叠,角色多时页面才不会太长。 */
  readonly expandedRoleIds = signal<string[]>([]);

  // -------- 审计日志 --------
  readonly logs = signal<AuditLog[]>([]);
  readonly logsLoading = signal(false);
  readonly logsError = signal<string | null>(null);
  readonly logAction = signal('');
  readonly logActor = signal('');
  readonly logFrom = signal('');
  readonly logTo = signal('');
  readonly logPage = signal(1);
  readonly logPageSize = signal(30);
  readonly logTotal = signal(0);
  readonly logTotalPages = signal(1);

  /** 操作人下拉的候选用户(审计筛选用,一次性拉全量)。 */
  readonly actorOptions = signal<AdminUser[]>([]);

  readonly logColumns: string[] = [
    'occurredAt', 'action', 'resource', 'userEmail', 'ipAddress', 'detail'
  ];

  // -------- 懒加载标记:避免每次切 tab 都重复请求 --------
  private usersLoaded = false;
  private rolesLoaded = false;
  private logsLoaded = false;
  private actorsLoaded = false;

  readonly activeTab = signal(0);

  readonly statRows = computed(() => {
    const s = this.stats();
    if (!s) return [];
    return [
      { label: this.t('admin.metricUsers'), value: s.totalUsers, icon: 'people_outline' },
      { label: this.t('admin.metricActive'), value: s.activeUsers, icon: 'person_outline' },
      { label: this.t('admin.metricNew7d'), value: s.newUsersLast7Days, icon: 'person_add' },
      { label: this.t('admin.metricRoles'), value: s.totalRoles, icon: 'badge' },
      { label: this.t('admin.metricAudit'), value: s.totalAuditEvents, icon: 'history' },
      { label: this.t('admin.metricFailed'), value: s.failedLoginsLast24h, icon: 'gpp_maybe' },
      { label: this.t('admin.metricLocked'), value: s.lockedAccounts, icon: 'lock_person' }
    ];
  });

  ngOnInit(): void {
    this.loadStats();
    // 角色与权限点随页面一起拉:新建/编辑用户要用到角色下拉,
    // 等到打开弹窗才去取会闪一下空列表。这两个接口都是几行的量,不值当省。
    this.loadRoles();
    this.loadPermissions();
    // 默认停在第一个 tab,顺手加载它,免得用户看到空白
    this.onTabChange(0);
  }

  /** tab 切换时才拉数据 —— 三个接口全在进页面时打会拖慢首屏。 */
  onTabChange(index: number): void {
    this.activeTab.set(index);
    if (index === 0 && !this.usersLoaded) this.loadUsers();
    if (index === 1 && !this.rolesLoaded) { this.loadRoles(); this.loadPermissions(); }
    if (index === 2 && !this.logsLoaded) { this.loadLogs(); this.loadActors(); }
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
      search: this.userSearch().trim(),
      role: this.userRoleFilter(),
      isActive: this.userStatusFilter()
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

  /** 角色 / 状态筛选变化时立即重查 —— 下拉本来就是明确选择,不需要再按回车。 */
  onUserFilterChange(): void {
    this.userPage.set(1);
    this.loadUsers();
  }

  userGoPage(p: number): void {
    if (p < 1 || p > this.userTotalPages() || p === this.userPage()) return;
    this.userPage.set(p);
    this.loadUsers();
  }

  isSelf(u: AdminUser): boolean {
    return this.meId() !== null && this.meId() === u.id;
  }

  isLocked(u: AdminUser): boolean {
    return !!u.lockedUntil && new Date(u.lockedUntil).getTime() > Date.now();
  }

  openCreateUser(): void {
    const ref = this.dialog.open(AdminUserDialogComponent, {
      data: { mode: 'create', roles: this.roles() },
      autoFocus: 'first-tabbable'
    });
    ref.afterClosed().subscribe((form?: AdminUserForm | null) => {
      if (!form) return;
      this.api.post<{ id: string }>('/api/admin/users', form).subscribe({
        next: () => {
          this.notify(this.t('admin.created'));
          this.usersLoaded = false;
          this.loadUsers();
          this.loadStats();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  openEditUser(u: AdminUser): void {
    const ref = this.dialog.open(AdminUserDialogComponent, {
      data: { mode: 'edit', user: u, roles: this.roles() },
      autoFocus: 'first-tabbable'
    });
    ref.afterClosed().subscribe((form?: AdminUserForm | null) => {
      if (!form) return;
      this.api.put<void>(`/api/admin/users/${u.id}`, {
        displayName: form.displayName,
        isActive: form.isActive,
        requirePasswordChange: form.requirePasswordChange,
        roles: form.roles
      }).subscribe({
        next: () => {
          this.notify(this.t('admin.saved'));
          this.loadUsers();
          this.loadStats();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  /** 启用/停用:整体更新语义,把当前字段一起回传。 */
  toggleActive(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    const next = !u.isActive;
    if (!confirm(this.tn(next ? 'admin.confirmActivate' : 'admin.confirmDeactivate', u.email))) return;

    this.api.put<void>(`/api/admin/users/${u.id}`, {
      displayName: u.displayName,
      isActive: next,
      requirePasswordChange: u.mustChangePassword,
      roles: [...(u.roles ?? [])]
    }).subscribe({
      next: () => {
        this.notify(next ? this.t('admin.activated') : this.t('admin.deactivated'));
        this.loadUsers();
        this.loadStats();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  resetPassword(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    const ref = this.dialog.open(AdminResetPasswordDialogComponent, {
      data: u.email,
      autoFocus: 'first-tabbable'
    });
    ref.afterClosed().subscribe((pw?: string | null) => {
      if (!pw) return;
      this.api.post<void>(`/api/admin/users/${u.id}/reset-password`, { newPassword: pw })
        .subscribe({
          next: () => this.notify(this.t('admin.pwdReset')),
          error: (e: Error) => this.notify(e.message, true)
        });
    });
  }

  unlock(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    this.api.post<void>(`/api/admin/users/${u.id}/unlock`, {}).subscribe({
      next: () => {
        this.notify(this.t('admin.unlocked'));
        this.loadUsers();
        this.loadStats();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  deleteUser(u: AdminUser, ev?: Event): void {
    ev?.stopPropagation();
    if (!confirm(this.tn('admin.confirmDelete', u.email))) return;

    this.api.delete<void>(`/api/admin/users/${u.id}`).subscribe({
      next: () => {
        this.notify(this.t('admin.deleted'));
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

  loadPermissions(): void {
    this.api.get<AdminPermission[]>('/api/admin/permissions')
      .pipe(catchError(() => of([])))
      .subscribe((list) => this.permissions.set(list ?? []));
  }

  isRoleExpanded(id: string): boolean {
    return this.expandedRoleIds().includes(id);
  }

  toggleRole(id: string): void {
    this.expandedRoleIds.update((ids) =>
      ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id]
    );
  }

  openCreateRole(): void {
    const ref = this.dialog.open(AdminRoleDialogComponent, {
      data: { mode: 'create', permissions: this.permissions() },
      autoFocus: 'first-tabbable'
    });
    ref.afterClosed().subscribe((form?: AdminRoleForm | null) => {
      if (!form) return;
      this.api.post<{ id: string }>('/api/admin/roles', form).subscribe({
        next: () => {
          this.notify(this.t('admin.roleSaved'));
          this.loadRoles();
          this.loadStats();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  openEditRole(r: AdminRole, ev?: Event): void {
    ev?.stopPropagation();
    const ref = this.dialog.open(AdminRoleDialogComponent, {
      data: { mode: 'edit', role: r, permissions: this.permissions() },
      autoFocus: 'first-tabbable'
    });
    ref.afterClosed().subscribe((form?: AdminRoleForm | null) => {
      if (!form) return;
      this.api.put<void>(`/api/admin/roles/${r.id}`, form).subscribe({
        next: () => {
          this.notify(this.t('admin.roleSaved'));
          this.loadRoles();
        },
        error: (e: Error) => this.notify(e.message, true)
      });
    });
  }

  deleteRole(r: AdminRole, ev?: Event): void {
    ev?.stopPropagation();
    if (r.isSystemRole) {
      this.notify(this.t('admin.systemRoleReadonly'), true);
      return;
    }
    if (!confirm(this.tn('admin.confirmDeleteRole', r.name))) return;

    this.api.delete<void>(`/api/admin/roles/${r.id}`).subscribe({
      next: () => {
        this.notify(this.t('admin.roleDeleted'));
        this.loadRoles();
        this.loadStats();
      },
      error: (e: Error) => this.notify(e.message, true)
    });
  }

  // ============================ 审计日志 ============================

  loadLogs(): void {
    this.logsLoading.set(true);
    this.logsError.set(null);

    this.api.get<Paged<AuditLog>>('/api/admin/audit', {
      page: this.logPage(),
      pageSize: this.logPageSize(),
      action: this.logAction().trim(),
      userId: this.logActor(),
      from: this.logFrom(),
      // 结束日期按当天 23:59:59 处理,否则选中当天也查不到当天的记录
      to: this.logTo() ? `${this.logTo()}T23:59:59` : ''
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

  loadActors(): void {
    if (this.actorsLoaded) return;
    this.api.get<Paged<AdminUser>>('/api/admin/users', { page: 1, pageSize: 200 })
      .pipe(catchError(() => of(null)))
      .subscribe((r) => {
        this.actorOptions.set(r?.items ?? []);
        this.actorsLoaded = true;
      });
  }

  applyLogFilters(): void {
    this.logPage.set(1);
    this.loadLogs();
  }

  clearLogFilters(): void {
    this.logAction.set('');
    this.logActor.set('');
    this.logFrom.set('');
    this.logTo.set('');
    this.applyLogFilters();
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
    if (a.startsWith('admin.')) return 'act-admin';
    return 'act-other';
  }

  /** 成功/失败圆点 —— 失败的操作在审计里最该被先看到。 */
  resultClass(success: boolean): string {
    return success ? 'res-ok' : 'res-fail';
  }

  pageInfo(cur: number, total: number): string {
    return this.t('admin.pageInfo').replace('{n}', String(cur)).replace('{m}', String(total));
  }

  private notify(msg: string, isError = false): void {
    this.snack.open(msg, this.t('common.close'), {
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
