import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDialog } from '@angular/material/dialog';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { analysisT } from './analysis.i18n';
import { InterviewActionDto } from './analysis.models';
import { QaConfirmDialogComponent } from '../qa/qa-confirm-dialog.component';

/**
 * Next actions(A2) —— F2 报告底部 + F5 Overview 待办区共用同一份数据。
 *
 * compact 模式(供 Overview 用):只列未完成事项、可勾选,隐藏新增表单与删除按钮。
 * 完整模式:新增(标题+补充+截止日期)、勾选切换 isDone、行内编辑、删除(MatDialog 二次确认)。
 */
@Component({
  selector: 'app-next-actions',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    MatIconModule, MatButtonModule, MatCheckboxModule,
    MatFormFieldModule, MatInputModule, MatSnackBarModule
  ],
  templateUrl: './next-actions.component.html',
  styleUrl: './next-actions.component.scss'
})
export class NextActionsComponent implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  private readonly snack = inject(MatSnackBar);
  private readonly dialog = inject(MatDialog);

  readonly t = analysisT(this.i18n);

  /** 面试条目 ID —— 组件自己调 API,不依赖父组件数据。 */
  readonly entryId = input.required<string>();
  /** 紧凑模式:Overview 待办区用。 */
  readonly compact = input(false);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly items = signal<InterviewActionDto[]>([]);
  readonly busyId = signal<string | null>(null);

  readonly openItems = computed(() => this.items().filter((a) => !a.isDone));
  readonly doneItems = computed(() => this.items().filter((a) => a.isDone));

  // 新增表单(非 signal —— 只在提交那一刻被读)
  newTitle = '';
  newDetail = '';
  newDueDate = '';

  // 行内编辑草稿
  readonly editingId = signal<string | null>(null);
  editTitle = '';
  editDueDate = '';

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.get<InterviewActionDto[]>(`/api/interviews/${this.entryId()}/actions`).subscribe({
      next: (list) => {
        this.items.set(list ?? []);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message || this.t('pb.an.loadFailed'));
        this.loading.set(false);
      }
    });
  }

  private base(): string {
    return `/api/interviews/${this.entryId()}/actions`;
  }

  toggle(a: InterviewActionDto): void {
    if (this.busyId()) return;
    this.busyId.set(a.id);
    this.api.patch<void>(`${this.base()}/${a.id}`, { isDone: !a.isDone }).subscribe({
      next: () => {
        this.busyId.set(null);
        this.items.update((list) =>
          list.map((x) => (x.id === a.id ? { ...x, isDone: !x.isDone } : x)));
      },
      error: (e: Error) => {
        this.busyId.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 4000 });
      }
    });
  }

  add(): void {
    const title = this.newTitle.trim();
    if (!title) {
      this.snack.open(this.t('pb.an.titleRequired'), this.t('common.close'), { duration: 2500 });
      return;
    }
    if (this.busyId()) return;
    this.busyId.set('new');
    const body: { title: string; detail?: string; dueDate?: string } = { title };
    const detail = this.newDetail.trim();
    if (detail) body.detail = detail;
    if (this.newDueDate) body.dueDate = new Date(this.newDueDate + 'T00:00:00').toISOString();
    this.api.post<{ id: string }>(this.base(), body).subscribe({
      next: () => {
        this.busyId.set(null);
        this.newTitle = '';
        this.newDetail = '';
        this.newDueDate = '';
        this.snack.open(this.t('pb.an.added'), this.t('common.close'), { duration: 2500 });
        this.reload();
      },
      error: (e: Error) => {
        this.busyId.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  startEdit(a: InterviewActionDto): void {
    this.editingId.set(a.id);
    this.editTitle = a.title;
    this.editDueDate = a.dueDate ? a.dueDate.slice(0, 10) : '';
  }

  cancelEdit(): void {
    this.editingId.set(null);
  }

  saveEdit(a: InterviewActionDto): void {
    const title = this.editTitle.trim();
    if (!title) {
      this.snack.open(this.t('pb.an.titleRequired'), this.t('common.close'), { duration: 2500 });
      return;
    }
    if (this.busyId()) return;
    this.busyId.set(a.id);
    this.api.patch<void>(`${this.base()}/${a.id}`, {
      title,
      dueDate: this.editDueDate ? new Date(this.editDueDate + 'T00:00:00').toISOString() : null
    }).subscribe({
      next: () => {
        this.busyId.set(null);
        this.editingId.set(null);
        this.snack.open(this.t('pb.an.saved'), this.t('common.close'), { duration: 2500 });
        this.reload();
      },
      error: (e: Error) => {
        this.busyId.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  remove(a: InterviewActionDto): void {
    if (this.busyId()) return;
    const ref = this.dialog.open(QaConfirmDialogComponent, {
      width: '440px',
      data: {
        title: this.t('pb.an.confirmDeleteTitle'),
        body: this.t('pb.an.confirmDelete'),
        confirmText: this.t('pb.an.delete'),
        cancelText: this.t('pb.an.cancel'),
        danger: true
      }
    });
    ref.afterClosed().subscribe(ok => {
      if (!ok) return;
      this.busyId.set(a.id);
      this.api.delete<void>(`${this.base()}/${a.id}`).subscribe({
        next: () => {
          this.busyId.set(null);
          this.snack.open(this.t('pb.an.deleted'), this.t('common.close'), { duration: 2500 });
          this.items.update((list) => list.filter((x) => x.id !== a.id));
        },
        error: (e: Error) => {
          this.busyId.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        }
      });
    });
  }

  /** 截止日期展示:只取日期部分,避免时区把日子挪前挪后。 */
  dueLabel(dueDate: string | null | undefined): string {
    if (!dueDate) return '';
    return dueDate.slice(0, 10);
  }
}
