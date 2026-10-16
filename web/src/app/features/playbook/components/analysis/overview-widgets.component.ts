import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { analysisT } from './analysis.i18n';
import { NextActionsComponent } from './next-actions.component';
import { AnalysisSnapshotDto, parseBullets } from './analysis.models';

/**
 * F5 Overview 备战总览部件:
 *   待办区(A2 未完成事项,可勾选 —— 复用 app-next-actions 紧凑模式,与 F2 同一份数据跨 tab 同步)
 * + 最新复盘结论(最近快照的前 3 条 bullets)。
 */
@Component({
  selector: 'app-overview-widgets',
  standalone: true,
  imports: [
    CommonModule, MatCardModule, MatIconModule, MatButtonModule,
    NextActionsComponent
  ],
  templateUrl: './overview-widgets.component.html',
  styleUrl: './overview-widgets.component.scss'
})
export class OverviewWidgetsComponent implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);

  readonly t = analysisT(this.i18n);

  /** 面试条目 ID —— 组件自己调 API,不依赖父组件数据。 */
  readonly entryId = input.required<string>();

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly latest = signal<AnalysisSnapshotDto | null>(null);

  /** 最新复盘结论:最近快照的前 3 条 bullets。 */
  readonly takeaways = computed(() => parseBullets(this.latest()?.summaryBulletsJson).slice(0, 3));

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    const id = this.entryId();
    this.loading.set(true);
    this.error.set(null);
    this.api.get<AnalysisSnapshotDto | null>(`/api/interviews/${id}/analysis/latest`).subscribe({
      next: (s) => {
        this.latest.set(s);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message || this.t('pb.an.loadFailed'));
        this.loading.set(false);
      }
    });
  }

  shortDate(iso: string | null | undefined): string {
    if (!iso) return '';
    return iso.slice(0, 16).replace('T', ' ');
  }
}
