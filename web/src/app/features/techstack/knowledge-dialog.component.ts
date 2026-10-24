import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { I18nService } from '../../core/i18n/i18n.service';
import { KnowledgeApiService } from '../../core/api/knowledge-api.service';


/** 新增条目的表单模型(仅本对话框内部使用)。 */
export interface KnowledgeForm {
  title: string;
  topic: string;
  question: string;
  conceptExplanation: string;
  keyPoints: string;
  commonMistakes: string;
  /** 我面试时实际的答案。 */
  myAnswer: string;
  /** 推荐的标准答案/更好的答法。 */
  betterAnswer: string;
  difficulty: number;
  importance: number;
  /** 来源公司(可选)。填了就标成"来自实战机经",为反查留线索。 */
  sourceCompanyName: string;
  /** 第几轮被问到的(可选)。 */
  sourceRoundNo: number | null;
  sourceRoundStage: string | null;
  sourceDate: string | null;
}

const ROUND_STAGES = ['Screen', 'Technical', 'SystemDesign', 'Behavioral', 'Final'];

/**
 * 新增技术栈条目的对话框。
 *
 * 关于"要点 / 常见误区"两个输入框:后端存的是 JSON 数组,
 * 但让用户手写 JSON 是反人类的 —— 这里用"一行一条"的纯文本,
 * 提交时再转成 JSON 数组。用户写起来是列表,机器拿到的是结构化数据。
 */
@Component({
  selector: 'app-knowledge-dialog',
  standalone: true,
  imports: [
    CommonModule, FormsModule, MatDialogModule, MatFormFieldModule,
    MatInputModule, MatSelectModule, MatButtonModule, MatIconModule
  ],
  template: `
    <h2 mat-dialog-title>{{ t('ts.dialog.title') }}</h2>

    <mat-dialog-content class="dlg">
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.titleLabel') }}</mat-label>
        <input matInput name="title" [(ngModel)]="form.title" required
               (ngModelChange)="clearFieldError('title')"
               [placeholder]="t('ts.dialog.titlePh')">
      </mat-form-field>
      @if (fieldErrors.title) {
        <div class="field-error" role="alert">{{ fieldErrors.title }}</div>
      }

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.topicLabel') }}</mat-label>
        <input matInput name="topic" [(ngModel)]="form.topic" required
               (ngModelChange)="clearFieldError('topic')"
               [placeholder]="t('ts.dialog.topicPh')">
      </mat-form-field>
      @if (fieldErrors.topic) {
        <div class="field-error" role="alert">{{ fieldErrors.topic }}</div>
      }

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.questionLabel') }}</mat-label>
        <textarea matInput name="question" [(ngModel)]="form.question" rows="2" required
                  (ngModelChange)="clearFieldError('question')"
                  [placeholder]="t('ts.dialog.questionPh')"></textarea>
      </mat-form-field>
      @if (fieldErrors.question) {
        <div class="field-error" role="alert">{{ fieldErrors.question }}</div>
      }

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.conceptLabel') }}</mat-label>
        <textarea matInput name="conceptExplanation" [(ngModel)]="form.conceptExplanation" rows="5"
                  [placeholder]="t('ts.dialog.conceptPh')"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.keyPointsLabel') }}</mat-label>
        <textarea matInput name="keyPoints" [(ngModel)]="form.keyPoints" rows="4"
                  [placeholder]="t('ts.dialog.keyPointsPh')"></textarea>
        <mat-hint>{{ t('ts.dialog.keyPointsHint') }}</mat-hint>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.mistakesLabel') }}</mat-label>
        <textarea matInput name="commonMistakes" [(ngModel)]="form.commonMistakes" rows="3"
                  [placeholder]="t('ts.dialog.mistakesPh')"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.block.myAnswer') }}</mat-label>
        <textarea matInput name="myAnswer" [(ngModel)]="form.myAnswer" rows="4"
                  [placeholder]="t('ts.block.myAnswerHint')"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.block.betterAnswer') }}</mat-label>
        <textarea matInput name="betterAnswer" [(ngModel)]="form.betterAnswer" rows="4"
                  [placeholder]="t('ts.block.betterAnswerHint')"></textarea>
      </mat-form-field>

      <div class="src-box">
        <span class="src-title">{{ t('ts.dialog.sourceTitle') }}</span>
        <span class="src-hint">{{ t('ts.dialog.sourceHint') }}</span>
        <div class="row2">
          <mat-form-field appearance="outline">
            <mat-label>{{ t('ts.dialog.companyLabel') }}</mat-label>
            <input matInput name="srcCompany" [(ngModel)]="form.sourceCompanyName">
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>{{ t('ts.dialog.roundLabel') }}</mat-label>
            <input matInput type="number" name="srcRound" min="1" [(ngModel)]="form.sourceRoundNo">
          </mat-form-field>
        </div>
        <div class="row2">
          <mat-form-field appearance="outline">
            <mat-label>{{ t('ts.dialog.stageLabel') }}</mat-label>
            <mat-select name="srcStage" [(ngModel)]="form.sourceRoundStage">
              <mat-option [value]="null">{{ t('ts.dialog.anyStage') }}</mat-option>
              @for (s of stages; track s) {
                <mat-option [value]="s">{{ stageLabel(s) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>{{ t('ts.dialog.dateLabel') }}</mat-label>
            <input matInput type="date" name="srcDate" [(ngModel)]="form.sourceDate">
          </mat-form-field>
        </div>
      </div>

      <div class="row2">
        <mat-form-field appearance="outline">
          <mat-label>{{ t('ts.dialog.difficultyLabel') }}</mat-label>
          <input matInput type="number" name="difficulty" min="1" max="5"
                 [(ngModel)]="form.difficulty">
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label>{{ t('ts.dialog.importanceLabel') }}</mat-label>
          <input matInput type="number" name="importance" min="1" max="5"
                 [(ngModel)]="form.importance">
        </mat-form-field>
      </div>
      @if (saveError) {
        <div class="save-error" role="alert">{{ saveError }}</div>
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="primary" [disabled]="saving" (click)="save()">
        {{ t('common.save') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .dlg { min-width: 460px; max-width: 560px; }
    .full { width: 100%; }
    .row2 { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
    @media (max-width: 560px) { .dlg { min-width: auto; } .row2 { grid-template-columns: 1fr; } }
    h2[mat-dialog-title] { display: flex; align-items: center; gap: 6px; }
    .src-box {
      margin: 6px 0 4px; padding: 10px 12px; border-radius: 8px;
      background: rgba(46, 58, 140, 0.05); border: 1px solid rgba(46, 58, 140, 0.16);
    }
    .src-box .src-title { display: block; font-size: 12.5px; font-weight: 600; color: #2e3a8c; }
    .src-box .src-hint { display: block; font-size: 11.5px; opacity: .65; margin: 2px 0 8px; }
    .field-error { color: #b3261e; font-size: 12px; margin: -14px 0 10px 2px; }
    .save-error {
      color: #b3261e; font-size: 12.5px; margin-top: 8px;
      padding: 8px 10px; border-radius: 6px; background: rgba(179, 38, 30, 0.06);
    }
  `]
})
export class KnowledgeDialogComponent {
  private readonly ref = inject(MatDialogRef<KnowledgeDialogComponent>);
  private readonly i18n = inject(I18nService);
  private readonly knowledgeApi = inject(KnowledgeApiService);
  t = (key: string): string => this.i18n.t(key);

