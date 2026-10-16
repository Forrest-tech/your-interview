import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import type { BriefEntry, UpcomingRoundItem } from './brief.models';
import {
  applyParams, daysUntil, latestEmail, makeBriefT, nextRound, todayStr,
} from './brief.helpers';

/**
 * F5 页头信息条（selector: app-playbook-header），两行：
 *  - 第一行：公司名 + 职位 + 状态徽章 + 下一轮倒计时
 *  - 第二行：recruiter（从各轮 emailsJson 取最近的 from）+ 上次联系时间
 *
 * 自包含：entryId input，自己调 API。
 * 倒计时优先用 /rounds/upcoming（带 meetingLink 等完整字段），回退详情 rounds。
 */
@Component({
  selector: 'app-playbook-header',
  standalone: true,
  imports: [CommonModule, MatCardModule, MatIconModule, MatChipsModule],
  templateUrl: './playbook-header.component.html',
  styleUrl: './playbook-header.component.scss',
})
export class PlaybookHeaderComponent implements OnInit {
  entryId = input.required<string>();

  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  t = makeBriefT(this.i18n);
  fmt = (key: string, params: Record<string, string | number>): string =>
    applyParams(this.t(key), params);

  readonly entry = signal<BriefEntry | null>(null);
  readonly upcomingMine = signal<UpcomingRoundItem[]>([]);

  readonly nextInfo = computed((): { label: string; date: string } | null => {
    const t = todayStr();
    const ups = this.upcomingMine()
      .filter((u) => (u.scheduledDate ?? '') >= t)
      .sort((a, b) => (a.scheduledDate ?? '').localeCompare(b.scheduledDate ?? ''));
    if (ups.length > 0) {
      const u = ups[0];
      return { label: this.roundLabel(u.order, u.stage), date: u.scheduledDate ?? '' };
    }
    const r = nextRound(this.entry()?.rounds);
    if (!r?.scheduledDate) return null;
    return { label: this.roundLabel(r.order, r.stage), date: r.scheduledDate };
  });

  readonly countdownText = computed((): string => {
    const n = this.nextInfo();
    if (!n) return this.t('pb.br.hd.noSchedule');
    const d = daysUntil(n.date);
    const when =
      d <= 0 ? this.t('pb.br.countdown.today')
      : d === 1 ? this.t('pb.br.countdown.tomorrow')
      : this.fmt('pb.br.countdown.daysLeft', { n: d });
    return `${n.label} · ${n.date} · ${when}`;
  });

  /** recruiter = 各轮 emailsJson 里最近一封邮件的 from；lastContact = 它的日期。 */
  readonly contact = computed(() => latestEmail(this.entry()?.rounds));

  statusLabel(s: string): string {
    const key = 'pb.status.' + s;
    const v = this.i18n.t(key);
    return v !== key ? v : s;
  }

  statusClass(s: string): string {
    if (s === 'Analyzed') return 'ok';
    if (s === 'Failed') return 'bad';
    if (s === 'Transcribing' || s === 'Analyzing') return 'busy';
    return 'idle';
  }

  private roundLabel(order: number, stage: string): string {
    return `${this.fmt('pb.round', { n: order })} ${stage}`;
  }

  ngOnInit(): void {
    const id = this.entryId();
    this.api.get<BriefEntry>(`/api/interviews/${id}`)
      .pipe(catchError(() => of(null)))
      .subscribe((e) => this.entry.set(e));
    this.api.get<UpcomingRoundItem[]>('/api/interviews/rounds/upcoming', { days: 60 })
      .pipe(catchError(() => of([] as UpcomingRoundItem[])))
      .subscribe((list) =>
        this.upcomingMine.set((list ?? []).filter((u) => u.entryId === id)));
  }
}
