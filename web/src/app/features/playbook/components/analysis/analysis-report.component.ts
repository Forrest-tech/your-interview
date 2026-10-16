import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatExpansionModule } from '@angular/material/expansion';
import { ApiClient } from '../../../../core/api/api-client';
import { I18nService } from '../../../../core/i18n/i18n.service';
import { analysisT } from './analysis.i18n';
import { NextActionsComponent } from './next-actions.component';
import {
  AnalysisEntryView, AnalysisSnapshotDto, AnalysisTrendPointDto, AnalysisQuestionView,
  DIMENSION_KEYS, DimScore, parseBullets, parseDimensions, scoreBand, splitSentences
} from './analysis.models';

interface SparkPoint { x: number; y: number; date: string; score: number; }

/**
 * F2 复盘报告:结论区(总分环+bullets) / 六维条形+雷达 / 逐题手风琴+证据 / 底部 Next actions。
 *
 * 数据全部自己拉:analysis/latest(快照)、analysis/trend(分差+迷你趋势)、
 * 条目详情(questions/candidateTranscript/analysisSummary)。
 * 无快照时显示"首次分析",结论兜底从 analysisSummary 按句切分。
 */
@Component({
  selector: 'app-analysis-report',
  standalone: true,
  imports: [
    CommonModule, MatCardModule, MatIconModule, MatButtonModule,
    MatProgressBarModule, MatExpansionModule, NextActionsComponent
  ],
  templateUrl: './analysis-report.component.html',
  styleUrl: './analysis-report.component.scss'
})
export class AnalysisReportComponent implements OnInit {
  private readonly api = inject(ApiClient);
  private readonly i18n = inject(I18nService);

  readonly t = analysisT(this.i18n);

  /** 面试条目 ID —— 组件自己调 API,不依赖父组件数据。 */
  readonly entryId = input.required<string>();

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly latest = signal<AnalysisSnapshotDto | null>(null);
  readonly trend = signal<AnalysisTrendPointDto[]>([]);
  readonly entry = signal<AnalysisEntryView | null>(null);

  /** 结论 bullets:快照 3~5 条;无快照时从 analysisSummary 按句切取前 5 句。 */
  readonly bullets = computed(() => {
    const fromJson = parseBullets(this.latest()?.summaryBulletsJson);
    if (fromJson.length > 0) return fromJson.slice(0, 5);
    return splitSentences(this.entry()?.analysisSummary, 5);
  });

  /** 六维(顺序固定)。 */
  readonly dims = computed<DimScore[]>(() => {
    const m = parseDimensions(this.latest()?.dimensionsJson);
    return DIMENSION_KEYS.map((d) => ({ key: d.key, labelKey: d.labelKey, value: m[d.key] ?? null }));
  });

  /** 只展示有分的维度 —— 零分和"没评"在视觉上必须区分开。 */
  readonly scoredDims = computed(() => this.dims().filter((d) => d.value !== null));

  readonly overallScore = computed(() => this.latest()?.overallScore ?? null);

  /** 较上次快照的分差 —— trend 至少 2 个有效点才算。 */
  readonly scoreDelta = computed(() => {
    const pts = this.trend().filter((p) => p.overallScore !== null && p.overallScore !== undefined);
    if (pts.length < 2) return null;
    return (pts[pts.length - 1].overallScore ?? 0) - (pts[pts.length - 2].overallScore ?? 0);
  });

  readonly questions = computed<AnalysisQuestionView[]>(() => this.entry()?.questions ?? []);
  readonly transcript = computed(() => this.entry()?.candidateTranscript ?? '');

  scoreBand = scoreBand;
  /** 模板里用 Math.abs —— 模板表达式访问不到全局 Math,显式暴露。 */
  readonly Math = Math;

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    const id = this.entryId();
    this.loading.set(true);
    this.error.set(null);

    this.api.get<AnalysisSnapshotDto | null>(`/api/interviews/${id}/analysis/latest`).subscribe({
      next: (s) => this.latest.set(s),
      error: () => this.latest.set(null)   // 无快照不是致命错误,走"首次分析"兜底
    });

    this.api.get<AnalysisTrendPointDto[]>(`/api/interviews/${id}/analysis/trend`).subscribe({
      next: (list) => this.trend.set((list ?? []).filter((p) => p.overallScore !== null && p.overallScore !== undefined)),
      error: () => this.trend.set([])
    });

