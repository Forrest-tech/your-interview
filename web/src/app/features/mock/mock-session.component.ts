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
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { MatDividerModule } from '@angular/material/divider';
import { catchError, of } from 'rxjs';
import { MockApiService } from '../../core/api/mock-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
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
  /** ★ 票108 A09:题目行读时版本 —— 作答回传钉版,过期得 409。 */
  version?: number | null;
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
 * 关于"下一题"(票95 裁定,含票48):不实现 next-question 端点 —— 出题是
 * 按 TargetQuestionCount 一次性批量生成(后端 POST /generate 编排),导航纯本地:
 * 从已加载题目里挑下一道未作答的题。旧实现每次点下一题都试调必-404 的
 * /next-question,已删除(会话页网络面不得再有必 404 请求)。
 *
 * 出题/评分(票95):进入 0 题会话自动调一次 /generate;答题成功后自动调
 * /questions/{qid}/score-ai。失败口径:生成失败→空态卡三态(生成中/失败可重试/
 * AI 未配置 503);评分失败→题目停 Answered + 评分失败提示与重试按钮。
 */
@Component({
  selector: 'app-mock-session',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
    MatChipsModule, MatFormFieldModule, MatInputModule,
    MatSnackBarModule, MatDividerModule, MatDialogModule
  ],
  templateUrl: './mock-session.component.html',
  styleUrl: './mock-session.component.scss'
})
export class MockSessionComponent implements OnInit, OnDestroy {
  private readonly mockApi = inject(MockApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);

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

  /** 票95:AI 出题状态机 —— 空态卡据此呈现真实状态,不再是无出路文案。 */
  readonly genState = signal<'idle' | 'generating' | 'failed' | 'notConfigured'>('idle');

