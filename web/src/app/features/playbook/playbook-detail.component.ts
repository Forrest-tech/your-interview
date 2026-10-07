import {
  Component, OnDestroy, OnInit, computed, inject, signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatChipsModule } from '@angular/material/chips';
import { MatTabsModule } from '@angular/material/tabs';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatSnackBar, MatSnackBarModule } from '@angular/material/snack-bar';
import { MatDividerModule } from '@angular/material/divider';
import { MatTooltipModule } from '@angular/material/tooltip';
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  AnalysisJob, GuidanceMaterial, GuidanceVersion, InterviewAsset, InterviewDetail, InterviewQuestion,
  InterviewRound, InterviewStatus, InterviewWeakness, QuestionCandidate, SpeechMetrics
} from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

/** 六维显示用的元数据 —— 顺序即评测报告里的推荐阅读顺序。 */
interface DimensionMeta {
  key: string;
  labelKey: string;
  hintKey: string;
  value?: number;
}

/** 手工新增问答的表单。 */
interface QuestionForm {
  questionText: string;
  myAnswerText: string;
  assessment: string;
  category: string;
  difficulty: number;
  gotStuck: boolean;
  stuckReason: string;
  recommendedAnswer: string;
}

/** 手工新增短板的表单。 */
interface WeaknessForm {
  category: string;
  title: string;
  detail: string;
  evidence: string;
  suggestion: string;
  severity: number;
}

/**
 * 机经详情 —— 整个应用价值最集中的一页。
 *
 * 结构说明:
 *   概览(结论)/ 问答(证据)/ 材料(输入)/ 编辑(元数据)四个页签,
 *   刻意按"使用频率"而非"数据表结构"来分:复盘时 90% 时间在"问答"页。
 *
 * 轮询:转写与分析都是异步流水线(状态机 Transcribing → Transcribed → Analyzing → Analyzed),
 * 这里每 15 秒拉一次详情。之所以用 setInterval 而不是长连接:
 *   一个自用工具的实时性要求就到这里,SSE/WebSocket 的复杂度不值得。
 *   ngOnDestroy 必须清掉定时器,否则离开页面后仍在偷偷发请求。
 */
