import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { I18nService } from '../../core/i18n/i18n.service';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { JobsApiService } from '../../core/api/jobs-api.service';
import { InterviewsApiService } from '../../core/api/interviews-api.service';
import { AnalyticsApiService } from '../../core/api/analytics-api.service';
import { ActionCenter, Application, ApplicationStatus, Dashboard, UpcomingRound } from '../../core/models/api.models';
import { PIPELINE_STAGES, PIPELINE_VERDICT } from '../../core/models/pipeline-stages';
import { weakestRadarPoints } from '../../core/models/radar.helpers';
import {
  dimensionLabel, localizeInsight, roundFormatLabel, stageLabel
} from '../../core/i18n/localized-messages.helpers';
import { formatLabel, makeV2T } from '../playbook/redesign/v2.helpers';
import { AuthService } from '../../core/auth/auth.service';

/** 行动中心卡片。 */
interface ActionCard {
  kind: 'followup' | 'deadline' | 'weak' | 'pipeline' | 'upcoming';
  icon: string;
  titleKey: string;
  count: number;
  link: string;
  ctaKey: string;
}

/**
 * 票123 R-9:Pipeline Band 上的一个节点。
 * verdict = true 表示它是断口之后的终局裁决节点(Rejected),不是阶段序列的一员。
 */
