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
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import { MockSession, Paged } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

import { MatTooltipModule } from '@angular/material/tooltip';
/** 新建模拟表单。字段与后端 CreateSessionBody 对齐。 */
interface NewSessionForm {
  title: string;
  mode: string;
  topic: string;
  difficulty: number;
}

/**
 * AI 模拟列表。
 *
 * 与机经列表的差异:模拟是「练」,机经是「复盘」。
 * 所以这里优先级最高的是「继续未完成的练习」,而不是浏览历史。
 */
@Component({
  selector: 'app-mock',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
    MatChipsModule, MatFormFieldModule, MatInputModule, MatSelectModule,
    MatSnackBarModule, MatTooltipModule
  ],
  templateUrl: './mock.component.html',
  styleUrl: './mock.component.scss'
})
export class MockComponent implements OnInit {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);

  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly sessions = signal<MockSession[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(12);

  readonly dialogOpen = signal(false);
  readonly saving = signal(false);
  readonly formError = signal<string | null>(null);
  form: NewSessionForm = this.emptyForm();

  /**
   * 后端 SessionMode 枚举值:TopicDrill / CompanyStyle / WeaknessFocus / FullMock。
   * 任务里写的 WeaknessDrill 在后端不存在,以实际枚举为准 —— 传错值后端会静默
   * 回退到 TopicDrill,导致"我明明选了薄弱点强化却出了通用题"这种难查的 bug。
   */
  readonly modeOptions = computed(() => [
    { value: 'TopicDrill', label: this.t('mock.modeTopicDrill'), hint: this.t('mock.modeTopicDrillHint') },
    { value: 'FullMock', label: this.t('mock.modeFullMock'), hint: this.t('mock.modeFullMockHint') },
    { value: 'WeaknessFocus', label: this.t('mock.modeWeakness'), hint: this.t('mock.modeWeaknessHint') },
    { value: 'CompanyStyle', label: this.t('mock.modeCompanyStyle'), hint: this.t('mock.modeCompanyStyleHint') }
  ]);

  readonly totalPages = computed(() =>
    Math.max(1, Math.ceil(this.total() / this.pageSize())));
  readonly canPrev = computed(() => this.page() > 1);
  readonly canNext = computed(() => this.page() < this.totalPages());

  /** 未完成的会话单独拎出来 —— 它们才是用户此刻该点的。 */
  readonly inProgress = computed(() =>
    this.sessions().filter((s) => s.status === 'InProgress'));
  readonly finished = computed(() =>
    this.sessions().filter((s) => s.status !== 'InProgress'));

  ngOnInit(): void {
    this.load();
    // 从 Playbook 带上下文跳过来(?company=&role=):预填标题并自动打开新建对话框。
    const company = this.route.snapshot.queryParamMap.get('company')?.trim();
    const role = this.route.snapshot.queryParamMap.get('role')?.trim();
    if (company || role) {
      this.form = this.emptyForm();
      this.form.title = [company, role].filter(Boolean).join(' - ');
      this.form.mode = 'CompanyStyle';
      this.form.topic = role ?? '';
      this.formError.set(null);
      this.dialogOpen.set(true);
    }
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.get<Paged<MockSession>>('/api/assessment/sessions', {
      page: this.page(),
      pageSize: this.pageSize()
    }).subscribe({
      next: (p) => {
        this.sessions.set(p.items ?? []);
        this.total.set(p.total ?? 0);
        this.loading.set(false);
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
      }
    });
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

  /** 选中「公司风格」时必须给主题 —— 提前提示,别等后端 400。 */
  get needsTopic(): boolean {
    return this.form.mode !== 'CompanyStyle';
  }

  save(): void {
    if (this.saving()) return;

    if (!this.form.title.trim()) {
      this.formError.set(this.t('mock.errTitleRequired'));
      return;
    }
    if (this.form.mode === 'TopicDrill' && !this.form.topic.trim()) {
      this.formError.set(this.t('mock.errTopicRequiredDrill'));
      return;
    }
    if (this.form.mode === 'CompanyStyle' && !this.form.topic.trim()) {
      this.formError.set(this.t('mock.errTopicRequiredCompany'));
      return;
    }

    this.saving.set(true);
    this.formError.set(null);

    this.api.post<{ id: string }>('/api/assessment/sessions', {
      title: this.form.title.trim(),
      mode: this.form.mode,
      topic: this.form.topic.trim() || null,
      difficulty: this.form.difficulty || 3
    }).subscribe({
      next: (r) => {
        this.saving.set(false);
        this.dialogOpen.set(false);
        this.snack.open(this.t('mock.createdToast'), this.t('common.close'), { duration: 3000 });
        this.router.navigate(['/mock', r.id]);
      },
      error: (e: Error) => {
        this.saving.set(false);
        this.formError.set(e.message);
      }
    });
  }

  // ---------------------------------------------------------------- 删除

  remove(s: MockSession, ev: Event): void {
    ev.preventDefault();
    ev.stopPropagation();

    if (!confirm(this.tn('mock.confirmDelete', s.title))) return;

    this.api.delete<void>(`/api/assessment/sessions/${s.id}`).subscribe({
      next: () => {
        this.snack.open(this.t('mock.deletedToast'), this.t('common.close'), { duration: 3000 });
        if (this.sessions().length === 1 && this.page() > 1) this.page.update((p) => p - 1);
        this.load();
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  modeLabel(mode: string): string {
    const keyMap: Record<string, string> = {
      TopicDrill: 'mock.modeTopicDrill',
      FullMock: 'mock.modeFullMock',
      WeaknessFocus: 'mock.modeWeakness',
      CompanyStyle: 'mock.modeCompanyStyle'
    };
    const key = keyMap[mode];
    return key ? this.t(key) : mode;
  }

  statusLabel(s: string): string {
    const map: Record<string, string> = {
      InProgress: this.t('mock.statusInProgress'),
      Completed: this.t('mock.statusCompleted'),
      Abandoned: this.t('mock.statusAbandoned')
    };
    return map[s] ?? s;
  }

  /** 第 X / Y 页 · 共 Z 场 —— 多占位符,模板里不方便直接拼。 */
  readonly pagerInfo = computed(() =>
    this.t('mock.pagerInfo')
      .replace('{p}', String(this.page()))
      .replace('{tp}', String(this.totalPages()))
      .replace('{total}', String(this.total())));

  statusClass(s: string): string {
    if (s === 'Completed') return 'ok';
    if (s === 'Abandoned') return 'idle';
    return 'busy';
  }

  scoreClass(score: number | undefined): string {
    if (score === undefined || score === null) return 'none';
    if (score >= 75) return 'good';
    if (score >= 55) return 'mid';
    return 'bad';
  }

  /** 答题进度百分比:后端已给两个计数,前端算比再发一个请求划算。 */
  progress(s: MockSession): number {
    if (!s.questionCount) return 0;
    return Math.round((s.answeredCount / s.questionCount) * 100);
  }

  private emptyForm(): NewSessionForm {
    return { title: '', mode: 'TopicDrill', topic: '', difficulty: 3 };
  }
}
