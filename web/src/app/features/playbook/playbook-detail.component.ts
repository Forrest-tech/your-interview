import {
  Component, ElementRef, OnDestroy, computed, effect, inject, signal, viewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { InterviewsApiService } from '../../core/api/interviews-api.service';
import { JobsApiService } from '../../core/api/jobs-api.service';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  Application, Company, InterviewDetail, InterviewEntry, InterviewRound, Paged
} from '../../core/models/api.models';
import { computeMatch, MatchResult } from '../../core/match/resume-match';
import { PdfContentViewerComponent } from '../../shared/pdf-viewer/pdf-content-viewer.component';
import { PlaybookV2Api } from './redesign/playbook-v2.api';
import { RoundReview, safeJson } from './redesign/v2.models';
import { IntakePanelComponent, IntakeType } from './redesign/intake/intake-panel.component';
import {
  entryEndKey, entryResultOutcome, entryStatusBadgeKey, formatLabel, htmlToPlainText, latestPrepInputAsset, makeV2Fmt, makeV2T,
  PREP_INPUT_EXPERIENCES, PREP_INPUT_RESUME, resolveActionDoneState, resolveScoreJd, roundDisplayNo, scoreTierKey, sortRoundsByDate
} from './redesign/v2.helpers';
import { RoundCardComponent } from './redesign/round/round-card.component';
import { CommLogComponent } from './redesign/round/comm-log.component';

interface ResumeFileItem {
  id: string;
  fileName: string;
  /** tailored | base | none —— 与 Tracker 有效简历接口同口径。 */
  kind: string;
  /** 有效简历的提取文本(评分与无 PDF 时的全文展示共用)。 */
  textContent?: string | null;
  isPrimary?: boolean;
}

/**
 * Playbook 详情页(2026-10-20 最终定稿 v2 全新设计):
 * ① 条目头(公司/岗位/状态 + 投递与 Recruiter 信息 + 下一步卡 + 岗位四格条)
 * ② 条目级统一入口「＋ 添加材料」(邮件 / 录音·转写 / 备注)
 * ③ 页面页签只剩 轮次 / 沟通记录(沟通有未处理安排时红点计数)
 * ④ 轮次 stepper(一屏一轮)+ Round 工作卡
 * ⑤ 轮次页下方保留:复盘与待办(行动项 + 复盘记录)
 * 旧版面(概览/简报/流水线等页签)按定稿取消;日历仍是独立页,不受影响。
 */
