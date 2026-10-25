import { Component, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { I18nService } from '../../core/i18n/i18n.service';

/**
 * 粘贴文本建资产(A6):把现成的转写稿/面试记录文本直接登记为一条 Text 资产,
 * 不走 STT。确认后返回文本,取消返回 null。
 */
@Component({
  selector: 'app-paste-text-dialog',
  standalone: true,
  imports: [
    FormsModule, MatButtonModule, MatDialogModule,
    MatFormFieldModule, MatInputModule
  ],
  template: `
    <h2 mat-dialog-title>{{ t('pb.asset.pasteTitle') }}</h2>
    <mat-dialog-content class="paste-text">
      <p class="hint">{{ t('pb.asset.pasteHint') }}</p>
      <mat-form-field appearance="outline" class="full">
        <mat-label>{{ t('pb.asset.pasteLabel') }}</mat-label>
        <textarea matInput rows="10" [(ngModel)]="text"
                  [placeholder]="t('pb.asset.pastePlaceholder')"></textarea>
      </mat-form-field>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="null">{{ t('common.cancel') }}</button>
      <button mat-flat-button color="primary"
              [mat-dialog-close]="text.trim() ? text.trim() : null"
              [disabled]="!text.trim()">
        {{ t('pb.asset.create') }}
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .paste-text { min-width: 560px; }
    .full { width: 100%; }
    .hint { color: #475569; font-size: 13px; margin: 0 0 12px; }
    textarea { font-family: inherit; }
  `]
})
export class PasteTextDialogComponent {
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

  text = '';
}
