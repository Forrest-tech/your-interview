import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MatDialogRef, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { RouterLink } from '@angular/router';
import { I18nService } from '../../core/i18n/i18n.service';

export interface GuidanceInputData {
  jdText: string;
  resumeText: string;
  interviewExperiences: string;
  customRequirements: string;
}

/**
 * 备战材料生成输入: JD / 简历 / 面经 / 自定义要求。
 * 四路输入都会拼进 AI prompt,生成针对性备战内容。
 */
@Component({
  selector: 'app-guidance-input-dialog',
  standalone: true,
  imports: [
    FormsModule, MatButtonModule, MatDialogModule,
    MatFormFieldModule, MatInputModule, RouterLink
  ],
  template: `
    <h2 mat-dialog-title>{{ t('pb.guidance.inputTitle') }}</h2>
    <mat-dialog-content class="guidance-input">
      <p class="hint">{{ t('pb.guidance.inputHint') }}</p>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.guidance.inputJd') }}</mat-label>
        <textarea matInput rows="5" [(ngModel)]="data.jdText"
                  [placeholder]="t('pb.guidance.inputJdPh')"></textarea>
        @if (!data.jdText.trim()) {
          <mat-hint>
            {{ t('pb.guidance.noJd') }}
            <a routerLink="/tracker" (click)="close()">{{ t('pb.guidance.goTracker') }}</a>
          </mat-hint>
        }
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.guidance.inputResume') }}</mat-label>
        <textarea matInput rows="5" [(ngModel)]="data.resumeText"
                  [placeholder]="t('pb.guidance.inputResumePh')"></textarea>
        @if (!data.resumeText.trim()) {
          <mat-hint>
            {{ t('pb.guidance.noResume') }}
            <a routerLink="/profile" (click)="close()">{{ t('pb.guidance.goProfile') }}</a>
          </mat-hint>
        } @else {
          <mat-hint>{{ t('pb.guidance.resumeFromProfile') }}</mat-hint>
        }
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.guidance.inputExperiences') }} ({{ t('common.optional') }})</mat-label>
        <textarea matInput rows="4" [(ngModel)]="data.interviewExperiences"
                  [placeholder]="t('pb.guidance.inputExperiencesPh')"></textarea>
      </mat-form-field>

      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.guidance.inputCustom') }} ({{ t('common.optional') }})</mat-label>
        <textarea matInput rows="3" [(ngModel)]="data.customRequirements"
                  [placeholder]="t('pb.guidance.inputCustomPh')"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="null">{{ t('common.cancel') }}</button>
      <button mat-flat-button color="primary" [mat-dialog-close]="data">
        {{ t('pb.guidance.generate') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .guidance-input { min-width: 600px; }
    .full { width: 100%; margin-bottom: 8px; }
    .hint { color: #666; font-size: 13px; margin: 0 0 12px; }
    textarea { font-family: inherit; }
  `]
})
export class GuidanceInputDialogComponent {
  readonly data = inject<GuidanceInputData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<GuidanceInputDialogComponent>);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  close(): void {
    this.dialogRef.close(null);
  }
}
