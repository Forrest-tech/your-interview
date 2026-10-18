import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { catchError, forkJoin, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { InterviewEntry, InterviewStatus, Paged, UpcomingRound } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';
import { BRIEF_I18N } from './components/brief/brief.i18n';
import type { CoverageRow } from './components/brief/brief.models';

import { MatTooltipModule } from '@angular/material/tooltip';
import { MatExpansionModule } from '@angular/material/expansion';

/** ★ #13:STAR 故事 */
export interface StarStory {
  id: string;
  title: string;
  situation: string;
  task: string;
  action: string;
  result: string;
  tags: string;
}
/**
 * 「公司聚合」—— 对应后端 CompanySummaryDto。
 * 只声明用到的字段:TS 是结构化类型,后端多给字段不影响编译。
 */
interface CompanySummary {
  companyId: string;
  companyName: string;
  entryCount: number;
  analyzedCount: number;
  averageScore?: number;
  latestInterviewDate?: string;
  latestResult?: string;
}

/** 后端 InterviewStatsDto 的子集 —— 顶部只放最该看的几个数。 */
interface InterviewStats {
  totalEntries: number;
  analyzed: number;
  pendingAnalysis: number;
  gotStuckQuestions: number;
  averageOverallScore?: number;
}

/** 新建条目表单模型。字段名与后端 CreateEntryCommand 严格对齐。 */
interface NewEntryForm {
  companyName: string;
  role: string;
  roundNo: number;
  interviewDate: string;
  interviewFormat: string;
  interviewers: string;
  location: string;
  jdText: string;
  jdSummary: string;
  companyProfile: string;
  notes: string;
}

/**
 * 实战机经列表 —— 一场面试一条记录的入口。
 *
 * 设计取舍:
 *   1. 用卡片网格而非表格。每条记录都带六维分数和短板数,表格单元格塞不下,
 *      还会把"分数"压成一行数字看不清。
 *   2. 公司聚合与统计是"锦上添花"的旁路数据,一律 catchError 兜底 ——
 *      后端某个环境没挂上聚合查询时,列表本身必须照常可用。
 */
@Component({
  selector: 'app-playbook',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
    MatChipsModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatSnackBarModule, MatTooltipModule, MatCheckboxModule, MatExpansionModule
  ],
  templateUrl: './playbook.component.html',
  styleUrl: './playbook.component.scss'
})
export class PlaybookComponent implements OnInit {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  /**
   * ★ F6（2026-10-16）：技术覆盖图谱文案走 brief/ 本地 BRIEF_I18N（key 前缀 pb.br.），
   * 先查本地表再回退全局 service —— coordinator 并入全局字典后行为不变。
   */
  t = (key: string): string => {
    const e = BRIEF_I18N[key];
    if (e) {
      const l = this.i18n.lang();
      return e[l] ?? e.en ?? e.zh ?? key;
    }
    return this.i18n.t(key);
  };
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  fmt = (key: string, params: Record<string, string | number>): string => {
    let s = this.t(key);
    for (const [k, v] of Object.entries(params)) s = s.replace(`{${k}}`, String(v));
    return s;
  };

  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entries = signal<InterviewEntry[]>([]);

  /** 进行中:没有填最终结果的 */
  readonly activeEntries = computed(() =>
    this.entries().filter(e => !e.result || e.result.trim().length === 0));

  /** 已结束:填了最终结果的(Offer/Rejected/Withdrawn 等) */
  readonly completedEntries = computed(() =>
    this.entries().filter(e => e.result && e.result.trim().length > 0));
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(12);

  /** 旁路数据:公司聚合与统计。失败时留空,页面不报错。 */
  readonly companies = signal<CompanySummary[]>([]);
  readonly stats = signal<InterviewStats | null>(null);

  /** 全局 upcoming:未来 30 天所有条目的轮次(日历视图)。旁路,失败留空。 */
  readonly upcomingRounds = signal<UpcomingRound[]>([]);

  /** F6 技术覆盖图谱:默认收起,展开时懒加载;失败静默留空。 */
  readonly coverageOpen = signal(false);
  readonly coverageLoading = signal(false);
  readonly coverage = signal<CoverageRow[]>([]);

