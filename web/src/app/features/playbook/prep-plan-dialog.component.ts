import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { InterviewsApiService } from '../../core/api/interviews-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import { makeBriefT } from './components/brief/brief.helpers';

/**
 * 备考方案输入对话框（2026-10-19；F-06 修复 2026-10-20）：
 * JD / 简历 / 全网机经 / 本轮要求。
 *
 * ★ F-06:打开时用条目已保存的 JD 与 Jobs 保存的简历预填（data.jdHint / data.resumeHint）；
 *   生成在对话框内完成 —— 成功才关窗并回传 PrepPlan；失败把本地化原因显示在
 *   对话框里（按后端错误码映射），绝不静默关窗让用户以为生成成功了。
 * data: { roundOrder, jdHint, resumeHint, prevRoundsCount, entryId, roundId }
 */
export interface PrepPlanInput {
  jdText: string;
  resumeText: string;
  experiences: string;
  requirements: string;
}

/** 与后端 PrepPlanDto 对齐（与 rounds-timeline 的 PrepPlan 同形）。 */
export interface PrepPlanResult {
  assetId: string;
  entryId: string;
  roundId: string;
  roundOrder: number;
  markdown: string;
  model?: string;
  generatedAt: string;
}

@Component({
  selector: 'app-prep-plan-dialog',
  standalone: true,
  imports: [
    FormsModule, MatButtonModule, MatDialogModule,
    MatFormFieldModule, MatInputModule, MatProgressBarModule
  ],
  template: `
    <h2 mat-dialog-title>{{ t('pb.br.prep.title') }} · Round {{ data.roundOrder }}</h2>
    <mat-dialog-content class="prep-dialog">
      @if (data.prevRoundsCount > 0) {
        <p class="hint">📌 {{ t('pb.br.prep.nextRoundNote') }}</p>
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dJd') }}</mat-label>
        <textarea matInput rows="4" [(ngModel)]="jdText" [disabled]="generating()"></textarea>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dResume') }}</mat-label>
        <textarea matInput rows="4" [(ngModel)]="resumeText" [disabled]="generating()"></textarea>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dExperiences') }}</mat-label>
        <textarea matInput rows="4" [(ngModel)]="experiences" [disabled]="generating()"
                  placeholder="LinkedIn / 牛客 / 一亩三分地…"></textarea>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dRequirements') }}</mat-label>
        <textarea matInput rows="3" [(ngModel)]="requirements" [disabled]="generating()"></textarea>
      </mat-form-field>
      @if (generating()) {
        <p class="hint">{{ t('pb.br.prep.generating') }}</p>
        <mat-progress-bar mode="indeterminate"></mat-progress-bar>
      }
      @if (errorMsg()) {
        <p class="err" role="alert">{{ errorMsg() }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="null" [disabled]="generating()">{{ t('common.cancel') }}</button>
      <button mat-flat-button color="primary" [disabled]="generating()" (click)="generate()">
        {{ t('pb.br.prep.confirm') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .prep-dialog { min-width: 560px; }
    .full { width: 100%; }
    .hint { color: #666; font-size: 13px; margin: 0 0 12px; }
    .err { color: #b3261e; font-size: 13px; margin: 12px 0 0; white-space: pre-wrap; }
  `]
})
export class PrepPlanDialogComponent {
  private readonly i18n = inject(I18nService);
  private readonly interviewsApi = inject(InterviewsApiService);
  private readonly ref = inject(MatDialogRef<PrepPlanDialogComponent, PrepPlanResult | null>);
  readonly data = inject(MAT_DIALOG_DATA) as {
    roundOrder: number; jdHint: string; resumeHint?: string; prevRoundsCount: number;
    entryId: string; roundId: string;
  };
  t = makeBriefT(this.i18n);

  // ★ F-06:预填 —— JD 用条目保存的，简历用 Jobs 保存的简历文本。
  jdText = this.data.jdHint ?? '';
  resumeText = this.data.resumeHint ?? '';
  experiences = '';
  requirements = '';

  readonly generating = signal(false);
  readonly errorMsg = signal('');

  /** 生成备考方案：留在对话框里等结果，失败显示本地化原因。 */
  generate(): void {
    if (this.generating()) return;
    this.generating.set(true);
    this.errorMsg.set('');
    const input: PrepPlanInput = {
      jdText: this.jdText.trim(),
      resumeText: this.resumeText.trim(),
      experiences: this.experiences.trim(),
      requirements: this.requirements.trim(),
    };
    this.interviewsApi.createPrepPlan<PrepPlanResult>(
      this.data.entryId, this.data.roundId, input)
      .subscribe({
        next: (p) => { this.generating.set(false); this.ref.close(p); },
        error: (e: unknown) => {
          this.generating.set(false);
          const code = (e as { code?: string } | null)?.code ?? '';
          if (code === 'PrepPlan.AiNotConfigured') {
            this.errorMsg.set(this.t('pb.br.prep.aiNotConfigured'));
          } else if (code === 'PrepPlan.AiFailed') {
            this.errorMsg.set(this.t('pb.br.prep.aiFailed'));
          } else {
            this.errorMsg.set((e as Error)?.message || this.t('pb.br.prep.aiFailed'));
          }
        },
      });
  }
}