interface BandNode {
  status: ApplicationStatus;
  count: number;
  verdict: boolean;
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
  private readonly jobsApi = inject(JobsApiService);
  private readonly analyticsApi = inject(AnalyticsApiService);
  private readonly interviewsApi = inject(InterviewsApiService);
  readonly auth = inject(AuthService);
  readonly i18n = inject(I18nService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly dashboard = signal<Dashboard | null>(null);
  readonly actionCenter = signal<ActionCenter | null>(null);
  /** ★ 即将到来的面试轮次。 */
  readonly upcomingRounds = signal<UpcomingRound[]>([]);

  /** REQ-08:无 scored 数据时为空——雷达面板走 dash.noAnalysis 空态,行动中心不出弱项卡。 */
  readonly weakest = computed(() => weakestRadarPoints(this.dashboard()?.radar ?? []));

  /** FUN-07(票35):insight 后端只回 Code+Params,这里本地化为当前语言文案。 */
  private readonly insightText = computed(() =>
    localizeInsight(this.dashboard()?.insight, this.i18n.lang(),
      (k) => this.i18n.t(k), (k, p) => this.i18n.tf(k, p)));
  readonly insight = computed(() => this.insightText()?.headline ?? '');
  readonly suggestedAction = computed(() => this.insightText()?.action ?? '');

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
    // ★ 即将到来的面试:最高优先级
    if (this.upcomingRounds().length > 0) {
      out.push({ kind: 'upcoming', icon: 'event', titleKey: 'dash.upcomingRounds', count: this.upcomingRounds().length, link: '/playbook', ctaKey: 'dash.goPrepare' });
    }
    if (ac.deadlines.length > 0) {
      out.push({ kind: 'deadline', icon: 'event_busy', titleKey: 'dash.deadlinesSoon', count: ac.deadlines.length, link: '/tracker', ctaKey: 'dash.goHandle' });
    }
    if (this.weakest().length > 0) {
      out.push({ kind: 'weak', icon: 'trending_down', titleKey: 'dash.weakAreas', count: this.weakest().length, link: '/practice', ctaKey: 'dash.goPractice' });
    }
    // 票123 R-9:「投递漏斗」卡删掉了 —— 漏斗由页面上方的 Pipeline Band 承担
    // (定稿:「The old "Pipeline" card is deleted」)。行动中心保留
    // Upcoming + Weak areas 两格。'pipeline' 这个 kind 仍在类型里保留,
    // 以免改动既有分支结构;但它不再被 push 进来。
    return out;
  });

  readonly hasActions = computed(() =>
    (this.actionCenter()?.followUps.length ?? 0) > 0 ||
    (this.actionCenter()?.deadlines.length ?? 0) > 0 ||
    this.weakest().length > 0 ||
    this.upcomingRounds().length > 0);

  // ── 票123 R-9:Pipeline Band ──────────────────────────────────────────────
  // 定稿:节点 = 规范阶段序列(共享常量)+ 断口后的终局裁决节点;
  // 计数从 ActionCenter.byStatus 取(缺席 ⇒ 0)—— 不新增任何 API。

  /** 漏斗带上的一个节点:阶段 + 计数 + 节点状态。 */
  readonly bandNodes = computed<BandNode[]>(() => {
    const byStatus = this.actionCenter()?.byStatus ?? [];
    const countOf = (s: string): number =>
      byStatus.find(x => x.status === s)?.count ?? 0;

    const nodes: BandNode[] = PIPELINE_STAGES.map(stage => ({
      status: stage,
      count: countOf(stage),
      verdict: false,
    }));
    // 断口之后的终局裁决节点
    nodes.push({
      status: PIPELINE_VERDICT,
      count: countOf(PIPELINE_VERDICT),
      verdict: true,
    });
    return nodes;
  });

  /** 带上的节点状态 —— 与 Tracker 列表 stepper 逐字一致的三种处理。 */
  bandNodeClass(n: BandNode): string {
    if (n.count <= 0) return 'hollow';
    if (n.verdict) return 'stop';
    // Offer 走终局裁决处理(go),其余走「已到达」
    return n.status === 'Offer' ? 'go' : 'reached';
  }

  /** 轨道段:一段落在「已到达」与「未来」之间时取 done 色。
   *  规则:该段的**下一节点**已到达时取 done —— 与 Tracker 列表 stepper 同一判定。 */
  bandSegmentDone(i: number): boolean {
    const nodes = this.bandNodes();
    const next = nodes[i + 1];
    if (!next) return false;
    return next.count > 0;
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    let pending = 3;
    const done = () => { if (--pending === 0) this.loading.set(false); };

    this.jobsApi.getActionCenter().subscribe({
      next: (d) => { this.actionCenter.set(d); done(); },
      error: (e: Error) => { this.error.set(e.message); done(); }
    });

    this.analyticsApi.getDashboard().subscribe({
      next: (d) => { this.dashboard.set(d); done(); },
      error: () => done()
    });

    // ★ 即将到来的面试轮次
    this.interviewsApi.getUpcomingRounds().subscribe({
      next: (list) => { this.upcomingRounds.set(list ?? []); done(); },
      error: () => done()
    });
  }

  t(key: string): string {
    return this.i18n.t(key);
  }

  /** 计数插值版(票123 R-9 的 Pipeline Band 说明行用)。 */
  tn(key: string, n: number): string {
    return this.i18n.tn(key, n);
  }

  scoreClass(score: number): string {
    if (score >= 75) return 'good';
    if (score >= 55) return 'mid';
    return 'bad';
  }

  /** B3 口径:缺键时仅 zh 回退 nameZh,EN/FR 回退 dimension 原值(共用纯函数)。 */
  dimLabel(r: { dimension: string; nameZh?: string }): string {
    return dimensionLabel(r.dimension, r.nameZh, this.i18n.lang(), (k) => this.i18n.t(k));
  }

  /** Upcoming 轮次号包裹串(A2):第 {n} 轮 / Round {n} / Tour {n}。 */
  roundLabel(order: number): string {
    return this.i18n.tn('dash.roundN', order);
  }

  /** Upcoming stage 存储值 → 本地化标签(已知规范值映射,未知原样)。 */
  stageText(stage?: string): string {
    return stageLabel(stage, (k) => this.i18n.t(k));
  }

  /** Upcoming format 存储值 → 本地化标签(中文规范值走 v2 formatLabel,英文码走 fm* 键)。 */
  formatText(format?: string): string {
    if (!format) return '';
    const en = roundFormatLabel(format, (k) => this.i18n.t(k));
    if (en !== format) return en;
    return formatLabel(makeV2T(this.i18n), format);
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

  /** ★ #16:弱项一键刷题 → 口语类去 AI Practice,知识类去 Tech Stack。 */
  drillWeakness(dimension: string): void {
    const speaking = ['pronunciation', 'fluency', 'sentenceintegrity', 'pron', 'flu'];
    const d = dimension.toLowerCase().replace(/[^a-z]/g, '');
    if (speaking.some(s => d.includes(s))) {
      this.router.navigate(['/practice']);
    } else {
      this.router.navigate(['/tech-stack'], { queryParams: { topic: dimension } });
    }
  }

  private readonly router = inject(Router);
}
