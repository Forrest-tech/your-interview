import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatDialogModule, MAT_DIALOG_DATA } from '@angular/material/dialog';
import { I18nService } from '../../core/i18n/i18n.service';

export interface BackfillReportData {
  skipped: string[];
  failed: string[];
  ambiguous: string[];
}

/**
 * 票 249 C3-reporting:回填报告对话框 —— 跳过 / 失败 / 待消歧三类公司名分开展示。
 * 跳过 = 本轮未找到可用 logo(需补 Website 后再跑);失败 = 尝试中出错(来源需修复);
 * 待消歧 = Wikidata 平局,域名未写,需人工判断。仅展示,不改数据;模板放独立 .html,
 * 供 check-templates 解析(内联模板不计入)。
 */
@Component({
  selector: 'app-backfill-report-dialog',
  standalone: true,
  imports: [CommonModule, MatDialogModule, MatButtonModule],
  templateUrl: './backfill-report-dialog.component.html',
})
export class BackfillReportDialogComponent {
  readonly data = inject(MAT_DIALOG_DATA) as BackfillReportData;
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
}
