import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiClient } from '../../../core/api/api-client';
import { I18nService } from '../../../core/i18n/i18n.service';

export interface CategoryInfo {
  name: string;
  count: number;
}

/**
 * 分类管理弹窗(对齐 Gmail 标签 / Obsidian Tag Wrangler 的成熟 UX)。
 *
 * - 列表:分类名(点击重命名) + 条目数 + 删除
 * - 重命名为已存在的名字 → 合并警告确认
 * - 删除:有条目的二次确认,条目变未分类;空分类直接删
 * - 新增:顶部输入框,查重
 */
@Component({
  selector: 'app-category-manager',
  standalone: true,
  imports: [CommonModule, FormsModule, MatButtonModule, MatDialogModule,
    MatIconModule, MatInputModule, MatFormFieldModule, MatTooltipModule],
  templateUrl: './category-manager.component.html',
  styleUrl: './category-manager.component.scss'
})
export class CategoryManagerComponent {
  private readonly api = inject(ApiClient);
  private readonly dialogRef = inject(MatDialogRef<CategoryManagerComponent>);
  private readonly i18n = inject(I18nService);

  t = (k: string) => this.i18n.t(k);

  /** 从父组件传入的分类列表(含计数) */
  categories = signal<CategoryInfo[]>([]);

  readonly newName = signal('');
  readonly editingName = signal<string | null>(null);
  readonly editDraft = signal('');
  readonly busy = signal(false);
  readonly error = signal('');

  /** 是否有改动(用于关闭后刷新) */
  changed = false;

  readonly sorted = computed(() =>
    [...this.categories()].sort((a, b) => b.count - a.count || a.name.localeCompare(b.name)));

  readonly newNameExists = computed(() => {
    const n = this.newName().trim().toLowerCase();
    return n.length > 0 && this.categories().some(c => c.name.toLowerCase() === n);
  });

  /** 用户手动新增的空分类,存 localStorage(后端无分类表,空分类只活在前端) */
  private loadCustom(): string[] {
    try {
      return JSON.parse(localStorage.getItem('ts.customCategories') ?? '[]');
    } catch { return []; }
  }
  private saveCustom(names: string[]): void {
    try { localStorage.setItem('ts.customCategories', JSON.stringify(names)); } catch { /* ignore */ }
  }

  /** 新增分类:0 条目的先放 localStorage,等有条目用它后自然转正 */
  addCategory(): void {
    const n = this.newName().trim();
    if (!n || this.newNameExists()) return;
    const custom = this.loadCustom();
    if (!custom.some(x => x.toLowerCase() === n.toLowerCase())) {
      custom.push(n);
      this.saveCustom(custom);
    }
    this.categories.update(list => [...list, { name: n, count: 0 }]);
    this.newName.set('');
    this.changed = true;
  }

  startRename(c: CategoryInfo): void {
    this.editingName.set(c.name);
    this.editDraft.set(c.name);
    this.error.set('');
  }

  cancelRename(): void {
    this.editingName.set(null);
    this.error.set('');
  }

  confirmRename(c: CategoryInfo): void {
    const oldName = c.name;
    const newName = this.editDraft().trim();
    if (!newName || newName.toLowerCase() === oldName.toLowerCase()) {
      this.cancelRename();
      return;
    }
    // 合并:新名字已存在
    const target = this.categories().find(x => x.name.toLowerCase() === newName.toLowerCase());
    if (target) {
      const msg = this.t('ts.cat.mergeWarn')
        .replace('{n}', String(c.count))
        .replace('{from}', oldName)
        .replace('{to}', target.name);
      if (!confirm(msg)) return;
    } else if (c.count > 0) {
      const msg = this.t('ts.cat.renameWarn')
        .replace('{n}', String(c.count))
        .replace('{from}', oldName)
        .replace('{to}', newName);
      if (!confirm(msg)) return;
    }
    this.busy.set(true);
    this.api.put<number>(`/api/knowledge/topics/rename`,
      { oldName, newName }).subscribe({
      next: () => {
        this.categories.update(list => {
          // 移除旧分类
          const rest = list.filter(x => x.name.toLowerCase() !== oldName.toLowerCase());
          if (target) {
            // 合并:目标计数增加
            return rest.map(x => x.name.toLowerCase() === target.name.toLowerCase()
              ? { ...x, count: x.count + c.count } : x);
          }
          // 重命名:加新分类(保持原计数)
          return [...rest, { name: newName, count: c.count }];
        });
        this.editingName.set(null);
        this.busy.set(false);
        this.changed = true;
      },
      error: (e: unknown) => { this.error.set(e instanceof Error ? e.message : String(e)); this.busy.set(false); }
    });
  }

  deleteCategory(c: CategoryInfo): void {
    if (c.count > 0) {
      const msg = this.t('ts.cat.deleteWarn')
        .replace('{n}', String(c.count))
        .replace('{name}', c.name);
      if (!confirm(msg)) return;
    }
    this.busy.set(true);
    // 空分类(0 条目)是纯前端占位,直接移除,同时清 localStorage
    if (c.count === 0) {
      this.categories.update(list => list.filter(x => x.name !== c.name));
      this.saveCustom(this.loadCustom().filter(x => x.toLowerCase() !== c.name.toLowerCase()));
      this.busy.set(false);
      this.changed = true;
      return;
    }
    this.api.delete<number>(`/api/knowledge/topics/${encodeURIComponent(c.name)}`).subscribe({
      next: () => {
        this.categories.update(list => list.filter(x => x.name !== c.name));
        this.busy.set(false);
        this.changed = true;
      },
      error: (e: unknown) => { this.error.set(e instanceof Error ? e.message : String(e)); this.busy.set(false); }
    });
  }

  close(): void {
    this.dialogRef.close(this.changed);
  }
}
