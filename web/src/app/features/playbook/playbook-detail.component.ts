import {
  Component, computed, effect, inject, signal, viewChild
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiClient } from '../../core/api/api-client';
import { I18nService } from '../../core/i18n/i18n.service';
import {
  Application, InterviewDetail, InterviewRound, Paged
} from '../../core/models/api.models';
import { PlaybookV2Api } from './redesign/playbook-v2.api';
import { RoundReview, safeJson } from './redesign/v2.models';
import { IntakePanelComponent, IntakeType } from './redesign/intake/intake-panel.component';
import {
  htmlToPlainText, joinNames, makeV2Fmt, makeV2T
} from './redesign/v2.helpers';
import { RoundCardComponent } from './redesign/round/round-card.component';
import { CommLogComponent } from './redesign/round/comm-log.component';

interface ResumeFileItem { id: string; fileName: string; isPrimary?: boolean; }

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
    IntakePanelComponent, RoundCardComponent, CommLogComponent
  ],
  templateUrl: './playbook-detail.component.html',
  styleUrl: './playbook-detail.component.scss'
})
export class PlaybookDetailComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiClient);
  private readonly v2 = inject(PlaybookV2Api);
  private readonly snack = inject(MatSnackBar);
  readonly i18n = inject(I18nService);
  readonly t = makeV2T(this.i18n);
  readonly fmt = makeV2Fmt(this.i18n);

  readonly id = signal('');
  readonly entry = signal<InterviewDetail | null>(null);
  readonly application = signal<Application | null>(null);
  readonly resumeFile = signal<ResumeFileItem | null>(null);
  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);

  readonly pageTab = signal<'rounds' | 'comm'>('rounds');
  readonly selectedRoundId = signal<string | null>(null);
  readonly pendingComm = signal(0);
  readonly expandedCell = signal<'jd' | 'company' | 'resume' | 'score' | null>(null);
  readonly editingBasic = signal(false);
  readonly confirmDelete = signal(false);
  readonly newActionTitle = signal('');
  readonly debriefDraft = signal('');

  readonly intake = viewChild(IntakePanelComponent);

  // 基本信息编辑草稿
  readonly bCompany = signal('');
  readonly bRole = signal('');
  readonly bResult = signal('');
  readonly bNotes = signal('');

  readonly rounds = computed(() =>
    [...(this.entry()?.rounds ?? [])].sort((a, b) => a.order - b.order));

  /** 头部联系人行:各轮面试官汇总(Recruiter 沟通对象在沟通记录里)。 */
  readonly recruiterLine = computed(() => {
    const names = new Set<string>();
    for (const r of this.rounds()) {
      for (const n of (r.interviewers ?? '').split(/[,、/]/)) {
        const t = n.trim();
        if (t) names.add(t);
      }
    }
    return joinNames(this.i18n, [...names]);
  });

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
  /** 四格条公司格:条目公司介绍 → 投递的公司介绍。 */
  readonly companyCellText = computed(() =>
    htmlToPlainText(this.entry()?.companyProfile)
    || htmlToPlainText(this.application()?.companyDescription));

  readonly selectedRound = computed(() => {
    const rs = this.rounds();
    if (!rs.length) return null;
    return rs.find(r => r.id === this.selectedRoundId()) ?? rs[rs.length - 1];
  });

  readonly statusText = computed(() => {
    const e = this.entry();
    if (!e) return '';
    if (e.result) return e.result;
    if (this.rounds().some(r => r.outcome === 'Pending')) return this.t('pb.v2.d.statusProgress');
    if ((e.status as string) === 'Analyzed') return this.t('pb.v2.d.statusDone');
    return e.result ?? this.t('pb.v2.d.statusDone');
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

  /** 下一步建议:按"最该做的一件事"排序,避免把待办一股脑堆给用户。 */
  readonly nextStep = computed(() => {
    const e = this.entry();
    if (!e) return null;
    if (this.pendingComm() > 0) {
      return {
        text: this.fmt('pb.v2.d.nextPendingComm', { n: this.pendingComm() }),
        action: 'comm' as const, btn: this.t('pb.v2.d.nextGoHandle'), roundId: undefined as string | undefined
      };
    }
    const today = new Date().toISOString().slice(0, 10);
    const upcoming = this.rounds().find(r =>
      r.outcome === 'Pending' && (!r.scheduledDate || r.scheduledDate >= today));
    if (upcoming) {
      return {
        text: this.fmt('pb.v2.d.nextPrep', { n: upcoming.order, stage: upcoming.stage }) +
          (upcoming.scheduledDate ? `(${upcoming.scheduledDate}${upcoming.scheduledTime ? ' ' + upcoming.scheduledTime : ''})` : ''),
        action: 'prep' as const, btn: this.t('pb.v2.d.nextGoPrep'), roundId: upcoming.id as string | undefined
      };
    }
    const unreviewed = [...this.rounds()].reverse()
      .find(r => r.outcome !== 'Pending' && !r.reviewJson);
    if (unreviewed) {
      return {
        text: this.fmt('pb.v2.d.nextReview', { n: unreviewed.order }),
        action: 'review' as const, btn: this.t('pb.v2.d.nextGoReview'), roundId: unreviewed.id as string | undefined
      };
    }
    const app = this.application();
    if (app?.nextFollowUpAt && app.nextFollowUpAt.slice(0, 10) <= today) {
      return {
        text: this.t('pb.v2.d.nextFollow'),
        action: 'comm' as const, btn: this.t('pb.v2.d.nextGoComm'), roundId: undefined as string | undefined
      };
    }
    return {
      text: this.t('pb.v2.d.nextIdle'),
      action: 'none' as const, btn: '', roundId: undefined as string | undefined
    };
  });

  constructor() {
    this.route.paramMap.subscribe(pm => {
      const id = pm.get('id');
      if (id && id !== this.id()) {
        this.id.set(id);
        void this.load();
      }
    });
    // 首次加载后选默认轮次:第一个未开始的,否则最近一轮。
    effect(() => {
      const rs = this.rounds();
      if (rs.length && !rs.some(r => r.id === this.selectedRoundId())) {
        const pending = rs.find(r => r.outcome === 'Pending');
        this.selectedRoundId.set((pending ?? rs[rs.length - 1]).id);
      }
    }, { allowSignalWrites: true });
  }

  async load(): Promise<void> {
    this.loading.set(true);
    this.loadError.set(null);
    try {
      const e = await this.api.get<InterviewDetail>(`/api/interviews/${this.id()}`).toPromise();
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
    this.api.get<Application>(`/api/jobs/applications/${appId}`).subscribe({
      next: app => this.application.set(app),
      error: () => undefined
    });
    this.loadResumeFile(appId);
  }

  /**
   * 本投递简历:走 Jobs 的「有效简历」接口(单数 resume —— tailored 优先、
   * 无 tailored 时回落基础简历,与 Tracker 同源)。
   * 此前误调不存在的 resume-files 列表接口,永远 404、永远显示未关联。
   */
  private loadResumeFile(appId: string): void {
    this.api.get<{ fileName?: string | null }>(`/api/jobs/applications/${appId}/resume`)
      .subscribe({
        next: r => this.resumeFile.set(
          r?.fileName ? { id: appId, fileName: r.fileName } : null),
        error: () => undefined
      });
  }

  /**
   * 条目未关联投递时:按公司名 + 岗位(归一化后全等)在 Tracker 投递里找候选。
   * 恰好一条才认定同源 —— 用它展示四格条/简历/分数,并 additive 回填关联
   * (后端 link-application 只在当前未关联时生效,不覆盖任何已有数据);
   * 0 条或多条都不配,保持未关联(避免重复投递时乱配)。
   */
  private async tryMatchApplication(e: InterviewDetail): Promise<void> {
    try {
      const page = await this.api.get<Paged<Application>>('/api/jobs/applications', {
        search: e.companyName, page: 1, pageSize: 50
      }).toPromise();
      const norm = (v: string) => v.toLowerCase().replace(/[\s\p{P}\p{S}]+/gu, '');
      const hits = (page?.items ?? []).filter(a =>
        norm(a.companyName) === norm(e.companyName) && norm(a.role) === norm(e.role));
      if (hits.length !== 1) return;
      const app = hits[0];
      this.application.set(app);
      this.loadResumeFile(app.id);
      void this.refreshPendingComm(app.id);
      try {
        await this.api.post(`/api/interviews/${e.id}/link-application`,
          { jobApplicationId: app.id }).toPromise();
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
    if (keepRound && this.rounds().some(r => r.id === keepRound)) {
      this.selectedRoundId.set(keepRound);
    }
  }

  // ---------------- 头部动作 ----------------

  openIntake(type: IntakeType = 'email', roundId?: string): void {
    this.intake()?.openPanel(type, roundId);
    window.scrollTo({ top: 0, behavior: 'smooth' });
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

  doNextStep(): void {
    const ns = this.nextStep();
    if (!ns) return;
    if (ns.action === 'comm') this.pageTab.set('comm');
    else if ((ns.action === 'prep' || ns.action === 'review') && ns.roundId) {
      this.selectedRoundId.set(ns.roundId);
      this.pageTab.set('rounds');
    }
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
      await this.api.put(`/api/interviews/${e.id}`, {
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
        debriefJson: e.debriefJson ?? null
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
      await this.api.delete(`/api/interviews/${this.id()}`).toPromise();
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
      await this.api.post(`/api/interviews/${this.id()}/actions`, { title }).toPromise();
      this.newActionTitle.set('');
      await this.load();
    } catch {
      this.snack.open(this.t('pb.v2.d.addFailed'), this.t('common.close'), { duration: 3000 });
    }
  }

  async toggleAction(actionId: string): Promise<void> {
    await this.api.put(`/api/interviews/${this.id()}/actions/${actionId}/toggle`, {}).toPromise()
      .catch(() => undefined);
    await this.load();
  }

  async deleteAction(actionId: string): Promise<void> {
    await this.api.delete(`/api/interviews/${this.id()}/actions/${actionId}`).toPromise()
      .catch(() => undefined);
    await this.load();
  }

  async saveDebrief(): Promise<void> {
    const e = this.entry();
    if (!e) return;
    try {
      await this.api.put(`/api/interviews/${e.id}`, {
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
        debriefJson: JSON.stringify({ text: this.debriefDraft() })
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
