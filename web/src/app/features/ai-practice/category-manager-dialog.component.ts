import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { CdkDragDrop, DragDropModule, moveItemInArray } from '@angular/cdk/drag-drop';
import { firstValueFrom } from 'rxjs';

import { PracticeApi, PracticeCategoryDto } from '../../core/api/practice-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { MaterialNode } from '../../shared/material-tree/material-tree.component';

interface DialogData {
  /** 打开弹窗时的类别快照(弹窗内自己维护副本,关闭时回传最终列表)。 */
  categories: PracticeCategoryDto[];
}

/** 关闭回传:类别终表 + 可选的「模板导入」产生的素材根节点。 */
export interface CategoryManagerResult {
  categories: PracticeCategoryDto[];
  importedNodes?: MaterialNode[];
}

/** 模板定义:一个模板 = 若干类别,每类一棵素材树(全部走 i18n 取名)。 */
interface TemplateCategoryDef { nameKey: string; folders: { nameKey: string; files: string[] }[] }
interface TemplateDef { key: string; labelKey: string; descKey: string; categories: TemplateCategoryDef[] }

/**
 * 练习类别管理弹窗(★ 2026-09-25 Forrest)。
 *
 * UX 参考(业界成熟模式):
 *  · Anki 卡组 / Notion 数据库视图的"管理"弹窗:列表 + 底部新增,
 *    高频的"改个名、拖个序"就地完成,不打断;
 *  · 双击名称进入就地重命名(Enter / 失焦提交,Esc 取消),
 *    与 VS Code / Finder 的文件重命名交互一致;
 *  · 删除采用**两步确认**(点一下变红色确认态,再点才真删),
 *    免弹系统 confirm,也不至于误触丢类别;
 *  · 拖拽排序用 CDK DragDrop,松手即持久化,无需"保存"按钮。
 *
 * 数据流:弹窗内对副本做增删改,每个动作直接调 API 持久化;
 * done() 关闭时回传最终列表,父级刷新下拉与素材树。
 */
