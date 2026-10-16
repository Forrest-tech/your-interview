import { Component, computed, inject, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { QaQuestion, qaCategoryOf } from './qa.models';
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
  imports: [CommonModule, MatCardModule, MatIconModule],
  templateUrl: './coverage-mini.component.html',
  styleUrl: './coverage-mini.component.scss'
})
export class CoverageMiniComponent {
  /** 父组件已加载的题目 —— 直接复用,不再单独请求(老代码 effect 内写 signal 触发 NG0600)。 */
  readonly questionsInput = input<QaQuestion[]>([]);

  private readonly i18n = inject(I18nService);

  t = (key: string): string => {
    const e = QA_I18N[key];
    if (e) return e[this.i18n.lang()] ?? e.en ?? key;
    return this.i18n.t(key);
  };
  tn = (key: string, n: string | number | null | undefined): string =>
    this.t(key).replace('{n}', String(n ?? ''));

  /** 各 category 计数,按数量 desc;bar 相对最大值归一。 */
  readonly dist = computed(() => {
    const counts = new Map<string, number>();
    for (const q of this.questionsInput()) {
      const c = qaCategoryOf(q);
      counts.set(c, (counts.get(c) ?? 0) + 1);
    }
    const rows = [...counts.entries()].map(([name, count]) => ({ name, count, pct: 0 }));
    const max = Math.max(1, ...rows.map(r => r.count));
    for (const r of rows) r.pct = Math.round((r.count / max) * 100);
    rows.sort((a, b) => b.count - a.count);
    return rows;
  });

  catLabel(c: string): string {
    return QA_I18N[`pb.qa.cat.${c}`] ? this.t(`pb.qa.cat.${c}`) : c;
  }
}
