import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { catchError, forkJoin, of } from 'rxjs';
import { PlaybookLocalService } from '../../core/api/playbook-local.service';
import {
  STAR_STORY_TITLE_MAX, shouldStartStorySave, starStoryBodyError, starStoryTitleError,
  type StarStoryBodyError, type StarStoryTitleError
} from '../../core/api/playbook-local.helpers';
import { I18nService } from '../../core/i18n/i18n.service';
import { InterviewEntry, InterviewStatus, Paged, UpcomingRound } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';
import { InterviewsApiService } from '../../core/api/interviews-api.service';
import { JobsApiService } from '../../core/api/jobs-api.service';
import { entryEndKey, entryResultOutcome } from './redesign/v2.helpers';
import type { CoverageRow } from '../../core/api/jobs-api.service';

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
    MatSnackBarModule, MatTooltipModule, MatCheckboxModule, MatExpansionModule, MatDialogModule
  ],
  templateUrl: './playbook.component.html',
  styleUrl: './playbook.component.scss'
})
export class PlaybookComponent implements OnInit {
  private readonly interviewsApi = inject(InterviewsApiService);
  private readonly jobsApi = inject(JobsApiService);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  /**
   * ★ F6（2026-10-16）：技术覆盖图谱文案 key 前缀 pb.br.；
   * 票43 ARC-13 起已并入全局 I18nService 字典，直委托行为不变。
   */
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);
  fmt = (key: string, params: Record<string, string | number>): string => {
    let s = this.t(key);
    for (const [k, v] of Object.entries(params)) s = s.replace(`{${k}}`, String(v));
    return s;
  };

  private readonly localData = inject(PlaybookLocalService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entries = signal<InterviewEntry[]>([]);

  /**
   * 结果归类(定稿第七节分组口径不变;判读来源票05/ARC-14 起收归后端):
   * 只消费 DTO 的结构化 outcome(后端 DeriveOutcome 派生,与 Tracker 回写
   * 同一事实源),经 v2.helpers 的 entryResultOutcome 映射成展示三态 ——
   * rejected(红) / positive(绿) / closed(中性);Pending/无 outcome 留 In Progress。
   * 不许再判读 e.result 自由文本。
   */
  resultOutcome(e: InterviewEntry): 'rejected' | 'positive' | 'closed' | null {
    return entryResultOutcome(e.outcome);
  }

  /** 进行中:没有落定最终结果的(含轮次全完但还在等回复的) */
  readonly activeEntries = computed(() =>
    this.entries().filter(e => this.resultOutcome(e) === null));

  /**
   * 已结束:最终结果已落定。按结束时间倒序 —— 列表 DTO 没有 UpdatedAt/结果时间,
   * 用 InterviewDate(回落 CreatedAt)作结束时间代理,与后端列表默认排序口径一致。
   */
  readonly completedEntries = computed(() =>
    this.entries()
      .filter(e => this.resultOutcome(e) !== null)
      .sort((a, b) => this.endKey(b).localeCompare(this.endKey(a))));

  private endKey(e: InterviewEntry): string {
    return entryEndKey(e);
  }

  /** Completed 区默认折叠,点标题展开/收起。 */
  readonly completedOpen = signal(false);
  toggleCompleted(): void {
    this.completedOpen.update(v => !v);
  }

  /**
   * 票253:卡片/列表视图切换。默认卡片(既有行为首访不变);偏好存 localStorage,
   * 键 yi-playbook-view(与 Tracker 的 yi-tracker-view 区分,互不串扰)。
   */
  readonly viewMode = signal<'cards' | 'list'>(
    ((): 'cards' | 'list' => {
      try {
        const v = localStorage.getItem('yi-playbook-view');
        return v === 'list' ? 'list' : 'cards';
      } catch { return 'cards'; }
    })()
  );
  setViewMode(m: 'cards' | 'list'): void {
    this.viewMode.set(m);
    try { localStorage.setItem('yi-playbook-view', m); } catch { /* 忽略 */ }
  }

  /**
   * 票253:计数行跳转。Completed 先展开再滚动并把焦点交给折叠标题
   * (已展开时同样滚动+聚焦);In Progress 直接滚动(锚点不存在时静默无动作)。
   */
  goToActive(): void {
    document.getElementById('active-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
  goToCompleted(): void {
    this.completedOpen.set(true);
    setTimeout(() => {
      document.getElementById('completed-section')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
      document.getElementById('completed-toggle-btn')?.focus();
    }, 0);
  }

  /** 票253:列表行点击 —— 编辑模式下勾选,常态下进详情页(与卡片行为一致)。 */
  onRowClick(e: InterviewEntry, ev: Event): void {
    if (this.editing()) {
      ev.preventDefault();
      this.toggleSelectById(e.id);
      return;
    }
    void this.router.navigate(['/playbook', e.id]);
  }

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

  /** 新建对话框状态与表单。 */
  readonly dialogOpen = signal(false);
  readonly saving = signal(false);
  readonly formError = signal<string | null>(null);
  form: NewEntryForm = this.emptyForm();

  ngOnInit(): void {
    // ★ COD-10b/票15:故事库改读后端(首次加载一次性导入本地存量);
    // 历史复盘/粘贴材料孤儿键一并一次性导入后端归档。本地旧键保留不清除。
    this.localData.ensureStarStoriesLoaded();
    this.localData.importLegacyOnce();
    this.load();
    this.loadCompanies();
    this.loadStats();
    this.loadUpcoming();
    // ★ 打通:从 Tracker 带公司名过来 (?company=...),自动过滤到该公司。
    this.route.queryParamMap.subscribe(params => {
      const c = params.get('company');
      if (c) { this.companyFilter = c; this.load(); }
    });
  }

  /** 全局 upcoming 轮次:后端聚合,失败时静默留空(不挡主列表)。 */
  private loadUpcoming(): void {
    this.interviewsApi.getUpcomingRounds()
      .pipe(catchError(() => of([] as UpcomingRound[])))
      .subscribe((list) => this.upcomingRounds.set(list ?? []));
  }

  /** 跳到条目详情的 Rounds tab。 */
  goToRound(r: UpcomingRound): void {
    this.router.navigate(['/playbook', r.entryId], { queryParams: { tab: 'rounds' } });
  }

  // ---------------------------------------------------------------- 数据加载

  /**
   * 全量拉取(定稿第七节):分组要横跨全部条目,服务端分页会把两区切乱,
   * 故按后端上限 200/页循环拉完筛选结果(个人数据量远低于上限的倍数级)。
   */
  load(): void {
    this.loading.set(true);
    this.error.set(null);

    const size = 200;
    const fetchPage = (page: number, acc: InterviewEntry[]): void => {
      this.interviewsApi.getEntries({
        page,
        pageSize: size,
        // 后端只认 companyId(Guid):公司名先经聚合列表解析,解析不到就不带条件
        // (旧代码传 company=<名字> 后端根本不认,筛选一直是空转 —— 本次顺手修掉)
        companyId: this.companyFilter
          ? this.companies().find(c => c.companyName === this.companyFilter)?.companyId
          : undefined,
        status: this.statusFilter || undefined
      }).subscribe({
        next: (p) => {
          const items = [...acc, ...(p.items ?? [])];
          const totalCount = p.total ?? items.length;
          if (items.length < totalCount && (p.items ?? []).length > 0 && page < 10) {
            fetchPage(page + 1, items);
            return;
          }
          this.entries.set(items);
          this.loading.set(false);
        },
        error: (e: Error) => {
          // 主数据失败才是真失败 —— 必须让用户看见并给重试按钮
          this.error.set(e.message);
          this.loading.set(false);
        }
      });
    };
    fetchPage(1, []);
  }

  /** 公司聚合:用于过滤下拉。失败退化为空列表。 */
  private loadCompanies(): void {
    this.interviewsApi.getCompanies()
      .pipe(catchError(() => of([] as CompanySummary[])))
      .subscribe((list) => {
        this.companies.set(list ?? []);
        // 公司名→Id 的解析依赖这份聚合:若筛选已先设好(如从 Tracker 带 ?company= 跳入),
        // 聚合到达后重查一次,筛选才真正生效。
        if (this.companyFilter) this.load();
      });
  }

  /** 统计:失败就当没有,不显示统计条即可。 */
  private loadStats(): void {
    this.interviewsApi.getStats()
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
    this.interviewsApi.getCoverage()
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
    this.load();
  }

  resetFilter(): void {
    this.companyFilter = '';
    this.statusFilter = '';
    this.applyFilter();
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

    this.interviewsApi.createEntry(body).subscribe({
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

  /** 票45 REQ-14:站内确认弹窗(全站 ConfirmDialog,F-02 标准)替代原生 confirm;
   *  true 才执行,取消/Esc/点遮罩一律不执行(与原生 confirm 的 false 同义)。 */
  private confirmDeleteDlg(body: string): Promise<boolean> {
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('common.delete'), body,
        confirmText: this.t('common.delete'), cancelText: this.t('common.cancel'),
        danger: true,
      } as ConfirmDialogData,
    });
    return new Promise((resolve) => ref.afterClosed().subscribe((v) => resolve(v === true)));
  }

  /** 批量删除:逐个调删除接口(后端暂无批量接口),全部完成后退出编辑模式。 */
  deleteSelected(): void {
    const ids = this.selectedIds();
    if (ids.length === 0) return;
    const names = this.entries().filter(e => ids.includes(e.id))
      .map(e => `${e.companyName}·${e.role}`).join('、');
    void this.confirmDeleteDlg(
      this.t('pb.list.confirmDeleteBatch')
        .replace('{n}', String(ids.length))
        .replace('{names}', names)).then((ok) => {
      if (!ok) return;

      forkJoin(ids.map(id => this.interviewsApi.deleteEntry(id,
        this.entries().find((e) => e.id === id)?.version ?? null))).subscribe({
        next: () => {
          this.snack.open(this.t('pb.deleted'), this.t('common.close'), { duration: 3000 });
          this.editing.set(false);
          this.selectedIds.set([]);
          this.load();
          this.loadStats();
        },
        error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
      });
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  statusLabel(s: InterviewStatus | string): string {
    const key = 'pb.status.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
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

  /** ★ 卡片头像:公司名首字母 + 哈希彩色背景。 */
  companyInitials(name?: string): string {
    const n = (name ?? '').trim();
    if (!n) return '?';
    const parts = n.split(/\s+/).filter(Boolean);
    if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
    return parts[0].slice(0, 2).toUpperCase();
  }
  avatarColor(name?: string): string {
    const n = (name ?? '?').trim() || '?';
    let h = 0;
    for (let i = 0; i < n.length; i++) h = (h * 31 + n.charCodeAt(i)) % 360;
    return `hsl(${h}, 55%, 55%)`;
  }

  /** ★ 分数条宽度(0-100 钳制),无分时 0。 */
  barWidth(v?: number): number {
    if (v === undefined || v === null) return 0;
    return Math.max(0, Math.min(100, v));
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

  // ★ #13:STAR 故事库 —— COD-10b/票15 起真源在 Interviews 后端
  // (star_stories 表),读写收敛到 PlaybookLocalService 单一路径;组件只消费
  // 同一份信号。localStorage 旧键(pb.starStories)仅作首次导入来源,保留不清除。
  readonly starStories = this.localData.starStories;
  readonly showStarBank = signal(false);
  readonly editingStory = signal<StarStory | null>(null);
  /**
   * 票79:STAR 标题字段级错误(口径同票12 Knowledge 弹窗字段级呈现)——
   * 点保存时由纯函数 starStoryTitleError 判定,错误文案挂在标题框下方;
   * 用户补填标题(ngModelChange)即清除;开新表单/编辑/取消时一并清空。
   */
  readonly storyTitleError = signal<StarStoryTitleError>(null);
  readonly storyTitleErrorText = computed(() => {
    const e = this.storyTitleError();
    if (e === 'required') return this.t('pb.star.titleRequired');
    if (e === 'tooLong') return this.fmt('pb.star.titleTooLong', { max: STAR_STORY_TITLE_MAX });
    return null;
  });
  /**
   * ML-FIX-PREP(R-27):STAR 正文级错误(口径同标题字段级呈现)——
   * 点保存时由纯函数 starStoryBodyError 判定,错误文案挂在表单下方;
   * 用户补填正文(ngModelChange)即清除;开新表单/编辑/取消时一并清空。
   */
  readonly storyBodyError = signal<StarStoryBodyError>(null);
  readonly storyBodyErrorText = computed(() => {
    const e = this.storyBodyError();
    if (!e) return null;
    if (e.kind === 'required') return this.fmt('pb.star.bodyRequired', { field: e.field });
    if (e.kind === 'resultNeedsNumber') return this.t('pb.star.resultNeedsNumber');
    return this.t('pb.star.actionTooVague');
  });
  newStory(): void {
    this.storyTitleError.set(null);
    this.storyBodyError.set(null);
    this.editingStory.set({
      id: 's' + Date.now(), title: '', situation: '', task: '', action: '', result: '', tags: ''
    });
  }
  editStory(s: StarStory): void {
    this.storyTitleError.set(null);
    this.storyBodyError.set(null);
    this.editingStory.set({ ...s });
  }
  cancelStoryEdit(): void {
    this.storyTitleError.set(null);
    this.storyBodyError.set(null);
    this.editingStory.set(null);
  }
  /** 票79:用户开始补填标题时先撤掉标题的旧错误(票12 clearFieldError 同口径)。 */
  clearStoryTitleError(): void {
    if (this.storyTitleError()) this.storyTitleError.set(null);
  }
  /** ML-FIX-PREP(R-27):用户开始补填正文时先撤掉正文的旧错误。 */
  clearStoryBodyError(): void {
    if (this.storyBodyError()) this.storyBodyError.set(null);
  }
  /** 票78:STAR 保存在飞守卫(口径同本组件 save() 的 saving 与票92)。 */
  readonly storySaving = signal(false);
  saveStory(s: StarStory): void {
    if (!shouldStartStorySave(this.storySaving())) return;
    // 票79:标题必填/超长校验落到界面 —— 此前此处静默 return,用户只看到「点了没反应」
    const titleError = starStoryTitleError(s.title);
    if (titleError) {
      this.storyTitleError.set(titleError);
      return;
    }
    this.storyTitleError.set(null);
    // ML-FIX-PREP(R-27):正文收紧 —— S/T/A/R 四项均必填、Result 须带数字、
    // Action 空泛拦截;取代此前“至少填一项”的 snack 口径(空泛叙述不再放行)。
    const bodyError = starStoryBodyError(s);
    if (bodyError) {
      this.storyBodyError.set(bodyError);
      return;
    }
    this.storyBodyError.set(null);
    this.storySaving.set(true);
    const settle = () => {
      this.storySaving.set(false);
      this.editingStory.set(null);
    };
    this.localData.saveStory(s).subscribe({ next: settle, error: settle, complete: settle });
  }
  deleteStory(id: string): void {
    // 票45 REQ-14:站内确认弹窗替代原生 confirm;原文案为硬编码中文,逐字作 body 保留
    void this.confirmDeleteDlg('删除这个故事?').then((ok) => {
      if (!ok) return;
      this.localData.deleteStory(id);
    });
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
