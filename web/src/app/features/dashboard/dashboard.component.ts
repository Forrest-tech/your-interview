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
import { ActionCenter, Application, ApplicationStatus, Dashboard } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

/** 行动中心卡片。 */
interface ActionCard {
  kind: 'followup' | 'deadline' | 'weak' | 'pipeline';
  icon: string;
  titleKey: string;
  count: number;
  link: string;
  ctaKey: string;
}

/**
 * 总览页 —— 一屏回答"我现在什么情况、下一步该做什么"。
 * M4:新增"行动中心",把已有的聚合数据变成按优先级排列的可执行清单。
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
  readonly actionCenter = signal<ActionCenter | null>(null);

  readonly weakest = computed(() => {
    const radar = this.dashboard()?.radar ?? [];
    return [...radar].sort((a, b) => a.current - b.current).slice(0, 3);
  });

  readonly insight = computed(() => this.dashboard()?.insight?.headline ?? '');
  readonly suggestedAction = computed(() => this.dashboard()?.insight?.suggestedAction ?? '');

  readonly total = computed(() => this.actionCenter()?.total ?? 0);
  readonly activeCount = computed(() => this.actionCenter()?.activeCount ?? 0);
  readonly interviewCount = computed(() => this.actionCenter()?.interviewCount ?? 0);
  readonly offerCount = computed(() => this.actionCenter()?.offerCount ?? 0);

  /** 行动中心卡片:按优先级(跟进 > 截止 > 短板 > 漏斗)排列。 */
  readonly cards = computed<ActionCard[]>(() => {
    const ac = this.actionCenter();
    if (!ac) return [];
    const out: ActionCard[] = [];
    if (ac.followUps.length > 0) {
      out.push({ kind: 'followup', icon: 'notifications_active', titleKey: 'dash.followUpsDue', count: ac.followUps.length, link: '/tracker', ctaKey: 'dash.goHandle' });
    }
    if (ac.deadlines.length > 0) {
      out.push({ kind: 'deadline', icon: 'event_busy', titleKey: 'dash.deadlinesSoon', count: ac.deadlines.length, link: '/tracker', ctaKey: 'dash.goHandle' });
    }
    if (this.weakest().length > 0) {
      out.push({ kind: 'weak', icon: 'trending_down', titleKey: 'dash.weakAreas', count: this.weakest().length, link: '/practice', ctaKey: 'dash.goPractice' });
    }
    out.push({ kind: 'pipeline', icon: 'account_tree', titleKey: 'dash.pipeline', count: ac.total, link: '/tracker', ctaKey: 'dash.viewTracker' });
    return out;
  });

  readonly hasActions = computed(() =>
    (this.actionCenter()?.followUps.length ?? 0) > 0 ||
    (this.actionCenter()?.deadlines.length ?? 0) > 0 ||
    this.weakest().length > 0);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    let pending = 2;
    const done = () => { if (--pending === 0) this.loading.set(false); };

    this.api.get<ActionCenter>('/api/jobs/action-center').subscribe({
      next: (d) => { this.actionCenter.set(d); done(); },
      error: (e: Error) => { this.error.set(e.message); done(); }
    });

    this.api.get<Dashboard>('/api/analytics/dashboard').subscribe({
      next: (d) => { this.dashboard.set(d); done(); },
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

  statusLabel(s: string): string {
    const key = 'status.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
  }

  priorityClass(priority?: string): string {
    if (priority === 'High') return 'pri-high';
    if (priority === 'Medium') return 'pri-mid';
    return 'pri-low';
  }

  companyInitials(name: string): string {
    const parts = (name || '?').trim().split(/\s+/);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }

  avatarColor(name: string): string {
    let h = 0;
    for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) % 360;
    return `hsl(${h} 62% 46%)`;
  }

  /** 兼容 DateTimeOffset 与 DateOnly 两种 ISO 串,只取日期部分。 */
  dateOnly(iso?: string): string {
    if (!iso) return '';
    return iso.slice(0, 10);
  }
}
