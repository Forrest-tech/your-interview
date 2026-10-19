import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { UpcomingRound } from '../../core/models/api.models';

interface CalDay {
  date: Date;
  inMonth: boolean;
  isToday: boolean;
  rounds: UpcomingRound[];
}

/**
 * R08 日历月视图:全局 upcoming 轮次的日历网格。
 * 数据源 GET /api/interviews/rounds/upcoming?days=60(后端已存在,注释"日历视图用")。
 * 点击日期 chip → 跳条目详情 rounds tab。
 */
@Component({
  selector: 'app-playbook-calendar',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatIconModule, MatButtonModule, MatTooltipModule],
  templateUrl: './playbook-calendar.component.html',
  styleUrl: './playbook-calendar.component.scss'
})
export class PlaybookCalendarComponent implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);
  private readonly router = inject(Router);

  readonly t = (key: string) => this.i18n.t(key);

  readonly loading = signal(true);
  readonly rounds = signal<UpcomingRound[]>([]);
  /** 当前显示月份的第一天。 */
  readonly viewMonth = signal<Date>(startOfMonth(new Date()));

  /** 按 yyyy-MM-dd 分组的轮次。 */
  private readonly roundsByDate = computed(() => {
    const map = new Map<string, UpcomingRound[]>();
    for (const r of this.rounds()) {
      if (!r.scheduledDate) continue;
      const key = r.scheduledDate.slice(0, 10);
      const list = map.get(key) ?? [];
      list.push(r);
      map.set(key, list);
    }
    return map;
  });

  /** 42 格日历(6 周 × 7 天),周一起始。 */
  readonly weeks = computed<CalDay[][]>(() => {
    const first = this.viewMonth();
    const map = this.roundsByDate();
    const today = new Date();
    const todayKey = toKey(today);

    // 周一起始:JS getDay() 周日=0 → 转为周一=0
    const leadDays = (first.getDay() + 6) % 7;
    const start = new Date(first);
    start.setDate(start.getDate() - leadDays);

    const days: CalDay[] = [];
    for (let i = 0; i < 42; i++) {
      const d = new Date(start);
      d.setDate(d.getDate() + i);
      const key = toKey(d);
      days.push({
        date: d,
        inMonth: d.getMonth() === first.getMonth(),
        isToday: key === todayKey,
        rounds: map.get(key) ?? []
      });
    }
    const weeks: CalDay[][] = [];
    for (let w = 0; w < 6; w++) weeks.push(days.slice(w * 7, w * 7 + 7));
    return weeks;
  });

  readonly monthLabel = computed(() => {
    const d = this.viewMonth();
    return `${d.getFullYear()} / ${String(d.getMonth() + 1).padStart(2, '0')}`;
  });

  readonly weekHeaders = ['一', '二', '三', '四', '五', '六', '日'];

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.api.get<UpcomingRound[]>(`/api/interviews/rounds/upcoming?days=60`).subscribe({
      next: (list) => {
        this.rounds.set(list ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.rounds.set([]);
        this.loading.set(false);
      }
    });
  }

  prevMonth(): void {
    const d = new Date(this.viewMonth());
    d.setMonth(d.getMonth() - 1);
    this.viewMonth.set(startOfMonth(d));
  }

  nextMonth(): void {
    const d = new Date(this.viewMonth());
    d.setMonth(d.getMonth() + 1);
    this.viewMonth.set(startOfMonth(d));
  }

  goToday(): void {
    this.viewMonth.set(startOfMonth(new Date()));
  }

  goRound(r: UpcomingRound): void {
    this.router.navigate(['/playbook', r.entryId], { queryParams: { tab: 'rounds' } });
  }
}

function startOfMonth(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), 1);
}

function toKey(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}
