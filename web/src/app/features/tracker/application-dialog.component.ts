import { Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog, MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatDividerModule } from '@angular/material/divider';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { TrackerApi } from '../../core/api/tracker-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { Application, ApplicationStatus, AnswerTemplate } from '../../core/models/api.models';
import { AnswerLibraryDialogComponent } from './answer-library-dialog.component';
import { STATUS_ORDER, CREATE_STATUS_OPTIONS, ApplicationForm, emptyForm, fromApplication, trimForm } from './tracker-form.helpers';
import { textLengthError } from '../../core/api/validation-errors.helpers';

/**
 * 新建/编辑投递的弹窗(内联模板)。
 *
 * 为什么内联而不是单独文件:这个表单只在 tracker 用一次,
 * 拆成独立组件要额外传 MAT_DIALOG_DATA + 回传结果,收益不大;
 * 超过两处复用再拆。
 */
@Component({
  selector: 'app-application-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatButtonModule, MatIconModule, MatSlideToggleModule, MatDividerModule
  ],
  template: `
    <h2 mat-dialog-title>{{ isEdit ? t('tracker.dlgEdit') : t('tracker.dlgNew') }}</h2>

    <mat-dialog-content class="dlg-body">
      <div class="row2">
        <div class="cell">
          <mat-form-field appearance="outline">
            <mat-label>{{ t('tracker.fCompany') }}</mat-label>
            <input matInput name="companyName" [(ngModel)]="form.companyName" required
                   (ngModelChange)="clearFieldError('companyName')">
          </mat-form-field>
          @if (fieldErrors.companyName) {
            <div class="field-error" role="alert">{{ fieldErrors.companyName }}</div>
          }
        </div>

        <div class="cell">
          <mat-form-field appearance="outline">
            <mat-label>{{ t('tracker.fRole') }}</mat-label>
            <input matInput name="role" [(ngModel)]="form.role" required
                   (ngModelChange)="clearFieldError('role')">
          </mat-form-field>
          @if (fieldErrors.role) {
            <div class="field-error" role="alert">{{ fieldErrors.role }}</div>
          }
        </div>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fLocation') }}</mat-label>
          <input matInput name="location" [(ngModel)]="form.location" [placeholder]="t('tracker.fLocationPh')">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fSalary') }}</mat-label>
          <input matInput name="salary" [(ngModel)]="form.salary" [placeholder]="t('tracker.fSalaryPh')">
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fStatus') }}</mat-label>
          <mat-select name="status" [(ngModel)]="form.status">
            @for (s of statusOptions; track s) {
              <mat-option [value]="s">{{ label(s) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fPriority') }}</mat-label>
          <mat-select name="priority" [(ngModel)]="form.priority">
            @for (p of priorities; track p) {
              <mat-option [value]="p">{{ p }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fAppliedDate') }}</mat-label>
          <input matInput type="date" name="appliedDate" [(ngModel)]="form.appliedDate">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('tracker.fOutreach') }}</mat-label>
          <input matInput name="outreachMessage" [(ngModel)]="form.outreachMessage"
                 [placeholder]="t('tracker.fOutreachPh')">
        </mat-form-field>
      </div>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('tracker.fLink') }}</mat-label>
        <input matInput name="link" [(ngModel)]="form.link" placeholder="https://..."
               (ngModelChange)="clearFieldError('link')">
      </mat-form-field>
      @if (fieldErrors.link) {
        <div class="field-error" role="alert">{{ fieldErrors.link }}</div>
      }

      <!-- ★ #6:投递文档版本 -->
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('tracker.fSentDocs') }}</mat-label>
        <input matInput name="sentDocuments" [(ngModel)]="form.sentDocuments"
               (ngModelChange)="clearFieldError('notes')"
               [placeholder]="t('tracker.fSentDocsPh')">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('tracker.fNotes') }}</mat-label>
        <textarea matInput name="notes" rows="4" [(ngModel)]="form.notes"
                  (ngModelChange)="clearFieldError('notes')"></textarea>
      </mat-form-field>
      @if (fieldErrors.notes) {
        <div class="field-error" role="alert">{{ fieldErrors.notes }}</div>
      }

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('tracker.fJdText') }}</mat-label>
        <textarea matInput name="jdText" rows="6" [(ngModel)]="form.jdText"
                  (ngModelChange)="clearFieldError('jdText')"
                  [placeholder]="t('tracker.fJdTextPh')"></textarea>
      </mat-form-field>
      @if (fieldErrors.jdText) {
        <div class="field-error" role="alert">{{ fieldErrors.jdText }}</div>
      }

      <div class="qa-row">
        <span class="qa-hint">{{ t('tracker.insertAnswer') }}</span>
        <button mat-stroked-button type="button" (click)="insertAnswer()">
          <mat-icon>library_books</mat-icon>{{ t('tracker.insertAnswer') }}
        </button>
      </div>

      <mat-divider></mat-divider>

      <mat-slide-toggle name="needsConnectFirst" [(ngModel)]="form.needsConnectFirst">
        {{ t('tracker.fConnectFirst') }}
      </mat-slide-toggle>

      @if (error()) {
        <div class="err">
          <mat-icon>error_outline</mat-icon> {{ error() }}
        </div>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="primary"
              (click)="save()">
        {{ isEdit ? t('common.save') : t('common.create') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .dlg-body { min-width: 520px; max-width: 640px; }
    @media (max-width: 640px) { .dlg-body { min-width: auto; } }
    .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    .cell { min-width: 0; }
    /* 票75:字段级校验提示(对齐票12 口径) */
    .field-error { color: var(--stop); font-size: 12px; margin: -14px 0 10px 2px; }
    mat-form-field { width: 100%; }
    .full { width: 100%; }
    mat-divider { margin: 6px 0 14px; }
    .err {
      display: flex; align-items: center; gap: 6px;
      margin-top: 12px; padding: 8px 12px; border-radius: 6px;
      background: var(--stop-tint); color: var(--stop); font-size: 13px;
    }
    .err mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .qa-row {
      display: flex; align-items: center; justify-content: space-between; gap: 10px;
      padding: 10px 12px; margin-bottom: 4px; border-radius: 8px;
      background: rgba(46, 58, 140, 0.06); border: 1px solid rgba(46, 58, 140, 0.16);
    }
    .qa-row .qa-hint { font-size: 12.5px; opacity: 0.7; }
    .qa-row mat-icon { font-size: 18px; width: 18px; height: 18px; margin-right: 4px; vertical-align: middle; }
  `]
})
export class ApplicationDialogComponent {
  readonly dialogRef = inject(MatDialogRef<ApplicationDialogComponent, ApplicationForm | null>);
  /** 传入 null 表示新建;传入 Application 表示编辑。 */
  readonly existing = inject<Application | null>(MAT_DIALOG_DATA);
  private readonly dialog = inject(MatDialog);
  private readonly trackerApi = inject(TrackerApi);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  /** 新建时只允许从 Saved 合法流转出去的状态;编辑时沿用全部顺序。 */
  readonly statusOptions: ApplicationStatus[] = this.existing ? STATUS_ORDER : CREATE_STATUS_OPTIONS;
  readonly priorities = ['High', 'Medium', 'Low'];