@Component({
  selector: 'app-playbook-detail',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterLink,
    MatButtonModule, MatIconModule, MatMenuModule,
    IntakePanelComponent, RoundCardComponent, CommLogComponent,
    PdfContentViewerComponent
  ],
  templateUrl: './playbook-detail.component.html',
  styleUrl: './playbook-detail.component.scss'
})
export class PlaybookDetailComponent implements OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly interviewsApi = inject(InterviewsApiService);
  private readonly jobsApi = inject(JobsApiService);
  private readonly v2 = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly id = signal('');
  readonly entry = signal<InterviewDetail | null>(null);

  // ---------------- 左侧条目导航(2026-10-21 定稿第八节) ----------------
  // 分组判定与列表页同一套:只消费后端 outcome 经 v2.helpers 的
  // entryResultOutcome 映射(票05/ARC-14 单一事实源),不判读 result 原文。
  // 导航只是切换路由(/playbook/:id),不整页刷新。
  readonly navEntries = signal<InterviewEntry[]>([]);
  readonly navCompletedOpen = signal(false);
  readonly navActive = computed(() =>
    this.navEntries().filter(e => entryResultOutcome(e.outcome) === null));
  readonly navCompleted = computed(() =>
    this.navEntries()
      .filter(e => entryResultOutcome(e.outcome) !== null)
      .sort((a, b) => entryEndKey(b).localeCompare(entryEndKey(a))));
  private navLoaded = false;

  navOutcome(e: InterviewEntry) { return entryResultOutcome(e.outcome); }

  goEntry(entryId: string): void {
    if (entryId === this.id()) return;
    void this.router.navigate(['/playbook', entryId]);
  }

  /** 全量拉取(与列表页同口径:分组要横跨全部条目,服务端分页会把两组切乱)。 */
  private async loadNav(): Promise<void> {
    try {
      const size = 200;
      const acc: InterviewEntry[] = [];
      for (let page = 1; page <= 10; page++) {
        const p = await this.interviewsApi.getEntries({
          page, pageSize: size
        }).toPromise();
        const items = p?.items ?? [];
        acc.push(...items);
        if (acc.length >= (p?.total ?? acc.length) || items.length === 0) break;
      }
      this.navEntries.set(acc);
      // 当前条目在 Completed 组时默认展开该组,否则高亮看不见
      const cur = acc.find(e => e.id === this.id());
      if (cur && entryResultOutcome(cur.outcome) !== null) this.navCompletedOpen.set(true);
    } catch { /* 导航拉不到不阻断详情页 */ }
  }
  readonly application = signal<Application | null>(null);
  readonly resumeFile = signal<ResumeFileItem | null>(null);
  /** 有效简历 PDF 的 object URL(展开面板内嵌预览用);换条目/销毁时回收。 */
  readonly resumePdfUrl = signal<string | null>(null);
  /** 基础简历全文(与 Tracker 同口径:有效简历无文本时回落它算分)。 */
  readonly baseResumeText = signal<string | null>(null);
  /** Jobs Company 记录的公司情报(Company.Profile —— Tracker 公司情报存这里)。 */
  readonly companyProfileText = signal<string | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly pageTab = signal<'rounds' | 'comm'>('rounds');
  readonly selectedRoundId = signal<string | null>(null);
  /** 票159：统一入口“保存并生成”的单次生成请求（透传给轮次卡，token 递增）。 */
  readonly prepGenRequest = signal<{ roundId: string; token: number } | null>(null);
  private prepGenToken = 0;
  readonly pendingComm = signal(0);
  readonly expandedCell = signal<'jd' | 'company' | 'resume' | 'score' | null>(null);
  readonly editingBasic = signal(false);
  readonly confirmDelete = signal(false);
  readonly newActionTitle = signal('');
  readonly debriefDraft = signal('');
  // 票 166：Review & to-dos 区块暂隐藏（隐藏不是删除：数据模型、saveDebrief()、debriefJson、i18n 键一律保留；恢复时改回 false 即一处恢复）。
  readonly hideReviewTodos = true;

  readonly intake = viewChild(IntakePanelComponent);

  // 基本信息编辑草稿
  readonly bCompany = signal('');
  readonly bRole = signal('');
  readonly bResult = signal('');
  readonly bNotes = signal('');

  // 票30(REQ-10):展示顺序 = 日期序(有日期升序在前、无日期按创建序在后),
  // 存储 order 只作创建序代理与数据关联键,不再直接决定展示顺序/编号。
  readonly rounds = computed(() => sortRoundsByDate(this.entry()?.rounds ?? []));

  /** 展示编号:轮次在日期序中的位置(与 stepper 的 $index+1 同一口径)。 */
  roundNo(r: InterviewRound): number {
    return roundDisplayNo(this.rounds(), r);
  }

  /**
   * 最近一封邮件(同一条记录同时供 Recruiter 与「最近联系」取数):
   * 各轮 emailsJson 里日期最近的一封 —— 与旧版头部 latestEmail 同一口径、同一底层数据。
   */
  readonly latestEmail = computed(() => {
    let best: { from: string; date: string } | null = null;
    for (const r of this.entry()?.rounds ?? []) {
      try {
        const list = JSON.parse(r.emailsJson ?? '[]') as { from?: string; date?: string }[];
        for (const m of list ?? []) {
          const d = (m.date ?? '').slice(0, 10);
          if (!m.from && !d) continue;
          if (!best || (d && d > best.date)) best = { from: m.from ?? '', date: d };
        }
      } catch { /* 坏 JSON 跳过该轮 */ }
    }
    return best;
  });

  /**
   * 头部 Recruiter(2026-10-21 用户裁决定版口径):= 最近一封邮件/沟通的发件人
   * (recruiter 本人,如 Khalil),不是各轮面试官汇总 —— 面试官只在各轮内逐人呈现。
   * 没有任何邮件记录时该项不显示(与其它元信息缺失时的处理一致)。
   */
  readonly recruiterLine = computed(() => this.latestEmail()?.from || null);

  /** 最近联系:与 Recruiter 同一条最新邮件记录的日期。 */
  readonly lastContactDate = computed(() => this.latestEmail()?.date || null);

  /** 四格条 JD 全文:条目自身 → 关联 Tracker 投递,展示前统一清洗 HTML。 */
  readonly jdFullText = computed(() =>
    htmlToPlainText(this.entry()?.jdText) || htmlToPlainText(this.application()?.jdText));
  /** 四格条 JD 摘要格:条目摘要 → 投递摘要 → 全文开头。 */
  readonly jdCellText = computed(() => {
    const sum = htmlToPlainText(this.entry()?.jdSummary)
      || htmlToPlainText(this.application()?.jdSummary);
    if (sum) return sum;
    const full = this.jdFullText();
    return full ? full.slice(0, 90) + '…' : '';
  });
  /**
   * 四格条公司格:条目公司介绍 → 投递的公司介绍 → Jobs Company 情报(Profile)。
   * 第三层是 2026-10-21 补的同源:公司情报在 Tracker 里维护在 Company 记录上,
   * 此前详情页只看前两处,情报明明存在却显示「暂无公司介绍」。
   */
  readonly companyCellText = computed(() =>
    htmlToPlainText(this.entry()?.companyProfile)
    || htmlToPlainText(this.application()?.companyDescription)
    || htmlToPlainText(this.companyProfileText()));

  /**
   * Resume Score —— 与 Tracker 同源同值(2026-10-21):
   * Tracker 的分数不是落库字段 resumeScore,而是详情打开时用「有效简历文本
   * (投递版优先)+ JD(全文优先、回落摘要)」喂共享 computeMatch 现算的;
   * 这里用完全相同的输入与同一函数(core/match/resume-match),所以同值。
   * 输入缺任一(JD 或简历文本)→ 与 Tracker 一样不出分,显示 Not scored yet。
   * 票 173: JD 输入走 resolveScoreJd —— 前两项与 Tracker 逐字一致(关联正常时
   * 分值一字不动),后两项回落条目 JD:旧实现只读投递 JD,投递未关联/关联失效/
   * JD 字段为空时,四格条明明展示着条目 JD 却算不出分,与 Score 页对不上。
   * 简历输入不动(有效简历文本 → 基础简历文本,与 Tracker 同口径)。
   */
  readonly resumeMatch = computed<MatchResult | null>(() => {
    const jd = resolveScoreJd(this.application(), this.entry());
    const resume = this.resumeFile()?.textContent ?? this.baseResumeText();
    if (!jd || !resume) return null;
    return computeMatch(resume, jd);
  });

  readonly resumeScore = computed(() => this.resumeMatch()?.score ?? null);

  /** 票 173:分数档位标签键(与 Tracker 圆环标签同阈值同文案),无分时为 null。 */
  readonly resumeTier = computed(() => {
    const s = this.resumeScore();
    return s == null ? null : scoreTierKey(s);
  });

  /**
   * 票164:详情卡绑定的选中轮。
   * 回落链:显式选中 → 当前轮 → 末轮。选中 id 为空或已失效(轮被删)时
   * 立即回落当前轮,不等默认选中 effect 纠正,首渲染即是当前轮、无闪变。
   */
  readonly selectedRound = computed(() => {
    const rs = this.rounds();
    if (!rs.length) return null;
    return rs.find(r => r.id === this.selectedRoundId())
      ?? rs.find(r => r.id === this.currentRoundId())
      ?? rs[rs.length - 1];
  });

  /** 票164:点选时间线单元即选中该轮,下方详情卡随之切换。 */
  selectRound(id: string): void {
    if (this.selectedRoundId() !== id) this.selectedRoundId.set(id);
  }

  // ── 票164:轮次选择 ─────────────────────────────────────────────
  // 「当前轮」定义沿用票123 R-9:最早的一个「日期在未来且没有结果」的轮,
  // 若不存在(全都有结果或都已过期),当前轮 = 最后一轮。当前轮只决定
  // 默认选中,不再决定详情卡(详情卡跟随用户点选)。

  /**
   * 某轮是否算「已完成」(当前轮判据)。
   * 有记录结果,或日期已过 —— 二者之一即成立。
   */
  roundIsDone(r: InterviewRound): boolean {
    if (r.outcome && r.outcome !== 'Pending' && r.outcome !== '') return true;
    return this.roundDatePassed(r);
  }

  /** 该轮的日期是否已过(没有日期则不算过)。 */
  private roundDatePassed(r: InterviewRound): boolean {
    if (!r.scheduledDate) return false;
    const d = new Date(r.scheduledDate);
    if (Number.isNaN(d.getTime())) return false;
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    d.setHours(0, 0, 0, 0);
    return d.getTime() < today.getTime();
  }

  /**
   * 当前轮 = 最早的一个「日期在未来且没有结果」的轮。
   * 若不存在(全都有结果或都已过期),当前轮 = 最后一轮。
   */
  readonly currentRoundId = computed<string | null>(() => {
    const rs = this.rounds();
    if (!rs.length) return null;
    const future = rs.find(r => !this.roundIsDone(r) && !this.roundDatePassed(r));
    return (future ?? rs[rs.length - 1]).id;
  });

  /** 某轮的材料数(与轮次工作卡的口径一致:按 roundId 归属)。 */
  roundAssetsCount(r: InterviewRound): number {
    return (this.entry()?.assets ?? []).filter(a => a.roundId === r.id).length;
  }

  /** 结果文案(与轮次工作卡同一套取值)。 */
  outcomeLabel(o: string): string {
    switch (o) {
      case 'Pending': return this.t('pb.v2.r.ocPending');
      case 'Passed': return this.t('outcome.Passed');
      case 'Failed': return this.t('outcome.Failed');
      case 'Cancelled': return this.t('pb.v2.r.ocCancelled');
      default: return o;
    }
  }

  /** 面试形式文案(共用 v2.helpers 的 formatLabel)。 */
  formatLabel(v: string | null | undefined): string {
    return formatLabel(this.t, v);
  }

  // 票80:头部徽章与分组共用同一事实源 —— 只经 e.outcome 取键
  // (entryStatusBadgeKey → entryResultOutcome),不再判读 result 原文、
  // 分析状态(Analyzed)或默认 Completed 兜底。
  readonly statusText = computed(() => {
    const e = this.entry();
    if (!e) return '';
    return this.t(entryStatusBadgeKey(e.outcome));
  });

  readonly readiness = computed(() => {
    const e = this.entry();
    if (!e) return 0;
    let s = 0;
    if (e.jdText || e.jdSummary) s += 20;
    if (e.companyProfile) s += 15;
    if (this.rounds().some(r => r.prepReportJson)) s += 25;
    if (e.questionsToAskJson && e.questionsToAskJson !== '[]') s += 15;
    const next = this.rounds().find(r => r.outcome === 'Pending');
    if (next?.checklistJson) {
      try {
        const items = JSON.parse(next.checklistJson) as { done?: boolean }[];
        if (items.length) {
          s += Math.round(25 * items.filter(i => i.done).length / items.length);
        }
      } catch { /* 清单损坏时不计分 */ }
    }
    return Math.min(100, s);
  });

  /** 下一步建议:按"最该做的一件事"排序,避免把待办一股脑堆给用户。
   *  票123 R-3:返回结构化 { title, lines[] } —— 标题是主句,其余事实各占一行弱化文字,
   *  不再把标签与值串在中点上(原 nextPrep 的「备考 Round {n} · {stage}(日期)」写法)。 */
  readonly nextStep = computed(() => {
    const e = this.entry();
    if (!e) return null;
    if (this.pendingComm() > 0) {
      return { title: this.fmt('pb.v2.d.nextPendingComm', { n: this.pendingComm() }), lines: [] as string[] };
    }
    const today = new Date().toISOString().slice(0, 10);
    const upcoming = this.rounds().find(r =>
      r.outcome === 'Pending' && (!r.scheduledDate || r.scheduledDate >= today));
    if (upcoming) {
      const lines: string[] = [
        this.fmt('pb.v2.d.nextPrepStage', { stage: upcoming.stage })
      ];
      if (upcoming.scheduledDate) {
        lines.push(upcoming.scheduledDate + (upcoming.scheduledTime ? ' ' + upcoming.scheduledTime : ''));
      }
      if (upcoming.format) lines.push(formatLabel(this.t, upcoming.format));
      return { title: this.fmt('pb.v2.d.nextPrepTitle', { n: this.roundNo(upcoming) }), lines };
    }
    const unreviewed = [...this.rounds()].reverse()
      .find(r => r.outcome !== 'Pending' && !r.reviewJson);
    if (unreviewed) {
      return { title: this.fmt('pb.v2.d.nextReview', { n: this.roundNo(unreviewed) }), lines: [] as string[] };
    }
    const app = this.application();
    if (app?.nextFollowUpAt && app.nextFollowUpAt.slice(0, 10) <= today) {
      return { title: this.t('pb.v2.d.nextFollow'), lines: [] as string[] };
    }
    return { title: this.t('pb.v2.d.nextIdle'), lines: [] as string[] };
  });

  constructor() {
    this.route.paramMap.subscribe(pm => {
      const id = pm.get('id');
      if (id && id !== this.id()) {
        this.id.set(id);
        this.host.nativeElement.scrollTop = 0;
        void this.load();
        if (!this.navLoaded) { this.navLoaded = true; void this.loadNav(); }
      }
    });
    // 票164:首次加载后选默认轮次:当前轮(与旧详情卡默认一致),否则最近一轮。
    // 选中 id 失效(轮被删)时同样纠正;selectedRound 回落链保证纠正前也不闪变。
    effect(() => {
      const rs = this.rounds();
      if (rs.length && !rs.some(r => r.id === this.selectedRoundId())) {
        const cur = this.currentRoundId();
        const pending = rs.find(r => r.outcome === 'Pending');
        this.selectedRoundId.set(cur ?? (pending ?? rs[rs.length - 1]).id);
      }
    }, { allowSignalWrites: true });
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const e = await this.interviewsApi.getEntry(this.id()).toPromise();
      if (!e) throw new Error('empty');
      this.entry.set(e);
      this.debriefDraft.set(parseDebrief(e.debriefJson));
      if (e.jobApplicationId) {
        this.loadApplication(e.jobApplicationId);
        void this.refreshPendingComm(e.jobApplicationId);
      } else {
        this.pendingComm.set(countPendingFromAssets(e));
        void this.tryMatchApplication(e);
      }
    } catch {
      this.loadError.set(this.t('pb.v2.d.loadFailed'));
    } finally {
      this.loading.set(false);
    }
  }

  private loadApplication(appId: string): void {
    this.jobsApi.getApplication(appId).subscribe({
      next: app => {
        this.application.set(app);
        if (app.companyId) this.loadCompanyProfile(app.companyId);
      },
      error: () => undefined
    });
    this.loadResumeFile(appId);
    this.loadBaseResumeText();
  }

  /** Jobs Company 情报(Profile):与 Tracker 详情里单独查 Company 的口径一致。 */
  private loadCompanyProfile(companyId: string): void {
    this.jobsApi.getCompany(companyId).subscribe({
      next: c => this.companyProfileText.set(c?.profile ?? null),
      error: () => undefined
    });
  }

  /** 基础简历全文(Tracker 同源:/api/jobs/resume-text)。 */
  private loadBaseResumeText(): void {
    this.jobsApi.getResumeText().subscribe({
      next: r => this.baseResumeText.set(r?.resumeText ?? null),
      error: () => undefined
    });
  }

  /**
   * 本投递简历:走 Jobs 的「有效简历」接口(单数 resume —— tailored 优先、
   * 无 tailored 时回落基础简历,与 Tracker 同源)。
   * 此前误调不存在的 resume-files 列表接口,永远 404、永远显示未关联。
   * 2026-10-21:不再只取 fileName —— kind/textContent 一并留住(算分 + 无 PDF
   * 时全文展示),并按 Tracker 同法取 resume-file 二进制在展开面板内嵌预览。
   */
  private loadResumeFile(appId: string): void {
    this.jobsApi.getApplicationResume(appId)
      .subscribe({
        next: r => {
          const kind = r?.kind === 'tailored' || r?.kind === 'base' ? r.kind : 'none';
          this.resumeFile.set(r?.fileName
            ? { id: appId, fileName: r.fileName, kind, textContent: r.textContent ?? null }
            : null);
          this.setResumePdfUrl(null);
          if (kind !== 'none') {
            this.jobsApi.getApplicationResumeFile(appId).subscribe({
              next: blob => this.setResumePdfUrl(URL.createObjectURL(blob)),
              error: () => this.setResumePdfUrl(null)
            });
          }
        },
        error: () => undefined
      });
  }

  private setResumePdfUrl(url: string | null): void {
    const old = this.resumePdfUrl();
    if (old && old !== url) URL.revokeObjectURL(old);
    this.resumePdfUrl.set(url);
  }

  ngOnDestroy(): void {
    this.setResumePdfUrl(null);
  }

  /**
   * 条目未关联投递时:按公司名 + 岗位在 Tracker 投递里找候选。
   * 恰好一条才认定同源 —— 用它展示四格条/简历/分数,并 additive 回填关联
   * (后端 link-application 只在当前未关联时生效,不覆盖任何已有数据);
   * 0 条或多条都不配,保持未关联(避免重复投递时乱配)。
   *
   * 匹配口径(2026-10-21 放宽):真实岗位标题常带变体 —— 级别前缀
   * (Senior/Lead…)、"at <公司>" 后缀、" — 城市" 后缀、括号注记、标点差异,
   * 上一版"归一化全等"对这些变体全部失败、头部元信息整行缺失。现归一化时
   * 先剥掉这些噪音再比对,并允许一方词串包含另一方;公司名走同一归一化。
   * 防误配的总闸不变:候选必须恰好 1 条。
   */
  private async tryMatchApplication(e: InterviewDetail): Promise<void> {
    try {
      // 不用服务端 search 先过滤:search 是原文包含匹配,公司名只要差一个空格/
      // 标点(条目手填 vs Tracker 抓取)候选就会被滤掉,归一化匹配无从谈起。
      // 拉最近 50 条在客户端做归一化匹配(恰好 1 条才回填)。
      const page = await this.jobsApi.getApplications<Application>({
        page: 1, pageSize: 50
      }).toPromise();
      const hits = (page?.items ?? []).filter(a =>
        nameMatches(a.companyName, e.companyName) && roleMatches(a.role, e.role, a.companyName, e.companyName));
      if (hits.length !== 1) return;
      const app = hits[0];
      this.application.set(app);
      this.loadResumeFile(app.id);
      if (app.companyId) this.loadCompanyProfile(app.companyId);
      this.loadBaseResumeText();
      void this.refreshPendingComm(app.id);
      try {
        await this.interviewsApi.linkApplication(e.id, app.id).toPromise();
        this.entry.set({ ...e, jobApplicationId: app.id });
      } catch { /* 回填失败不影响本次展示;下次打开会重新匹配 */ }
    } catch { /* 匹配失败保持未关联,不阻塞页面 */ }
  }

  private async refreshPendingComm(appId: string): Promise<void> {
    try {
      const rows = await this.v2.listCommunications(appId).toPromise();
      const items = (rows ?? []).map(r => PlaybookV2Api.commFromJobs(r));
      this.pendingComm.set(items.filter(c =>
        !c.meta.movedToRoundId && PlaybookV2Api.isPending(c)).length);
    } catch { /* 红点统计失败不阻塞页面 */ }
  }

  onCommPending(n: number): void { this.pendingComm.set(n); }

  async onChildChanged(): Promise<void> {
    const keepRound = this.selectedRoundId();
    await this.load();
    void this.loadNav();
    if (keepRound && this.rounds().some(r => r.id === keepRound)) {
      this.selectedRoundId.set(keepRound);
    }
  }

  // ---------------- 头部动作 ----------------

  openIntake(type: IntakeType = 'email', roundId?: string): void {
    this.intake()?.openPanel(type, roundId);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  /** 票159：Prep 页读回区“编辑输入” / “添加输入”——打开统一对话框并预填。 */
  openPrepInput(roundId: string, purpose: 'experiences' | 'resume', assetId: string | null, text: string): void {
    this.intake()?.openPrepInput(purpose, roundId, text, assetId ?? undefined);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  /**
   * 票159：统一入口“保存并生成”跳转。先刷新（新资产必须已在 entry 内），再分两路：
   * - 目标轮就是当前详情卡展示的轮：切到轮次页签，把 token 透传给轮次卡 →
   *   轮次卡切到 Prep 页签 → 备考报告触发一次生成（失败提示留在 Prep 页）；
   * - 目标轮不是当前展示轮：直接以该轮当前归档原文调一次生成，
   *   成功后刷新并提示，失败以 snackbar 提示（无 Prep 页可落失败提示）。
   * 注意：详情卡绑定的是 selectedRound（票164 点选机制），故“当前展示轮”以
   * selectedRound 为准；原 159 基线无点选时以 currentRound 为准，164 落地后收敛至此
   * （currentRound 计算属性已随 164 的轨道拆除一并删除，本方法不再引用它）。
   */
  async onPrepGenerate(ev: { roundId: string }): Promise<void> {
    await this.onChildChanged();
    this.pageTab.set('rounds');
    const entry = this.entry();
    if (!entry) return;
    if (this.selectedRound()?.id === ev.roundId) {
      this.prepGenRequest.set({ roundId: ev.roundId, token: ++this.prepGenToken });
      return;
    }
    const exp = latestPrepInputAsset(entry.assets ?? [], ev.roundId, PREP_INPUT_EXPERIENCES);
    const res = latestPrepInputAsset(entry.assets ?? [], ev.roundId, PREP_INPUT_RESUME);
    try {
      const expText = exp
        ? await this.v2.getTranscript(entry.id, exp.id).toPromise().then(t => t?.transcriptText ?? '').catch(() => '')
        : '';
      const resText = res
        ? await this.v2.getTranscript(entry.id, res.id).toPromise().then(t => t?.transcriptText ?? '').catch(() => '')
        : '';
      await this.v2.generatePrepReport(entry.id, ev.roundId, {
        jdText: entry.jdText ?? null,
        resumeText: resText.trim() || null,
        experiences: expText.trim() || null,
        language: this.i18n.lang()
      }).toPromise();
      this.snack.open(this.t('pb.v2.p.generatedOk'), this.t('common.close'), { duration: 2500 });
      await this.onChildChanged();
    } catch (e) {
      this.snack.open(this.fmt('pb.v2.p.generateFailed', { msg: e instanceof Error ? e.message : this.t('pb.v2.rec.unknownError') }), this.t('common.close'), { duration: 4000 });
    }
  }

  goMock(): void {
    const e = this.entry();
    void this.router.navigate(['/mock'], {
      queryParams: { company: e?.companyName ?? '', role: e?.role ?? '' }
    });
  }

  openJd(): void {
    const link = this.application()?.link;
    if (link) { window.open(link, '_blank', 'noopener'); return; }
    this.expandedCell.set(this.expandedCell() === 'jd' ? null : 'jd');
  }

  toggleCell(cell: 'jd' | 'company' | 'resume' | 'score'): void {
    this.expandedCell.set(this.expandedCell() === cell ? null : cell);
  }

  roundScore(r: InterviewRound): number | null {
    return safeJson<RoundReview>(r.reviewJson)?.overall ?? null;
  }

  roundStateIcon(r: InterviewRound): string {
    if (r.outcome === 'Passed') return '✓';
    if (r.outcome === 'Failed') return '✗';
    if (r.outcome === 'Cancelled') return '—';
    return '·';
  }

  // ---------------- 基本信息 / 删除 ----------------

  startEditBasic(): void {
    const e = this.entry();
    if (!e) return;
    this.bCompany.set(e.companyName);
    this.bRole.set(e.role);
    this.bResult.set(e.result ?? '');
    this.bNotes.set(e.notes ?? '');
    this.editingBasic.set(true);
    this.confirmDelete.set(false);
  }

  async saveBasic(): Promise<void> {
    const e = this.entry();
    if (!e) return;
    try {
      await this.interviewsApi.updateEntry(e.id, {
        companyName: this.bCompany(),
        role: this.bRole(),
        companyProfile: e.companyProfile ?? null,
        jdText: e.jdText ?? null,
        jdSummary: e.jdSummary ?? null,
        roundNo: e.roundNo,
        interviewDate: e.interviewDate ?? null,
        interviewFormat: e.interviewFormat ?? null,
        interviewers: e.interviewers ?? null,
        location: e.location ?? null,
        result: this.bResult() || null,
        notes: this.bNotes() || null,
        questionsToAskJson: e.questionsToAskJson ?? null,
        debriefJson: e.debriefJson ?? null,
        version: e.version ?? null
      }).toPromise();
      this.editingBasic.set(false);
      this.snack.open(this.t('pb.v2.d.saved'), this.t('common.close'), { duration: 2000 });
      await this.load();
    } catch {
      this.snack.open(this.t('pb.v2.d.saveFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  async deleteEntry(): Promise<void> {
    if (!this.confirmDelete()) { this.confirmDelete.set(true); return; }
    try {
      await this.interviewsApi.deleteEntry(this.id(), this.entry()?.version ?? null).toPromise();
      void this.router.navigate(['/playbook']);
    } catch {
      this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  // ---------------- 复盘与待办 ----------------

  async addAction(): Promise<void> {
    const title = this.newActionTitle().trim();
    if (!title) return;
    try {
      await this.interviewsApi.createAction(this.id(), title).toPromise();
      this.newActionTitle.set('');
      await this.load();
    } catch {
      this.snack.open(this.t('pb.v2.d.addFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  async toggleAction(actionId: string, ev?: Event): Promise<void> {
    const action = this.entry()?.actions?.find(a => a.id === actionId);
    if (!action) return;
    const beforeDone = action.isDone;
    let failed = false;
    try {
      await this.interviewsApi.updateAction(this.id(), actionId, !beforeDone).toPromise();
    } catch {
      failed = true;
      this.snack.open(this.t('pb.v2.d.toggleFailed'), this.t('common.close'), { duration: 3000 });
    }
    await this.load();
    if (failed) {
      // 票116 F-H1:点击已由浏览器原生翻转 checkbox DOM,而失败后 load() 载回的
      // isDone 与点前同值,[checked] 单向绑定不会重写 DOM —— 显式按回读后的
      // 服务端真值回写该 checkbox,使勾选态/标签(done 类同源真值)/服务端三方一致。
      const box = ev?.target as HTMLInputElement | null;
      if (box) box.checked = resolveActionDoneState(this.entry()?.actions, actionId, beforeDone);
    }
  }

  async deleteAction(actionId: string): Promise<void> {
    // 票116 O-H4(票80 同项):改前 .catch(() => undefined) 静默吞错,删除失败
    // 用户无感、列表与服务端真值可不一致。对齐票12/75 族与本文件 deleteEntry
    // 先例:失败 snack 明示(pb.v2.d.deleteFailed 既有键),成败都 load() 以
    // 服务端真值刷新;成功路径(请求→load)逐字守恒。
    try {
      await this.interviewsApi.deleteAction(this.id(), actionId).toPromise();
    } catch {
      this.snack.open(this.t('pb.v2.d.deleteFailed'), this.t('common.close'), { duration: 3000 });
    }
    await this.load();
  }

  async saveDebrief(): Promise<void> {
    const e = this.entry();
    if (!e) return;
    try {
      await this.interviewsApi.updateEntry(e.id, {
        companyName: e.companyName,
        role: e.role,
        companyProfile: e.companyProfile ?? null,
        jdText: e.jdText ?? null,
        jdSummary: e.jdSummary ?? null,
        roundNo: e.roundNo,
        interviewDate: e.interviewDate ?? null,
        interviewFormat: e.interviewFormat ?? null,
        interviewers: e.interviewers ?? null,
        location: e.location ?? null,
        result: e.result ?? null,
        notes: e.notes ?? null,
        questionsToAskJson: e.questionsToAskJson ?? null,
        debriefJson: JSON.stringify({ text: this.debriefDraft() }),
        version: e.version ?? null
      }).toPromise();
      this.snack.open(this.t('pb.v2.d.debriefSaved'), this.t('common.close'), { duration: 2000 });
    } catch {
      this.snack.open(this.t('pb.v2.d.saveFailed'), this.t('common.close'), { duration: 3000 });
    }
  }
}

function parseDebrief(json: string | null | undefined): string {
  if (!json) return '';
  try {
    const j = JSON.parse(json) as { text?: string; notes?: string };
    return j.text ?? j.notes ?? '';
  } catch {
    return json;
  }
}

function countPendingFromAssets(e: InterviewDetail): number {
  return (e.assets ?? []).filter(a => {
    if (a.roundId) return false;
    try {
      const m = JSON.parse(a.metaJson ?? '{}') as { arrangement?: unknown; handlingStatus?: string };
      return !!m.arrangement && m.handlingStatus === 'Pending';
    } catch { return false; }
  }).length;
}

/** 名称归一化:小写、去括号注记、标点变空格、分词后重组(词间单空格)。 */
function wordsNorm(raw: string | null | undefined): string {
  const s = (raw ?? '').toLowerCase()
    .replace(/[（(][^）)]*[）)]/g, ' ')
    .replace(/[\p{P}\p{S}]+/gu, ' ');
  return s.split(/\s+/).filter(Boolean).join(' ');
}

/** 相等,或一方词串完整包含另一方(两边都非空才谈包含)。 */
function eqOrContains(a: string, b: string): boolean {
  if (!a || !b) return false;
  return a === b || a.includes(b) || b.includes(a);
}

/** 公司名匹配:归一化后相等或互相包含。 */
function nameMatches(a: string, b: string): boolean {
  return eqOrContains(wordsNorm(a), wordsNorm(b));
}

const LEVEL_WORDS = new Set([
  'senior', 'sr', 'lead', 'principal', 'staff', 'junior', 'jr', 'intermediate', 'associate'
]);

/**
 * 岗位归一化:在 wordsNorm 基础上再剥三类噪音 ——
 * "at <公司>" 后缀、" — <城市>" 类破折号后缀、开头的级别词(Senior/Lead…)。
 */
function roleNorm(raw: string | null | undefined, company: string | null | undefined): string {
  let s = (raw ?? '').toLowerCase();
  const comp = (company ?? '').trim().toLowerCase();
  if (comp) s = s.split(` at ${comp}`).join(' ');
  s = s.replace(/\s+[—–]\s+.*$/, ' ');
  const words = wordsNorm(s).split(' ').filter(Boolean);
  while (words.length > 1 && LEVEL_WORDS.has(words[0])) words.shift();
  return words.join(' ');
}

/** 岗位匹配:各自按"对方的公司名"剥后缀后归一化,再相等或互相包含。 */
function roleMatches(appRole: string, entryRole: string, appCompany: string, entryCompany: string): boolean {
  return eqOrContains(roleNorm(appRole, appCompany), roleNorm(entryRole, entryCompany));
}