@Component({
  selector: 'app-category-manager-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatTooltipModule, MatSnackBarModule, DragDropModule
  ],
  template: `
    <h2 mat-dialog-title>{{ t('practice.categoryManageTitle') }}</h2>

    <mat-dialog-content class="cat-body">
      <p class="cat-hint">{{ t('practice.categoryHint') }}</p>

      @if (items().length > 0) {
        <div class="cat-list" cdkDropList cdkDropListOrientation="vertical"
             (cdkDropListDropped)="drop($event)">
          @for (c of items(); track c.id) {
            <div class="cat-row" cdkDrag [class.confirming]="confirmDeleteId() === c.id">
              <mat-icon class="drag-handle" cdkDragHandle
                        [matTooltip]="t('practice.categoryDrag')">drag_indicator</mat-icon>

              @if (editingId() === c.id) {
                <input class="cat-edit" [(ngModel)]="editName"
                       (keyup.enter)="commitRename(c)" (keyup.escape)="cancelRename()"
                       (blur)="commitRename(c)" />
              } @else {
                <span class="cat-name" (dblclick)="startRename(c)">{{ c.name }}</span>
              }

              <span class="spacer"></span>

              @if (editingId() === c.id) {
                <button mat-icon-button (mousedown)="commitRename(c)"
                        [matTooltip]="t('dialog.saveConfirm')">
                  <mat-icon>check</mat-icon>
                </button>
              } @else {
                <button mat-icon-button (click)="startRename(c)"
                        [matTooltip]="t('practice.categoryRename')">
                  <mat-icon>edit</mat-icon>
                </button>
              }

              @if (confirmDeleteId() === c.id) {
                <span class="confirm-text">{{ t('practice.categoryDelConfirm') }}</span>
                <button mat-icon-button class="del-yes" (click)="doDelete(c)">
                  <mat-icon>check</mat-icon>
                </button>
                <button mat-icon-button (click)="confirmDeleteId.set(null)">
                  <mat-icon>close</mat-icon>
                </button>
              } @else if (editingId() !== c.id) {
                <button mat-icon-button (click)="askDelete(c)"
                        [matTooltip]="t('practice.categoryDelete')">
                  <mat-icon>delete_outline</mat-icon>
                </button>
              }
            </div>
          }
        </div>
      } @else {
        <p class="cat-empty">{{ t('practice.categoryEmpty') }}</p>
      }

      <!-- ★ 2026-09-25(Forrest 第二轮):「未分类」系统行常驻底部。
           参考 Gmail 的系统标签(收件箱/已加星标不可删改,固定在标签列表):
           它不是用户数据,永远存在,所以放列表外、锁图标 + 说明,
           与可拖拽/可删除的用户类别在视觉上明显区分。 -->
      <div class="cat-row cat-system">
        <mat-icon class="sys-lock">lock_outline</mat-icon>
        <span class="cat-name">{{ t('practice.categoryUncategorized') }}</span>
        <span class="sys-note">{{ t('practice.categorySystemNote') }}</span>
      </div>

      <div class="cat-add">
        <input class="cat-new" [(ngModel)]="newName"
               [placeholder]="t('practice.categoryNewPlaceholder')"
               (keyup.enter)="add()" maxlength="100" />
        <button mat-stroked-button [disabled]="!newName.trim() || saving()" (click)="add()">
          <mat-icon>add</mat-icon>{{ t('practice.categoryAdd') }}
        </button>
      </div>

      <!-- ★ 2026-09-25(Forrest 第二轮):导入模板 ——
           一键生成"类别 + 素材骨架",结构导入后可随意改名/拖动/删除。
           UX 参考图形工具的 template picker:下拉选模板 → 说明文字预览 →
           显式点「导入」才执行,不搞一次点击就写库的隐式行为。 -->
      <div class="cat-import">
        <div class="cat-import-title">{{ t('practice.templateTitle') }}</div>
        <div class="cat-import-row">
          <select class="cat-tpl-select" [(ngModel)]="selectedTemplateKey">
            <option [value]="''" disabled>{{ t('practice.templatePlaceholder') }}</option>
            @for (tpl of templates; track tpl.key) {
              <option [value]="tpl.key">{{ t(tpl.labelKey) }}</option>
            }
          </select>
          <button mat-stroked-button
                  [disabled]="!selectedTemplateKey || importing() || saving()"
                  (click)="importTemplate()">
            <mat-icon>download</mat-icon>{{ t('practice.templateImport') }}
          </button>
        </div>
        @if (templateByKey(selectedTemplateKey); as tpl) {
          <p class="cat-tpl-desc">{{ t(tpl.descKey) }}</p>
        }
      </div>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button (click)="done()">{{ t('common.close') }}</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .cat-body { min-width: 380px; max-width: 460px; }
    .cat-hint { margin: 0 0 12px; font-size: 12.5px; color: #7a8393; }

    .cat-list { display: flex; flex-direction: column; gap: 4px; }
    .cat-row {
      display: flex; align-items: center; gap: 4px;
      padding: 4px 6px;
      background: #fff;
      border: 1px solid #e3e7ee;
      border-radius: 8px;
    }
    .cat-row.confirming { border-color: #f0b4b4; background: #fdf3f3; }

    .drag-handle {
      cursor: grab; color: #a7b0bf;
      font-size: 18px; width: 18px; height: 18px;
    }
    .drag-handle:active { cursor: grabbing; }
    .cdk-drag-preview .drag-handle, .cdk-drag-dragging .drag-handle { cursor: grabbing; }

    .cat-name {
      flex: 1; min-width: 0;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      font-size: 13.5px; color: #333a44;
      cursor: default;
    }
    .cat-edit {
      flex: 1; min-width: 0;
      height: 28px; padding: 0 8px;
      font-size: 13px;
      border: 1px solid #2f6fed; border-radius: 6px;
      outline: none;
    }

    .spacer { flex: 1; }

    .confirm-text { font-size: 12px; color: #c0392b; white-space: nowrap; }
    .del-yes { color: #c0392b !important; }

    .cat-empty { margin: 4px 0 10px; font-size: 13px; color: #9aa3b0; }

    /* ★ 未分类系统行:锁图标 + 灰调,不可拖/不可删 */
    .cat-row.cat-system { margin-top: 8px; background: #f6f8fb; border-style: dashed; }
    .sys-lock { color: #a7b0bf; font-size: 18px; width: 18px; height: 18px; }
    .sys-note { font-size: 11.5px; color: #9aa3b0; white-space: nowrap; }

    /* ★ 导入模板区 */
    .cat-import {
      margin-top: 12px; padding-top: 12px;
      border-top: 1px solid #eef0f4;
    }
    .cat-import-title {
      font-size: 12.5px; font-weight: 600; color: #55606f;
      margin-bottom: 8px;
    }
    .cat-import-row { display: flex; align-items: center; gap: 8px; }
    .cat-tpl-select {
      flex: 1; min-width: 0;
      height: 32px; padding: 0 8px;
      font-size: 13px; font-family: inherit;
      border: 1px solid #d7dce4; border-radius: 6px;
      outline: none; background: #fff; color: #333a44;
      transition: border-color .15s ease, box-shadow .15s ease;
    }
    .cat-tpl-select:focus { border-color: #2f6fed; box-shadow: 0 0 0 2px rgba(47,111,237,.14); }
    .cat-tpl-desc { margin: 6px 2px 0; font-size: 12px; color: #8a93a2; }

    .cat-add {
      display: flex; align-items: center; gap: 8px;
      margin-top: 14px; padding-top: 12px;
      border-top: 1px solid #eef0f4;
    }
    .cat-new {
      flex: 1; min-width: 0;
      height: 32px; padding: 0 10px;
      font-size: 13px;
      border: 1px solid #d7dce4; border-radius: 6px;
      outline: none;
      transition: border-color .15s ease, box-shadow .15s ease;
    }
    .cat-new:focus { border-color: #2f6fed; box-shadow: 0 0 0 2px rgba(47,111,237,.14); }

    .cdk-drag-preview {
      box-shadow: 0 4px 14px rgba(20, 30, 50, .16);
      border-radius: 8px;
    }
    .cdk-drag-placeholder { opacity: .35; }
  `]
})
export class CategoryManagerDialogComponent {
  readonly dialogRef = inject<MatDialogRef<CategoryManagerDialogComponent, CategoryManagerResult>>(MatDialogRef);
  readonly data = inject<DialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(PracticeApi);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** 弹窗内工作副本(排序/增删都先改这里,动作本身直接持久化)。 */
  readonly items = signal<PracticeCategoryDto[]>([...(this.data.categories ?? [])]);