  /** 批量管理模式:右上角"编辑"进入,卡片变多选,统一删除。 */
  readonly editing = signal(false);
  readonly selectedIds = signal<string[]>([]);

  /** 服务端过滤条件(改动后重查,不做本地过滤 —— 数据量会增长)。 */
  companyFilter = '';
  statusFilter = '';

  readonly statusOptions: { value: InterviewStatus }[] = [
    { value: 'Draft' },
    { value: 'AssetsUploaded' },
    { value: 'Transcribing' },
    { value: 'Transcribed' },
    { value: 'Analyzing' },
    { value: 'Analyzed' },
    { value: 'Failed' }
  ];

  readonly formatOptions: { value: string; labelKey: string }[] = [
    { value: '电话初筛', labelKey: 'pb.format.phoneScreen' },
    { value: '技术一面', labelKey: 'pb.format.techRound1' },
    { value: '技术二面', labelKey: 'pb.format.techRound2' },
    { value: '系统设计', labelKey: 'pb.format.systemDesign' },
    { value: '行为面', labelKey: 'pb.format.behavioral' },
    { value: 'Hiring Manager', labelKey: 'pb.format.hiringManager' },
    { value: '终面', labelKey: 'pb.format.final' },
    { value: '其他', labelKey: 'pb.format.other' }
  ];

  readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.pageSize())));
  readonly canPrev = computed(() => this.page() > 1);
  readonly canNext = computed(() => this.page() < this.totalPages());

  /** 新建对话框状态与表单。 */
  readonly dialogOpen = signal(false);
  readonly saving = signal(false);
  readonly formError = signal<string | null>(null);
  form: NewEntryForm = this.emptyForm();

  ngOnInit(): void {
    this.load();
    this.loadCompanies();
    this.loadStats();
    this.loadUpcoming();
  }

  /** 全局 upcoming 轮次:后端聚合,失败时静默留空(不挡主列表)。 */
  private loadUpcoming(): void {
    this.api.get<UpcomingRound[]>('/api/interviews/rounds/upcoming', { days: 30 })
      .pipe(catchError(() => of([] as UpcomingRound[])))
      .subscribe((list) => this.upcomingRounds.set(list ?? []));
  }

  /** 跳到条目详情的 Rounds tab。 */
  goToRound(r: UpcomingRound): void {
    this.router.navigate(['/playbook', r.entryId], { queryParams: { tab: 'rounds' } });
  }

  // ---------------------------------------------------------------- 数据加载

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get<Paged<InterviewEntry>>('/api/interviews', {
      page: this.page(),
      pageSize: this.pageSize(),
      company: this.companyFilter || undefined,
      status: this.statusFilter || undefined
    }).subscribe({
      next: (p) => {
        this.entries.set(p.items ?? []);
        this.total.set(p.total ?? 0);
        this.loading.set(false);
      },
      error: (e: Error) => {
        // 主数据失败才是真失败 —— 必须让用户看见并给重试按钮
        this.error.set(e.message);
        this.loading.set(false);
      }
    });
  }

  /** 公司聚合:用于过滤下拉。失败退化为空列表。 */
  private loadCompanies(): void {
    this.api.get<CompanySummary[]>('/api/interviews/companies')
      .pipe(catchError(() => of([] as CompanySummary[])))
      .subscribe((list) => this.companies.set(list ?? []));
  }

  /** 统计:失败就当没有,不显示统计条即可。 */
  private loadStats(): void {
    this.api.get<InterviewStats>('/api/interviews/stats')
      .pipe(catchError(() => of(null)))
      .subscribe((s) => this.stats.set(s));
  }

  // ---------------------------------------------------------------- F6 技术覆盖图谱

  /** 折叠切换:首次展开时加载数据。 */
  toggleCoverage(): void {
    const open = !this.coverageOpen();
    this.coverageOpen.set(open);
    if (open && this.coverage().length === 0 && !this.coverageLoading()) {
      this.loadCoverage();
    }
  }

  private loadCoverage(): void {
    this.coverageLoading.set(true);
    this.api.get<CoverageRow[]>('/api/interviews/coverage')
      .pipe(catchError(() => of([] as CoverageRow[])))
      .subscribe((list) => {
        this.coverage.set(list ?? []);
        this.coverageLoading.set(false);
      });
  }

  /** 掌握度小条:绿 mastered / 红 stuck，按 askedCount 占比。 */
  covBar(c: CoverageRow): { masteredPct: number; stuckPct: number } {
    const total = c.askedCount || 1;
    return {
      masteredPct: Math.round((c.masteredCount / total) * 100),
      stuckPct: Math.round((c.stuckCount / total) * 100),
    };
  }

  // ---------------------------------------------------------------- 筛选与翻页

  applyFilter(): void {
    this.page.set(1);
    this.load();
  }

  resetFilter(): void {
    this.companyFilter = '';
    this.statusFilter = '';
    this.applyFilter();
  }

  prevPage(): void {
    if (!this.canPrev()) return;
    this.page.update((p) => p - 1);
    this.load();
  }

  nextPage(): void {
    if (!this.canNext()) return;
    this.page.update((p) => p + 1);
    this.load();
  }

  // ---------------------------------------------------------------- 新建

  openDialog(): void {
    this.form = this.emptyForm();
    this.formError.set(null);
    this.dialogOpen.set(true);
  }

  closeDialog(): void {
    this.dialogOpen.set(false);
  }

  save(): void {
    if (this.saving()) return;

    // 前端先做最小校验:公司名/岗位是后端硬性必填,提前拦下来省一次往返
    if (!this.form.companyName.trim() || !this.form.role.trim()) {
      this.formError.set(this.t('pb.edit.requiredFields'));
      return;
    }

    this.saving.set(true);
    this.formError.set(null);

    // 后端签名是 CreateEntryCommand —— 显式列出字段,不用展开运算符一把梭,
    // 避免把表单内部字段(如空字符串)误当业务字段发给后端。
    const body = {
      companyName: this.form.companyName.trim(),
      role: this.form.role.trim(),
      roundNo: this.form.roundNo || 1,
      interviewDate: this.form.interviewDate || null,
      interviewFormat: this.form.interviewFormat || null,
      interviewers: this.form.interviewers || null,
      location: this.form.location || null,
      jdText: this.form.jdText || null,
      jdSummary: this.form.jdSummary || null,
      companyProfile: this.form.companyProfile || null,
      notes: this.form.notes || null
    };

    this.api.post<{ id: string }>('/api/interviews', body).subscribe({
      next: (r) => {
        this.saving.set(false);
        this.dialogOpen.set(false);
        this.snack.open(this.t('pb.created'), this.t('common.close'), { duration: 3000 });
        // 直接进详情页 —— 建完就要传材料,少一次"自己找到那条再点进去"
        this.router.navigate(['/playbook', r.id]);
      },
      error: (e: Error) => {
        this.saving.set(false);
        this.formError.set(e.message);
      }
    });
  }

  // ---------------------------------------------------------------- 删除

  /** 右上角"编辑":进入/退出批量管理模式。退出时清空已选项。 */
  toggleEdit(): void {
    const next = !this.editing();
    this.editing.set(next);
    if (!next) this.selectedIds.set([]);
  }

  isSelected(id: string): boolean {
    return this.selectedIds().includes(id);
  }

  /** 编辑模式下点卡片 = 勾选/取消勾选,不跳转详情页。 */
  toggleSelect(id: string, ev: Event): void {
    if (!this.editing()) return;
    ev.preventDefault();
    ev.stopPropagation();
    this.toggleSelectById(id);
  }

  toggleSelectById(id: string): void {
    this.selectedIds.update(list =>
      list.includes(id) ? list.filter(x => x !== id) : [...list, id]);
  }

  /** 批量删除:逐个调删除接口(后端暂无批量接口),全部完成后退出编辑模式。 */
  deleteSelected(): void {
    const ids = this.selectedIds();
    if (ids.length === 0) return;
    const names = this.entries().filter(e => ids.includes(e.id))
      .map(e => `${e.companyName}·${e.role}`).join('、');
    const ok = confirm(
      this.t('pb.list.confirmDeleteBatch')
        .replace('{n}', String(ids.length))
        .replace('{names}', names));
    if (!ok) return;

    forkJoin(ids.map(id => this.api.delete<void>(`/api/interviews/${id}`))).subscribe({
      next: () => {
        this.snack.open(this.t('pb.deleted'), this.t('common.close'), { duration: 3000 });
        this.editing.set(false);
        this.selectedIds.set([]);
        // 删掉当前页最后一条时页码可能越界,回退一页更自然
        if (this.entries().length <= ids.length && this.page() > 1) this.page.update((p) => p - 1);
        this.load();
        this.loadStats();
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  statusLabel(s: InterviewStatus | string): string {
    const key = 'pb.status.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
  }

  /** 翻页信息:"第 1 / 3 页 · 共 25 条"。 */
  pagerInfo(): string {
    return this.i18n.t('pb.pager.info')
      .replace('{page}', String(this.page()))
      .replace('{totalPages}', String(this.totalPages()))
      .replace('{total}', String(this.total()));
  }

  /** 状态色档 —— 让"卡住不动"的条目一眼能挑出来。 */
  statusClass(s: string): string {
    if (s === 'Analyzed') return 'ok';
    if (s === 'Failed') return 'bad';
    if (s === 'Transcribing' || s === 'Analyzing') return 'busy';
    if (s === 'Transcribed' || s === 'AssetsUploaded') return 'ready';
    return 'idle';
  }

  scoreClass(score: number | undefined): string {
    if (score === undefined || score === null) return 'none';
    if (score >= 75) return 'good';
    if (score >= 55) return 'mid';
    return 'bad';
  }

  /** 六维打平成数组,模板里 @for 更省事,避免手写五段重复结构。 */
  dims(e: InterviewEntry): { key: string; labelKey: string; value?: number }[] {
    return [
      { key: 'pronunciation', labelKey: 'dim.pronunciation', value: e.pronunciationScore },
      { key: 'fluency', labelKey: 'dim.fluency', value: e.fluencyScore },
      { key: 'structure', labelKey: 'dim.structure', value: e.structureScore },
      { key: 'depth', labelKey: 'dim.technicalDepth', value: e.technicalDepthScore },
      { key: 'relevance', labelKey: 'dim.relevance', value: e.relevanceScore }
    ];
  }

  private emptyForm(): NewEntryForm {
    return {
      companyName: '', role: '', roundNo: 1,
      interviewDate: new Date().toISOString().slice(0, 10),
      interviewFormat: '', interviewers: '', location: '',
      jdText: '', jdSummary: '', companyProfile: '', notes: ''
    };
  }

  // ★ #13:STAR 故事库 (localStorage)
  readonly starStories = signal<StarStory[]>(this.loadStarStories());
  readonly showStarBank = signal(false);
  readonly editingStory = signal<StarStory | null>(null);

  private loadStarStories(): StarStory[] {
    try { return JSON.parse(localStorage.getItem('pb.starStories') || '[]'); }
    catch { return []; }
  }
  private saveStarStories(list: StarStory[]): void {
    localStorage.setItem('pb.starStories', JSON.stringify(list));
    this.starStories.set(list);
  }
  newStory(): void {
    this.editingStory.set({
      id: 's' + Date.now(), title: '', situation: '', task: '', action: '', result: '', tags: ''
    });
  }
  editStory(s: StarStory): void {
    this.editingStory.set({ ...s });
  }
  saveStory(s: StarStory): void {
    if (!s.title.trim()) return;
    const list = this.loadStarStories();
    const idx = list.findIndex(x => x.id === s.id);
    if (idx >= 0) list[idx] = s; else list.push(s);
    this.saveStarStories(list);
    this.editingStory.set(null);
  }
  deleteStory(id: string): void {
    if (!confirm('删除这个故事?')) return;
    this.saveStarStories(this.loadStarStories().filter(x => x.id !== id));
  }
  /** ★ #13:按 JD 关键词推荐故事 */
  recommendStories(jdText: string): StarStory[] {
    if (!jdText) return this.starStories();
    const jd = jdText.toLowerCase();
    return [...this.starStories()].sort((a, b) => {
      const score = (s: StarStory) => s.tags.toLowerCase().split(/[,，\s]+/)
        .filter(t => t && jd.includes(t)).length;
      return score(b) - score(a);
    });
  }
}
