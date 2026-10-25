import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';

import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { TrackerApi } from '../../core/api/tracker-api.service';
import { fieldErrorFromServer, textLengthError } from '../../core/api/validation-errors.helpers';
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
            <input matInput [(ngModel)]="category" [placeholder]="t('tracker.alCatPh')"
                   (ngModelChange)="clearFieldError('category')">
          </mat-form-field>
          @if (fieldErrors().category) {
            <div class="field-error" role="alert">{{ fieldErrors().category }}</div>
          }
          <mat-form-field appearance="outline" class="full">
            <mat-label>{{ t('tracker.alQuestion') }}</mat-label>
            <input matInput [(ngModel)]="question" (ngModelChange)="clearFieldError('question')">
          </mat-form-field>
          @if (fieldErrors().question) {
            <div class="field-error" role="alert">{{ fieldErrors().question }}</div>
          }
          <mat-form-field appearance="outline" class="full">
            <mat-label>{{ t('tracker.alAnswer') }}</mat-label>
            <textarea matInput rows="4" [(ngModel)]="answer" (ngModelChange)="clearFieldError('answer')"></textarea>
          </mat-form-field>
          @if (fieldErrors().answer) {
            <div class="field-error" role="alert">{{ fieldErrors().answer }}</div>
          }
          <div class="al-form-actions">
            <button mat-flat-button color="primary" [disabled]="!question.trim() || !answer.trim()"
                    (click)="save()">
              {{ editingId() ? t('tracker.alEdit') : t('tracker.alNew') }}
            </button>
            <button mat-button (click)="cancel()">{{ t('common.cancel') }}</button>
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
                <button mat-icon-button [matTooltip]="t('common.edit')" (click)="startEdit(item)"
                [attr.aria-label]="t('common.edit')">
                  <mat-icon>edit</mat-icon>
                </button>
                <button mat-icon-button class="danger" [matTooltip]="t('common.delete')"
                        (click)="remove(item)"
                [attr.aria-label]="t('common.delete')">
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
      <button mat-button mat-dialog-close>{{ t('common.close') }}</button>
    </mat-dialog-actions>
  `,
  styles: [`
    .al-body { min-width: 460px; max-width: 680px; }
    @media (max-width: 680px) { .al-body { min-width: auto; } }
    mat-progress-bar { margin-bottom: 10px; }
    .al-empty { font-size: 13px; opacity: 0.55; margin: 6px 0 0; }
    .al-form {
      padding: 12px; margin-bottom: 14px; border-radius: 10px;
      background: rgba(15, 23, 42, 0.025); border: 1px solid rgba(15, 23, 42, 0.08);
    }
    .al-form mat-form-field { width: 100%; }
    /* 票75:字段级校验提示(对齐票12 口径) */
    .field-error { color: #dc2626; font-size: 12px; margin: -14px 0 10px 2px; }
    .al-form .full { width: 100%; }
    .al-form-actions { display: flex; gap: 8px; }
    .al-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 10px; }
    .al-item {
      display: flex; gap: 10px; align-items: flex-start;
      padding: 12px 14px; border-radius: 10px; border: 1px solid rgba(15, 23, 42, 0.08);
    }
    .al-main { flex: 1; min-width: 0; }
    .al-cat {
      display: inline-block; margin-bottom: 4px; padding: 1px 8px; border-radius: 10px;
      background: rgba(37, 99, 235,0.1); color: #2563eb; font-size: 11px;
    }
    .al-q { font-size: 13.5px; font-weight: 600; margin-bottom: 4px; }
    .al-a { margin: 0; font-size: 13px; line-height: 1.65; white-space: pre-wrap;
            word-break: break-word; opacity: 0.82; max-height: 160px; overflow-y: auto; }
    .al-actions { display: flex; flex-direction: column; gap: 4px; flex: 0 0 auto; }
    .al-actions .danger { color: #dc2626; }
  `]
})
export class AnswerLibraryDialogComponent {
  readonly data = inject<DialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<AnswerLibraryDialogComponent, AnswerTemplate | null>);
  private readonly trackerApi = inject(TrackerApi);
  private readonly dialog = inject(MatDialog);
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

  // ---- 票75:字段级校验错误(上限与 Create/UpdateAnswerTemplateCommandValidator 对齐) ----
  readonly LIMITS = { category: 100, question: 500, answer: 20_000 } as const;
  readonly fieldErrors = signal<Partial<Record<'category' | 'question' | 'answer', string>>>({});

  clearFieldError(field: 'category' | 'question' | 'answer'): void {
    if (this.fieldErrors()[field]) {
      const next = { ...this.fieldErrors() };
      delete next[field];
      this.fieldErrors.set(next);
    }
  }

  private tooLongText(max: number): string {
    return this.i18n.tf('validation.tooLong', { max });
  }

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
    this.fieldErrors.set({});
    this.editingId.set(null);
    this.category = '';
    this.question = '';
    this.answer = '';
    this.showForm.set(true);
  }

  startEdit(item: AnswerTemplate): void {
    this.fieldErrors.set({});
    this.editingId.set(item.id);
    this.category = item.category;
    this.question = item.question;
    this.answer = item.answer;
    this.showForm.set(true);
  }

  cancel(): void {
    this.fieldErrors.set({});
    this.showForm.set(false);
    this.editingId.set(null);
  }

  /** 挑选态:选中一条并关闭弹窗,把模板回传给调用方。 */
  pick(item: AnswerTemplate): void {
    this.dialogRef.close(item);
  }

  save(): void {
    if (!this.question.trim() || !this.answer.trim()) {
      // 票45 REQ-14:原生 alert 改走本弹窗既有 snack 通道,文案逐字不变
      this.snack.open(this.t('tracker.alRequired'), this.t('common.close'), { duration: 3000 });
      return;
    }
    const body = {
      category: this.category.trim() || this.t('tracker.alDefaultCat'),
      question: this.question.trim(),
      answer: this.answer.trim()
    };
    // 票75:三字段长度先挡,超限字段下亮含上限提示,不走通用 snack
    const preErrors: Partial<Record<'category' | 'question' | 'answer', string>> = {};
    for (const f of ['category', 'question', 'answer'] as const) {
      if (textLengthError(body[f], this.LIMITS[f])) preErrors[f] = this.tooLongText(this.LIMITS[f]);
    }
    if (Object.keys(preErrors).length > 0) { this.fieldErrors.set(preErrors); return; }
    this.fieldErrors.set({});
    const editing = this.editingId();
    const req: Observable<unknown> = editing
      ? this.trackerApi.updateAnswerTemplate(editing,
          { ...body, version: this.list().find(t => t.id === editing)?.version ?? null })
      : this.trackerApi.createAnswerTemplate(body);
    req.subscribe({
      next: () => {
        this.showForm.set(false);
        this.editingId.set(null);
        this.load();
        this.snack.open(this.t(editing ? 'tracker.alSaved' : 'tracker.alCreated'), this.t('common.close'), { duration: 2200 });
      },
      // 票75:errors{Category/Question/Answer} 逐字段落回表单;无字段级错误回落原通用 snack
      error: (err) => {
        const mapped: Partial<Record<'category' | 'question' | 'answer', string>> = {};
        const serverNames = { category: 'Category', question: 'Question', answer: 'Answer' } as const;
        for (const f of ['category', 'question', 'answer'] as const) {
          const c = fieldErrorFromServer(err, serverNames[f], { sentLength: body[f].length, max: this.LIMITS[f] });
          if (!c) continue;
          mapped[f] = c.kind === 'tooLong' ? this.tooLongText(this.LIMITS[f])
            : c.kind === 'required' ? this.t('validation.required') : c.text;
        }
        if (Object.keys(mapped).length > 0) { this.fieldErrors.set(mapped); return; }
        this.snack.open(this.readError(err), this.t('common.close'), { duration: 3000 });
      }
    });
  }

  remove(item: AnswerTemplate): void {
    // 票45 REQ-14:站内确认弹窗(全站 ConfirmDialog,F-02 标准)替代原生 confirm
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('common.delete'), body: this.t('tracker.alConfirmDelete'),
        confirmText: this.t('common.delete'), cancelText: this.t('common.cancel'),
        danger: true,
      } as ConfirmDialogData,
    });
    ref.afterClosed().subscribe((v) => {
      if (v !== true) return;
      this.trackerApi.deleteAnswerTemplate(item.id).subscribe({
        next: () => {
          this.load();
          this.snack.open(this.t('tracker.alDeleted'), this.t('common.close'), { duration: 2200 });
        },
        error: (err) => this.snack.open(this.readError(err), this.t('common.close'), { duration: 3000 })
      });
    });
  }

  private readError(err: unknown): string {
    const e = err as { error?: { description?: string; detail?: string; title?: string }; message?: string };
    return e?.error?.description ?? e?.error?.detail ?? e?.error?.title ?? e?.message ?? this.t('tracker.opFailed');
  }
}