  newName = '';
  saving = signal(false);
  editingId = signal<string | null>(null);
  editName = '';
  /** 两步删除:正在等待二次确认的类别 id。 */
  readonly confirmDeleteId = signal<string | null>(null);

  private err(e: unknown): string {
    const msg = (e as { message?: string; error?: { detail?: string; title?: string } } | null);
    return msg?.error?.detail || msg?.error?.title || msg?.message || this.t('practice.errUnknown');
  }

  private notify(msg: string): void {
    this.snack.open(msg, this.t('common.close'), { duration: 3000 });
  }

  // ---------- 新增 ----------
  add(): void {
    const name = this.newName.trim();
    if (!name || this.saving()) return;
    this.saving.set(true);
    this.api.createCategory(name).subscribe({
      next: (created) => {
        this.items.update((list) => [...list, created]);
        this.newName = '';
        this.saving.set(false);
      },
      error: (e) => {
        this.saving.set(false);
        this.notify(this.err(e));
      }
    });
  }

  // ---------- 重命名(就地编辑,Enter/失焦提交,Esc 取消) ----------
  startRename(c: PracticeCategoryDto): void {
    this.confirmDeleteId.set(null);
    this.editingId.set(c.id);
    this.editName = c.name;
  }

  cancelRename(): void {
    this.editingId.set(null);
  }

  commitRename(c: PracticeCategoryDto): void {
    if (this.editingId() !== c.id) return;          // Enter 后紧跟的 blur 是空操作
    const name = this.editName.trim();
    this.editingId.set(null);
    if (!name || name === c.name) return;
    this.api.renameCategory(c.id, name).subscribe({
      next: () => { c.name = name; },
      error: (e) => this.notify(this.err(e))
    });
  }

  // ---------- 删除(两步确认) ----------
  askDelete(c: PracticeCategoryDto): void {
    this.confirmDeleteId.set(c.id);
  }

  doDelete(c: PracticeCategoryDto): void {
    this.confirmDeleteId.set(null);
    this.api.deleteCategory(c.id).subscribe({
      next: () => this.items.update((list) => list.filter((x) => x.id !== c.id)),
      error: (e) => this.notify(this.err(e))
    });
  }

  // ---------- 拖拽排序(松手即持久化) ----------
  drop(ev: CdkDragDrop<PracticeCategoryDto[]>): void {
    const list = [...this.items()];
    if (ev.previousIndex === ev.currentIndex) return;
    moveItemInArray(list, ev.previousIndex, ev.currentIndex);
    this.items.set(list);
    this.api.reorderCategories(list.map((x) => x.id)).subscribe({
      error: (e) => this.notify(this.err(e))
    });
  }

  // ---------- 导入模板(★ 2026-09-25 Forrest 第二轮) ----------