    this.api.get<AnalysisEntryView>(`/api/interviews/${id}`).subscribe({
      next: (e) => {
        this.entry.set(e ?? null);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message || this.t('pb.an.loadFailed'));
        this.loading.set(false);
      }
    });
  }

  // ------------------------------------------------------------ 总分环

  private static readonly RING_R = 58;
  private static readonly RING_C = 2 * Math.PI * AnalysisReportComponent.RING_R;

  ringDash(score: number | null): string {
    const c = AnalysisReportComponent.RING_C;
    if (score === null || score === undefined) return `0 ${c}`;
    return `${((score / 100) * c).toFixed(1)} ${c.toFixed(1)}`;
  }

  // ------------------------------------------------------------ 雷达图(六维,0-100)

  private static readonly RADAR_CX = 130;
  private static readonly RADAR_CY = 130;
  private static readonly RADAR_R = 105;

  private radarVertex(i: number, frac: number): string {
    const a = -Math.PI / 2 + (i * Math.PI * 2) / 6;
    const r = AnalysisReportComponent.RADAR_R * frac;
    const x = AnalysisReportComponent.RADAR_CX + r * Math.cos(a);
    const y = AnalysisReportComponent.RADAR_CY + r * Math.sin(a);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }

  /** 网格六边形:100%/75%/50%/25% 四圈。 */
  readonly radarGrids = computed(() =>
    [1, 0.75, 0.5, 0.25].map((f) =>
      [0, 1, 2, 3, 4, 5].map((i) => this.radarVertex(i, f)).join(' '))
  );

  /** 轴线(中心 → 顶点)。 */
  readonly radarAxes = computed(() =>
    [0, 1, 2, 3, 4, 5].map((i) => {
      const [x, y] = this.radarVertex(i, 1).split(',').map(Number);
      return { x1: AnalysisReportComponent.RADAR_CX, y1: AnalysisReportComponent.RADAR_CY, x2: x, y2: y };
    })
  );

  /** 顶点标签位置。 */
  readonly radarLabels = computed(() =>
    this.dims().map((d, i) => {
      const a = -Math.PI / 2 + (i * Math.PI * 2) / 6;
      const r = AnalysisReportComponent.RADAR_R + 24;
      return {
        label: this.t(d.labelKey),
        x: AnalysisReportComponent.RADAR_CX + r * Math.cos(a),
        y: AnalysisReportComponent.RADAR_CY + r * Math.sin(a)
      };
    })
  );

  /** 数据多边形 —— 缺分维度按 0 画,至少 3 个有效维度才显示。 */
  readonly radarPolygon = computed(() => {
    if (this.scoredDims().length < 3) return '';
    return this.dims()
      .map((d, i) => this.radarVertex(i, (d.value ?? 0) / 100))
      .join(' ');
  });

  // ------------------------------------------------------------ 迷你趋势

  /** 趋势折线点 —— ≥2 个有效点才画。 */
  readonly sparkPoints = computed<SparkPoint[]>(() => {
    const pts = this.trend().filter((p) => p.overallScore !== null && p.overallScore !== undefined);
    if (pts.length < 2) return [];
    const W = 240, H = 72, padX = 12, padTop = 10, padBottom = 16;
    const scores = pts.map((p) => p.overallScore as number);
    const min = Math.min(...scores), max = Math.max(...scores);
    const span = max - min || 1;
    return pts.map((p, i) => ({
      x: padX + (i * (W - padX * 2)) / (pts.length - 1),
      y: padTop + (1 - ((p.overallScore as number) - min) / span) * (H - padTop - padBottom),
      date: (p.createdAt ?? '').slice(0, 10),
      score: p.overallScore as number
    }));
  });

  readonly sparkLine = computed(() =>
    this.sparkPoints().map((p) => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')
  );

  // ------------------------------------------------------------ 展示辅助

  /** 题型 → 本地化标签(无对应键时回退原枚举值)。 */
  categoryLabel(cat: string | null | undefined): string {
    if (!cat) return this.t('pb.an.cat.Other');
    const key = 'pb.an.cat.' + cat;
    const s = this.t(key);
    return s === key ? cat : s;
  }

  /** 难度星级:1~5 颗 ★。 */
  difficultyStars(n: number | null | undefined): string {
    const v = Math.max(0, Math.min(5, Math.round(n ?? 0)));
    return '★'.repeat(v) + '☆'.repeat(5 - v);
  }

  shortDate(iso: string | null | undefined): string {
    if (!iso) return '';
    return iso.slice(0, 16).replace('T', ' ');
  }
}
