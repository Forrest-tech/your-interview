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
import { catchError, of } from 'rxjs';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  InterviewAsset, InterviewDetail, InterviewQuestion, InterviewStatus, InterviewWeakness
} from '../../core/models/api.models';
import { AuthService } from '../../core/auth/auth.service';

/** 六维显示用的元数据 —— 顺序即评测报告里的推荐阅读顺序。 */
interface DimensionMeta {
  key: string;
  label: string;
  hint: string;
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
    MatSelectModule, MatCheckboxModule, MatSnackBarModule, MatDividerModule
  ],
  templateUrl: './playbook-detail.component.html',
  styleUrl: './playbook-detail.component.scss'
})
export class PlaybookDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(ApiClient);
  /** ★ 2026-09-23:页面 tooltip 接入全站语言设置。 */
  private readonly i18n = inject(I18nService);
  t = (key: string): string => this.i18n.t(key);

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

  readonly statusOptions: { value: InterviewStatus; label: string }[] = [
    { value: 'Draft', label: '草稿' },
    { value: 'AssetsUploaded', label: '材料已上传' },
    { value: 'Transcribing', label: '转写中' },
    { value: 'Transcribed', label: '已转写' },
    { value: 'Analyzing', label: '分析中' },
    { value: 'Analyzed', label: '已分析' },
    { value: 'Failed', label: '失败' }
  ];

  readonly questionCategories = ['Technical', 'SystemDesign', 'Behavioral', 'Coding', 'Culture', 'Other'];
  readonly weaknessCategories = [
    'Pronunciation', 'Fluency', 'Structure', 'TechnicalDepth', 'Relevance',
    'SentenceIntegrity', 'Communication', 'Other'
  ];

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
  readonly audioAssets = computed(() =>
    this.assets().filter((a) => (a.kind ?? '').toLowerCase() === 'audio'));
  readonly isPolling = computed(() =>
    PlaybookDetailComponent.POLLING_STATES.includes(this.entry()?.status ?? ''));

  readonly dims = computed<DimensionMeta[]>(() => {
    const e = this.entry();
    return [
      { key: 'overall', label: '总分', hint: '六维加权结果', value: e?.overallScore },
      { key: 'pronunciation', label: '发音', hint: '音准与重音', value: e?.pronunciationScore },
      { key: 'fluency', label: '流畅度', hint: '停顿与语速', value: e?.fluencyScore },
      { key: 'structure', label: '结构', hint: '是否有清晰框架', value: e?.structureScore },
      { key: 'technicalDepth', label: '技术深度', hint: '是否讲到原理与权衡', value: e?.technicalDepthScore },
      { key: 'relevance', label: '相关性', hint: '是否答到点上', value: e?.relevanceScore }
    ];
  });

  /** 只展示有分的维度做进度条 —— 零分和"没评"在视觉上必须区分开。 */
  readonly scoredDims = computed(() => this.dims().filter((d) => d.value !== undefined));

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.id.set(id);
    if (!id) {
      this.error.set('缺少条目 ID');
      this.loading.set(false);
      return;
    }
    this.load();
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  // ---------------------------------------------------------------- 加载

  load(showSpinner = true): void {
    if (showSpinner) this.loading.set(true);
    this.error.set(null);

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
    this.run('transcription', '开始转写',
      this.api.post<void>(`/api/interviews/${this.id()}/transcription/start`));
  }

  triggerAnalysis(): void {
    this.run('analyze', '触发分析',
      this.api.post<void>(`/api/interviews/${this.id()}/analyze`));
  }

  /** 三个动作按钮共用一条流程:置忙 → 请求 → 提示 → 重载详情。 */
  private run(tag: string, label: string, obs: ReturnType<ApiClient['post']>): void {
    if (this.busy()) return;
    this.busy.set(tag);

    (obs as ReturnType<ApiClient['post']>).subscribe({
      next: () => {
        this.busy.set(null);
        this.snack.open(`${label}已提交,稍后自动刷新`, '关闭', { duration: 4000 });
        // 流水线状态变了,重新拉一次顺便把轮询打开
        setTimeout(() => this.load(false), 1200);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, '关闭', { duration: 5000 });
      }
    });
  }

  // ---------------------------------------------------------------- 材料

  /**
   * 上传录音。
   *
   * 后端 /assets 接受 JSON,但文件本体走 IFormFile 之外的存储路径,
   * 所以这里用 ApiClient.upload(FormData)—— 由它统一带 boundary 和 kind 字段。
   */
  onFileSelected(ev: Event, assetId?: string): void {
    const input = ev.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.busy.set('upload');
    this.api.upload<{ id: string }>(
      `/api/interviews/${this.id()}/assets`, file,
      { kind: 'Audio', assetId: assetId ?? '' })
      .subscribe({
        next: () => {
          input.value = '';   // 清空,否则同名文件第二次选不会触发 change
          this.busy.set(null);
          this.snack.open('录音已上传,可点击「开始转写」', '关闭', { duration: 4000 });
          this.load(false);
        },
        error: (e: Error) => {
          input.value = '';
          this.busy.set(null);
          this.snack.open(e.message, '关闭', { duration: 5000 });
        }
      });
  }

  /** 粘贴转写文本 → 回写该 material 的 transcript。 */
  saveTranscript(asset: InterviewAsset): void {
    const text = (this.transcriptDrafts[asset.id] ?? '').trim();
    if (!text) {
      this.snack.open('请先粘贴转写文本', '关闭', { duration: 3000 });
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
          this.snack.open('转写文本已保存', '关闭', { duration: 3000 });
          this.load(false);
        },
        error: (e: Error) => {
          this.busy.set(null);
          this.snack.open(e.message, '关闭', { duration: 5000 });
        }
      });
  }

  // ---------------------------------------------------------------- 问答

  addQuestion(): void {
    if (!this.questionForm.questionText.trim()) {
      this.snack.open('请填写面试官的问题', '关闭', { duration: 3000 });
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
        this.snack.open('问答已添加', '关闭', { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, '关闭', { duration: 5000 });
      }
    });
  }

  removeQuestion(q: InterviewQuestion): void {
    if (!confirm('删除这条问答?')) return;

    this.api.delete<void>(`/api/interviews/${this.id()}/questions/${q.id}`).subscribe({
      next: () => {
        this.snack.open('已删除', '关闭', { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => this.snack.open(e.message, '关闭', { duration: 5000 })
    });
  }

  // ---------------------------------------------------------------- 短板

  addWeakness(): void {
    if (!this.weaknessForm.title.trim()) {
      this.snack.open('请填写短板标题', '关闭', { duration: 3000 });
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
        this.snack.open('短板已记录', '关闭', { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, '关闭', { duration: 5000 });
      }
    });
  }

  removeWeakness(w: InterviewWeakness): void {
    if (!confirm(`删除短板「${w.title}」?`)) return;

    this.api.delete<void>(`/api/interviews/${this.id()}/weaknesses/${w.id}`).subscribe({
      next: () => {
        this.snack.open('已删除', '关闭', { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => this.snack.open(e.message, '关闭', { duration: 5000 })
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
      this.snack.open('公司名与岗位不能为空', '关闭', { duration: 3000 });
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
        this.snack.open('已保存', '关闭', { duration: 3000 });
        this.load(false);
      },
      error: (e: Error) => {
        this.busy.set(null);
        this.snack.open(e.message, '关闭', { duration: 5000 });
      }
    });
  }

  // ---------------------------------------------------------------- 展示辅助

  statusLabel(s: string): string {
    return this.statusOptions.find((o) => o.value === s)?.label ?? s;
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
