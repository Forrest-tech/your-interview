import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDividerModule } from '@angular/material/divider';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { DimensionIssue, MockQuestion, MockSessionDetail } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

/**
 * 后端 QuestionDto(Assessment 服务)。
 * 与 api.models.ts 里的 MockQuestion 字段名不同(那边对应另一版契约),
 * 这里以后端实际 JSON 为准单独声明,避免"类型看着对、字段全是 undefined"。
 */
interface AssessmentQuestion {
  id: string;
  sequence: number;
  questionText: string;
  type: string;
  difficulty: number;
  state: string;
  expectedPointsJson?: string;
  answerText?: string;
  comment?: string;
  recommendedAnswer?: string;
  betterStructure?: string;
  overall?: number;
  scores?: AssessmentScores;
  issues?: AssessmentIssue[];
  topIssueTitle?: string;
}

/** 六维分数(Assessment 服务用 ScoresDto,比 MockSession 多一个 sentenceIntegrity)。 */
interface AssessmentScores {
  pronunciation: number;
  fluency: number;
  sentenceIntegrity: number;
  structure: number;
  technicalDepth: number;
  relevance: number;
  overall: number;
  weakestDimension: string;
}

/** 后端 DimensionIssueDto:字段是 title/detail,不是 issue。 */
interface AssessmentIssue {
  dimension: string;
  title: string;
  detail?: string;
  evidence?: string;
  suggestion?: string;
  severity: number;
}

/** 会话详情(Assessment 的 SessionDetailDto)。 */
interface AssessmentSessionDetail {
  id: string;
  title: string;
  mode: string;
  topic?: string;
  status: string;
  questionCount?: number;
  startedAt: string;
  completedAt?: string;
  overallSummary?: string;
  priorityAction?: string;
  overallScore?: number;
  averageScores?: AssessmentScores;
  questions: AssessmentQuestion[];
  weaknesses?: { dimension: string; count: number; avgSeverity: number }[];
}

/**
 * AI 模拟答题页 —— 交互核心。
 *
 * 一屏只做一件事:答当前这题。所以布局是「题干 → 输入框 → 提交 → 评分结果」的单列流,
 * 而不是把所有题铺开。理由是练习时注意力就是一次一题,铺开反而让人想跳过。
 *
 * 关于"下一题":后端暂未提供 next-question 端点。
 * 这里实现为 getNextQuestion() —— 先尝试请求该端点,404/409 则退化为
 * 「从已加载的题目列表里找第一道未作答的题」。这样端点上线后无需改前端。
 */