  readonly error = signal<string | null>(null);

  /** 票75:字段级长度错误(落对应输入框下方,呈现对齐票12)。 */
  fieldErrors: Partial<Record<'companyName' | 'role' | 'link' | 'notes' | 'jdText', string>> = {};

  clearFieldError(field: keyof ApplicationDialogComponent['fieldErrors']): void {
    if (this.fieldErrors[field]) {
      const next = { ...this.fieldErrors };
      delete next[field];
      this.fieldErrors = next;
    }
  }

  private tooLongText(max: number): string {
    return this.i18n.tf('validation.tooLong', { max });
  }

  form: ApplicationForm = this.existing ? fromApplication(this.existing) : emptyForm();

  get isEdit(): boolean {
    return this.existing !== null;
  }

  label(s: ApplicationStatus): string {
    return this.i18n.t('status.' + s);
  }

  canSave(): boolean {
    return this.form.companyName.trim().length > 0 && this.form.role.trim().length > 0;
  }

  /**
   * 从问答库挑一条标准回答,插入到备注末尾(留一个空行分隔)。
   * 选完即关弹窗,不阻断主表单填写。
   */
  insertAnswer(): void {
    const ref = this.dialog.open(AnswerLibraryDialogComponent, {
      data: { pick: true as const },
      maxWidth: '720px'
    });
    ref.afterClosed().subscribe((tpl?: AnswerTemplate | null) => {
      if (!tpl) return;
      const block = `【${tpl.question}】\n${tpl.answer}`;
      const cur = this.form.notes?.trim() ?? '';
      this.form.notes = cur ? `${cur}\n\n${block}` : block;
    });
  }

  save(): void {
    if (!this.canSave()) {
      // 票 118 / D-6:校验全站统一为「提交不禁用、点提交后字段级报错」(对齐 Tech Stack 弹窗)。
      // 必填缺失落到对应字段下方,不再只给一条顶部总报错、更不靠禁用按钮预防。
      const errors: typeof this.fieldErrors = { ...this.fieldErrors };
      if (!this.form.companyName.trim()) errors.companyName = this.t('tracker.required');
      if (!this.form.role.trim()) errors.role = this.t('tracker.required');
      this.fieldErrors = errors;
      return;
    }
    // 票75:长度拦截(弹窗关之前在字段下亮含上限提示;后端规则分新建/编辑两路——
    // Create 校验 Role300/Link2048/Notes8000/JdText200000 + 公司名300(CreateCompany);
    // Update 仅 Jd* 四段 200000 有校验,其余字段后端不设上限,前端不擅自加限)。
    const errors: typeof this.fieldErrors = {};
    const check = (field: keyof typeof errors, sent: string, max: number) => {
      if (textLengthError(sent, max)) errors[field] = this.tooLongText(max);
    };
    if (!this.isEdit) {
      check('companyName', this.form.companyName.trim(), 300);
      check('role', this.form.role.trim(), 300);
      check('link', this.form.link.trim(), 2048);
      // Notes 实际送出的是 composeNotes 口径:[DOCS:...] 前缀 + 正文(tracker.component 同名方法)
      const sd = this.form.sentDocuments?.trim() ?? '';
      const composedNotes = sd ? `[DOCS:${sd}] ${this.form.notes || ''}`.trim() : (this.form.notes || '');
      check('notes', composedNotes, 8000);
    }
    check('jdText', (this.form.jdText ?? '').trim(), 200_000);
    this.fieldErrors = errors;
    if (Object.keys(errors).length > 0) return;
    // 空字符串在这里统一转 clean,避免后端存下一堆 "" 的脏字段
    this.dialogRef.close(trimForm(this.form));
  }
}
