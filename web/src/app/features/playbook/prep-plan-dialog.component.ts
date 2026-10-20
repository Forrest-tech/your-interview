import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { I18nService } from '../../core/i18n/i18n.service';
import { makeBriefT } from './components/brief/brief.helpers';

/**
 * 备考方案输入对话框（2026-10-19）：
 * JD / 简历 / 全网机经 / 本轮要求 —— 确认后返回输入对象，取消返回 null。
 * data: { roundOrder, jdHint, prevRoundsCount }
 */
export interface PrepPlanInput {
  jdText: string;
  resumeText: string;
  experiences: string;
  requirements: string;
}

@Component({
  selector: 'app-prep-plan-dialog',
  standalone: true,
  imports: [
    FormsModule, MatButtonModule, MatDialogModule,
    MatFormFieldModule, MatInputModule
  ],
  template: `
    <h2 mat-dialog-title>{{ t('pb.br.prep.title') }} · Round {{ data.roundOrder }}</h2>
    <mat-dialog-content class="prep-dialog">
      @if (data.prevRoundsCount > 0) {
        <p class="hint">📌 {{ t('pb.br.prep.nextRoundNote') }}</p>
      }
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dJd') }}</mat-label>
        <textarea matInput rows="4" [(ngModel)]="jdText"></textarea>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dResume') }}</mat-label>
        <textarea matInput rows="4" [(ngModel)]="resumeText"></textarea>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dExperiences') }}</mat-label>
        <textarea matInput rows="4" [(ngModel)]="experiences"
                  placeholder="LinkedIn / 牛客 / 一亩三分地…"></textarea>
      </mat-form-field>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.br.prep.dRequirements') }}</mat-label>
        <textarea matInput rows="3" [(ngModel)]="requirements"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="null">{{ t('common.cancel') }}</button>
      <button mat-flat-button color="primary" [mat-dialog-close]="result()">
        {{ t('pb.br.prep.confirm') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .prep-dialog { min-width: 560px; }
    .full { width: 100%; }
    .hint { color: #666; font-size: 13px; margin: 0 0 12px; }
  `]
})
export class PrepPlanDialogComponent {
  private readonly i18n = inject(I18nService);
  readonly data = inject(MAT_DIALOG_DATA) as {
    roundOrder: number; jdHint: string; prevRoundsCount: number;
  };
  t = makeBriefT(this.i18n);

  jdText = this.data.jdHint ?? '';
  resumeText = '';
  experiences = '';
  requirements = '';

  result(): PrepPlanInput {
    return {
      jdText: this.jdText.trim(),
      resumeText: this.resumeText.trim(),
      experiences: this.experiences.trim(),
      requirements: this.requirements.trim(),
    };
  }
}
