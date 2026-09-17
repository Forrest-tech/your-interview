import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { I18nService } from '../../core/i18n/i18n.service';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ApiClient } from '../../core/api/api-client';
import { Dashboard, TrackerStats } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

/**
 * 总览页 —— 一屏回答"我现在什么情况、下一步该做什么"。
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule, RouterLink, MatCardModule, MatIconModule,
    MatButtonModule, MatTooltipModule, MatProgressBarModule
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(ApiClient);
  readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly dashboard = signal<Dashboard | null>(null);
  readonly stats = signal<TrackerStats | null>(null);

  readonly weakest = computed(() => {
    const radar = this.dashboard()?.radar ?? [];
    return [...radar].sort((a, b) => a.current - b.current).slice(0, 3);
  });

  readonly insight = computed(() => this.dashboard()?.insight?.headline ?? '');

  readonly offerCount = computed(() => this.stats()?.offerCount ?? 0);
  readonly interviewCount = computed(() => this.stats()?.interviewCount ?? 0);
  readonly activeCount = computed(() => this.stats()?.activeCount ?? 0);
  readonly totalCount = computed(() => this.stats()?.total ?? 0);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    let pending = 2;
    const done = () => { if (--pending === 0) this.loading.set(false); };

    this.api.get<Dashboard>('/api/analytics/dashboard').subscribe({
      next: (d) => { this.dashboard.set(d); done(); },
      error: (e: Error) => { this.error.set(e.message); done(); }
    });

    this.api.get<TrackerStats>('/api/jobs/stats').subscribe({
      next: (s) => { this.stats.set(s); done(); },
      error: () => done()
    });
  }

  t(key: string): string {
    return this.i18n.t(key);
  }

  scoreClass(score: number): string {
    if (score >= 75) return 'good';
    if (score >= 55) return 'mid';
    return 'bad';
  }

  dimLabel(r: { dimension: string; nameZh?: string }): string {
    const key = 'dim.' + r.dimension;
    const translated = this.i18n.t(key);
    if (translated !== key) return translated;
    return r.nameZh ?? r.dimension;
  }
}