@Component({
  selector: 'app-playbook-detail',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatCardModule, MatIconModule, MatButtonModule, MatProgressBarModule,
    MatChipsModule, MatTabsModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatCheckboxModule, MatSnackBarModule, MatDividerModule, MatTooltipModule
  ],
  templateUrl: './playbook-detail.component.html',
  styleUrl: './playbook-detail.component.scss'
})
export class PlaybookDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);
  tn = (key: string, n: string | number | null | undefined): string => this.i18n.tn(key, n);

  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  readonly auth = inject(AuthService);

  readonly id = signal('');
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly entry = signal<InterviewDetail | null>(null);
  readonly busy = signal<string | null>(null);

  /** 轮询定时器句柄 —— 必须在销毁时清掉。 */
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  /** 需要轮询的中间态:流水线在跑,用户希望看到自己"进度到哪了"。 */
  private static readonly POLLING_STATES: string[] = ['Transcribing', 'Analyzing'];

  readonly statusOptions: { value: InterviewStatus }[] = [
    { value: 'Draft' },
    { value: 'AssetsUploaded' },
    { value: 'Transcribing' },
    { value: 'Transcribed' },
    { value: 'Analyzing' },
    { value: 'Analyzed' },
    { value: 'Failed' }
  ];

  readonly questionCategories = ['Technical', 'SystemDesign', 'Behavioral', 'Coding', 'Culture', 'Other'];
  readonly weaknessCategories = [
    'Pronunciation', 'Fluency', 'Structure', 'TechnicalDepth', 'Relevance',
    'SentenceIntegrity', 'Communication', 'Other'
  ];

  /** 轮次阶段 / 结果选项 —— 与后端 InterviewRoundOutcome / Stage 约定一致。 */
  readonly roundStages = ['Screen', 'Technical', 'SystemDesign', 'Behavioral', 'Final'];
  readonly roundOutcomes = ['Pending', 'Passed', 'Rejected', 'Ghosted', 'Cancelled', 'NoShow'];

  /** 轮次结果 → 本地化标签(找不到键时回退英文枚举)。 */
  roundOutcomeLabelFor(o: string): string {
    const key = 'pb.roundOutcome.' + o;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : o;
  }

  /** 轮次行内编辑草稿:key = round.id,打开编辑时从行数据复制一份。 */
  roundDrafts: Record<string, InterviewRound> = {};
  /** 新增轮次的阶段选择。 */
  newRoundStage = 'Technical';

  // 表单模型(非 signal —— 它们只在提交那一刻被读,不需要触发变更检测)
  questionForm: QuestionForm = this.emptyQuestionForm();
  weaknessForm: WeaknessForm = this.emptyWeaknessForm();

  /**
   * 表单复位工厂。
   * 提交流程要"清空表单",如果直接写对象字面量,新增字段时必然漏改一处;
   * 抽成工厂方法后只有这一个定义点,初始化和复位共用,不会漂移。
   */
  private emptyQuestionForm(): QuestionForm {
    return {
      questionText: '', myAnswerText: '', assessment: '', category: '',
      difficulty: 3, gotStuck: false, stuckReason: '', recommendedAnswer: ''
    };
  }

  private emptyWeaknessForm(): WeaknessForm {
    return {
      category: '', title: '', detail: '', evidence: '', suggestion: '', severity: 3
    };
  }
  transcriptDrafts: Record<string, string> = {};

  /** 编辑表单:点「编辑」页签时用当前数据填充。 */
  editForm = {
    companyName: '', role: '', roundNo: 1, interviewDate: '',
    interviewFormat: '', interviewers: '', location: '', result: '',
    jdText: '', jdSummary: '', companyProfile: '', notes: ''
  };
  editLoaded = false;

  readonly questions = computed(() => this.entry()?.questions ?? []);
  readonly weaknesses = computed(() => this.entry()?.weaknesses ?? []);
  readonly assets = computed(() => this.entry()?.assets ?? []);
  readonly rounds = computed(() => this.entry()?.rounds ?? []);
  readonly audioAssets = computed(() =>
    this.assets().filter((a) => (a.kind ?? '').toLowerCase() === 'audio'));

  // ------------------------------------------------------------ 指导材料(缺口2)
  readonly guidance = signal<GuidanceMaterial | null>(null);
  readonly guidanceVersions = signal<GuidanceVersion[]>([]);
  readonly guidanceLoading = signal(false);

  /** 当前材料的 Markdown → 安全 HTML(极简渲染:标题/加粗/列表/换行,先转义防 XSS)。 */
  readonly guidanceHtml = computed(() => this.renderMarkdown(this.guidance()?.contentMarkdown ?? ''));

  private renderMarkdown(md: string): string {
    const esc = md
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    const lines = esc.split('\n');
    const out: string[] = [];
    let inList = false;
    for (const line of lines) {
      const t = line.trim();
      if (/^#{1,3}\s/.test(t)) {
        if (inList) { out.push('</ul>'); inList = false; }
        const level = t.match(/^#+/)![0].length;
        out.push(`<h${level + 1}>${this.inlineMd(t.replace(/^#+\s*/, ''))}</h${level + 1}>`);
      } else if (/^[-*]\s/.test(t)) {
        if (!inList) { out.push('<ul>'); inList = true; }
        out.push(`<li>${this.inlineMd(t.replace(/^[-*]\s*/, ''))}</li>`);
      } else if (/^\d+[.)]\s/.test(t)) {
        if (inList) { out.push('</ul>'); inList = false; }
        out.push(`<p class="md-num">${this.inlineMd(t)}</p>`);
      } else if (t === '') {
        if (inList) { out.push('</ul>'); inList = false; }
      } else {
        if (inList) { out.push('</ul>'); inList = false; }
        out.push(`<p>${this.inlineMd(t)}</p>`);
      }
    }
    if (inList) out.push('</ul>');
    return out.join('\n');
  }

  private inlineMd(s: string): string {
    return s
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/`([^`]+)`/g, '<code>$1</code>');
  }

  loadGuidance(): void {
    this.guidanceLoading.set(true);
    this.api.get<GuidanceMaterial>(`/api/interviews/${this.id()}/guidance`).subscribe({
      next: (g) => {
        this.guidance.set(g);
        this.guidanceLoading.set(false);
        this.loadGuidanceVersions();
      },
      error: () => {
        // 404 = 还没生成过,不是错误
        this.guidance.set(null);
        this.guidanceLoading.set(false);
        this.loadGuidanceVersions();
      }
    });
  }

  private loadGuidanceVersions(): void {
    this.api.get<GuidanceVersion[]>(`/api/interviews/${this.id()}/guidance/versions`).subscribe({
      next: (v) => this.guidanceVersions.set(v ?? []),
      error: () => this.guidanceVersions.set([])
    });
  }

  generateGuidance(): void {
    if (!confirm(this.t('pb.guidance.confirmGenerate'))) return;
    this.busy.set('guidance');
    this.api.post<GuidanceMaterial>(`/api/interviews/${this.id()}/guidance/generate`, {}).subscribe({
      next: (g) => {
        this.busy.set(null);
        this.guidance.set(g);
        this.loadGuidanceVersions();
        this.snack.open(
          this.t('pb.guidance.generated').replace('{version}', String(g.version)),
          this.t('common.close'), { duration: 3000 });
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 6000 });
      }
    });
  }

  selectGuidanceVersion(v: GuidanceVersion): void {
    this.guidanceLoading.set(true);
    this.api.get<GuidanceMaterial>(
      `/api/interviews/${this.id()}/guidance`, { version: v.version }).subscribe({
      next: (g) => { this.guidance.set(g); this.guidanceLoading.set(false); },
      error: (e: Error) => {
        this.guidanceLoading.set(false);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  readonly selectedGuidanceVersion = computed(() => {
    const g = this.guidance();
    if (!g) return null;
    return this.guidanceVersions().find((v) => v.version === g.version) ?? null;
  });

  /** 填充词明细 → [词, 次数][] ,按次数降序,供模板展示。 */
  fillerEntries(m: SpeechMetrics): [string, number][] {
    const d = m.fillerWordBreakdown ?? {};
    return Object.entries(d).sort((a, b) => b[1] - a[1]);
  }

  /** 结构骨架标记 → [标记, 次数][],按次数降序。 */
  structureMarkerEntries(m: SpeechMetrics): [string, number][] {
    const d = m.structureMarkers ?? {};
    return Object.entries(d).sort((a, b) => b[1] - a[1]);
  }

  selectGuidanceVersionById(id: string): void {
    const v = this.guidanceVersions().find((x) => x.id === id);
    if (v) this.selectGuidanceVersion(v);
  }

  /** 导出 PDF:走浏览器打印(用户选"另存为 PDF")。 */
  exportGuidancePdf(): void {
    window.print();
  }

  /** 导出 Word:拼一个 Word 能打开的 HTML 文件下载。 */
  exportGuidanceWord(): void {
    const g = this.guidance();
    if (!g) return;
    const html = `<!DOCTYPE html><html><head><meta charset="utf-8"></head><body>${this.guidanceHtml()}</body></html>`;
    const blob = new Blob(['\ufeff' + html], { type: 'application/msword' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = this.t('pb.guidance.fileName').replace('{version}', String(g.version));
    a.click();
    URL.revokeObjectURL(a.href);
  }

  /**
   * 分析任务台账(流水线记录)。
   * 与详情分开拉:详情 15s 轮询时任务列表也该跟着刷新(投递中 → 已回写)。
   */
  readonly jobs = signal<AnalysisJob[]>([]);

  /** 台账状态 → 本地化标签(与后端状态机一一对应,直出不下拉;找不到键时回退英文)。 */
  jobStatusLabelFor(s: string): string {
    const key = 'pb.jobStatus.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
  }

  /** 任务行文案:"尝试 1/3"。 */
  attemptsLabel(j: AnalysisJob): string {
    return this.i18n.t('pb.asset.attempts')
      .replace('{a}', String(j.attempts))
      .replace('{m}', String(j.maxAttempts));
  }

  private loadJobs(): void {
    this.api.get<AnalysisJob[]>(`/api/interviews/${this.id()}/jobs`)
      .subscribe({ next: (j) => this.jobs.set(j ?? []), error: () => this.jobs.set([]) });
  }
  readonly isPolling = computed(() =>
    PlaybookDetailComponent.POLLING_STATES.includes(this.entry()?.status ?? ''));

  readonly dims = computed<DimensionMeta[]>(() => {
    const e = this.entry();
    return [
      { key: 'overall', labelKey: 'dim.overall', hintKey: 'pb.dimHint.overall', value: e?.overallScore },
      { key: 'pronunciation', labelKey: 'dim.pronunciation', hintKey: 'pb.dimHint.pronunciation', value: e?.pronunciationScore },
      { key: 'fluency', labelKey: 'dim.fluency', hintKey: 'pb.dimHint.fluency', value: e?.fluencyScore },
      { key: 'structure', labelKey: 'dim.structure', hintKey: 'pb.dimHint.structure', value: e?.structureScore },
      { key: 'technicalDepth', labelKey: 'dim.technicalDepth', hintKey: 'pb.dimHint.technicalDepth', value: e?.technicalDepthScore },
      { key: 'relevance', labelKey: 'dim.relevance', hintKey: 'pb.dimHint.relevance', value: e?.relevanceScore }
    ];
  });

  /** 只展示有分的维度做进度条 —— 零分和"没评"在视觉上必须区分开。 */
  readonly scoredDims = computed(() => this.dims().filter((d) => d.value !== undefined));

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.id.set(id);
    if (!id) {
      this.error.set(this.t('pb.detail.missingId'));
      this.loading.set(false);
      return;
    }
    this.load();
  }

  ngOnDestroy(): void {
    this.stopPolling();
    // 释放试听用的 Object URL —— 泄漏会让已卸载的音频 blob 无法被 GC
    for (const url of this.assetAudioUrls.values()) URL.revokeObjectURL(url);
    this.assetAudioUrls.clear();
  }

  // ---------------------------------------------------------------- 试听

  /**
   * 已上传录音的试听缓存(assetId → objectURL)。
   *
   * ★ 2026-09-24(M1):录音现在真正落盘了,支持回放。
   * 不能把后端端点直接塞给 `<audio src>` —— 浏览器发那个请求不带
   * Authorization 头,而端点带鉴权 → 必然 401(ai-practice 踩过的坑)。
   * 所以走 ApiClient(带 Bearer)拉 blob,再 createObjectURL。
   * 缓存后同一条重复试听不再重复下载。
   */
  readonly assetAudioUrls = new Map<string, string>();
  readonly audioLoadingId = signal<string | null>(null);

  /** 拉取并缓存某条录音的音频流,失败如实提示。 */
  playAsset(a: InterviewAsset): void {
    if (this.assetAudioUrls.has(a.id)) return;   // 已缓存(模板直接渲染 <audio>)

    this.audioLoadingId.set(a.id);
    this.api.getBlob(`/api/interviews/${this.id()}/assets/${a.id}/audio`)
      .subscribe({
        next: (blob) => {
          this.assetAudioUrls.set(a.id, URL.createObjectURL(blob));
          this.audioLoadingId.set(null);
        },
        error: (e: Error) => {
          this.audioLoadingId.set(null);
          this.snack.open(e.message || this.t('pb.asset.audioLoadFail'), this.t('common.close'), { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 加载

  load(showSpinner = true): void {
    if (showSpinner) this.loading.set(true);
    this.error.set(null);
    this.loadJobs();
    this.loadGuidance();

    this.api.get<InterviewDetail>(`/api/interviews/${this.id()}`).subscribe({
      next: (d) => {
        this.entry.set(d);
        this.loading.set(false);
        this.syncPolling(d.status);
        // 编辑表单只在第一次加载时填充 —— 否则用户正在输入时被轮询覆盖
        if (!this.editLoaded) {
          this.fillEditForm(d);
          this.editLoaded = true;
        }
      },
      error: (e: Error) => {
        this.error.set(e.message);
        this.loading.set(false);
        this.stopPolling();
      }
    });
  }

  /** 轮询只在"流水线进行中"才开;状态落定就立刻停,免得空转。 */
  private syncPolling(status: string): void {
    const shouldPoll = PlaybookDetailComponent.POLLING_STATES.includes(status);

    if (shouldPoll && !this.pollTimer) {
      this.pollTimer = setInterval(() => this.load(false), 15000);
    } else if (!shouldPoll && this.pollTimer) {
      this.stopPolling();
    }
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }

  // ---------------------------------------------------------------- 动作

  startTranscription(): void {
    this.run('transcription', 'pb.action.transcriptionStarted',
      this.api.post<void>(`/api/interviews/${this.id()}/transcription/start`));
  }

  triggerAnalysis(): void {
    this.run('analyze', 'pb.action.analysisStarted',
      this.api.post<void>(`/api/interviews/${this.id()}/analyze`));
  }

  /** 三个动作按钮共用一条流程:置忙 → 请求 → 提示 → 重载详情。 */
  private run(tag: string, msgKey: string, obs: ReturnType<ApiClient['post']>): void {
    if (this.busy()) return;
    this.busy.set(tag);

    (obs as ReturnType<ApiClient['post']>).subscribe({
      next: () => {
        this.busy.set(null);
        this.snack.open(this.i18n.t(msgKey), this.t('common.close'), { duration: 4000 });
        // 流水线状态变了,重新拉一次顺便把轮询打开
        setTimeout(() => this.load(false), 1200);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  // ---------------------------------------------------------------- 材料

  /**
   * 上传录音(multipart)。
   *
   * ★ 2026-09-24(M1 修复):后端 /assets 现在按 Content-Type 分流 ——
   * FormData 走真正的文件落盘(原子写 + SHA-256),数据库登记相对路径;
   * 之前这里发 FormData、后端却只收 JSON,类型不匹配,录音从未存上过。
   */
  onFileSelected(ev: Event, assetId?: string): void {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.busy.set('upload');
    this.api.upload<{ id: string }>(
      `/api/interviews/${this.id()}/assets`, file)
      .subscribe({
        next: () => {
          input.value = '';   // 清空,否则同名文件第二次选不会触发 change
          this.busy.set(null);
          this.snack.open(this.t('pb.asset.uploaded'), this.t('common.close'), { duration: 4000 });
          this.load(false);
        },
        error: (e: Error) => {
          input.value = '';
          this.busy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        }
      });
  }

  /** 粘贴转写文本 → 回写该 material 的 transcript。 */
  saveTranscript(asset: InterviewAsset): void {
    const text = (this.transcriptDrafts[asset.id] ?? '').trim();
    if (!text) {
      this.snack.open(this.t('pb.transcript.emptyFirst'), this.t('common.close'), { duration: 3000 });
      return;
    }

    this.busy.set(asset.id);
    // segmentsJson 传合法空数组字符串而不是 null —— 后端把它当"无分段"解析,
    // 传 null 在部分 JSON 解析路径上会退化成空串导致 400。
    this.api.post<void>(
      `/api/interviews/${this.id()}/assets/${asset.id}/transcript`,
      { fullText: text, segmentsJson: '[]' })
      .subscribe({
        next: () => {
          this.busy.set(null);
          this.transcriptDrafts[asset.id] = '';
          this.snack.open(this.t('pb.transcript.saved'), this.t('common.close'), { duration: 3000 });
          this.load(false);
        },
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 问答

  addQuestion(): void {
    if (!this.questionForm.questionText.trim()) {
      this.snack.open(this.t('pb.question.questionRequired'), this.t('common.close'), { duration: 3000 });
      return;
    }

    this.busy.set('question');
    const body = {
      questionText: this.questionForm.questionText.trim(),
      myAnswerText: this.questionForm.myAnswerText || null,
      assessment: this.questionForm.assessment || null,
      category: this.questionForm.category || 'Technical',
      difficulty: this.questionForm.difficulty || 3,
      // 后端当前签名只消费前五个字段,这两个前端保留 —— 等后端补上即可生效,
      // 不会因为多传字段被拒(ASP.NET 默认忽略未知属性)。
      gotStuck: this.questionForm.gotStuck,
      stuckReason: this.questionForm.stuckReason || null,
      recommendedAnswer: this.questionForm.recommendedAnswer || null
    };

    this.api.post<{ id: string }>(`/api/interviews/${this.id()}/questions`, body).subscribe({
      next: () => {
        this.busy.set(null);
        this.questionForm = this.emptyQuestionForm();
        this.snack.open(this.t('pb.question.added'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  removeQuestion(q: InterviewQuestion): void {
    if (!confirm(this.t('pb.question.confirmDelete'))) return;

    this.api.delete<void>(`/api/interviews/${this.id()}/questions/${q.id}`).subscribe({
      next: () => {
        this.snack.open(this.t('pb.deleted'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
    });
  }

  // ------------------------------------------------------------ 候选导入 TechStack(缺口3)

  readonly showCandidates = signal(false);
  readonly candidates = signal<QuestionCandidate[]>([]);
  readonly candidatesLoading = signal(false);

  readonly selectedCandidates = computed(() => this.candidates().filter((c) => c.selected));

  openCandidateImport(): void {
    this.showCandidates.set(true);
    this.candidatesLoading.set(true);
    this.api.get<QuestionCandidate[]>(`/api/interviews/${this.id()}/question-candidates`).subscribe({
      next: (list) => {
        // 默认全选;编辑草稿预填当前值
        for (const c of list) {
          c.selected = true;
          c.editText = c.questionText;
          c.editAnswer = c.myAnswerText ?? c.recommendedAnswer ?? '';
          c.editCategory = c.category ?? 'Technical';
        }
        this.candidates.set(list);
        this.candidatesLoading.set(false);
      },
      error: (e: Error) => {
        this.candidatesLoading.set(false);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  closeCandidateImport(): void {
    this.showCandidates.set(false);
    this.candidates.set([]);
  }

  toggleAllCandidates(select: boolean): void {
    this.candidates.update((list) => list.map((c) => ({ ...c, selected: select })));
  }

  importCandidates(): void {
    const sel = this.selectedCandidates();
    if (sel.length === 0) {
      this.snack.open(this.t('pb.import.selectAtLeastOne'), this.t('common.close'), { duration: 3000 });
      return;
    }
    const e = this.entry();
    this.busy.set('candidates');
    this.api.post<{ created: number; skipped: number; total: number }>(
      '/api/knowledge/import-candidates',
      {
        entryId: this.id(),
        company: e?.companyName ?? null,
        date: e?.interviewDate ?? null,
        roundNo: e?.roundNo ?? null,
        roundStage: null,
        applicationId: null,
        items: sel.map((c) => ({
          title: (c.editText ?? c.questionText).slice(0, 80),
          topic: c.editCategory ?? 'Technical',
          question: c.editText ?? c.questionText,
          difficulty: c.difficulty ?? 3,
          importance: c.gotStuck ? 4 : 3,
          betterAnswer: c.editAnswer || null,
          clientKey: c.questionId
        }))
      }).subscribe({
      next: (r) => {
        this.busy.set(null);
        this.closeCandidateImport();
        this.snack.open(
          this.t('pb.import.done')
            .replace('{created}', String(r.created))
            .replace('{skipped}', String(r.skipped)),
          this.t('common.close'), { duration: 4000 });
      },
      error: (err: Error) => {
        this.busy.set(null);
        this.snack.open(err.message, this.t('common.close'), { duration: 6000 });
      }
    });
  }

  // ---------------------------------------------------------------- 轮次(缺口1)

  addRound(): void {
    this.busy.set('round');
    this.api.post<{ id: string }>(`/api/interviews/${this.id()}/rounds`, {
      stage: this.newRoundStage
    }).subscribe({
      next: () => {
        this.busy.set(null);
        this.snack.open(this.t('pb.round.added'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  /** 打开行内编辑:复制一份草稿,改完点保存才提交。 */
  editRound(r: InterviewRound): void {
    this.roundDrafts[r.id] = { ...r };
  }

  cancelEditRound(r: InterviewRound): void {
    delete this.roundDrafts[r.id];
  }

  isEditingRound(r: InterviewRound): boolean {
    return r.id in this.roundDrafts;
  }

  saveRound(r: InterviewRound): void {
    const d = this.roundDrafts[r.id];
    if (!d) return;
    this.busy.set('round');
    this.api.put<void>(`/api/interviews/${this.id()}/rounds/${r.id}`, {
      stage: d.stage,
      scheduledDate: d.scheduledDate || null,
      interviewers: d.interviewers || null,
      format: d.format || null,
      location: d.location || null,
      outcome: d.outcome,
      notes: d.notes || null,
      feedback: d.feedback || null
    }).subscribe({
      next: () => {
        this.busy.set(null);
        delete this.roundDrafts[r.id];
        this.snack.open(this.t('pb.round.saved'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  removeRound(r: InterviewRound): void {
    if (!confirm(this.t('pb.round.confirmDelete').replace('{n}', String(r.order)))) return;

    this.api.delete<void>(`/api/interviews/${this.id()}/rounds/${r.id}`).subscribe({
      next: () => {
        this.snack.open(this.t('pb.deleted'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
    });
  }

  // ---------------------------------------------------------------- 短板

  addWeakness(): void {
    if (!this.weaknessForm.title.trim()) {
      this.snack.open(this.t('pb.weakness.titleRequired'), this.t('common.close'), { duration: 3000 });
      return;
    }

    this.busy.set('weakness');
    const body = {
      category: this.weaknessForm.category || 'Other',
      title: this.weaknessForm.title.trim(),
      detail: this.weaknessForm.detail || null,
      evidence: this.weaknessForm.evidence || null,
      severity: this.weaknessForm.severity || 3,
      suggestion: this.weaknessForm.suggestion || null
    };

    this.api.post<{ id: string }>(`/api/interviews/${this.id()}/weaknesses`, body).subscribe({
      next: () => {
        this.busy.set(null);
        this.weaknessForm = this.emptyWeaknessForm();
        this.snack.open(this.t('pb.weakness.added'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  removeWeakness(w: InterviewWeakness): void {
    if (!confirm(this.t('pb.weakness.confirmDelete').replace('{title}', w.title))) return;

    this.api.delete<void>(`/api/interviews/${this.id()}/weaknesses/${w.id}`).subscribe({
      next: () => {
        this.snack.open(this.t('pb.deleted'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => this.snack.open(e.message, this.t('common.close'), { duration: 5000 })
    });
  }

  // ---------------------------------------------------------------- 编辑

  private fillEditForm(d: InterviewDetail): void {
    this.editForm = {
      companyName: d.companyName ?? '',
      role: d.role ?? '',
      roundNo: d.roundNo ?? 1,
      interviewDate: d.interviewDate ?? '',
      interviewFormat: d.interviewFormat ?? '',
      interviewers: d.interviewers ?? '',
      location: d.location ?? '',
      result: d.result ?? '',
      jdText: d.jdText ?? '',
      jdSummary: d.jdSummary ?? '',
      companyProfile: d.companyProfile ?? '',
      notes: d.notes ?? ''
    };
  }

  saveEdit(): void {
    if (!this.editForm.companyName.trim() || !this.editForm.role.trim()) {
      this.snack.open(this.t('pb.edit.requiredFields'), this.t('common.close'), { duration: 3000 });
      return;
    }

    this.busy.set('edit');
    this.api.put<void>(`/api/interviews/${this.id()}`, {
      companyName: this.editForm.companyName.trim(),
      role: this.editForm.role.trim(),
      roundNo: this.editForm.roundNo || 1,
      interviewDate: this.editForm.interviewDate || null,
      interviewFormat: this.editForm.interviewFormat || null,
      interviewers: this.editForm.interviewers || null,
      location: this.editForm.location || null,
      result: this.editForm.result || null,
      jdText: this.editForm.jdText || null,
      jdSummary: this.editForm.jdSummary || null,
      companyProfile: this.editForm.companyProfile || null,
      notes: this.editForm.notes || null
    }).subscribe({
      next: () => {
        this.busy.set(null);
        this.snack.open(this.t('pb.edit.saved'), this.t('common.close'), { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, this.t('common.close'), { duration: 5000 });
      }
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  statusLabel(s: string): string {
    const key = 'pb.status.' + s;
    const translated = this.i18n.t(key);
    return translated !== key ? translated : s;
  }

  /** 候选导入副标题:"已选 3 / 10 · 重复条目会自动跳过"。 */
  importSub(): string {
    return this.i18n.t('pb.import.sub')
      .replace('{selected}', String(this.selectedCandidates().length))
      .replace('{total}', String(this.candidates().length));
  }

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

  severityClass(sev: number): string {
    if (sev >= 4) return 'bad';
    if (sev >= 3) return 'mid';
    return 'low';
  }

  humanSize(bytes: number): string {
    if (!bytes) return '—';
    const mb = bytes / 1024 / 1024;
    if (mb >= 1) return `${mb.toFixed(1)} MB`;
    return `${Math.max(1, Math.round(bytes / 1024))} KB`;
  }

  humanDuration(sec: number | undefined): string {
    if (!sec) return '';
    const m = Math.floor(sec / 60);
    const s = Math.round(sec % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  /**
   * 解析后端的 JSON 数组字段(如 missedPointsJson)。
   *
   * 一定要 try/catch:这是 AI 产出的自由文本,历史上出现过被截断的半截 JSON。
   * 解析失败就当成空数组 —— 页面绝不能因为一个字段格式不对整块崩掉。
   */
  parseList(json: string | undefined): string[] {
    if (!json) return [];
    try {
      const parsed: unknown = JSON.parse(json);
      if (Array.isArray(parsed)) {
        return parsed.map((x) => typeof x === 'string' ? x : JSON.stringify(x));
      }
      return [];
    } catch {
      return [];
    }
  }

  /** 详情页也支持刷新按钮,顺便复用同一套错误处理。 */
  refresh(): void {
    this.load();
  }
}