  /** 内置模板:求职面试 / 生活英语 / 学习计划。全部文案走 i18n。 */
  readonly templates: TemplateDef[] = [
    {
      key: 'jobInterview',
      labelKey: 'practice.tpl.jobInterview.label',
      descKey: 'practice.tpl.jobInterview.desc',
      categories: [
        {
          nameKey: 'practice.tpl.jobInterview.label',
          folders: [
            { nameKey: 'practice.ti.selfIntro', files: ['practice.ti.intro1m', 'practice.ti.whyCompany'] },
            { nameKey: 'practice.ti.tech', files: ['practice.ti.techList'] },
            { nameKey: 'practice.ti.behavioral', files: ['practice.ti.star'] },
            { nameKey: 'practice.ti.questions', files: ['practice.ti.questionsList'] }
          ]
        }
      ]
    },
    {
      key: 'dailyEnglish',
      labelKey: 'practice.tpl.dailyEnglish.label',
      descKey: 'practice.tpl.dailyEnglish.desc',
      categories: [
        {
          nameKey: 'practice.tpl.dailyEnglish.label',
          folders: [
            { nameKey: 'practice.ti.dailyTalk', files: ['practice.ti.smallTalkTopics'] },
            { nameKey: 'practice.ti.travel', files: ['practice.ti.airport'] },
            { nameKey: 'practice.ti.dining', files: ['practice.ti.restaurant'] }
          ]
        }
      ]
    },
    {
      key: 'studyPlan',
      labelKey: 'practice.tpl.studyPlan.label',
      descKey: 'practice.tpl.studyPlan.desc',
      categories: [
        {
          nameKey: 'practice.tpl.studyPlan.label',
          folders: [
            { nameKey: 'practice.ti.dailyPractice', files: ['practice.ti.practiceLog'] },
            { nameKey: 'practice.ti.notes', files: ['practice.ti.mistakeBook'] }
          ]
        }
      ]
    }
  ];

  selectedTemplateKey = '';
  readonly importing = signal(false);

  templateByKey(key: string): TemplateDef | undefined {
    return this.templates.find((tp) => tp.key === key);
  }

  private newId(): string {
    return 'n_' + Date.now().toString(36) + '_' + Math.random().toString(36).slice(2, 7);
  }

  /**
   * 导入所选模板:
   *  1. 逐个解析模板里的类别 —— 同名类别复用,没有就现场创建(按模板顺序落库);
   *  2. 为每个类别构建一棵素材树(根节点挂 categoryId,子级为模板骨架);
   *  3. 关闭弹窗把 importedNodes 回传父级,由父级合并进整树并保存。
   * 全程 async 串行 —— 类别创建要等前一个返回才能保持顺序。
   */
  async importTemplate(): Promise<void> {
    const tpl = this.templateByKey(this.selectedTemplateKey);
    if (!tpl || this.importing()) return;
    this.importing.set(true);
    try {
      const importedNodes: MaterialNode[] = [];
      for (const catDef of tpl.categories) {
        const name = this.t(catDef.nameKey);
        // 同名复用,避免重复导入产生一堆同名类别
        let cat: PracticeCategoryDto | undefined = this.items().find((c) => c.name === name);
        if (!cat) {
          const created: PracticeCategoryDto = await firstValueFrom(this.api.createCategory(name));
          this.items.update((list) => [...list, created]);
          cat = created;
        }
        importedNodes.push(this.buildTemplateRoot(cat.id, cat.name, catDef.folders));
      }
      this.dialogRef.close({ categories: this.items(), importedNodes });
    } catch (e) {
      this.importing.set(false);
      this.notify(this.err(e));
    }
  }

  /** 模板类别 → 根节点(根名即类别名,children 为骨架文件夹)。 */
  private buildTemplateRoot(categoryId: string, rootName: string, folders: TemplateCategoryDef['folders']): MaterialNode {
    return {
      id: this.newId(),
      name: rootName,
      folder: true,
      expanded: true,
      categoryId,
      children: folders.map((f) => ({
        id: this.newId(),
        name: this.t(f.nameKey),
        folder: true,
        expanded: true,
        children: f.files.map((fk) => ({
          id: this.newId(),
          name: this.t(fk),
          folder: false,
          content: ''
        }))
      }))
    };
  }

  /** 关闭并回传最终列表(+ 可选导入节点);父级据此刷新下拉、合并素材并保存。 */
  done(): void {
    this.dialogRef.close({ categories: this.items() });
  }
}
