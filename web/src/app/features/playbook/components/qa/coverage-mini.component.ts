import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { QaEntryDetail, QaQuestion, qaCategoryOf } from './qa.models';
import { QA_I18N } from './qa.i18n';

/**
 * F5 技术覆盖小条 —— 本场面试各 category 题目计数。
 *
 * 自包含:只认 entryId,从 entry detail 的 questions 本地聚合,
 * 不调 A5 全局接口。父组件在 Overview tab 内放
 * <app-coverage-mini [entryId]="id()"></app-coverage-mini>。
 */
@Component({
  selector: 'app-coverage-mini',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatIconModule, MatProgressSpinnerModule],
  templateUrl: './coverage-mini.component.html',
  styleUrl: './coverage-mini.component.scss'
})
export class CoverageMiniComponent {
  readonly entryId = input.required<string>();

  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);

  t = (key: string): string => {
    const e = QA_I18N[key];
    if (e) return e[this.i18n.lang()] ?? e.en ?? key;
    return this.i18n.t(key);
  };
  tn = (key: string, n: string | number | null | undefined): string =>
    this.t(key).replace('{n}', String(n ?? ''));

  readonly loading = signal(true);
  readonly questions = signal<QaQuestion[]>([]);

  constructor() {
    effect(() => { this.load(this.entryId()); });
  }

  /** 各 category 计数,按数量 desc;bar 相对最大值归一。 */
  readonly dist = computed(() => {
    const counts = new Map<string, number>();
    for (const q of this.questions()) {
      const c = qaCategoryOf(q);
      counts.set(c, (counts.get(c) ?? 0) + 1);
    }
    const rows = [...counts.entries()].map(([name, count]) => ({ name, count, pct: 0 }));
    const max = Math.max(1, ...rows.map(r => r.count));
    for (const r of rows) r.pct = Math.round((r.count / max) * 100);
    rows.sort((a, b) => b.count - a.count);
    return rows;
  });

  private load(id: string): void {
    if (!id) { this.loading.set(false); return; }
    this.loading.set(true);
    this.api.get<QaEntryDetail>(`/api/interviews/${id}`).subscribe({
      next: d => {
        this.questions.set(d?.questions ?? []);
        this.loading.set(false);
      },
      error: () => {
        // 小条是点缀,失败时静默留空,不挡 Overview 主内容。
        this.questions.set([]);
        this.loading.set(false);
      }
    });
  }

  catLabel(c: string): string {
    return QA_I18N[`pb.qa.cat.${c}`] ? this.t(`pb.qa.cat.${c}`) : c;
  }
}