  /** 字段级错误(前端拦截或后端 400 映射),按字段名挂在对应输入框下。 */
  fieldErrors: Partial<Record<'title' | 'topic' | 'question', string>> = {};
  /** 非字段级的保存失败原因(弹窗内展示,弹窗不关、输入不丢)。 */
  saveError: string | null = null;
  saving = false;

  readonly stages = ROUND_STAGES;
  /** 轮次类型 → 本地化标签(找不到键时回退英文枚举)。 */
  stageLabel(s: string): string {
    const key = 'ts.stage.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
  }

  form: KnowledgeForm = {
    title: '', topic: '', question: '', conceptExplanation: '',
    keyPoints: '', commonMistakes: '', myAnswer: '', betterAnswer: '',
    difficulty: 3, importance: 3,
    sourceCompanyName: '', sourceRoundNo: null, sourceRoundStage: null, sourceDate: null
  };

  /**
   * 标题、分类、面试题干都是后端必填项(CreateKnowledgeCommandValidator 三者
   * 均 NotEmpty),前端先挡一道并给字段级提示,避免白跑一次 400。
   * FUN-05:此前漏了 question —— UI 没标必填、Save 可点,后端必回
   * 400 errors{Question:["The Question field is required."]} 且无处提示。
   */
  canSubmit(): boolean {
    return this.form.title.trim().length > 0
      && this.form.topic.trim().length > 0
      && this.form.question.trim().length > 0;
  }

