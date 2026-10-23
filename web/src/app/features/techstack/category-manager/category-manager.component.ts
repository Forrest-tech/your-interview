import { Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatTooltipModule } from '@angular/material/tooltip';
import { KnowledgeApiService } from '../../../core/api/knowledge-api.service';
import { TechStackLocalService } from '../../../core/api/techstack-local.service';
import {
  MAX_CATEGORY_NAME_LENGTH, validateCategoryName,
} from '../../../core/api/techstack-local.helpers';
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
  private readonly knowledgeApi = inject(KnowledgeApiService);
  private readonly tsLocal = inject(TechStackLocalService);
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

  /** 票83:新名超长(>100,Trim 后计)—— 字段级提示 + 禁用添加,不再假成功 */
  readonly newNameTooLong = computed(() =>
    validateCategoryName(this.newName()).reason === 'tooLong');

  /** 票83:改名草稿超长 —— 禁用保存,confirm 前拦截(不触发合并/改名确认框) */
  readonly editDraftTooLong = computed(() =>
    validateCategoryName(this.editDraft()).reason === 'tooLong');

  readonly tooLongText = () => this.t('ts.cat.nameTooLong')
    .replace('{max}', String(MAX_CATEGORY_NAME_LENGTH));

  /**
   * 票83:错误文案提取 —— 先看服务层纯校验标记(validation.reason),
   * 再按票12 applyServerError 先例从 ValidationProblemDetails 的
   * errors 字典抠后端原文(含上限 100),最后退 e.message。
   */
  private describeError(e: unknown): string {
    const err = e as {
      message?: string;
      validation?: { reason?: string };
      original?: { error?: { errors?: Record<string, string[]> } };
      error?: { errors?: Record<string, string[]> };
    };
    if (err?.validation?.reason === 'tooLong') return this.tooLongText();
    const raw = err?.original?.error?.errors ?? err?.error?.errors ?? null;
    if (raw && typeof raw === 'object') {
      const texts = Object.values(raw).flat().filter(Boolean);
      if (texts.length) return texts.join(' ');
    }
    return err?.message ?? String(e);
  }

  /**
   * 新增分类:0 条目的空分类经 TechStackLocalService 落后端(票16),等有条目用它后自然转正。
   * 票83:提交前纯校验先挡超长;订阅服务结果 —— **成功才**进弹窗列表/清输入,
   * 失败只报错、不动列表(旧实现无条件加列表 = 假成功、刷新即失,D4b)。
   */
  addCategory(): void {
    const n = this.newName().trim();
    if (!n || this.newNameExists()) return;
    const v = validateCategoryName(n);
    if (!v.valid) {
      this.error.set(v.reason === 'tooLong' ? this.tooLongText() : '');
      return;
    }
    this.error.set('');
    this.busy.set(true);
    this.tsLocal.addCategory(n).subscribe({
      next: () => {
        this.categories.update(list => [...list, { name: n, count: 0 }]);
        this.newName.set('');
        this.changed = true;
        this.busy.set(false);
      },
      error: (e: unknown) => { this.error.set(this.describeError(e)); this.busy.set(false); }
    });
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
    // 票83:超长在 confirm 之前拦截 —— 字段级报错、保持编辑态,不触发确认框
    if (!validateCategoryName(newName).valid) {
      this.error.set(this.tooLongText());
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
    // 空分类(0 条目)是后端分类表里的占位行,改名走分类服务(删旧+加新);
    // 改前实现对空分类也打 topics/rename(无条目可改、恒返回 0)且旧名残留
    // localStorage 会「复活」—— 票16 服务化后在此收口,UI/文案不变。
    if (c.count === 0) {
      // 票83:订阅结果 —— 成功才改弹窗列表;失败报错并保持编辑态,不假成功
      this.busy.set(true);
      this.tsLocal.renameCategory(oldName, newName).subscribe({
        next: () => {
          this.categories.update(list => {
            const rest = list.filter(x => x.name.toLowerCase() !== oldName.toLowerCase());
            if (target) {
              return rest.map(x => x.name.toLowerCase() === target.name.toLowerCase()
                ? { ...x, count: x.count + c.count } : x);
            }
            return [...rest, { name: newName, count: c.count }];
          });
          // 票84 邻居:改名已落服务端 —— 同步通知页面呈现(条目卡/chip),不等关弹窗
          this.tsLocal.notifyCategoryRenamed(oldName, target ? target.name : newName);
          this.editingName.set(null);
          this.changed = true;
          this.busy.set(false);
        },
        error: (e: unknown) => { this.error.set(this.describeError(e)); this.busy.set(false); }
      });
      return;
    }
    this.busy.set(true);
    this.knowledgeApi.renameTopic(oldName, newName).subscribe({
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
        // 票84 邻居:该名若同时是自定义行(先建空分类后挂条目),只改 topics 会把
        // 旧名自定义行留在服务端 —— 整页刷新后旧名以空分类身份在管理器复活。
        // 信号里确有旧名时,经分类服务把自定义行一并改名(票83 的 PUT 先行口径);
        // 从未注册过的 topic 不凭空建行,不订阅结果之外的呈现(失败由服务回读纠正)。
        if (this.tsLocal.customCategories().some(x => x.toLowerCase() === oldName.toLowerCase())) {
          this.tsLocal.renameCategory(oldName, target ? target.name : newName)
            .subscribe({ error: () => undefined });
        }
        // 票84 邻居:改名已落服务端 —— 同步通知页面呈现(条目卡/chip),不等关弹窗
        this.tsLocal.notifyCategoryRenamed(oldName, target ? target.name : newName);
        this.editingName.set(null);
        this.busy.set(false);
        this.changed = true;
      },
      error: (e: unknown) => { this.error.set(this.describeError(e)); this.busy.set(false); }
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
    // 空分类(0 条目)是后端分类表占位行,经分类服务删除(票16)
    if (c.count === 0) {
      this.categories.update(list => list.filter(x => x.name !== c.name));
      this.tsLocal.removeCategory(c.name);
      // 票84:与 removeCategory 的乐观信号同拍通知页面(空分类无条目受影响,
      // 页面同步为无害空转;若计数口径下有漏计条目则正好一并回落)
      this.tsLocal.notifyCategoryDeleted(c.name);
      this.busy.set(false);
      this.changed = true;
      return;
    }
    this.knowledgeApi.deleteTopic(c.name).subscribe({
      next: () => {
        this.categories.update(list => list.filter(x => x.name !== c.name));
        // 票84:该名若同时是自定义行(先建空分类后挂条目,T6 同形),只删 topics
        // 会把自定义行留在服务端 —— 整页刷新后它会以空分类身份在管理器复活。
        // 同一删除动作一并删掉该名的自定义行(端点幂等,不存在也成功;
        // 服务端各端点行为不动,只是前端把同一次删除做完整)。
        this.tsLocal.removeCategory(c.name);
        // 票84:删除已落服务端 —— 通知页面即时回落条目卡/chip,不等关弹窗/刷新(F4)
        this.tsLocal.notifyCategoryDeleted(c.name);
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
