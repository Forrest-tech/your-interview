import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { I18nService } from '../../core/i18n/i18n.service';
import { AdminPermission, AdminRole } from '../../core/models/api.models';

export interface AdminRoleDialogData {
  mode: 'create' | 'edit';
  role?: AdminRole;
  /** 权限点目录(GET /api/admin/permissions),按 resource 分组渲染。 */
  permissions: AdminPermission[];
}

/** 与 POST/PUT /api/admin/roles 的 body 对应。 */
export interface AdminRoleForm {
  name: string;
  description?: string;
  permissions: string[];
}

interface PermGroup {
  resource: string;
  items: AdminPermission[];
}

/**
 * 新建 / 编辑角色弹窗。
 *
 * 权限点按资源分组而不是一条长列表:
 *   21 个权限点平铺成一列,管理员要勾"mock 相关的三个"得上下翻找两屏;
 *   分组后每组一行,眼睛扫一遍就能定位。
 */
@Component({
  selector: 'app-admin-role-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatCheckboxModule, MatButtonModule, MatIconModule, MatTooltipModule
  ],
  template: `
    <h2 mat-dialog-title>{{ isEdit ? t('admin.editRole') : t('admin.newRole') }}</h2>

    <mat-dialog-content class="rd-body">
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('admin.fieldRoleName') }}</mat-label>
        <input matInput name="roleName" [disabled]="systemRole" [(ngModel)]="name"
               autocomplete="off">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('admin.fieldRoleDesc') }}</mat-label>
        <input matInput name="roleDesc" [(ngModel)]="description" autocomplete="off">
      </mat-form-field>

      @if (systemRole) {
        <p class="sys-note"><mat-icon>lock</mat-icon> {{ t('admin.systemRoleReadonly') }}</p>
      }

      <div class="perm-head">
        <span class="perm-title">{{ tn('admin.permCount', selected().length) }}</span>
        <span class="spacer"></span>
        <button mat-button type="button" (click)="selectAll()">{{ t('admin.selectAll') }}</button>
        <button mat-button type="button" (click)="clearAll()">{{ t('admin.clearAll') }}</button>
      </div>

      <div class="perm-groups">
        @for (g of groups(); track g.resource) {
          <div class="perm-group">
            <div class="group-head">
              <span class="group-name">{{ g.resource }}</span>
              <span class="group-meta">
                {{ pickedIn(g) }} / {{ g.items.length }}
              </span>
              <button mat-button type="button" class="mini"
                      (click)="toggleGroup(g)">
                {{ pickedIn(g) === g.items.length ? t('admin.clearAll') : t('admin.selectAll') }}
              </button>
            </div>
            <div class="perm-items">
              @for (p of g.items; track p.key) {
                <mat-checkbox [checked]="selected().includes(p.key)"
                              (change)="toggle(p.key, $any($event).checked)"
                              [matTooltip]="p.description">
                  <span class="perm-key">{{ p.key }}</span>
                </mat-checkbox>
              }
            </div>
          </div>
        }
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
    .rd-body { display: flex; flex-direction: column; min-width: 560px; max-width: 720px; }
    .full { width: 100%; }
    .sys-note {
      display: flex; align-items: center; gap: 6px;
      margin: -4px 0 8px; font-size: 12.5px; opacity: 0.65;
    }
    .sys-note mat-icon { font-size: 16px; width: 16px; height: 16px; }
    .perm-head { display: flex; align-items: center; gap: 4px; margin: 6px 0 8px; }
    .perm-title { font-size: 12.5px; font-weight: 600; opacity: 0.75; }
    .spacer { flex: 1 1 auto; }
    .perm-groups {
      display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
      gap: 10px; max-height: 46vh; overflow-y: auto; padding-right: 4px;
    }
    .perm-group { border: 1px solid rgba(0, 0, 0, 0.08); border-radius: 8px; padding: 8px 10px; }
    .group-head { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
    .group-name { font-size: 13px; font-weight: 600; text-transform: capitalize; }
    .group-meta { font-size: 11.5px; opacity: 0.5; }
    .mini { --mdc-button-label-text-size: 11.5px; font-size: 11.5px; min-width: 0; }
    .perm-items { display: flex; flex-direction: column; }
    .perm-key {
      font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px;
    }
    .err {
      display: flex; align-items: center; gap: 6px;
      margin-top: 8px; padding: 8px 12px; border-radius: 6px;
      background: #fdecea; color: #b3261e; font-size: 13px;
    }
    .err mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `]
})
export class AdminRoleDialogComponent {
  private readonly ref = inject(MatDialogRef<AdminRoleDialogComponent, AdminRoleForm | null>);
  readonly data = inject<AdminRoleDialogData>(MAT_DIALOG_DATA);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  /** 带计数占位符的文案(Angular 模板里拿不到全局 String())。 */
  tn = (key: string, n: number): string => this.i18n.tn(key, n);

  readonly isEdit = this.data.mode === 'edit';
  /** 系统内置角色:名字锁死,只有说明可改。 */
  readonly systemRole = this.data.role?.isSystemRole === true;

  name = this.data.role?.name ?? '';
  description = this.data.role?.description ?? '';
  readonly selected = signal<string[]>([...(this.data.role?.permissions ?? [])]);
  readonly error = signal<string | null>(null);

  readonly groups = computed<PermGroup[]>(() => {
    const byResource = new Map<string, AdminPermission[]>();
    for (const p of this.data.permissions) {
      const list = byResource.get(p.resource) ?? [];
      list.push(p);
      byResource.set(p.resource, list);
    }
    return [...byResource.entries()]
      .map(([resource, items]) => ({ resource, items }))
      .sort((a, b) => a.resource.localeCompare(b.resource));
  });

  pickedIn(g: PermGroup): number {
    const s = this.selected();
    return g.items.filter((p) => s.includes(p.key)).length;
  }

  toggle(key: string, on: boolean): void {
    this.selected.update((cur) =>
      on ? (cur.includes(key) ? cur : [...cur, key]) : cur.filter((k) => k !== key));
  }

  toggleGroup(g: PermGroup): void {
    const keys = g.items.map((p) => p.key);
    const allPicked = this.pickedIn(g) === g.items.length;
    this.selected.update((cur) =>
      allPicked ? cur.filter((k) => !keys.includes(k))
        : [...cur.filter((k) => !keys.includes(k)), ...keys]);
  }

  selectAll(): void {
    this.selected.set(this.data.permissions.map((p) => p.key));
  }

  clearAll(): void {
    this.selected.set([]);
  }

  canSave(): boolean {
    return this.name.trim().length > 0;
  }

  save(): void {
    if (!this.canSave()) {
      this.error.set(this.t('admin.fieldRoleName'));
      return;
    }
    this.ref.close({
      name: this.name.trim(),
      description: this.description?.trim() || undefined,
      permissions: this.selected()
    });
  }
}