@Component({
  selector: 'app-mock-session',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
    MatChipsModule, MatFormFieldModule, MatInputModule,
    MatSnackBarModule, MatDividerModule
  ],
  templateUrl: './mock-session.component.html',
  styleUrl: './mock-session.component.scss'
})
export class MockSessionComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiClient);
  private readonly route = inject(ActivatedRoute);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly id = signal('');
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly session = signal<AssessmentSessionDetail | null>(null);
  readonly busy = signal<string | null>(null);

  /** 当前正在答的题。答完不自动跳 —— 用户要先看完评分再决定。 */
  readonly current = signal<AssessmentQuestion | null>(null);

  /** 当前题上次提交后的评分结果(来自作答响应或重新加载的会话数据)。 */
  readonly lastResult = signal<AssessmentQuestion | null>(null);

  /** "下一题"失败时的友好提示(端点不存在或已无题可出)。 */
  readonly nextHint = signal<string | null>(null);

  readonly answerText = signal('');

  /** 结束后的总评。 */
  readonly summary = signal<{
    overallScore?: number;
    scoredQuestions: number;
    weakestDimension?: string;
    weaknesses: { dimension: string; count: number; avgSeverity: number }[];
  } | null>(null);

  readonly questions = computed(() => this.session()?.questions ?? []);
  readonly answeredCount = computed(() =>
    this.questions().filter((q) => q.state !== 'Asked').length);
  readonly questionCount = computed(() => this.questions().length);
  readonly progress = computed(() => {
    const total = this.questionCount();
    return total ? Math.round((this.answeredCount() / total) * 100) : 0;
  });
  readonly isCompleted = computed(() => this.session()?.status === 'Completed');

  /** 六维元数据:顺序与仪表盘一致,便于横向比较。 */
  readonly dims = computed(() => {
    const s = this.lastResult()?.scores ?? this.session()?.averageScores;
    if (!s) return [];
    return [
      { key: 'pronunciation', label: '发音', value: s.pronunciation },
      { key: 'fluency', label: '流畅度', value: s.fluency },
      { key: 'sentenceIntegrity', label: '句子完整', value: s.sentenceIntegrity },
      { key: 'structure', label: '结构', value: s.structure },
      { key: 'technicalDepth', label: '技术深度', value: s.technicalDepth },
      { key: 'relevance', label: '相关性', value: s.relevance }
    ];
  });

  readonly issues = computed<AssessmentIssue[]>(() =>
    this.lastResult()?.issues ?? this.session()?.questions
      ?.find((q) => q.id === this.current()?.id)?.issues ?? []);

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.id.set(id);
    if (!id) {
      this.error.set('缺少会话 ID');
      this.loading.set(false);
      return;
    }
    this.load();
  }

  ngOnDestroy(): void {
    // 该页没有轮询,这里留空方法是刻意的占位:
    // 将来若加"AI 评分中"的自动刷新,清理逻辑写在这里。
  }

  // ---------------------------------------------------------------- 加载

  load(showSpinner = true): void {
    if (showSpinner) this.loading.set(true);
    this.error.set(null);

    this.api.get<AssessmentSessionDetail>(`/api/assessment/sessions/${this.id()}`)
      .pipe(catchError((e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
        return of(null);
      }))
      .subscribe((d) => {
        if (!d) return;
        this.session.set(d);
        this.loading.set(false);
        // 优先停在"已答但未评分"或"未答"的第一题,这是用户接着练的位置
        this.syncCurrent(d.questions ?? []);
      });
  }

  /** 决定"当前题"是哪一道:优先未评分,其次未作答,最后停在最后一题。 */
  private syncCurrent(list: AssessmentQuestion[]): void {
    if (list.length === 0) {
      this.current.set(null);
      return;
    }

    const existing = this.current();
    // 用户手动切过题就别抢回来了,只刷新同 id 那题的最新数据
    if (existing) {
      const fresh = list.find((q) => q.id === existing.id);
      if (fresh) {
        this.current.set(fresh);
        if (fresh.state === 'Scored') this.lastResult.set(fresh);
        return;
      }
    }

    const next = list.find((q) => q.state === 'Asked') ?? list[list.length - 1];
    this.current.set(next);
    this.lastResult.set(next.state === 'Scored' ? next : null);
  }

  // ---------------------------------------------------------------- 提交回答

  submitAnswer(): void {
    const q = this.current();
    const text = this.answerText().trim();

    if (!q) return;
    if (!text) {
      this.snack.open('请先写下你的回答', '关闭', { duration: 3000 });
      return;
    }

    this.busy.set('answer');

    // 后端签名是 AnswerBody(AnswerText, AudioPath, DurationSeconds, TranscriptJson)。
    // 端点路径按 questionId 走 —— 这是每个题目的答案,不是会话级答案。
    this.api.post<void>(
      `/api/assessment/sessions/${this.id()}/questions/${q.id}/answer`,
      { answerText: text, audioPath: null, durationSeconds: null, transcriptJson: null })
      .subscribe({
        next: () => {
          this.busy.set(null);
          this.answerText.set('');
          this.snack.open('回答已提交', '关闭', { duration: 2500 });
          // 重新拉会话:评分由后端流水线写回,以服务端为准而不是本地拼
          this.load(false);
        },
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, '关闭', { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 下一题

  /**
   * 下一题。
   *
   * 后端没有 next-question 端点,所以我们先试一次(为了将来端点上线时自动受益),
   * 失败就本地挑下一道未作答的题。这是刻意的优雅降级 ——
   * 让"接口不存在"表现为"功能仍然可用",而不是一个红色报错。
   */
  nextQuestion(): void {
    this.nextHint.set(null);
    this.busy.set('next');

    this.api.post<AssessmentQuestion>(
      `/api/assessment/sessions/${this.id()}/next-question`, {})
      .pipe(catchError(() => {
        // 404/409 或任何错误:回退到本地逻辑,不把错误抛给用户
        const list = this.questions();
        const idx = list.findIndex((q) => q.id === this.current()?.id);
        const upcoming = list.slice(idx + 1).find((q) => q.state === 'Asked')
          ?? list.find((q) => q.state === 'Asked');

        if (upcoming) {
          this.current.set(upcoming);
          this.lastResult.set(upcoming.state === 'Scored' ? upcoming : null);
          this.answerText.set('');
        } else {
          this.nextHint.set('没有更多待答题目了。可以「结束并总结」拿到本轮总评。');
        }
        return of(null);
      }))
      .subscribe((q) => {
        this.busy.set(null);
        if (q) {
          // 端点存在:直接用后端返回的题
          this.current.set(q);
          this.lastResult.set(null);
          this.answerText.set('');
        }
      });
  }

  /** 手动切到某道题(左侧题号列表用)。 */
  selectQuestion(q: AssessmentQuestion): void {
    this.current.set(q);
    this.lastResult.set(q.state === 'Scored' ? q : null);
    this.answerText.set('');
    this.nextHint.set(null);
  }

  // ---------------------------------------------------------------- 结束

  complete(): void {
    if (!confirm('结束本轮练习并生成总评?结束后不能再作答。')) return;

    this.busy.set('complete');
    this.api.post<{
      overallScore?: number;
      scoredQuestions: number;
      weaknesses: { dimension: string; count: number; avgSeverity: number }[];
      weakestDimension?: string;
    }>(`/api/assessment/sessions/${this.id()}/complete`,
      { overallSummary: null, priorityAction: null })
      .subscribe({
        next: (r) => {
          this.busy.set(null);
          this.summary.set(r);
          this.snack.open('本轮已结束,总评已生成', '关闭', { duration: 4000 });
          this.load(false);
        },
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, '关闭', { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 展示辅助

  stateLabel(state: string): string {
    const map: Record<string, string> = {
      Asked: '未作答', Answered: '已作答', Scored: '已评分', Skipped: '已跳过'
    };
    return map[state] ?? state;
  }

  statusLabel(s: string): string {
    const map: Record<string, string> = {
      InProgress: '进行中', Completed: '已完成', Abandoned: '已放弃'
    };
    return map[s] ?? s;
  }

  scoreClass(score: number | undefined): string {
    if (score === undefined || score === null) return 'none';
    if (score >= 75) return 'good';
    if (score >= 55) return 'mid';
    return 'bad';
  }

  severityClass(sev: number): string {
    if (sev >= 4) return 'bad';
    if (sev >= 3) return 'mid';
    return 'low';
  }

  // 解析 expectedPointsJson(期望要点):
  // AI 产出的自由文本,历史上出现过截断的半截 JSON —— 一律 try/catch,
  // 失败当空数组,绝不让一个格式问题炸掉整页。
  parseList(json: string | undefined): string[] {
    if (!json) return [];
    try {
      const parsed: unknown = JSON.parse(json);
      return Array.isArray(parsed)
        ? parsed.map((x) => typeof x === 'string' ? x : JSON.stringify(x))
        : [];
    } catch {
      return [];
    }
  }

  refresh(): void {
    this.load();
  }
}
