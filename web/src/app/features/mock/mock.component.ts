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
import { AssessmentApiService } from '../../core/api/assessment-api.service';
import { MockApiService } from '../../core/api/mock-api.service';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { ConfirmDialogComponent, ConfirmDialogData } from '../../shared/confirm-dialog/confirm-dialog.component';
import { I18nService } from '../../core/i18n/i18n.service';
import { MockSession, Paged } from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';
import { fieldErrorFromServer, textLengthError } from '../../core/api/validation-errors.helpers';

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
    MatSnackBarModule, MatTooltipModule, MatDialogModule
  ],
  templateUrl: './mock.component.html',
  styleUrl: './mock.component.scss'
})
export class MockComponent implements OnInit {
  private readonly mockApi = inject(MockApiService);
  private readonly assessmentApi = inject(AssessmentApiService);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);

  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly sessions = signal<MockSession[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pageSize = signal(12);

  /** 票95:Suggest 推荐(最弱维度)—— 列表页推荐卡数据源;失败静默不挡列表。 */
  readonly suggestion = signal<{
    title: string; mode: string; topic?: string;
    targetQuestionCount: number; rationale: string;
  } | null>(null);

  readonly dialogOpen = signal(false);
  readonly saving = signal(false);
  readonly formError = signal<string | null>(null);
  /** 票106(C3):建会话字段级错误(后端 CreateSession 校验器 Title≤200 / Topic≤200 / CompanyStyle≤200)。 */
  readonly TITLE_MAX = 200;
  readonly TOPIC_MAX = 200;
  readonly fieldErrors = signal<Partial<Record<'title' | 'mode' | 'topic', string>>>({});
  form: NewSessionForm = this.emptyForm();

  private tooLongText(max: number): string {
    return this.i18n.tf('validation.tooLong', { max });
  }

  /** 服务端字段错误 → 本地化文案(票75 同型);无该字段错误返回 null,调用方回落原 formError 路径。 */
  private mapFieldError(e: unknown, field: string, sentLength: number, max: number): string | null {
    const c = fieldErrorFromServer(e, field, { sentLength, max });
    if (!c) return null;
    if (c.kind === 'tooLong') return this.tooLongText(max);
    if (c.kind === 'required') return this.t('validation.required');
    return c.text;
  }

  clearFieldError(field: 'title' | 'mode' | 'topic'): void {
    if (this.fieldErrors()[field]) {
      const next = { ...this.fieldErrors() };
      delete next[field];
      this.fieldErrors.set(next);
    }
  }

  /**
   * 后端 SessionMode 枚举值:TopicDrill / CompanyStyle / WeaknessFocus / FullLoop。
   * 任务里写的 WeaknessDrill 在后端不存在,以实际枚举为准 —— 提交值必须与枚举
   * 逐字一致:后端对非法 mode 直接回 400(票 63 起,不再静默回退 TopicDrill)。
   * (历史教训:这里曾误写 FullMock —— 后端无此值,当时又静默回退,导致用户选的
   * 「完整模拟」实际建成了主题专项会话。)
   */
  readonly modeOptions = computed(() => [
    { value: 'TopicDrill', label: this.t('mock.modeTopicDrill'), hint: this.t('mock.modeTopicDrillHint') },
    { value: 'FullLoop', label: this.t('mock.modeFullMock'), hint: this.t('mock.modeFullMockHint') },
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
    this.loadSuggestion();
    // 从 Playbook 带上下文跳过来(?company=&role=):预填标题并自动打开新建对话框。
    const company = this.route.snapshot.queryParamMap.get('company')?.trim();
    const role = this.route.snapshot.queryParamMap.get('role')?.trim();
    if (company || role) {
      this.form = this.emptyForm();
      this.form.title = [company, role].filter(Boolean).join(' - ');
      this.form.mode = 'CompanyStyle';
      this.form.topic = role ?? '';
      this.formError.set(null);
      this.fieldErrors.set({});
      this.dialogOpen.set(true);
    }
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.assessmentApi.getSessions({
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

  // ---------------------------------------------------------------- 推荐(票95)

  /** 拉 Suggest 推荐;失败静默(推荐是增益,不是主流程)。
      ★ ML-FIX-UI(W-UI-9):传当前界面语言,英文 UI 下推荐卡不再出中文文案。 */
  loadSuggestion(): void {
    this.mockApi.suggest<{
      title: string; mode: string; topic?: string;
      targetQuestionCount: number; rationale: string;
    }>(5, this.i18n.lang()).subscribe({
      next: (sug) => this.suggestion.set(sug),
      error: () => this.suggestion.set(null)
    });
  }

  /** 点「按推荐新建」:预填建会话表单并打开对话框(mode 为后端枚举的 camelCase 串,转 PascalCase 与表单提交值同口径)。 */
  applySuggestion(): void {
    const sug = this.suggestion();
    if (!sug) return;
    this.form = this.emptyForm();
    this.form.title = sug.title;
    this.form.mode = sug.mode.charAt(0).toUpperCase() + sug.mode.slice(1);
    this.form.topic = sug.topic ?? '';
    this.formError.set(null);
    this.fieldErrors.set({});
    this.dialogOpen.set(true);
  }

  // ---------------------------------------------------------------- 新建

  openDialog(): void {
    this.form = this.emptyForm();
    this.formError.set(null);
    this.fieldErrors.set({});
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

    // 票106(C3):长度拦截(必填检查之后、原顺序不变)——超限字段下亮含上限提示,不走表单级 formError
    const sentTitle = this.form.title.trim();
    const sentTopic = this.form.topic.trim();
    const preErrors: Partial<Record<'title' | 'mode' | 'topic', string>> = {};
    if (textLengthError(sentTitle, this.TITLE_MAX)) preErrors.title = this.tooLongText(this.TITLE_MAX);
    if (textLengthError(sentTopic, this.TOPIC_MAX)) preErrors.topic = this.tooLongText(this.TOPIC_MAX);
    if (Object.keys(preErrors).length > 0) { this.fieldErrors.set(preErrors); return; }
    this.fieldErrors.set({});

    this.saving.set(true);
    this.formError.set(null);

    this.assessmentApi.createSession({
      title: sentTitle,
      mode: this.form.mode,
      topic: sentTopic || null,
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
        // 票106(C3):errors{Title/Topic/CompanyStyle/Mode} 落字段下方(CompanyStyle 与 Topic
        // 共用 topic 输入——前端提交结构里只有 topic 一个文本字段);无字段级错误回落原 formError
        const mapped: Partial<Record<'title' | 'mode' | 'topic', string>> = {};
        const titleText = this.mapFieldError(e, 'Title', sentTitle.length, this.TITLE_MAX);
        if (titleText) mapped.title = titleText;
        const topicText = this.mapFieldError(e, 'Topic', sentTopic.length, this.TOPIC_MAX)
          ?? this.mapFieldError(e, 'CompanyStyle', sentTopic.length, this.TOPIC_MAX);
        if (topicText) mapped.topic = topicText;
        // Mode 无长度上限:只取后端原文/必填分类,不传长度判据(防 classify 按长度误判 tooLong)
        const modeClassified = fieldErrorFromServer(e, 'Mode');
        const modeText = modeClassified
          ? (modeClassified.kind === 'required' ? this.t('validation.required') : modeClassified.text)
          : null;
        if (modeText) mapped.mode = modeText;
        if (Object.keys(mapped).length > 0) { this.fieldErrors.set(mapped); return; }
        this.formError.set(e.message);
      }
    });
  }

  // ---------------------------------------------------------------- 删除

  remove(s: MockSession, ev: Event): void {
    ev.preventDefault();
    ev.stopPropagation();

    // 票45 REQ-14:站内确认弹窗(全站 ConfirmDialog,F-02 标准)替代原生 confirm
    const ref = this.dialog.open(ConfirmDialogComponent, {
      width: '360px', panelClass: 'app-confirm', autoFocus: false,
      data: {
        title: this.t('common.delete'), body: this.tn('mock.confirmDelete', s.title),
        confirmText: this.t('common.delete'), cancelText: this.t('common.cancel'),
        danger: true,
      } as ConfirmDialogData,
    });
    ref.afterClosed().subscribe((v) => {
      if (v !== true) return;
      // 票113:带列表项读时版本(If-Match 钉版);409 = 期间已被改动,提示走
      // ApiClient 全站 concurrency_conflict 统一文案,并刷新列表对齐最新版本
      // (先例 practice-material-tree 票111段2 的 409 刷新)。
      this.mockApi.deleteSession(s.id, s.version).subscribe({
        next: () => {
          this.snack.open(this.t('mock.deletedToast'), this.t('common.close'), { duration: 3000 });
          if (this.sessions().length === 1 && this.page() > 1) this.page.update((p) => p - 1);
          this.load();
        },
        error: (e: Error) => {
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
          if ((e as { status?: number })?.status === 409) this.load();
        }
      });
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  modeLabel(mode: string): string {
    const keyMap: Record<string, string> = {
      TopicDrill: 'mock.modeTopicDrill',
      // 显示别名:FullMock 是票 63 前表单的旧提交值(后端从无此枚举,当时被静默
      // 回退成 TopicDrill 落库,故实际不会有 FullMock 的已存会话)—— 保留同键映射
      // 只作防御,保证任何来源的该字符串都不直出枚举原文。
      FullMock: 'mock.modeFullMock',
      // 后端 SessionMode 的完整模拟值是 FullLoop(见 AssessmentAggregates),
      // 也是表单现在的提交值,与上面的显示别名共用同一本地化名。
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
