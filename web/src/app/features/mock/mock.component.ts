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
import { ApiClient } from '../../core/api/api-client';
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
  private readonly router = inject(Router);
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
  readonly modeOptions = [
    { value: 'TopicDrill', label: '主题专项', hint: '就某个技术主题连问' },
    { value: 'FullMock', label: '完整模拟', hint: '技术 + 系统设计 + 行为混合' },
    { value: 'WeaknessFocus', label: '薄弱点强化', hint: '从历史短板里挑题' },
    { value: 'CompanyStyle', label: '公司风格', hint: '模仿目标公司的面试风格' }
  ];

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
      this.formError.set('请填写练习标题');
      return;
    }
    if (this.form.mode === 'TopicDrill' && !this.form.topic.trim()) {
      this.formError.set('主题专项必须填写主题(后端会校验)');
      return;
    }
    if (this.form.mode === 'CompanyStyle' && !this.form.topic.trim()) {
      this.formError.set('公司风格练习必须填写目标公司');
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
        this.snack.open('模拟已创建,开始答题吧', '关闭', { duration: 3000 });
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

    if (!confirm(`删除模拟「${s.title}」?答题记录会一并丢失。`)) return;

    this.api.delete<void>(`/api/assessment/sessions/${s.id}`).subscribe({
      next: () => {
        this.snack.open('已删除', '关闭', { duration: 3000 });
        if (this.sessions().length === 1 && this.page() > 1) this.page.update((p) => p - 1);
        this.load();
      },
      error: (e: Error) => this.snack.open(e.message, '关闭', { duration: 5000 })
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  modeLabel(mode: string): string {
    return this.modeOptions.find((m) => m.value === mode)?.label ?? mode;
  }

  statusLabel(s: string): string {
    const map: Record<string, string> = {
      InProgress: '进行中', Completed: '已完成', Abandoned: '已放弃'
    };
    return map[s] ?? s;
  }

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