  /** 点 Save 时逐字段校验:缺哪个就在哪个字段下亮提示,返回是否全过。 */
  private validate(): boolean {
    const errors: typeof this.fieldErrors = {};
    if (!this.form.title.trim()) errors.title = this.t('ts.dialog.titleRequired');
    if (!this.form.topic.trim()) errors.topic = this.t('ts.dialog.topicRequired');
    if (!this.form.question.trim()) errors.question = this.t('ts.dialog.questionRequired');
    this.fieldErrors = errors;
    return Object.keys(errors).length === 0;
  }

  /** 用户开始补填某字段时,先撤掉该字段的旧错误。 */
  clearFieldError(field: 'title' | 'topic' | 'question'): void {
    if (this.fieldErrors[field]) {
      const next = { ...this.fieldErrors };
      delete next[field];
      this.fieldErrors = next;
    }
  }

  /**
   * 后端 400 的字段映射(双保险):前端拦截漏网时,把 ValidationProblemDetails
   * 的 errors{Question:[...]} 按字段名映射回弹窗对应字段,而不是只弹一句
   * 通用错误。ApiClient.rethrow 只透传 title/detail,原始响应挂在 error.original。
   */
  private applyServerError(e: unknown): void {
    const err = e as {
      message?: string;
      original?: { error?: { errors?: Record<string, string[]> } };
      error?: { errors?: Record<string, string[]> };
    };
    const raw = err?.original?.error?.errors ?? err?.error?.errors ?? null;
    if (raw && typeof raw === 'object') {
      const fieldErrors: typeof this.fieldErrors = {};
      const others: string[] = [];
      const requiredKey = {
        title: 'ts.dialog.titleRequired',
        topic: 'ts.dialog.topicRequired',
        question: 'ts.dialog.questionRequired'
      } as const;
      for (const [key, messages] of Object.entries(raw)) {
        const field = key.toLowerCase() as keyof typeof requiredKey;
        const text = (messages ?? []).join(' ');
        if (field in requiredKey) {
          // 必填类后端文案换成本地化口径;长度等其它校验保留后端原文。
          fieldErrors[field] = /required|must not be empty/i.test(text)
            ? this.t(requiredKey[field])
            : text;
        } else if (text) {
          others.push(text);
        }
      }
      this.fieldErrors = fieldErrors;
      this.saveError = others.length > 0 ? others.join(' ') : null;
      if (Object.keys(fieldErrors).length > 0 || this.saveError) return;
    }
    this.saveError = err?.message || 'Save failed';
  }

  /** 把"一行一条"的文本转成后端要的 JSON 数组字符串;空则给 null 而不是 "[]"。 */
  private toJsonList(text: string): string | null {
    const lines = text.split('\n').map((x) => x.trim()).filter((x) => x.length > 0);
    return lines.length > 0 ? JSON.stringify(lines) : null;
  }

  save(): void {
    if (this.saving) return;
    this.saveError = null;
    // 前端拦截:缺必填字段时不发请求、不关弹窗,字段下直接亮提示。
    if (!this.validate()) return;
    const payload = {
      title: this.form.title.trim(),
      topic: this.form.topic.trim(),
      question: this.form.question.trim(),
      conceptExplanation: this.form.conceptExplanation.trim() || null,
      keyPointsJson: this.toJsonList(this.form.keyPoints),
      commonMistakesJson: this.toJsonList(this.form.commonMistakes),
      myAnswer: this.form.myAnswer.trim() || null,
      betterAnswer: this.form.betterAnswer.trim() || null,
      difficulty: Number(this.form.difficulty) || 3,
      importance: Number(this.form.importance) || 3,
      // 填了公司名才算"来自实战机经";没填就是我自己主动录入的
      source: this.form.sourceCompanyName.trim() ? 'FromInterview' : 'Personal',
      sourceCompanyName: this.form.sourceCompanyName.trim() || null,
      sourceRoundNo: this.form.sourceRoundNo ? Number(this.form.sourceRoundNo) : null,
      sourceRoundStage: this.form.sourceRoundStage || null,
      sourceDate: this.form.sourceDate || null
    };
    // 弹窗内直接保存:成功才关弹窗(父组件只负责刷新列表);
    // 失败留在弹窗内显示字段级/表单级原因,用户已填内容不丢。
    this.saving = true;
    this.knowledgeApi.createItem(payload).subscribe({
      next: () => {
        this.saving = false;
        this.ref.close(payload);
      },
      error: (e: unknown) => {
        this.saving = false;
        this.applyServerError(e);
      }
    });
  }
}