  /** 票95:AI 评分失败态(题目停 Answered 时展示,带重试入口)。 */
  readonly scoreError = signal<'failed' | 'notConfigured' | null>(null);

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
      { key: 'pronunciation', label: this.t('dim.pronunciation'), value: s.pronunciation },
      { key: 'fluency', label: this.t('dim.fluency'), value: s.fluency },
      { key: 'sentenceIntegrity', label: this.t('dim.sentenceIntegrity'), value: s.sentenceIntegrity },
      { key: 'structure', label: this.t('dim.structure'), value: s.structure },
      { key: 'technicalDepth', label: this.t('dim.technicalDepth'), value: s.technicalDepth },
      { key: 'relevance', label: this.t('dim.relevance'), value: s.relevance }
    ];
  });

  readonly issues = computed<AssessmentIssue[]>(() =>
    this.lastResult()?.issues ?? this.session()?.questions
      ?.find((q) => q.id === this.current()?.id)?.issues ?? []);

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.id.set(id);
    if (!id) {
      this.error.set(this.t('mock.errMissingId'));
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

    this.mockApi.getSession<AssessmentSessionDetail>(this.id())
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
        // 票95:0 题且会话未终结 → 自动编排出题一次(失败后由空态卡手动重试,
        // 不在每次 load 里无限重试)。
        if ((d.questions?.length ?? 0) === 0
          && d.status !== 'Completed' && d.status !== 'Abandoned'
          && this.genState() === 'idle') {
          this.generateQuestions();
        }
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

  // ---------------------------------------------------------------- AI 编排(票95)

  /** 调后端编排生成题目(幂等补差额)。成功后重拉会话;失败置 genState 供空态卡重试。 */
  generateQuestions(): void {
    if (this.genState() === 'generating') return;
    this.genState.set('generating');
    this.mockApi.generateQuestions(this.id())
      .subscribe({
        next: () => {
          this.genState.set('idle');
          this.load(false);
        },
        error: (e: Error) => {
          const code = (e as { code?: string }).code;
          this.genState.set(code === 'Mock.AiNotConfigured' ? 'notConfigured' : 'failed');
        }
      });
  }

  /** 调后端编排为某题评分。答题后自动调用;失败置 scoreError,题目停 Answered 可重试。 */
  scoreWithAi(questionId: string): void {
    this.busy.set('score');
    this.scoreError.set(null);
    this.mockApi.scoreWithAi(this.id(), questionId)
      .subscribe({
        next: () => {
          this.busy.set(null);
          this.load(false);
        },
        error: (e: Error) => {
          this.busy.set(null);
          const code = (e as { code?: string }).code;
          this.scoreError.set(code === 'Mock.AiNotConfigured' ? 'notConfigured' : 'failed');
          this.load(false);
        }
      });
  }

  /** 评分失败后的重试入口(当前题仍 Answered 时)。 */
  retryScoring(): void {
    const q = this.current();
    if (q && q.state === 'Answered') this.scoreWithAi(q.id);
  }

  // ---------------------------------------------------------------- 提交回答

  submitAnswer(): void {
    const q = this.current();
    const text = this.answerText().trim();

    if (!q) return;
    if (!text) {
      this.snack.open(this.t('mock.errAnswerRequired'), this.t('common.close'), { duration: 3000 });
      return;
    }

    this.busy.set('answer');

    // 后端签名是 AnswerBody(AnswerText, AudioPath, DurationSeconds, TranscriptJson)。
    // 端点路径按 questionId 走 —— 这是每个题目的答案,不是会话级答案。
    this.mockApi.answerQuestion<void>(
      this.id(), q.id,
      { answerText: text, audioPath: null, durationSeconds: null, transcriptJson: null, version: q.version ?? null })
      .subscribe({
        next: () => {
          this.busy.set(null);
          this.answerText.set('');
          this.snack.open(this.t('mock.answerSubmitted'), this.t('common.close'), { duration: 2500 });
          // 票95:答完立即触发后端 AI 评分编排;评分结果以重拉的服务端数据为准。
          this.scoreWithAi(q.id);
        },
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 下一题

  /**
   * 下一题 —— 纯本地导航(票95/48):题目已在建会话后批量生成完毕,
   * 这里只在已加载列表里找下一道未作答的题,不发任何请求
   * (旧实现试调不存在的 /next-question,每次点击必 404,已删除)。
   */
  nextQuestion(): void {
    this.nextHint.set(null);
    const list = this.questions();
    const idx = list.findIndex((q) => q.id === this.current()?.id);
    const upcoming = list.slice(idx + 1).find((q) => q.state === 'Asked')
      ?? list.find((q) => q.state === 'Asked');

    if (upcoming) {
      this.current.set(upcoming);
      this.lastResult.set(upcoming.state === 'Scored' ? upcoming : null);
      this.answerText.set('');
      this.scoreError.set(null);
    } else {
      this.nextHint.set(this.t('mock.noMoreQuestions'));
    }
  }

  /** 手动切到某道题(左侧题号列表用)。 */
  selectQuestion(q: AssessmentQuestion): void {
    this.current.set(q);
    this.lastResult.set(q.state === 'Scored' ? q : null);
    this.answerText.set('');
    this.nextHint.set(null);
    this.scoreError.set(null);
  }

  // ---------------------------------------------------------------- 结束

  complete(): void {
    // 票45 REQ-14:站内确认弹窗(全站 ConfirmDialog,F-02 标准)替代原生 confirm
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('common.confirmTitle'), body: this.t('mock.confirmComplete'),
        confirmText: this.t('common.confirm'), cancelText: this.t('common.cancel'),
      } as ConfirmDialogData,
    });
    ref.afterClosed().subscribe((v) => {
      if (v !== true) return;
      this.doComplete();
    });
  }

  private doComplete(): void {
    this.busy.set('complete');
    this.mockApi.completeSession<{
      overallScore?: number;
      scoredQuestions: number;
      weaknesses: { dimension: string; count: number; avgSeverity: number }[];
      weakestDimension?: string;
    }>(this.id(), { overallSummary: null, priorityAction: null })
      .subscribe({
        next: (r) => {
          this.busy.set(null);
          this.summary.set(r);
          this.snack.open(this.t('mock.completedToast'), this.t('common.close'), { duration: 4000 });
          this.load(false);
        },
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 展示辅助

  stateLabel(state: string): string {
    const map: Record<string, string> = {
      Asked: this.t('mock.stateAsked'),
      Answered: this.t('mock.stateAnswered'),
      Scored: this.t('mock.stateScored'),
      Skipped: this.t('mock.stateSkipped')
    };
    return map[state] ?? state;
  }

  /** 模式标签走 i18n 映射(含后端枚举值 FullLoop),不直出枚举原文。 */
  modeLabel(mode: string): string {
    const keyMap: Record<string, string> = {
      TopicDrill: 'mock.modeTopicDrill',
      FullMock: 'mock.modeFullMock',
      FullLoop: 'mock.modeFullMock',
      WeaknessFocus: 'mock.modeWeakness',
      CompanyStyle: 'mock.modeCompanyStyle'
    };
    const key = keyMap[mode];
    return key ? this.t(key) : mode;
  }

  statusLabel(s: string): string {
    const map: Record<string, string> = {
      Draft: this.t('mock.statusDraft'),
      InProgress: this.t('mock.statusInProgress'),
      Completed: this.t('mock.statusCompleted'),
      Abandoned: this.t('mock.statusAbandoned')
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
