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

/** 新增条目的表单模型(仅本对话框内部使用)。 */
export interface KnowledgeForm {
  title: string;
  topic: string;
  question: string;
  conceptExplanation: string;
  keyPoints: string;
  commonMistakes: string;
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
               [placeholder]="t('ts.dialog.titlePh')">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.topicLabel') }}</mat-label>
        <input matInput name="topic" [(ngModel)]="form.topic" required
               [placeholder]="t('ts.dialog.topicPh')">
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('ts.dialog.questionLabel') }}</mat-label>
        <textarea matInput name="question" [(ngModel)]="form.question" rows="2"
                  [placeholder]="t('ts.dialog.questionPh')"></textarea>
      </mat-form-field>

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
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>{{ t('common.cancel') }}</button>
      <button mat-raised-button color="primary" [disabled]="!canSubmit()" (click)="save()">
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
      background: rgba(63,81,181,0.05); border: 1px solid rgba(63,81,181,0.16);
    }
    .src-box .src-title { display: block; font-size: 12.5px; font-weight: 600; color: #3f51b5; }
    .src-box .src-hint { display: block; font-size: 11.5px; opacity: .65; margin: 2px 0 8px; }
  `]
})
export class KnowledgeDialogComponent {
  private readonly ref = inject(MatDialogRef<KnowledgeDialogComponent>);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  readonly stages = ROUND_STAGES;
  /** 轮次类型 → 本地化标签(找不到键时回退英文枚举)。 */
  stageLabel(s: string): string {
    const key = 'ts.stage.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
  }

  form: KnowledgeForm = {
    title: '', topic: '', question: '', conceptExplanation: '',
    keyPoints: '', commonMistakes: '', difficulty: 3, importance: 3,
    sourceCompanyName: '', sourceRoundNo: null, sourceRoundStage: null, sourceDate: null
  };

  /** 标题和分类是后端必填项,前端先挡一道,避免白跑一次 400。 */
  canSubmit(): boolean {
    return this.form.title.trim().length > 0 && this.form.topic.trim().length > 0;
  }

  /** 把"一行一条"的文本转成后端要的 JSON 数组字符串;空则给 null 而不是 "[]"。 */
  private toJsonList(text: string): string | null {
    const lines = text.split('\n').map((x) => x.trim()).filter((x) => x.length > 0);
    return lines.length > 0 ? JSON.stringify(lines) : null;
  }

  save(): void {
    if (!this.canSubmit()) return;
    const payload = {
      title: this.form.title.trim(),
      topic: this.form.topic.trim(),
      question: this.form.question.trim() || null,
      conceptExplanation: this.form.conceptExplanation.trim() || null,
      keyPointsJson: this.toJsonList(this.form.keyPoints),
      commonMistakesJson: this.toJsonList(this.form.commonMistakes),
      difficulty: Number(this.form.difficulty) || 3,
      importance: Number(this.form.importance) || 3,
      // 填了公司名才算"来自实战机经";没填就是我自己主动录入的
      source: this.form.sourceCompanyName.trim() ? 'FromInterview' : 'Personal',
      sourceCompanyName: this.form.sourceCompanyName.trim() || null,
      sourceRoundNo: this.form.sourceRoundNo ? Number(this.form.sourceRoundNo) : null,
      sourceRoundStage: this.form.sourceRoundStage || null,
      sourceDate: this.form.sourceDate || null
    };
    this.ref.close(payload);
  }
}
