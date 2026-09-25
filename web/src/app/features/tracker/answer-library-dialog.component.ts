import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';

import { TrackerApi } from '../../core/api/tracker-api.service';
import { AnswerTemplate } from '../../core/models/api.models';
import { I18nService } from '../../core/i18n/i18n.service';

interface DialogData {
  /** pick=true 时弹窗用于"挑一条插入",关闭时回传选中的模板;否则是管理态。 */
  pick: boolean;
}

/**
 * 申请问答库弹窗(M3)。
 *
 * 两个用途合在一个组件里:
 *  - 管理态(pick=false):增删改用户自己的"常见问题 → 标准回答"库。
 *  - 挑选态(pick=true):从库里挑一条插到投递备注,关闭时回传该模板。
 *    挑选态默认只给"插入"按钮;点"管理"可临时切到 CRUD(比如临时补一条再插)。
 */
@Component({
  selector: 'app-answer-library-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatButtonModule, MatIconModule,
    MatFormFieldModule, MatInputModule, MatTooltipModule, MatProgressBarModule, MatSnackBarModule
  ],
  template: `
    <h2 mat-dialog-title>
      {{ data.pick ? t('tracker.insertAnswer') : t('tracker.alTitle') }}
    </h2>

    <mat-dialog-content class="al-body">
      @if (loading()) {
        <mat-progress-bar mode="indeterminate"></mat-progress-bar>
      }

      @if (showForm()) {
        <div class="al-form">
          <mat-form-field appearance="outline">
            <mat-label>{{ t('tracker.alCategory') }}</mat-label>
            <input matInput [(ngModel)]="category" placeholder="如 自我介绍">
          </mat-form-field>
          <mat-form-field appearance="outline" class="full">
            <mat-label>{{ t('tracker.alQuestion') }}</mat-label>
            <input matInput [(ngModel)]="question">
          </mat-form-field>
          <mat-form-field appearance="outline" class="full">
            <mat-label>{{ t('tracker.alAnswer') }}</mat-label>
            <textarea matInput rows="4" [(ngModel)]="answer"></textarea>
          </mat-form-field>
          <div class="al-form-actions">
            <button mat-flat-button color="primary" [disabled]="!question.trim() || !answer.trim()"
                    (click)="save()">
              {{ editingId() ? t('tracker.alEdit') : t('tracker.alNew') }}
            </button>
            <button mat-button (click)="cancel()">取消</button>
          </div>
        </div>
      }

      @if (list().length === 0 && !showForm()) {
        <p class="al-empty">{{ t('tracker.alEmpty') }}</p>
      }

      <ul class="al-list">
        @for (item of list(); track item.id) {
          <li class="al-item">
            <div class="al-main">
              @if (item.category) { <span class="al-cat">{{ item.category }}</span> }
              <div class="al-q">{{ item.question }}</div>
              <div class="al-a pre">{{ item.answer }}</div>
            </div>
            <div class="al-actions">
              @if (data.pick) {
                <button mat-stroked-button type="button" (click)="pick(item)">
                  <mat-icon>playlist_add</mat-icon>{{ t('tracker.alInsert') }}
                </button>
              }
              @if ((data.pick && manage()) || !data.pick) {
                <button mat-icon-button [matTooltip]="t('common.edit')" (click)="startEdit(item)">
                  <mat-icon>edit</mat-icon>
                </button>
                <button mat-icon-button class="danger" [matTooltip]="t('common.delete')"
                        (click)="remove(item)">
                  <mat-icon>delete_outline</mat-icon>
                </button>
              }
            </div>
          </li>
        }
      </ul>
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      @if (data.pick && !manage()) {
        <button mat-button type="button" (click)="manage.set(true)">{{ t('tracker.alManage') }}</button>
      }
      @if ((data.pick && manage()) || !data.pick) {
        <button mat-stroked-button type="button" (click)="startNew()">
          <mat-icon>add</mat-icon>{{ t('tracker.alNew') }}
        </button>
      }
      <button mat-button mat-dialog-close>关闭</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .al-body { min-width: 460px; max-width: 680px; }
    @media (max-width: 680px) { .al-body { min-width: auto; } }
    mat-progress-bar { margin-bottom: 10px; }
    .al-empty { font-size: 13px; opacity: 0.55; margin: 6px 0 0; }
    .al-form {
      padding: 12px; margin-bottom: 14px; border-radius: 10px;
      background: rgba(0,0,0,0.025); border: 1px solid rgba(0,0,0,0.08);
    }
    .al-form mat-form-field { width: 100%; }
    .al-form .full { width: 100%; }
    .al-form-actions { display: flex; gap: 8px; }
    .al-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 10px; }
    .al-item {
      display: flex; gap: 10px; align-items: flex-start;
      padding: 12px 14px; border-radius: 10px; border: 1px solid rgba(0,0,0,0.08);
    }
    .al-main { flex: 1; min-width: 0; }
    .al-cat {
      display: inline-block; margin-bottom: 4px; padding: 1px 8px; border-radius: 10px;
      background: rgba(48,63,159,0.1); color: #303f9f; font-size: 11px;
    }
    .al-q { font-size: 13.5px; font-weight: 600; margin-bottom: 4px; }
    .al-a { margin: 0; font-size: 13px; line-height: 1.65; white-space: pre-wrap;
            word-break: break-word; opacity: 0.82; max-height: 160px; overflow-y: auto; }
    .al-actions { display: flex; flex-direction: column; gap: 4px; flex: 0 0 auto; }
    .al-actions .danger { color: #c62828; }
  `]
})
export class AnswerLibraryDialogComponent {
  readonly data = inject<DialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<AnswerLibraryDialogComponent, AnswerTemplate | null>);
  private readonly trackerApi = inject(TrackerApi);
  private readonly snack = inject(MatSnackBar);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** 挑选态默认只给"插入";点"管理"切到 CRUD。非挑选态恒为 true。 */
  readonly manage = signal(!this.data.pick);
  readonly loading = signal(false);
  readonly list = signal<AnswerTemplate[]>([]);
  readonly showForm = signal(false);
  readonly editingId = signal<string | null>(null);

  category = '';
  question = '';
  answer = '';

  constructor() {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.trackerApi.listAnswerTemplates().subscribe({
      next: (r) => { this.list.set(r); this.loading.set(false); },
      error: () => this.loading.set(false)
    });
  }

  startNew(): void {
    this.editingId.set(null);
    this.category = '';
    this.question = '';
    this.answer = '';
    this.showForm.set(true);
  }

  startEdit(item: AnswerTemplate): void {
    this.editingId.set(item.id);
    this.category = item.category;
    this.question = item.question;
    this.answer = item.answer;
    this.showForm.set(true);
  }

  cancel(): void {
    this.showForm.set(false);
    this.editingId.set(null);
  }

  /** 挑选态:选中一条并关闭弹窗,把模板回传给调用方。 */
  pick(item: AnswerTemplate): void {
    this.dialogRef.close(item);
  }

  save(): void {
    if (!this.question.trim() || !this.answer.trim()) {
      alert('问题和回答均为必填');
      return;
    }
    const body = {
      category: this.category.trim() || '通用',
      question: this.question.trim(),
      answer: this.answer.trim()
    };
    const editing = this.editingId();
    const req: Observable<unknown> = editing
      ? this.trackerApi.updateAnswerTemplate(editing, body)
      : this.trackerApi.createAnswerTemplate(body);
    req.subscribe({
      next: () => {
        this.showForm.set(false);
        this.editingId.set(null);
        this.load();
        this.snack.open(this.t(editing ? 'tracker.alSaved' : 'tracker.alCreated'), '关闭', { duration: 2200 });
      },
      error: (err) => alert(this.readError(err))
    });
  }

  remove(item: AnswerTemplate): void {
    if (!confirm(this.t('tracker.alConfirmDelete'))) return;
    this.trackerApi.deleteAnswerTemplate(item.id).subscribe({
      next: () => {
        this.load();
        this.snack.open(this.t('tracker.alDeleted'), '关闭', { duration: 2200 });
      },
      error: (err) => alert(this.readError(err))
    });
  }

  private readError(err: unknown): string {
    const e = err as { error?: { description?: string; detail?: string; title?: string }; message?: string };
    return e?.error?.description ?? e?.error?.detail ?? e?.error?.title ?? e?.message ?? '操作失败,请重试。';
  }
}
