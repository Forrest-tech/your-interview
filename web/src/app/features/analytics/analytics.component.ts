import {
  AfterViewInit, Component, ElementRef, NgZone, OnDestroy, OnInit,
  computed, inject, signal, viewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import * as echarts from 'echarts';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { AbilityTrend, Dashboard } from '../../core/models/api.models';

/** 漏斗阶段的颜色档:越靠后越"贵",用同一色系由浅到深表示推进。 */
const FUNNEL_COLORS = ['#9fa8da', '#7986cb', '#5c6bc0', '#3f51b5', '#303f9f'];

/**
 * 数据分析页。
 *
 * 为什么用 ECharts 画雷达而不是 CSS:
 * 六边形雷达用 CSS 要拿 clip-path + 固定顶点坐标硬算,轴标签定位更难;
 * echarts 一行配置就能出,且能给出 tooltip。
 * 其余图形(漏斗、熟练度)用纯 CSS 条形 —— 它们本质是"一排宽度不同的条",
 * 再引第二套图表库属于杀鸡用牛刀。
 */
@Component({
  selector: 'app-analytics',
  standalone: true,
  imports: [
    CommonModule, RouterLink, MatCardModule, MatIconModule, MatButtonModule,
    MatProgressBarModule, MatChipsModule, MatTooltipModule
  ],
  templateUrl: './analytics.component.html',
  styleUrl: './analytics.component.scss'
})
export class AnalyticsComponent implements OnInit, AfterViewInit, OnDestroy {
  private readonly api = inject(ApiClient);
  private readonly zone = inject(NgZone);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly dashboard = signal<Dashboard | null>(null);
  readonly trend = signal<AbilityTrend | null>(null);

  /** 雷达容器:用 viewChild 而不是 @ViewChildren,Angular 17 的 signal query 更贴合 signal 风格。 */
  private readonly radarHost = viewChild<ElementRef<HTMLDivElement>>('radarHost');

  private chart: echarts.ECharts | null = null;

  /**
   * ResizeObserver 比 window.resize 更准:侧边栏折叠时 window 尺寸不变,
   * 但容器宽度变了,监听 window 会导致图表被拉伸变形。
   */
  private resizeObserver: ResizeObserver | null = null;

  // 后端 insight 是对象 {headline, detail, suggestedAction, severity}
  readonly insight = computed(() => this.dashboard()?.insight ?? null);

  readonly radarPoints = computed(() => this.dashboard()?.radar ?? []);

  /**
   * 漏斗:后端 pipeline 是"每天的累计快照"数组,不是按状态聚合的漏斗。
   * 所以取最后一天那条快照,把它的各阶段读成一个漏斗。
   * 直接 map 整个数组会把 30 天的数据画成 30 组柱子,完全读不出转化。
   */
  readonly funnel = computed<{ stage: string; count: number; pct: number }[]>(() => {
    const snaps = this.dashboard()?.pipeline ?? [];
    if (snaps.length === 0) return [];
    const last = [...snaps].sort((a, b) => a.date.localeCompare(b.date))[snaps.length - 1];
    const stages = [
      { stage: '已收藏', count: last.saved },
      { stage: '已投递', count: last.applied },
      { stage: '初筛', count: last.screening },
      { stage: '面试', count: last.interviewing },
      { stage: 'Offer', count: last.offered },
      { stage: '已拒', count: last.rejected }
    ];
    const max = Math.max(...stages.map((r) => r.count), 1);
    return stages.map((r) => ({ ...r, pct: Math.round((r.count / max) * 100) }));
  });

  /** 熟练度分布:以 total 为分母算百分比,条形宽度才有可比性。 */
  readonly masteryRows = computed(() => {
    const rows = this.dashboard()?.mastery ?? [];
    return rows.map((m) => {
      const total = m.total > 0 ? m.total : 1;
      return {
        topic: m.topic,
        total: m.total,
        mastered: m.mastered,
        masteredPct: Math.round((m.mastered / total) * 100),
        familiarPct: Math.round((m.fresh / total) * 100),
        learningPct: Math.round((m.learning / total) * 100)
      };
    }).sort((a, b) => b.total - a.total);
  });

  readonly weakest = computed(() =>
    [...this.radarPoints()].sort((a, b) => a.current - b.current).slice(0, 3)
  );

  /** 趋势只做粗粒度摘要 —— 先把"在变好还是变差"说清楚,曲线细节留给图表。 */
  readonly trendSummary = computed(() => {
    const t = this.trend();
    if (!t || !t.series || t.series.length === 0) return null;

    return t.series.map((ser) => {
      // points 后端已按日期升序给出;仍然不假设顺序,显式排序更稳
      const pts = [...(ser.points ?? [])].sort((a, b) => a.date.localeCompare(b.date));
      const first = pts.length > 0 ? pts[0].value : 0;
      const last = pts.length > 0 ? pts[pts.length - 1].value : 0;
      return {
        dimension: ser.dimension,
        nameZh: ser.nameZh,
        delta: Math.round((last - first) * 10) / 10,
        from: first,
        to: last
      };
    }).sort((a, b) => a.delta - b.delta);
  });

  ngOnInit(): void {
    this.load();
  }

  ngAfterViewInit(): void {
    // 首帧时容器可能还没尺寸,交给 load 完成后再画
    this.tryRenderChart();
  }

  ngOnDestroy(): void {
    // 必须显式销毁:echarts 实例持有 canvas 与全局事件,不 dispose 会在反复进出页面时泄漏
    this.resizeObserver?.disconnect();
    this.resizeObserver = null;
    this.chart?.dispose();
    this.chart = null;
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    let pending = 2;
    const done = () => {
      if (--pending === 0) {
        this.loading.set(false);
        this.tryRenderChart();
      }
    };

    this.api.get<Dashboard>('/api/analytics/dashboard').subscribe({
      next: (d) => { this.dashboard.set(d); done(); },
      error: (e: Error) => { this.error.set(e.message); done(); }
    });

    // 趋势是可选增强:接口不存在也不该让整页报错
    this.api.get<AbilityTrend>('/api/analytics/ability/trend', { days: 30 })
      .pipe(catchError(() => of(null)))
      .subscribe((t) => { this.trend.set(t); done(); });
  }

  // ------------------------------ 图表 ------------------------------

  private tryRenderChart(): void {
    const host = this.radarHost()?.nativeElement;
    if (!host) return;

    const points = this.radarPoints();
    if (points.length === 0) {
      // 没有数据就不建实例,否则会渲染出一个空骨架让人以为加载失败
      this.chart?.dispose();
      this.chart = null;
      return;
    }

    // echarts 的 init 与 resize 都在 Angular 之外触发重绘,放进 runOutsideAngular 避免
    // 每帧都触发变更检测(图层多的页面会明显卡)。
    this.zone.runOutsideAngular(() => {
      if (!this.chart) {
        this.chart = echarts.init(host, undefined, { renderer: 'canvas' });
      }
      this.chart.setOption(this.buildOption(points), true);

      if (!this.resizeObserver) {
        this.resizeObserver = new ResizeObserver(() => this.chart?.resize());
        this.resizeObserver.observe(host);
      }
      this.chart.resize();
    });
  }

  private buildOption(points: Dashboard['radar']): echarts.EChartsOption {
    const dims = points.map((p) => p.nameZh || p.dimension);

    return {
      tooltip: { trigger: 'item' },
      legend: {
        bottom: 0,
        itemWidth: 10,
        itemHeight: 10,
        textStyle: { fontSize: 12, color: '#5f6368' },
        data: ['当前水平', '历史最佳']
      },
      radar: {
        indicator: dims.map((name) => ({ name, max: 100 })),
        radius: '62%',
        center: ['50%', '46%'],
        splitNumber: 4,
        axisName: { color: '#5f6368', fontSize: 12 },
        splitLine: { lineStyle: { color: 'rgba(0,0,0,0.09)' } },
        splitArea: { areaStyle: { color: ['rgba(63,81,181,0.02)', 'rgba(63,81,181,0.05)'] } },
        axisLine: { lineStyle: { color: 'rgba(0,0,0,0.12)' } }
      },
      series: [
        {
          type: 'radar',
          symbolSize: 5,
          data: [
            {
              value: points.map((p) => p.current),
              name: '当前水平',
              lineStyle: { width: 2, color: '#3f51b5' },
              itemStyle: { color: '#3f51b5' },
              areaStyle: { color: 'rgba(63,81,181,0.22)' }
            },
            {
              value: points.map((p) => p.best),
              name: '历史最佳',
              lineStyle: { width: 1.5, type: 'dashed', color: '#2e7d32' },
              itemStyle: { color: '#2e7d32' },
              areaStyle: { color: 'rgba(46,125,50,0.1)' }
            }
          ]
        }
      ]
    };
  }

  // ------------------------------ 展示辅助 ------------------------------

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

  funnelColor(i: number): string {
    return FUNNEL_COLORS[i % FUNNEL_COLORS.length];
  }
}
