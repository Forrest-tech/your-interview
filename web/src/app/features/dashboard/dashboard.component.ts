import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { ApiClient } from '../../core/api/api-client';
import { Dashboard, TrackerStats } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

/**
 * 总览页 —— 一屏回答"我现在什么情况、下一步该做什么"。
 *
 * 刻意不做成数据大杂烩:只放四块最该看的(进度条 + 六维 + 短板 Top + 建议下一步)。
 * 数据多的页面等于把分析工作丢回给用户。
 */
@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    CommonModule, RouterLink, MatCardModule, MatIconModule,
    MatButtonModule, MatProgressBarModule, MatChipsModule
  ],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(ApiClient);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly dashboard = signal<Dashboard | null>(null);
  readonly stats = signal<TrackerStats | null>(null);

  /** 六维取最弱的三项 —— 用户该先改的就是这三个。 */
  readonly weakest = computed(() => {
    const radar = this.dashboard()?.radar ?? [];
    return [...radar].sort((a, b) => a.current - b.current).slice(0, 3);
  });

  // insight 是对象 {headline, detail, suggestedAction, severity},不是字符串。
  // 只取 headline 放进度条旁边 —— detail 太长,塞进小字会挤成一团。
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

    // 两个请求并行 —— 串行会让首屏白等一个来回
    let pending = 2;
    const done = () => { if (--pending === 0) this.loading.set(false); };

    this.api.get<Dashboard>('/api/analytics/dashboard').subscribe({
      next: (d) => { this.dashboard.set(d); done(); },
      error: (e: Error) => { this.error.set(e.message); done(); }
    });

    this.api.get<TrackerStats>('/api/jobs/stats').subscribe({
      next: (s) => { this.stats.set(s); done(); },
      error: () => done()   // 统计失败不该拖垮整页
    });
  }

  /** 分数对应的颜色档位 —— 让"哪项差"一眼看出来,不用读数字。 */
  scoreClass(score: number): string {
    if (score >= 75) return 'good';
    if (score >= 55) return 'mid';
    return 'bad';
  }

  statusLabel(s: string): string {
    const map: Record<string, string> = {
      Saved: '已收藏', Applied: '已投递', Screen: '初筛',
      Interview: '面试中', Offer: 'Offer', Rejected: '已拒',
      Paused: '暂停', Withdrawn: '已撤回'
    };
    return map[s] ?? s;
  }
}
