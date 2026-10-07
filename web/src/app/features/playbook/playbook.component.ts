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
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { InterviewEntry, InterviewStatus, Paged } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

import { MatTooltipModule } from '@angular/material/tooltip';
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
  totalWeaknesses: number;
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
    MatSnackBarModule, MatTooltipModule
  ],
  templateUrl: './playbook.component.html',
  styleUrl: './playbook.component.scss'
})
export class PlaybookComponent implements OnInit {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);

  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entries = signal<InterviewEntry[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(12);

  /** 旁路数据:公司聚合与统计。失败时留空,页面不报错。 */
  readonly companies = signal<CompanySummary[]>([]);
  readonly stats = signal<InterviewStats | null>(null);

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

  remove(entry: InterviewEntry, ev: Event): void {
    // 卡片本身是链接,删除按钮必须阻止冒泡,否则点删除会先进详情页
    ev.preventDefault();
    ev.stopPropagation();

    const ok = confirm(
      this.t('pb.list.confirmDelete')
        .replace('{company}', entry.companyName)
        .replace('{role}', entry.role));
    if (!ok) return;

    this.api.delete<void>(`/api/interviews/${entry.id}`).subscribe({
      next: () => {
        this.snack.open(this.t('pb.deleted'), this.t('common.close'), { duration: 3000 });
        // 删掉当前页最后一条时页码可能越界,回退一页更自然
        if (this.entries().length === 1 && this.page() > 1) this.page.update((p) => p - 1);
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
}
